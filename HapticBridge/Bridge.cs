using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

// All of the PC side of the haptic link is in this file; the window is HapticBridge.pyw (Python), which runs this program with --ui.
//
//   HapticBridge.exe [options]       runs until it is killed, logging to the console (scripts, the Unity self-checks)
//   HapticBridge.exe --ui [options]  the same, but talks to the Python window: one JSON line per message on stdout
//                                    ({"log":..} / {"status":..} every 100 ms / {"error":..} if it could not start),
//                                    and takes "limit N", "force on|off" and "quit" on stdin (quit or the end of stdin = stop and exit)
//   options: --sim (simulated stylus, a circle; no hardware)  --port N  --max-force N (newtons, default 1)  --tau MS (smoothing of the force, default 5)
//            --no-force  --no-adb  --adb PATH  --runs-dir PATH (where the force graph's runs are saved as CSV; default Documents\AR Quest\Force runs)

/// <summary>
/// The bridge itself, with no user interface: reads the Touch 3D Stylus with the OpenHaptics HD API (hd.dll, installed with the Touch
/// drivers), serves its pose and buttons to the headset over TCP, and applies the force the headset asks for.
/// Start opens the device and the port, Stop gives the force back to zero and closes both; they can be repeated.
/// The force is only applied while the headset keeps asking for it: if its commands stop for 100 ms (or the link drops) it goes to zero.
/// While no headset is connected it runs "adb reverse tcp:PORT tcp:PORT" every 5 s, so a headset plugged in by USB reaches this PC at 127.0.0.1.
/// The headset also sends each finished run of its force graph (CSV text); the bridge saves it in the runs folder and answers with the file's name.
/// </summary>
sealed class Bridge
{
    // OpenHaptics hdDefines.h
    const uint HD_CURRENT_BUTTONS = 0x2000, HD_CURRENT_INKWELL_SWITCH = 0x2002, HD_CURRENT_TRANSFORM = 0x2052;
    const uint HD_CURRENT_FORCE = 0x2700, HD_FORCE_OUTPUT = 0x4000;
    const uint HD_INVALID_HANDLE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)] struct HdError { public uint code; public int internalCode; public uint device; }
    delegate int Servo(IntPtr data);

    [DllImport("hd")] static extern uint hdInitDevice(IntPtr name);
    [DllImport("hd")] static extern void hdDisableDevice(uint hHD);
    [DllImport("hd")] static extern void hdEnable(uint cap);
    [DllImport("hd")] static extern void hdStartScheduler();
    [DllImport("hd")] static extern void hdStopScheduler();
    [DllImport("hd")] static extern uint hdScheduleAsynchronous(Servo callback, IntPtr data, ushort priority);
    [DllImport("hd")] static extern void hdUnschedule(uint handle);
    [DllImport("hd")] static extern void hdBeginFrame(uint hHD);
    [DllImport("hd")] static extern void hdEndFrame(uint hHD);
    [DllImport("hd")] static extern void hdGetDoublev(uint pname, double[] v);
    [DllImport("hd")] static extern void hdGetIntegerv(uint pname, int[] v);
    [DllImport("hd")] static extern void hdGetBooleanv(uint pname, byte[] v);
    [DllImport("hd")] static extern void hdSetDoublev(uint pname, double[] v);
    [DllImport("hd")] static extern HdError hdGetError();
    [DllImport("hd")] static extern IntPtr hdGetErrorString(uint code);
    [DllImport("hd")] static extern uint hdCheckCalibration();

    /// <summary>What the bridge is doing right now, for a screen. Read from any thread.</summary>
    public sealed class Snapshot
    {
        public bool Running, Simulated, Connected;
        public string Remote, AdbState, LastError;
        public readonly float[] M = new float[16]; // the device's transform: column major, the tip (mm) in 12..14
        public int Buttons;
        public bool Inkwell, Asked;               // Asked: the headset wants force and its commands are fresh
        public float Fx, Fy, Fz;                  // the force being applied, N
        public float CommandAgeMs = -1;           // how old the headset's last command is, -1 = none
        public float OutPerSecond, InPerSecond;   // frames sent to the headset, commands received
        public long HdErrors;
        public float MaxForce;                    // the limit and the switch as they are now (N)
        public bool ForceOutput;
        public float Force => MathF.Sqrt(Fx * Fx + Fy * Fy + Fz * Fz);
    }

    public event Action<string> Log;
    public bool Running => _running;
    public string LastError { get; private set; }
    public float MaxForce { get => _maxForce; set => _maxForce = Math.Max(0, value); } // N, takes effect at once
    public bool ForceOutput { get => !_noForce; set => _noForce = !value; }           // false: never send force, whatever the headset asks

    static readonly long Fresh = Stopwatch.Frequency / 10; // a command older than 100 ms is not obeyed
    static bool _resolverSet;

    readonly object _gate = new();
    readonly float[] _m = new float[16];       // latest reading, under _gate
    int _buttons, _flags;
    float _cx, _cy, _cz;                       // latest command from the headset, under _gate
    bool _want;
    long _cmdTicks;
    uint _ack;
    float _sx, _sy, _sz;                       // the force being applied: owned by the loop that calls Output
    float _alpha = 1 - MathF.Exp(-1f / 5f);
    volatile float _maxForce = 1f;
    volatile bool _noForce, _running, _dead, _connected;
    string _remote, _adbState, _adb;
    string _runsDir;
    readonly Dictionary<long, string> _saved = new(); // the runs saved since the bridge started, by id: one sent again is not saved twice
    uint _hHD, _handle, _lastHdError;
    long _hdErrors, _tx, _rx;
    bool _sim;
    TcpListener _listener;
    Thread _accept, _simulate;
    bool _hdErrorSaid;
    long _rateAt, _rateTx, _rateRx;
    float _outRate, _inRate;
    readonly Servo _servo;                     // kept in a field: the native scheduler calls it, the GC must not move it

    public Bridge() { _servo = Tick; }

    public bool Start(Options o)
    {
        if (_running) return true;
        LastError = null;
        MaxForce = o.MaxForce;
        ForceOutput = !o.NoForce;
        _alpha = 1 - MathF.Exp(-1f / Math.Max(o.TauMs, 0.1f)); // the loop runs at 1 kHz: 1 ms per step
        lock (_gate) { _want = false; _cmdTicks = 0; _ack = 0; _buttons = _flags = 0; Array.Clear(_m); }
        _sx = _sy = _sz = 0;
        _tx = _rx = _hdErrors = 0;
        _hdErrorSaid = false;
        _remote = _adbState = null;
        _rateAt = 0; _outRate = _inRate = 0;
        _sim = o.Sim;
        _runsDir = o.RunsDir;

        if (_sim) Say("SIMULATED stylus: a circle of 40 mm, buttons toggle every 3 s. Forces are only measured, not applied.");
        else if (!StartDevice()) return false;

        try
        {
            _listener = new TcpListener(IPAddress.Any, o.Port);
            _listener.Start();
        }
        catch (SocketException e)
        {
            Fail($"Port {o.Port} is not available ({e.Message}). Is another bridge running?");
            if (!_sim) StopDevice();
            return false;
        }
        _adb = o.NoAdb ? null : FindAdb(o.AdbPath);
        Say($"Waiting for the headset on port {o.Port}" + (_adb == null ? " (over Wi-Fi: the PC's address; for USB see adb reverse)." : " (USB, through adb reverse)."));
        Say($"Force graph runs are saved in {_runsDir}.");
        _running = true;
        if (_sim) { _simulate = new Thread(Simulate) { IsBackground = true, Name = "bridge sim" }; _simulate.Start(); }
        _accept = new Thread(() => AcceptLoop(o.Port)) { IsBackground = true, Name = "bridge accept" };
        _accept.Start();
        return true;
    }

    /// <summary>Force to zero first, then the headset's link, the port and the device are closed.</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        lock (_gate) _want = false; // the loop that owns the force brings it to zero
        _accept.Join(2000);
        _simulate?.Join(1000);
        _listener.Stop();
        _connected = false;
        if (!_sim)
        {
            Thread.Sleep(60); // let the servo loop write the zero force before the scheduler stops
            StopDevice();
        }
        _sx = _sy = _sz = 0;
        Say("Stopped.");
    }

    public Snapshot Read()
    {
        if (_hdErrors > 0 && !_hdErrorSaid) { _hdErrorSaid = true; Say($"HD API error: {Describe(_lastHdError)}"); } // said once, from here: the servo loop must not
        var s =new Snapshot { Running = _running, Simulated = _sim, Connected = _connected, Remote = _remote, AdbState = _adbState, LastError = LastError, HdErrors = _hdErrors, MaxForce = _maxForce, ForceOutput = !_noForce };
        lock (_gate)
        {
            Array.Copy(_m, s.M, 16);
            s.Buttons = _buttons;
            s.Inkwell = (_flags & HapticProtocol.Inkwell) != 0;
            s.Asked = _want && _cmdTicks != 0 && Stopwatch.GetTimestamp() - _cmdTicks < Fresh;
            if (_cmdTicks != 0) s.CommandAgeMs = (Stopwatch.GetTimestamp() - _cmdTicks) * 1000f / Stopwatch.Frequency;
        }
        s.Fx = _sx; s.Fy = _sy; s.Fz = _sz;
        long now = Stopwatch.GetTimestamp();
        if (_rateAt == 0) { _rateAt = now; _rateTx = _tx; _rateRx = _rx; }
        else if (now - _rateAt >= Stopwatch.Frequency)
        {
            float dt = (now - _rateAt) / (float)Stopwatch.Frequency;
            _outRate = (_tx - _rateTx) / dt; _inRate = (_rx - _rateRx) / dt;
            _rateAt = now; _rateTx = _tx; _rateRx = _rx;
        }
        s.OutPerSecond = _outRate; s.InPerSecond = _inRate;
        return s;
    }

    void Say(string text) => Log?.Invoke(text);

    void Fail(string text)
    {
        LastError = text;
        Say(text);
    }

    // ---- USB: the headset reaches this PC's port through adb ----

    static string FindAdb(string path)
    {
        if (!string.IsNullOrEmpty(path)) return path;
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(d => Path.Combine(d.Trim(), "adb.exe")).FirstOrDefault(File.Exists);
        if (onPath != null) return onPath;
        string hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor");
        if (!Directory.Exists(hub)) return null;
        return Directory.GetDirectories(hub).OrderDescending() // Unity's own copy, newest editor first
            .Select(d => Path.Combine(d, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", "adb.exe")).FirstOrDefault(File.Exists);
    }

    // adb reverse is idempotent; say something only when its outcome changes.
    void Reverse(int port)
    {
        string state;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_adb, $"reverse tcp:{port} tcp:{port}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            string text = (p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd()).Trim();
            p.WaitForExit(5000);
            state = p.ExitCode == 0 ? "USB ready (adb reverse is set)" : $"adb: {text.Split('\n')[0].Trim()}";
        }
        catch (Exception e) { state = $"adb could not run: {e.Message}"; }
        if (state != _adbState) { _adbState = state; Say(state); }
    }

    // ---- the device ----

    readonly double[] _t = new double[16], _f = new double[3];
    readonly int[] _b = new int[1];
    readonly byte[] _ink = new byte[1];

    bool StartDevice()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "3D Systems", "Touch Device Drivers");
        if (!_resolverSet) // it may be set once per process
        {
            NativeLibrary.SetDllImportResolver(typeof(Bridge).Assembly, (name, _, _) =>
                name == "hd" && NativeLibrary.TryLoad(Path.Combine(dir, "hd.dll"), out var lib) ? lib : IntPtr.Zero);
            _resolverSet = true;
        }
        try { _hHD = hdInitDevice(IntPtr.Zero); }
        catch (DllNotFoundException) { Fail($"hd.dll not found in {dir}: install the Touch device drivers."); return false; }
        var err = hdGetError();
        if (_hHD == HD_INVALID_HANDLE || err.code != 0)
        {
            Fail($"The stylus did not start: {Describe(err.code)}. Is it plugged in and set up (Touch Setup)?");
            return false;
        }
        hdEnable(HD_FORCE_OUTPUT);
        Say($"Touch ready (calibration status 0x{hdCheckCalibration():X}).");
        hdStartScheduler();
        _handle = hdScheduleAsynchronous(_servo, IntPtr.Zero, ushort.MaxValue);
        return true;
    }

    void StopDevice()
    {
        hdStopScheduler();
        hdUnschedule(_handle);
        hdDisableDevice(_hHD);
    }

    // The device's 1 kHz loop, on the scheduler's thread: it must not wait or allocate.
    int Tick(IntPtr _)
    {
        hdBeginFrame(_hHD);
        hdGetDoublev(HD_CURRENT_TRANSFORM, _t);
        hdGetIntegerv(HD_CURRENT_BUTTONS, _b);
        hdGetBooleanv(HD_CURRENT_INKWELL_SWITCH, _ink);
        var (fx, fy, fz) = Output();
        _f[0] = fx; _f[1] = fy; _f[2] = fz; // always set: a force left from an earlier frame must not stay on
        hdSetDoublev(HD_CURRENT_FORCE, _f);
        hdEndFrame(_hHD);
        Publish(_t, _b[0], _ink[0] != 0);
        var e = hdGetError();
        if (e.code != 0) { _lastHdError = e.code; _hdErrors++; }
        return 1; // HD_CALLBACK_CONTINUE
    }

    void Publish(double[] t, int buttons, bool inkwell)
    {
        lock (_gate)
        {
            for (int i = 0; i < 16; i++) _m[i] = (float)t[i];
            _buttons = buttons;
            _flags = (inkwell ? HapticProtocol.Inkwell : 0) | (_want && !_noForce ? HapticProtocol.ForceOn : 0);
        }
    }

    // The force to apply this millisecond: what the headset asked for, if it asked lately, limited and smoothed.
    (float x, float y, float z) Output()
    {
        float x = 0, y = 0, z = 0;
        lock (_gate)
            if (_want && !_noForce && Stopwatch.GetTimestamp() - _cmdTicks < Fresh) { x = _cx; y = _cy; z = _cz; }
        float mag = MathF.Sqrt(x * x + y * y + z * z);
        if (!float.IsFinite(mag)) x = y = z = 0; // a NaN would stay in the smoothing for ever
        else if (mag > _maxForce) { float s = _maxForce / mag; x *= s; y *= s; z *= s; }
        _sx += (x - _sx) * _alpha; _sy += (y - _sy) * _alpha; _sz += (z - _sz) * _alpha;
        return (_sx, _sy, _sz);
    }

    string Describe(uint code) => $"0x{code:X} {Marshal.PtrToStringAnsi(hdGetErrorString(code))}";

    void Simulate()
    {
        var sw = Stopwatch.StartNew();
        var t = new double[16];
        while (_running)
        {
            double s = sw.Elapsed.TotalSeconds, a = s * 2 * Math.PI / 4;
            Array.Clear(t);
            t[0] = t[5] = t[10] = t[15] = 1;
            t[12] = 40 * Math.Cos(a); t[13] = 30 * Math.Sin(a); t[14] = 10 * Math.Sin(2 * a);
            Output();
            Publish(t, (int)(s / 3) % 3, false);
            Thread.Sleep(1);
        }
    }

    // ---- the headset ----

    void AcceptLoop(int port)
    {
        long nextAdb = 0;
        while (_running)
        {
            if (!_listener.Pending())
            {
                if (_adb != null && Environment.TickCount64 >= nextAdb) { Reverse(port); nextAdb = Environment.TickCount64 + 5000; } // a re-plugged headset loses it
                Thread.Sleep(50);
                continue;
            }
            using var client = _listener.AcceptTcpClient();
            client.NoDelay = true;
            client.SendTimeout = 1000; // a headset that stops reading must not hold the bridge
            _remote = client.Client.RemoteEndPoint?.ToString();
            _connected = true;
            Say($"Headset connected from {_remote}.");
            try { Serve(client); } catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
            _connected = false;
            lock (_gate) _want = false; // nobody asks for force any more
            if (_running) Say("Headset gone.");
        }
    }

    // The headset's finished run of the force graph: saved as run-DATE-TIME.csv in the runs folder (a second one in the same second gets -2, ...).
    // Returns the file's name, or null if it could not be written.
    string SaveRun(HapticProtocol.RunFrame run)
    {
        lock (_saved)
        {
            if (_saved.TryGetValue(run.id, out var known)) return known;
            try
            {
                Directory.CreateDirectory(_runsDir);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"), name = $"run-{stamp}.csv";
                for (int n = 2; File.Exists(Path.Combine(_runsDir, name)); n++) name = $"run-{stamp}-{n}.csv";
                File.WriteAllText(Path.Combine(_runsDir, name), run.csv, new System.Text.UTF8Encoding(false));
                _saved[run.id] = name;
                Say($"Run saved: {Path.Combine(_runsDir, name)} ({run.csv.Count(ch => ch == '\n') - 1} samples).");
                return name;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Say($"Could not save the run in {_runsDir}: {e.Message}");
                return null;
            }
        }
    }

    void Serve(TcpClient client)
    {
        var stream = client.GetStream();
        var writing = new object(); // the state frames and the answers to runs go out on the same stream: one at a time
        _dead = false;
        new Thread(() =>
        {
            try
            {
                var r = new BinaryReader(stream);
                var c = new HapticProtocol.CommandFrame();
                var run = new HapticProtocol.RunFrame();
                var saved = new HapticProtocol.SavedFrame();
                while (true)
                {
                    byte type = r.ReadByte();
                    if (type == HapticProtocol.CommandType)
                    {
                        c.Read(r);
                        lock (_gate) { _cx = c.fx; _cy = c.fy; _cz = c.fz; _want = (c.flags & HapticProtocol.Enable) != 0; _cmdTicks = Stopwatch.GetTimestamp(); _ack = c.seq; }
                        _rx++;
                    }
                    else if (type == HapticProtocol.RunType)
                    {
                        run.Read(r);
                        string name = SaveRun(run);
                        if (name == null) continue; // no answer: the headset sends it again after its next reconnection
                        saved.id = run.id;
                        saved.name = name;
                        var ms = new MemoryStream();
                        saved.Write(new BinaryWriter(ms));
                        lock (writing) stream.Write(ms.GetBuffer(), 0, (int)ms.Length);
                    }
                    else break;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException or InvalidDataException) { }
            _dead = true;
        }) { IsBackground = true, Name = "bridge read" }.Start();

        var frame = new HapticProtocol.StateFrame();
        var ms = new MemoryStream(HapticProtocol.StateSize);
        var w = new BinaryWriter(ms);
        while (!_dead && _running)
        {
            lock (_gate)
            {
                frame.seq++; frame.ack = _ack; frame.buttons = _buttons; frame.flags = _flags;
                Array.Copy(_m, frame.m, 16);
            }
            frame.fx = _sx; frame.fy = _sy; frame.fz = _sz;
            ms.SetLength(0);
            frame.Write(w);
            lock (writing) stream.Write(ms.GetBuffer(), 0, (int)ms.Length);
            _tx++;
            Thread.Sleep(8);
        }
    }
}

