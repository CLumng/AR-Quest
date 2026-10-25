using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Checks the haptic link without a headset or a stylus: the wire format, the device-to-Unity pose conversion, the ball's force, and the
/// whole chain (client, TCP, bridge) against the bridge running with --headless --sim. Needs the bridge built:
/// dotnet build HapticBridge -c Release
/// </summary>
public static class HapticLinkCheck
{
    const int Port = 5599; // not the default, so a bridge left running on the real device is not disturbed

    [MenuItem("Tools/Haptic Link/Self-check")]
    static void Menu() => Debug.Log(Run());

    public static string Run()
    {
        var report = new StringBuilder();
        int failed = 0;
        void Expect(string what, bool ok) { if (!ok) failed++; report.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        bool Is(float a, float b, float eps = 1e-4f) => Mathf.Abs(a - b) < eps;
        bool Near(Vector3 a, Vector3 b, float eps = 1e-4f) => (a - b).magnitude < eps;

        // the wire format
        var state = new HapticProtocol.StateFrame { seq = 7, ack = 3, buttons = 2, flags = 1, fx = 0.5f, fy = -1, fz = 2 };
        for (int i = 0; i < 16; i++) state.m[i] = i * 1.5f;
        var ms = new MemoryStream();
        state.Write(new BinaryWriter(ms));
        Expect("a state frame is StateSize bytes", ms.Length == HapticProtocol.StateSize);
        ms.Position = 0;
        var back = new HapticProtocol.StateFrame();
        var r = new BinaryReader(ms);
        Expect("it starts with its type", r.ReadByte() == HapticProtocol.StateType);
        back.Read(r);
        Expect("and comes back the same", back.seq == 7 && back.ack == 3 && back.buttons == 2 && back.flags == 1 && back.fx == 0.5f && back.fz == 2 && back.m[15] == 22.5f);
        ms = new MemoryStream();
        new HapticProtocol.CommandFrame { seq = 9, flags = 1, fx = 1, fy = 2, fz = 3 }.Write(new BinaryWriter(ms));
        Expect("a command frame is CommandSize bytes", ms.Length == HapticProtocol.CommandSize);
        ms = new MemoryStream();
        new HapticProtocol.RunFrame { id = 42, csv = "time_s,tissue\r\n0.02,Epidural, piel — ñ\r\n" }.Write(new BinaryWriter(ms));
        ms.Position = 0;
        r = new BinaryReader(ms);
        var run = new HapticProtocol.RunFrame();
        Expect("a run frame starts with its type", r.ReadByte() == HapticProtocol.RunType);
        run.Read(r);
        Expect("and comes back the same, accents included", run.id == 42 && run.csv == "time_s,tissue\r\n0.02,Epidural, piel — ñ\r\n" && ms.Position == ms.Length);
        ms = new MemoryStream();
        var lie = new BinaryWriter(ms);
        lie.Write(42L); lie.Write(int.MaxValue); // a length that is not a run
        ms.Position = 0;
        bool refused = false;
        try { new HapticProtocol.RunFrame().Read(new BinaryReader(ms)); } catch (InvalidDataException) { refused = true; }
        Expect("a run frame that claims gigabytes is refused, not allocated", refused);
        ms = new MemoryStream();
        new HapticProtocol.SavedFrame { id = 42, name = "run-20261021-123456.csv" }.Write(new BinaryWriter(ms));
        ms.Position = 0;
        r = new BinaryReader(ms);
        var savedFrame = new HapticProtocol.SavedFrame();
        Expect("a saved frame starts with its type and comes back the same", r.ReadByte() == HapticProtocol.SavedType && Read(savedFrame, r) && savedFrame.id == 42 && savedFrame.name == "run-20261021-123456.csv");

        // the device's frame to Unity's
        var host = new GameObject("HapticLinkCheck");
        host.SetActive(false); // no OnEnable: there is no scene to wire
        var test = host.AddComponent<HapticLinkTest>();
        var m = new float[16];
        m[0] = m[5] = m[10] = m[15] = 1;
        m[12] = 10; m[13] = 20; m[14] = 30;
        HapticStylus.ToUnity(m, Vector3.back, out var p, out var q);
        Expect("the tip: x and y as they are, z flipped (toward the user is -Z), mm to m", Near(p, new Vector3(0.01f, 0.02f, -0.03f)));
        Expect("at rest the pen points away from the user (+Z) with the top up", Near(q * Vector3.forward, Vector3.forward) && Near(q * Vector3.up, Vector3.up));
        // a right handed turn of 90 degrees about the device's Y: the pen (-Z) goes to -X, which is Unity's yaw -90
        System.Array.Clear(m, 0, 16);
        m[2] = -1; m[5] = 1; m[8] = 1; m[15] = 1; // columns: (0,0,-1) (0,1,0) (1,0,0)
        HapticStylus.ToUnity(m, Vector3.back, out _, out q);
        Expect("turned 90 deg about the device's Y, the pen points to -X", Near(q * Vector3.forward, Vector3.left) && Near(q * Vector3.up, Vector3.up));
        HapticStylus.ToUnity(m, Vector3.forward, out _, out q);
        Expect("with the axis flipped the pen is drawn the other way", Near(q * Vector3.forward, Vector3.right));

        // the ball
        test.ballRadius = 0.025f; test.stiffness = 100;
        Expect("no push outside the ball", test.BallForce(new Vector3(0, 0, 0.03f)) == Vector3.zero);
        Expect("1 cm in, 1.5 N straight out (k times the 1.5 cm of depth)", Near(test.BallForce(new Vector3(0, 0, 0.01f)), new Vector3(0, 0, 1.5f)));
        Expect("dead centre it pushes up, not NaN", Near(test.BallForce(Vector3.zero), Vector3.up * 2.5f));
        Expect("the push is limited", Is(HapticLinkTest.Clamp(new Vector3(3, 4, 0), 1).magnitude, 1) && Near(HapticLinkTest.Clamp(new Vector3(0.3f, 0.4f, 0), 1), new Vector3(0.3f, 0.4f, 0)));
        Object.DestroyImmediate(host);

        // a bridge that does not know runs (an old build) drops the link at each one: the headset gives up after MaxRunTries
        var stub = new TcpListener(IPAddress.Loopback, Port + 1);
        stub.Start();
        int runFrames = 0;
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    using var c = stub.AcceptTcpClient();
                    var rd = new BinaryReader(c.GetStream());
                    for (byte t; (t = rd.ReadByte()) != 0;)
                    {
                        if (t == HapticProtocol.CommandType) { rd.ReadBytes(HapticProtocol.CommandSize - 1); continue; }
                        if (t == HapticProtocol.RunType) runFrames++;
                        break; // anything but a command: the old bridge closes the link
                    }
                }
            }
            catch (System.Exception) { } // the listener was stopped
        }) { IsBackground = true }.Start();
        var oldBridge = new HapticLinkClient("127.0.0.1", Port + 1);
        try
        {
            oldBridge.SendRun(1, "time_s\r\n0.02\r\n");
            Thread.Sleep(5500); // each retry waits the client's second to reconnect
            Expect($"a bridge that drops the link at a run gets it {runFrames} times, and the headset gives up", runFrames == HapticLinkClient.MaxRunTries && !oldBridge.RunSaved(1, out _));
        }
        finally
        {
            oldBridge.Dispose();
            stub.Stop();
        }

        // the whole chain, against the simulated bridge
        string exe =System.Environment.GetEnvironmentVariable("HAPTIC_BRIDGE_EXE") // a build somewhere else, when the release one is running
            ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HapticBridge", "bin", "Release", "net10.0-windows", "HapticBridge.exe"));
        if (!File.Exists(exe))
        {
            report.AppendLine($"FAIL the bridge is not built ({exe}): dotnet build HapticBridge -c Release");
            return report.Append("1 failed").ToString();
        }
        string runs = Path.Combine(Path.GetTempPath(), "HapticLinkCheck-" + System.Guid.NewGuid().ToString("N")); // where the bridge saves the runs of this check
        var bridge = Process.Start(new ProcessStartInfo(exe, $"--headless --sim --no-adb --port {Port} --max-force 1 --tau 5 --runs-dir \"{runs}\"") { UseShellExecute = false, CreateNoWindow = true });
        HapticLinkClient link = null;
        try
        {
            link = new HapticLinkClient("127.0.0.1", Port);
            var s = new HapticProtocol.StateFrame();
            float age = 0;
            bool got = Wait(() => link.Latest(s, out age), 4000);
            Expect("the client connects to the bridge and a state arrives", got && link.Connected);
            Thread.Sleep(1300);
            Expect($"states come at about 125 Hz (got {link.StatesPerSecond:F0})", link.StatesPerSecond > 80 && link.StatesPerSecond < 160);
            link.Latest(s, out age);
            Expect($"and are fresh ({age:F0} ms old)", age < 100);
            Expect("the simulated tip is on its circle (|x| <= 40 mm, |y| <= 30 mm)", Mathf.Abs(s.m[12]) <= 40.5f && Mathf.Abs(s.m[13]) <= 30.5f && s.m[15] == 1);
            var at0 = new Vector2(s.m[12], s.m[13]); // on an ellipse x alone can pass the same value twice: compare the point
            Thread.Sleep(500);
            link.Latest(s, out _);
            Expect("and it moves", Vector2.Distance(at0, new Vector2(s.m[12], s.m[13])) > 2);

            Expect("no force is applied before one is asked", Mathf.Abs(s.fx) + Mathf.Abs(s.fy) + Mathf.Abs(s.fz) < 1e-4f && (s.flags & HapticProtocol.ForceOn) == 0);
            link.SetForce(5, 0, 0, true);
            Wait(() => link.Latest(s, out _) && s.fx > 0.5f, 1500);
            Thread.Sleep(300);
            link.Latest(s, out _);
            Expect($"5 N asked is limited to the bridge's 1 N (applied {s.fx:F2})", Is(s.fx, 1f, 0.02f) && (s.flags & HapticProtocol.ForceOn) != 0);
            Expect($"the round trip is measured ({link.RoundTripMs:F0} ms)", link.RoundTripMs > 0 && link.RoundTripMs < 100);
            link.SetForce(float.NaN, 0, 0, true);
            Thread.Sleep(300);
            link.Latest(s, out _);
            Expect("a NaN force is dropped to zero, not kept", float.IsFinite(s.fx) && Mathf.Abs(s.fx) < 0.01f);

            // a finished run goes to the PC, which saves it and says as which file
            link.SetForce(0, 0, 0, false);
            string csv = "time_s,depth_mm,tissue\r\n0.02,10,Skin\r\n0.04,11,Subcutaneous fat\r\n";
            link.SendRun(42, csv);
            bool saved = Wait(() => link.RunSaved(42, out _), 3000);
            link.RunSaved(42, out var name);
            string file = name == null ? null : Path.Combine(runs, name);
            Expect($"a run sent to the bridge is saved on the PC as run-DATE-TIME.csv, with the text as it was ({name})",
                saved && name.StartsWith("run-") && name.EndsWith(".csv") && File.Exists(file) && File.ReadAllText(file) == csv);
            Expect("and no answer for a run that was not sent", !link.RunSaved(43, out _));

            link.SetForce(0, 1, 0, true);
            Thread.Sleep(250);
            link.Dispose(); // the headset vanishes while a force is on
            link = new HapticLinkClient("127.0.0.1", Port);
            got = Wait(() => link.Latest(s, out _) && Mathf.Abs(s.fy) < 0.02f, 2500);
            Expect($"when the link drops the force goes to zero (now {s.fy:F2})", got);

            // the same run sent again by a headset that did not get the answer: the same file, saved once; sent before the link was up, it waits for it
            link.SendRun(42, csv);
            Expect("the same run sent again is answered with the same file", Wait(() => link.RunSaved(42, out var again) && again == name, 3000));
            Expect("and is not saved twice", Directory.GetFiles(runs).Length == 1);
            link.SendRun(44, csv + "0.06,12,Epidural space\r\n");
            Expect("another run is another file", Wait(() => link.RunSaved(44, out var other) && other != name, 3000) && Directory.GetFiles(runs).Length == 2);
        }
        finally
        {
            link?.Dispose();
            if (!bridge.HasExited) bridge.Kill();
            bridge.Dispose();
            if (Directory.Exists(runs)) Directory.Delete(runs, true);
        }
        report.Append(failed == 0 ? "all passed" : $"{failed} failed");
        return report.ToString();
    }

    static bool Read(HapticProtocol.SavedFrame frame, BinaryReader r) { frame.Read(r); return true; }

    static bool Wait(System.Func<bool> done, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (done()) return true;
            Thread.Sleep(10);
        }
        return done();
    }
}
