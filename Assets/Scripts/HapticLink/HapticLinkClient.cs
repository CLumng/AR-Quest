using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// The headset's end of the haptic link: connects to the PC bridge (HapticBridge) over TCP, keeps the latest stylus state and sends the
/// force it is given. Two background threads (read, write) so a slow network never stalls a frame; reconnects by itself every second.
/// Everything is in the device's frame (mm, N, see HapticProtocol). Plain class, no Unity types: the self-check drives it in the Editor.
/// </summary>
public sealed class HapticLinkClient : IDisposable
{
    public const int SendIntervalMs = 8; // the write thread sends the command this often (125 Hz)
    public const int MaxRunTries = 3;    // times a run goes out before the headset gives up on the PC (its copy on the headset stays)

    readonly string _host;
    readonly int _port;
    readonly object _lock = new();
    readonly HapticProtocol.StateFrame _state = new();
    readonly long[] _sent = new long[256]; // when each command seq went out, by seq % 256, for the round trip
    readonly Thread _reader, _writer;
    volatile bool _stop;
    NetworkStream _stream;
    TcpClient _client;
    long _lastRx;                           // Stopwatch ticks of the last state, 0 = none yet
    float _fx, _fy, _fz;
    bool _enable;
    uint _seq;
    string _runCsv, _savedName;             // the run waiting to be saved on the PC (null once it is), and the file the PC saved the last one as
    long _runId, _savedId;
    bool _runSent;                          // it went out on this connection: after a drop it goes again, until the PC answers
    int _runTries;                          // ... at most MaxRunTries times: a bridge that does not know runs (an old build) drops the link at each one
    long _rxCount, _countFrom;
    volatile float _rtt, _rate;

    public bool Connected { get; private set; }
    public float RoundTripMs => _rtt;       // command out to the state that acknowledges it: includes the bridge's 8 ms send period
    public float StatesPerSecond => _rate;

    public HapticLinkClient(string host, int port)
    {
        _host = host;
        _port = port;
        _reader = new Thread(Read) { IsBackground = true, Name = "HapticLink read" };
        _writer = new Thread(Write) { IsBackground = true, Name = "HapticLink write" };
        _reader.Start();
        _writer.Start();
    }

    /// <summary>Copies the latest state into "into"; false if none has arrived yet. ageMs = how old it is.</summary>
    public bool Latest(HapticProtocol.StateFrame into, out float ageMs)
    {
        lock (_lock)
        {
            ageMs = _lastRx == 0 ? float.MaxValue : (Stopwatch.GetTimestamp() - _lastRx) * 1000f / Stopwatch.Frequency;
            if (_lastRx == 0) return false;
            into.CopyFrom(_state);
            return true;
        }
    }

    /// <summary>The force to put on the stylus, N in the device's frame. It is re-sent every SendIntervalMs; the bridge zeroes it if it stops coming.</summary>
    public void SetForce(float fx, float fy, float fz, bool enable)
    {
        lock (_lock) { _fx = fx; _fy = fy; _fz = fz; _enable = enable; }
    }

    /// <summary>
    /// A finished run as CSV text, for the bridge to save on the PC. It goes out when the link is up and again after a drop (up to MaxRunTries
    /// times in all) until the bridge answers (RunSaved). One run waits at a time: a new one replaces it. A big one holds the commands back while it goes (the bridge zeroes
    /// the force if they stop for 100 ms), which is why it is only sent when a run is over.
    /// </summary>
    public void SendRun(long id, string csv)
    {
        lock (_lock) { _runId = id; _runCsv = csv; _runSent = false; _runTries = 0; }
    }

    /// <summary>True once the PC has saved the run `id`; `name` is its file there.</summary>
    public bool RunSaved(long id, out string name)
    {
        lock (_lock)
        {
            name = _savedId == id ? _savedName : null;
            return name != null;
        }
    }

    void Read()
    {
        while (!_stop)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                client.Connect(_host, _port);
                lock (_lock) { _client = client; _stream = client.GetStream(); }
                Connected = true;
                var r = new BinaryReader(_stream);
                var frame = new HapticProtocol.StateFrame();
                var saved = new HapticProtocol.SavedFrame();
                while (!_stop)
                {
                    byte type = r.ReadByte();
                    if (type == HapticProtocol.SavedType)
                    {
                        saved.Read(r);
                        lock (_lock)
                            if (saved.id == _runId) { _savedId = saved.id; _savedName = saved.name; _runCsv = null; }
                        continue;
                    }
                    if (type != HapticProtocol.StateType) break;
                    frame.Read(r);
                    long now = Stopwatch.GetTimestamp();
                    lock (_lock)
                    {
                        _state.CopyFrom(frame);
                        _lastRx = now;
                        long sent = _sent[frame.ack % 256];
                        if (sent != 0) _rtt = (now - sent) * 1000f / Stopwatch.Frequency;
                    }
                    _rxCount++;
                    if (now - _countFrom >= Stopwatch.Frequency)
                    {
                        _rate = _rxCount * (float)Stopwatch.Frequency / (now - _countFrom);
                        _rxCount = 0;
                        _countFrom = now;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
            Drop();
            for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100); // retry in a second
        }
    }

    void Write()
    {
        var ms = new MemoryStream(HapticProtocol.CommandSize);
        var w = new BinaryWriter(ms);
        var cmd = new HapticProtocol.CommandFrame();
        while (!_stop)
        {
            Thread.Sleep(SendIntervalMs);
            NetworkStream stream;
            string csv = null;
            long runId = 0;
            lock (_lock)
            {
                stream = _stream;
                if (stream == null) continue;
                cmd.seq = ++_seq;
                cmd.flags = _enable ? HapticProtocol.Enable : 0;
                cmd.fx = _fx; cmd.fy = _fy; cmd.fz = _fz;
                _sent[cmd.seq % 256] = Stopwatch.GetTimestamp();
                if (_runCsv != null && !_runSent && _runTries < MaxRunTries) { csv = _runCsv; runId = _runId; }
            }
            try
            {
                ms.SetLength(0);
                cmd.Write(w);
                stream.Write(ms.GetBuffer(), 0, (int)ms.Length);
                if (csv != null)
                {
                    var run = new MemoryStream();
                    new HapticProtocol.RunFrame { id = runId, csv = csv }.Write(new BinaryWriter(run));
                    stream.Write(run.GetBuffer(), 0, (int)run.Length);
                    lock (_lock) if (_runId == runId) { _runSent = true; _runTries++; }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { Drop(); }
        }
    }

    // The link is gone: close the socket (which also ends a blocked read) and forget the state, so a stale pose is never shown as live.
    void Drop()
    {
        TcpClient client;
        lock (_lock)
        {
            client = _client;
            _client = null; _stream = null; _lastRx = 0;
            _runSent = false; // a run not yet answered goes again on the next connection
            Array.Clear(_sent, 0, _sent.Length);
        }
        Connected = false;
        try { client?.Close(); } catch (SocketException) { }
    }

    public void Dispose()
    {
        _stop = true;
        SetForce(0, 0, 0, false);
        Drop();
    }
}