static class Program
{
    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static int Main(string[] args)
    {
        timeBeginPeriod(1); // Sleep(8) really is 8 ms and not 15
        var o = Options.Parse(args);
        bool ui = args.Contains("--ui");
        var bridge = new Bridge();
        bridge.Log += s => Write(ui ? JsonSerializer.Serialize(new { log = s }) : s);
        if (!bridge.Start(o))
        {
            if (ui) Write(JsonSerializer.Serialize(new { error = bridge.LastError }));
            return 1;
        }
        Console.CancelKeyPress += (_, _) => bridge.Stop(); // Ctrl+C gives the force back to zero before the process ends
        if (!ui) { Thread.Sleep(Timeout.Infinite); return 0; }

        new Thread(() => { while (true) { Write(JsonSerializer.Serialize(new { status = bridge.Read() }, Json)); Thread.Sleep(100); } }) { IsBackground = true, Name = "bridge status" }.Start();
        for (string line; (line = Console.ReadLine()) != null && line != "quit";) // the end of stdin (the window died) stops it too
        {
            var w = line.Split(' ');
            if (w.Length < 2) continue;
            if (w[0] == "limit" && float.TryParse(w[1], System.Globalization.CultureInfo.InvariantCulture, out var n)) bridge.MaxForce = n;
            else if (w[0] == "force") bridge.ForceOutput = w[1] == "on";
        }
        bridge.Stop();
        return 0;
    }

    static void Write(string line)
    {
        try { Console.WriteLine(line); } catch (IOException) { } // the reader is gone
    }
}

sealed class Options
{
    public bool Sim, NoForce, NoAdb;
    public float MaxForce = 1f, TauMs = 5f;
    public int Port = HapticProtocol.Port;
    public string AdbPath;
    public string RunsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AR Quest", "Force runs");

    public static Options Parse(string[] a)
    {
        string Value(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        float Number(string name, float fallback) => float.TryParse(Value(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var o = new Options
        {
            Sim = a.Contains("--sim"), NoForce = a.Contains("--no-force"), NoAdb = a.Contains("--no-adb"),
            MaxForce = Number("--max-force", 1f), TauMs = Number("--tau", 5f), Port = (int)Number("--port", HapticProtocol.Port), AdbPath = Value("--adb"),
        };
        if (Value("--runs-dir") != null) o.RunsDir = Path.GetFullPath(Value("--runs-dir"));
        return o;
    }
}
