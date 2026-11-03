using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

// All of the PC side of the haptic link is in this file; the window is HapticBridge.pyw (Python), which runs this program with --ui.
//
//   HapticBridge.exe [options]       runs until it is killed, logging to the console (scripts, the Unity self-checks)
//   HapticBridge.exe --ui [options]  the same, but talks to the Python window: one JSON line per message on stdout
//                                    ({"log":..} / {"status":..} every 100 ms / {"error":..} if it could not start),
//                                    and takes "limit N", "force on|off" and "quit" on stdin (quit or the end of stdin = stop and exit)
//   options: --sim (simulated stylus, a circle; no hardware)  --port N  --max-force N (newtons, default 1)  --tau MS (smoothing of the force, default 5)
//            --no-force  --no-adb  --adb PATH  --runs-dir PATH (where the force graph's runs are saved as CSV; default Documents\AR Quest\Force runs)
//            --perf-dir PATH (where the performance log goes; default Documents\AR Quest\Performance)
//            --sensors-file PATH (reads the headset's sensors from this file, SensorsCommand's output, instead of asking adb: for the checks)

/// <summary>
/// The bridge itself, with no user interface: reads the Touch 3D Stylus with the OpenHaptics HD API (hd.dll, installed with the Touch
/// drivers), serves its pose and buttons to the headset over TCP, and applies the force the headset asks for.
/// Start opens the device and the port, Stop gives the force back to zero and closes both; they can be repeated.
/// The force is only applied while the headset keeps asking for it: if its commands stop for 100 ms (or the link drops) it goes to zero.
/// While no headset is connected it runs "adb reverse tcp:PORT tcp:PORT" every 5 s, so a headset plugged in by USB reaches this PC at 127.0.0.1.
/// The headset also sends each finished run of its force graph (CSV text); the bridge saves it in the runs folder and answers with the file's name.
/// Once a second the bridge reads the headset's temperatures, clocks and battery through adb (an app cannot: the OS closes them to it) and logs
/// them, next to the app's own fps and frame times that the headset sends, in a ';' separated CSV (one file per start) that the window also shows.
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
        public HapticProtocol.PerfFrame Perf;     // how the app ran in its last second; null if it has not said for 2.5 s
        public Sensors Sensors;                   // the headset's last reading through adb; null if there is none lately
        public string PerfFile;                   // the performance log being written (null until the first row) and its rows so far
        public int PerfRows;
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
    string _perfDir, _sensorsFile, _perfFile, _sensorsState;
    int _perfRows;
    bool _perfFailed;
    long _perfStart;
    readonly object _perfLock = new();
    readonly HapticProtocol.PerfFrame _perf = new(); // the app's last PerfFrame and when it came, under _perfLock
    long _perfTicks, _sensorsTicks;
    Sensors _sensors;                                // the last reading, under _perfLock
    Thread _sense;
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
        _perfDir = o.PerfDir; _sensorsFile = o.SensorsFile;
        _perfFile = _sensorsState = null; _perfRows = 0; _perfFailed = false;
        lock (_perfLock) { _perfTicks = _sensorsTicks = 0; _sensors = null; }

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
        Say($"The performance log (temperatures, fps) goes to {_perfDir}.");
        _running = true;
        _sense = new Thread(SenseLoop) { IsBackground = true, Name = "bridge sensors" };
        _sense.Start();
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
        _sense?.Join(4000); // it may be waiting for adb, which is given 3 s
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
        lock (_perfLock)
        {
            if (_perfTicks != 0 && AgeMs(_perfTicks) < PerfFreshMs) { s.Perf = new HapticProtocol.PerfFrame(); s.Perf.CopyFrom(_perf); }
            if (_sensors != null && AgeMs(_sensorsTicks) < SensorsFreshMs) s.Sensors = _sensors; // never changed after it was made
        }
        s.PerfFile = _perfFile; s.PerfRows = _perfRows;
        return s;
    }

    static float AgeMs(long ticks) => (Stopwatch.GetTimestamp() - ticks) * 1000f / Stopwatch.Frequency;

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

    // ---- the headset's sensors and the performance log ----

    const int SensePeriodMs = 1000, AdbTimeoutMs = 3000, PerfFreshMs = 2500, SensorsFreshMs = 5000;

    // What an app cannot read on the Quest (sysfs is closed to it, even to adb's shell): the OS's own temperatures from the thermal HAL
    // (the "Cached" ones in dumpsys are stale, the "Current ... from HAL" are live), the battery, the clocks and the GPU load.
    const string SensorsCommand = "dumpsys thermalservice; echo @@battery; dumpsys battery; echo @@freq; " +
        "cat /sys/devices/system/cpu/cpu*/cpufreq/scaling_cur_freq 2>/dev/null; echo @@gpu; " +
        "cat /sys/class/kgsl/kgsl-3d0/gpuclk /sys/class/kgsl/kgsl-3d0/gpu_busy_percentage 2>/dev/null";

    public const string PerfHeader = "time;elapsed_s;app;fps;frame_ms;frame_max_ms;late_frames;refresh_hz;" +
        "cpu_max_c;cpu_avg_c;gpu_max_c;gpu_avg_c;soc_c;pcb_max_c;surface_c;display_c;battery_c;batt_virt_c;" +
        "thermal_status;fan;cpu_mhz;gpu_mhz;gpu_busy_pct;battery_pct;charging";

    // One row per second while there is something to log: the sensors (adb) or the app's numbers (the headset). app = 1 when the app's
    // numbers are in the row. Decimal point, ';' between columns, an empty cell where a number is not available.
    public static string PerfRow(DateTime now, double elapsed, HapticProtocol.PerfFrame app, Sensors s)
    {
        string N(float? v, string f) => v.HasValue ? v.Value.ToString(f, CultureInfo.InvariantCulture) : "";
        string A(float v, string f) => app == null ? "" : v.ToString(f, CultureInfo.InvariantCulture);
        return string.Join(";",
            now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), elapsed.ToString("F0", CultureInfo.InvariantCulture), app == null ? "0" : "1",
            A(app?.fps ?? 0, "F1"), A(app?.frameMs ?? 0, "F2"), A(app?.maxMs ?? 0, "F2"), A(app?.late ?? 0, "F0"), A(app?.refreshHz ?? 0, "F0"),
            N(s?.CpuMax, "F1"), N(s?.CpuAvg, "F1"), N(s?.GpuMax, "F1"), N(s?.GpuAvg, "F1"), N(s?.Soc, "F1"), N(s?.Pcb, "F1"),
            N(s?.Surface, "F1"), N(s?.Display, "F1"), N(s?.Battery, "F1"), N(s?.BattVirt, "F1"),
            N(s?.Status, "F0"), N(s?.Fan, "F0"), N(s?.CpuMHz, "F0"), N(s?.GpuMHz, "F0"), N(s?.GpuBusy, "F0"), N(s?.BatteryPct, "F0"),
            s?.Charging == null ? "" : s.Charging.Value ? "1" : "0");
    }

    void SenseLoop()
    {
        while (_running)
        {
            long next = Environment.TickCount64 + SensePeriodMs;
            var sensors = Sensors.Parse(ReadSensors());
            HapticProtocol.PerfFrame app = null;
            lock (_perfLock)
            {
                if (sensors != null) { _sensors = sensors; _sensorsTicks = Stopwatch.GetTimestamp(); }
                if (_perfTicks != 0 && AgeMs(_perfTicks) < PerfFreshMs) { app = new HapticProtocol.PerfFrame(); app.CopyFrom(_perf); }
            }
            if (sensors != null || app != null) AppendPerfRow(DateTime.Now, app, sensors);
            while (_running && Environment.TickCount64 < next) Thread.Sleep(50);
        }
    }

    // The log is perf-DATE-TIME.csv in the performance folder, one per start; each row is appended and closed at once so the file can be opened while it grows.
    void AppendPerfRow(DateTime now, HapticProtocol.PerfFrame app, Sensors sensors)
    {
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false);
            if (_perfFile == null)
            {
                Directory.CreateDirectory(_perfDir);
                string stamp = now.ToString("yyyyMMdd-HHmmss"), name = $"perf-{stamp}.csv";
                for (int n = 2; File.Exists(Path.Combine(_perfDir, name)); n++) name = $"perf-{stamp}-{n}.csv";
                File.WriteAllText(Path.Combine(_perfDir, name), PerfHeader + "\r\n", utf8);
                _perfStart = Stopwatch.GetTimestamp();
                _perfFile = Path.Combine(_perfDir, name);
                Say($"Performance log: {_perfFile}");
            }
            double elapsed = (Stopwatch.GetTimestamp() - _perfStart) / (double)Stopwatch.Frequency;
            File.AppendAllText(_perfFile, PerfRow(now, elapsed, app, sensors) + "\r\n", utf8);
            _perfRows++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_perfFailed) Say($"Could not write the performance log in {_perfDir}: {e.Message}");
            _perfFailed = true;
        }
    }

    // SensorsCommand's output from adb (or from the --sensors-file the checks use); null when there is none. Said once each time the outcome changes.
    string ReadSensors()
    {
        string text = null, state;
        if (_sensorsFile != null)
        {
            text = File.Exists(_sensorsFile) ? File.ReadAllText(_sensorsFile) : null;
            state = text == null ? $"sensors file not found: {_sensorsFile}" : $"reading the sensors from {_sensorsFile}";
        }
        else if (_adb == null) state = "no adb, so no temperatures (the app's own fps still arrive)";
        else
        {
            try
            {
                var psi = new ProcessStartInfo(_adb) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("shell");
                psi.ArgumentList.Add(SensorsCommand);
                using var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEndAsync();
                var errors = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(AdbTimeoutMs)) { p.Kill(); state = "adb did not answer"; }
                else
                {
                    text = output.Result; // the exit status is the last cat's, which a headset without that GPU file fails: judge by the text
                    state = text.Contains("Thermal Status") ? "reading the headset's sensors through adb" : "adb: " + (errors.Result + text).Trim().Split('\n')[0].Trim();
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { state = $"adb could not run: {e.Message}"; }
        }
        if (state != _sensorsState) { _sensorsState = state; Say("Sensors: " + state); }
        return text;
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
                var perf = new HapticProtocol.PerfFrame();
                while (true)
                {
                    byte type = r.ReadByte();
                    if (type == HapticProtocol.CommandType)
                    {
                        c.Read(r);
                        lock (_gate) { _cx = c.fx; _cy = c.fy; _cz = c.fz; _want = (c.flags & HapticProtocol.Enable) != 0; _cmdTicks = Stopwatch.GetTimestamp(); _ack = c.seq; }
                        _rx++;
                    }
                    else if (type == HapticProtocol.PerfType)
                    {
                        perf.Read(r);
                        lock (_perfLock) { _perf.CopyFrom(perf); _perfTicks = Stopwatch.GetTimestamp(); }
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
                frame.seq++; frame.ack = _ack; frame.buttons = _buttons; frame.flags = _flags | HapticProtocol.PerfOk;
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

/// <summary>
/// One reading of the headset's sensors as the OS reports them (null = not there), parsed from SensorsCommand's output. Temperatures in °C.
/// Written once and never changed, so the window's thread can read it while the next one is made.
/// </summary>
sealed class Sensors
{
    public float? CpuMax, CpuAvg, GpuMax, GpuAvg;     // the CPU cores and clusters, the GPU sensors
    public float? Soc, Pcb, Surface, Display;         // the SoC, the hottest board sensor, the OS's surface and display estimates
    public float? Battery, BattVirt, Fan;             // the cell, the OS's battery estimate (what it throttles by), the cooling device's level
    public float? CpuMHz, GpuMHz, GpuBusy;            // the fastest core's clock, the GPU's clock and load (%)
    public float? CpuLimit, GpuLimit, BattVirtLimit;  // where the OS starts to throttle (its LIGHT level)
    public int? Status, BatteryPct;                   // Android's thermal status (0 none, 1 light, 2 moderate, 3 severe ... 6 shutdown), the charge
    public bool? Charging;

    static readonly Regex Temp = new(@"Temperature\{mValue=(-?[\d.]+), mType=(\d+), mName=([^,]+), mStatus=\d+\}");
    static readonly Regex Limit = new(@"TemperatureThreshold\{mType=(\d+), mName=([^,]+), mHotThrottlingThresholds=\[([^\]]*)\]");
    static readonly Regex Cooling = new(@"CoolingDevice\{mValue=(\d+), mType=\d+, mName=([^}]*)\}");

    /// <summary>Null if the text is not a thermal service dump (adb's error, an empty answer).</summary>
    public static Sensors Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string Section(string name)
        {
            int i = text.IndexOf("@@" + name, StringComparison.Ordinal);
            if (i < 0 || (i = text.IndexOf('\n', i)) < 0) return "";
            int j = text.IndexOf("@@", i, StringComparison.Ordinal);
            return j < 0 ? text[i..] : text[i..j];
        }
        float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        // The live block ("Current temperatures from HAL") ends where the cooling devices start; the cached one (an older Android) where "HAL Ready" starts.
        string thermal = text.Contains("@@") ? text[..text.IndexOf("@@", StringComparison.Ordinal)] : text;
        string Block(string from, string until)
        {
            int i = thermal.IndexOf(from, StringComparison.Ordinal);
            if (i < 0) return null;
            int j = thermal.IndexOf(until, i, StringComparison.Ordinal);
            return thermal[i..(j < 0 ? thermal.Length : j)];
        }
        string block = Block("Current temperatures from HAL:", "Current cooling devices") ?? Block("Cached temperatures:", "HAL Ready");
        if (block == null) return null;
        var temps = Temp.Matches(block).Select(m => (v: F(m.Groups[1].Value), type: int.Parse(m.Groups[2].Value), name: m.Groups[3].Value)).ToList();
        if (temps.Count == 0) return null;

        var s = new Sensors();
        var cpu = temps.Where(t => t.type == 0).Select(t => t.v).ToList();
        var gpu = temps.Where(t => t.type == 1).Select(t => t.v).ToList();
        var pcb = temps.Where(t => t.name.StartsWith("pcb")).Select(t => t.v).ToList();
        float R(float v) => MathF.Round(v, 1); // the sensors give millidegrees; a tenth is plenty, and the window and the CSV then show the same digits
        float? Named(string name) { foreach (var t in temps) if (t.name == name) return R(t.v); return null; }
        s.CpuMax = cpu.Count > 0 ? R(cpu.Max()) : null; s.CpuAvg = cpu.Count > 0 ? R(cpu.Average()) : null;
        s.GpuMax = gpu.Count > 0 ? R(gpu.Max()) : null; s.GpuAvg = gpu.Count > 0 ? R(gpu.Average()) : null;
        s.Pcb = pcb.Count > 0 ? R(pcb.Max()) : null;
        s.Soc = Named("soc-usr"); s.Surface = Named("surf-virt-usr"); s.Display = Named("disp-virt");
        s.Battery = Named("battery"); s.BattVirt = Named("batt-virt-usr");

        var status = Regex.Match(thermal, @"Thermal Status: (\d+)");
        if (status.Success) s.Status = int.Parse(status.Groups[1].Value);
        foreach (Match m in Cooling.Matches(thermal))
            if (m.Groups[2].Value.Contains("fan")) { s.Fan = F(m.Groups[1].Value); break; }
        foreach (Match m in Limit.Matches(thermal)) // the lowest LIGHT threshold of each group
        {
            var parts = m.Groups[3].Value.Split(',');
            if (parts.Length < 2 || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var light) || !float.IsFinite(light)) continue;
            int type = int.Parse(m.Groups[1].Value);
            if (type == 0) s.CpuLimit = Math.Min(s.CpuLimit ?? light, light);
            else if (type == 1) s.GpuLimit = Math.Min(s.GpuLimit ?? light, light);
            else if (m.Groups[2].Value == "batt-virt-usr") s.BattVirtLimit = light;
        }

        string battery = Section("battery");
        var level = Regex.Match(battery, @"^\s*level: (\d+)", RegexOptions.Multiline);
        if (level.Success) s.BatteryPct = int.Parse(level.Groups[1].Value);
        var tenths = Regex.Match(battery, @"^\s*temperature: (-?\d+)", RegexOptions.Multiline);
        if (tenths.Success) s.Battery ??= int.Parse(tenths.Groups[1].Value) / 10f;
        if (battery.Contains("powered:")) s.Charging = Regex.IsMatch(battery, @"(AC|USB|Wireless|Dock) powered: true");

        var clocks = Section("freq").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => long.TryParse(l, out var khz) ? khz : -1).Where(khz => khz > 0).ToList();
        if (clocks.Count > 0) s.CpuMHz = clocks.Max() / 1000f;
        var gpuLines = Section("gpu").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (gpuLines.Length > 0 && long.TryParse(gpuLines[0], out var hz)) s.GpuMHz = hz / 1e6f;
        if (gpuLines.Length > 1 && Regex.Match(gpuLines[1], @"(\d+)\s*%") is { Success: true } busy) s.GpuBusy = int.Parse(busy.Groups[1].Value);
        return s;
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
    public string PerfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AR Quest", "Performance");
    public string SensorsFile;

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
        if (Value("--perf-dir") != null) o.PerfDir = Path.GetFullPath(Value("--perf-dir"));
        if (Value("--sensors-file") != null) o.SensorsFile = Path.GetFullPath(Value("--sensors-file"));
        return o;
    }
}
