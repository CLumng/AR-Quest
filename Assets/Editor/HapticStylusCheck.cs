using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Checks the stylus in the main scene without a headset or a device, with states injected into HapticStylus: that the docked tip is at
/// the home point and the stylus moves the needle's tip the way the device's axes were turned into the model, the buttons (1 = A, 2 arms and
/// disarms the forces; docking or losing the link disarms), the force's way into the device's frame and its limit, the tissue's force law
/// (side spring, bone wall, the table's force against the motion, the pop's kick), NeedleHaptics' numbers for it (spring, damper, friction),
/// and on a synthetic body (skin = the plane z = 0, flesh at z > 0) the needle in the stylus' hand: the force it feels, the side spring,
/// resting while docked, hidden after leaving the dock.
/// </summary>
public static class HapticStylusCheck
{
    [MenuItem("Tools/Haptic Link/Self-check Stylus")]
    static void Menu() => Debug.Log(Run());

    public static string Run()
    {
        var report = new StringBuilder();
        int failed = 0;
        void Expect(string what, bool ok) { if (!ok) failed++; report.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        bool Near(Vector3 a, Vector3 b, float eps = 1e-4f) => (a - b).magnitude < eps;
        bool Is(float a, float b, float eps = 1e-3f) => Mathf.Abs(a - b) < eps;

        var made = new List<GameObject>();
        GameObject Make(string name, bool active = true)
        {
            var go = new GameObject("check " + name);
            go.SetActive(active);
            made.Add(go);
            return go;
        }
        // A state with the tip here (m, in Unity's device frame: the device's z is toward the user) and the pen straight (the stylus' forward is +Z).
        // tilt: degrees the pen is pitched about the device's X (negative: the tip points down)
        HapticProtocol.StateFrame State(Vector3 tip, int buttons = 0, int flags = 0, float tilt = 0)
        {
            var s = new HapticProtocol.StateFrame { buttons = buttons, flags = flags };
            s.m[0] = s.m[15] = 1;
            float c = Mathf.Cos(tilt * Mathf.Deg2Rad), sn = Mathf.Sin(tilt * Mathf.Deg2Rad);
            s.m[5] = s.m[10] = c;
            s.m[6] = sn;
            s.m[9] = -sn;
            s.m[12] = tip.x * 1000; s.m[13] = tip.y * 1000; s.m[14] = -tip.z * 1000;
            return s;
        }

        // A simulated bridge (headless, no device, its own port) for the stylus' link, if it is built: the force graph's run goes to it for real.
        // The states are still injected, so the bridge only receives forces and runs.
        const int BridgePort = 5598;
        string bridgeExe = System.Environment.GetEnvironmentVariable("HAPTIC_BRIDGE_EXE")
            ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HapticBridge", "bin", "Release", "net10.0-windows", "HapticBridge.exe"));
        string pcDir = Path.Combine(Path.GetTempPath(), "HapticStylusCheckPC-" + System.Guid.NewGuid().ToString("N"));
        System.Diagnostics.Process bridge = null;
        HapticStylus linked = null;
        MethodInfo stylusOnDisable = typeof(HapticStylus).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance);

        try
        {
            if (File.Exists(bridgeExe))
                bridge = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(bridgeExe, $"--headless --sim --no-adb --port {BridgePort} --runs-dir \"{pcDir}\"") { UseShellExecute = false, CreateNoWindow = true });
            else report.AppendLine($"skip the force graph's run is not sent to a real bridge ({bridgeExe} is not built: dotnet build HapticBridge -c Release)");

            // ---- the mapping, with the home point turned like the builder does (180 degrees about Y) ----
            var stylus = Make("stylus", active: false).AddComponent<HapticStylus>(); // no OnEnable (no scene to wire): states are injected
            if (bridge != null)
            {
                stylus.port = BridgePort;
                typeof(HapticStylus).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(stylus, null); // opens the link
                linked = stylus;
            }
            var home = Make("home").transform;
            home.SetPositionAndRotation(new Vector3(1, 2, 3), Quaternion.Euler(0, 180, 0));
            stylus.home = home;
            stylus.dock = new Vector3(0, -0.1f, 0.09f);
            var calls = 0;
            stylus.onButton1 = () => calls++;

            stylus.Inject(State(stylus.dock, flags: HapticProtocol.Inkwell));
            var pose = stylus.NeedlePose(0.1f);
            Expect("docked, the needle's tip is at the home point", Near(pose.position + pose.rotation * Vector3.forward * 0.1f, home.position));
            Expect("the pen points into the model's back (-Z) with the top up", Near(pose.rotation * Vector3.forward, Vector3.back) && Near(pose.rotation * Vector3.up, Vector3.up));
            Expect("docked is read from the bridge's inkwell flag", stylus.Docked);
            stylus.Inject(State(stylus.dock + new Vector3(0, 0.02f, 0)));
            pose = stylus.NeedlePose(0);
            Expect("2 cm up on the device is 2 cm up on the model", Near(pose.position, home.position + Vector3.up * 0.02f));
            stylus.Inject(State(stylus.dock + new Vector3(0, 0, -0.03f)));
            pose = stylus.NeedlePose(0);
            Expect("3 cm toward the user is 3 cm out of the model's back (+Z)", Near(pose.position, home.position + Vector3.forward * 0.03f));
            stylus.Inject(State(stylus.dock + new Vector3(0.01f, 0, 0)));
            pose = stylus.NeedlePose(0);
            Expect("1 cm to the user's right is the model's -X (the model's back faces the user)", Near(pose.position, home.position + Vector3.left * 0.01f));

            // ---- the buttons ----
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            Expect("forces start disarmed", !stylus.ForcesOn);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button1));
            stylus.Tick(0.02f);
            stylus.Tick(0.02f);
            Expect("button 1 does A once while it is held", calls == 1);
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button1));
            stylus.Tick(0.02f);
            Expect("and again on the next press", calls == 2);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            Expect("button 2 arms the forces", stylus.ForcesOn);
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            Expect("and disarms them", !stylus.ForcesOn);
            stylus.Inject(State(Vector3.zero, flags: HapticProtocol.Inkwell, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            Expect("a press in the inkwell arms nothing", !stylus.ForcesOn);
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, flags: HapticProtocol.Inkwell));
            stylus.Tick(0.02f);
            Expect("docking disarms them", !stylus.ForcesOn);
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero), live: false);
            stylus.Tick(0.02f);
            Expect("so does losing the link", !stylus.ForcesOn);

            // ---- the force's way out ----
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            stylus.Push(new Vector3(0, 0, 0.5f));
            stylus.Flush();
            Expect("a push out of the back (+Z, toward the user) goes to the device as +Z (toward the user)", stylus.SentEnabled && Near(stylus.SentForce, new Vector3(0, 0, 0.5f)));
            stylus.Push(new Vector3(0.5f, 0, 0));
            stylus.Flush();
            Expect("a push along the model's +X is the device's -X (the home point is turned)", Near(stylus.SentForce, new Vector3(-0.5f, 0, 0)));
            stylus.Push(new Vector3(0, 5, 0));
            stylus.Flush();
            Expect("it is limited to maxForce", Is(stylus.SentForce.magnitude, stylus.maxForce));
            stylus.strength = 0.5f;
            stylus.Push(new Vector3(0, 5, 0));
            stylus.Flush();
            Expect("and strength scales it", Is(stylus.SentForce.magnitude, 0.5f * stylus.maxForce));
            stylus.strength = 1;
            stylus.Push(new Vector3(float.NaN, 0, 0));
            stylus.Flush();
            Expect("a NaN is sent as zero", stylus.SentForce == Vector3.zero);
            stylus.Flush();
            Expect("with nothing pushed this frame, it is zero", stylus.SentForce == Vector3.zero);
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            stylus.Push(new Vector3(0, 0, 0.5f));
            stylus.Flush();
            Expect("disarmed, nothing is sent", !stylus.SentEnabled && stylus.SentForce == Vector3.zero);

            // ---- the tissue's force law ----
            Vector3 F(Vector3 offset, bool blocked = false, float pushed = 0, float axial = 0, float pop = 0) =>
                NeedleAction.StylusForce(offset, Vector3.forward, blocked, pushed, axial, pop, 150, 300, 1);
            Expect("the hand 1 cm off the track is pulled back to it (1.5 N)", Near(F(new Vector3(0.01f, 0, 0)), new Vector3(-1.5f, 0, 0)));
            Expect("along the track alone there is no side force", Near(F(new Vector3(0, 0, 0.01f)), Vector3.zero));
            Expect("a bone pushed 2 mm into pushes back 0.6 N", Near(F(Vector3.zero, blocked: true, pushed: 0.002f), new Vector3(0, 0, -0.6f)));
            Expect("the table's force against the advance (-0.5 N) pushes the hand back", Near(F(Vector3.zero, axial: -0.5f), new Vector3(0, 0, -0.5f)));
            Expect("and one against the withdrawal (+0.5 N) pushes it on", Near(F(Vector3.zero, axial: 0.5f), new Vector3(0, 0, 0.5f)));
            Expect("the pop kicks the hand forward", Near(F(Vector3.zero, pop: 0.5f), new Vector3(0, 0, 0.5f)));

            // ---- NeedleHaptics' numbers for it ----
            var haptics = Make("haptics", active: false).AddComponent<NeedleHaptics>();
            // a: a weak tissue (k 20 N/m, up to 10 mm), b: a strong one (k 200 N/m, 10 mm) that continues in c (same spring); c is free of spring
            haptics.layers = new[]
            {
                new NeedleHaptics.Layer { name = "a", start = 0, resistance = 0.4f, pop = 0, stiffness = 20, viscosity = 2, friction = 0.1f, thickness = 0.01f },
                new NeedleHaptics.Layer { name = "b", start = 0.02f, resistance = 0.8f, pop = 0.6f, stiffness = 200, viscosity = 4, friction = 0.5f, thickness = 0.01f },
                new NeedleHaptics.Layer { name = "c", start = 0.04f, resistance = 0.8f, pop = 0, stiffness = 200, viscosity = 4, friction = 0.5f, thickness = 0.01f, continues = true },
                new NeedleHaptics.Layer { name = "d", start = 0.06f, resistance = 0.1f, pop = 0, stiffness = 0, viscosity = 3, friction = 0.2f, thickness = 0.005f },
            };
            haptics.silent = true;
            Expect("the strongest steady force of the table is b's spring at its stretch plus its friction (2.5 N)", Is(haptics.Peak, 2.5f));
            Expect("at rest 5 mm into a only the spring is felt: -k·dx = -0.1 N", Is(haptics.Force(0.005f, 0), -0.1f));
            Expect("a spring stops stretching at the thickness: 15 mm into a is -0.2 N", Is(haptics.Force(0.015f, 0), -0.2f));
            Expect("moving in at 10 mm/s adds the damper and the friction (whole above 3 mm/s): -(0.1 + 2·0.01) - 0.1 = -0.22 N", Is(haptics.Force(0.005f, 0.01f), -0.22f));
            Expect("moving out at 10 mm/s they turn around: -(0.1 - 0.02) + 0.1 = +0.02 N", Is(haptics.Force(0.005f, -0.01f), 0.02f));
            Expect("the friction fades out below 3 mm/s (half of it at 1.5): -0.1 - 0.003 - 0.05 = -0.153 N", Is(haptics.Force(0.005f, 0.0015f), -0.153f));
            Expect("each tissue starts its spring over: 1 mm into b is -0.2 N, at rest", Is(haptics.Force(0.021f, 0), -0.2f));
            Expect("a tissue that continues does not: c is 21 mm into the spring that began at b, capped at 10 mm (-2 N)", Is(haptics.Force(0.041f, 0), -2f));
            Expect("past a spring-free tissue only the damper and the friction are left: d at 5 mm/s is -(3·0.005) - 0.2 = -0.215 N", Is(haptics.Force(0.07f, 0.005f), -0.215f));
            Expect("before the first layer there is nothing", haptics.Force(-0.001f, 0.01f) == 0);
            float depth = 0;
            for (int i = 0; i < 40; i++) { depth += haptics.fullSpeed * 0.02f; haptics.Feel(depth, 0.02f); } // 12 mm in, the second layer starts at 20
            Expect($"advancing at full speed in a: the spring (capped at 0.2 N) and friction oppose it ({haptics.Axial:F2} N)", haptics.Axial < -0.3f && haptics.Axial > -0.45f && haptics.PopPulse == 0);
            while (haptics.PopPulse == 0 && depth < 0.03f) { depth += haptics.fullSpeed * 0.02f; haptics.Feel(depth, 0.02f); }
            Expect("entering the next layer gives its pop, at once", Is(haptics.PopPulse, 0.6f));
            for (int i = 0; i < 6; i++) { depth += haptics.fullSpeed * 0.02f; haptics.Feel(depth, 0.02f); }
            Expect("the pop runs down within popSeconds", haptics.PopPulse == 0);
            Expect($"and the force is the new tissue's: 2 mm into b it is already about three times a's strongest, -0.33 N ({haptics.Axial:F2} N)", haptics.Axial < -0.8f);
            for (int i = 0; i < 20; i++) { depth -= haptics.fullSpeed * 0.02f; haptics.Feel(depth, 0.02f); }
            Expect($"withdrawing, the damper and the friction push the other way ({haptics.Axial:F2} N)", haptics.Axial > -0.2f);
            Expect("silent, the controller is not buzzed but the amplitude is still worked out", haptics.LastSent > 0);
            haptics.Hit(depth);
            Expect("on bone the axial force and the pop are zero", haptics.Axial == 0 && haptics.PopPulse == 0);
            haptics.Release();
            Expect("released, zero", haptics.Axial == 0);

            // The body test has a and b (the layer tester's materials below are for two). The table's constants are on only where the check is
            // about them: the guide's and the hold's checks leave the tissue's axial force out.
            haptics.layers = new[] { haptics.layers[0], haptics.layers[1] };
            var kept = new[] { new[] { 20f, 2f, 0.1f }, new[] { 200f, 4f, 0.5f } };
            void Tissue(bool on)
            {
                for (int i = 0; i < 2; i++)
                {
                    var l = haptics.layers[i];
                    (l.stiffness, l.viscosity, l.friction) = on ? (kept[i][0], kept[i][1], kept[i][2]) : (0, 0, 0);
                }
            }

            // ---- the needle in the stylus' hand, on a synthetic body ----
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var skin = Make("skin");
            skin.transform.position = new Vector3(0, 0, 0.2f);
            skin.transform.localScale = new Vector3(1, 1, 0.4f); // the face the needle meets is z = 0
            var skinCollider = skin.AddComponent<MeshCollider>();
            skinCollider.sharedMesh = cube;
            Physics.SyncTransforms();
            var model = Make("model").AddComponent<ModelTarget>();
            model.pathEntry = Make("entry").transform;
            model.pathTarget = Make("target").transform;
            model.pathTarget.position = new Vector3(0.2f, 0.2f, 0.1f); // far from where the check pokes
            var action = Make("needle", active: false).AddComponent<NeedleAction>();
            action.model = model;
            action.bones = new Collider[0];
            action.skin = skinCollider;
            action.message = Make("message").AddComponent<TextMeshPro>();
            action.card = action.message.transform;
            action.hint = Make("hint");
            action.hintKey = Make("key").AddComponent<TextMeshPro>();
            action.hintLabel = Make("label").AddComponent<TextMeshPro>();
            action.haptics = haptics;
            action.ghost = Make("ghost", active: false).transform;
            haptics.layers[0].start = 0.001f;
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube); // something to hide while the needle is gone
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(action.transform, false);
            made.Add(body);
            var onEnable = typeof(NeedleAction).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);
            onEnable.Invoke(action, null);
            action.stylus = stylus;
            stylus.needle = action;
            stylus.home.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            stylus.dock = Vector3.zero; // so the device's tip is the world's tip, with the pen along +Z
            haptics.silent = false;
            haptics.fullSpeed = 0.015f;
            var graph = Make("graph").AddComponent<ForceGraph>(); // with a card, a plot and its label template, as the builder makes them
            graph.needle = action;
            graph.stylus = stylus;
            action.graph = graph;
            graph.card = Make("graph card", active: false).transform;
            graph.plot = Make("graph plot").AddComponent<RawImage>();
            graph.tick = Make("graph tick").AddComponent<TextMeshProUGUI>();
            graph.tick.transform.SetParent(graph.plot.transform, false);
            graph.tick.gameObject.SetActive(false);
            graph.legend = Make("graph legend").AddComponent<TextMeshPro>();
            graph.summary = Make("graph summary").AddComponent<TextMeshPro>();
            graph.saved = Make("graph saved").AddComponent<TextMeshPro>();
            string runsDir = Path.Combine(Path.GetTempPath(), "HapticStylusCheck-" + System.Guid.NewGuid().ToString("N"));
            graph.runsDir = runsDir; // the headset's copy of each run, not the real folder

            void Hold(Vector3 tip, int buttons = 0, int flags = 0, float tilt = 0)
            {
                stylus.Inject(State(tip, buttons, flags, tilt));
                var p = stylus.NeedlePose(action.tipLength);
                action.transform.SetPositionAndRotation(p.position, p.rotation); // what LandmarkAligner does
                stylus.Tick(0.02f);
                action.Step(0.02f);
                stylus.Flush();
                graph.Tick(0.02f);
            }
            Hold(Vector3.zero);
            stylus.Inject(State(Vector3.zero, buttons: HapticProtocol.Button2)); // arm the forces: a press of button 2
            stylus.Tick(0.02f);
            stylus.Inject(State(Vector3.zero));
            stylus.Tick(0.02f);

            Hold(new Vector3(0, 0, -0.05f));
            Expect("in the air the needle is outside and no force is asked", action.Current == NeedleAction.State.Outside && stylus.SentForce == Vector3.zero);
            Hold(new Vector3(0, 0, 0.01f));
            Expect("1 cm through the skin it is inside", action.Current == NeedleAction.State.Inside);
            for (int i = 0; i < 20; i++) Hold(new Vector3(0, 0, 0.01f + 0.0003f * (i + 1))); // 15 mm/s: full speed
            Expect($"the table's strongest force is the stylus' limit: 1 N / 2.5 N = {action.TableScale:F2}", Is(action.TableScale, 0.4f));
            Expect($"pushing in at full speed, the stylus is pushed back toward the user with the table's force, scaled (z {stylus.SentForce.z:F3})",
                stylus.SentEnabled && stylus.SentForce.z > 0.1f && Is(stylus.SentForce.z, -haptics.Axial * action.TableScale, 1e-4f) && Mathf.Abs(stylus.SentForce.x) < 0.01f);
            var at = new Vector3(0, 0, 0.016f);
            for (int i = 0; i < 20; i++) Hold(at); // held still
            Expect($"held still, the damper and the friction are gone and the spring stays: 20 N/m · 10 mm · 0.4 = 0.08 N (z {stylus.SentForce.z:F3})", Is(stylus.SentForce.z, 0.08f, 0.01f));
            Hold(at + new Vector3(0.01f, 0, 0));
            Expect($"1 cm sideways it is pulled back toward the track (x {stylus.SentForce.x:F2})", stylus.SentForce.x < -0.5f);
            Tissue(false);

            Hold(new Vector3(0, 0, -0.08f)); // out of the skin again
            Expect("out of the body the force is zero again", action.Current == NeedleAction.State.Outside && stylus.SentForce == Vector3.zero);

            // docked and the way out of the dock
            Hold(new Vector3(0, 0, 0.03f), flags: HapticProtocol.Inkwell);
            Expect("docked deep in the body, the needle rests: outside, no force", action.parked && action.Current == NeedleAction.State.Outside && stylus.SentForce == Vector3.zero);
            Expect("and is shown there", body.GetComponent<Renderer>().enabled);
            Hold(new Vector3(0, 0, 0.03f));
            Expect("leaving the dock still deep in the body, it stays hidden and outside", !action.parked && action.Current == NeedleAction.State.Outside && !body.GetComponent<Renderer>().enabled);
            Hold(new Vector3(0, 0, -0.06f));
            Expect("it appears once its axis is out of the body", body.GetComponent<Renderer>().enabled && action.Current == NeedleAction.State.Outside);
            Hold(new Vector3(0, 0, 0.02f));
            Expect("and goes in as usual from there", action.Current == NeedleAction.State.Inside);
            stylus.Inject(State(new Vector3(0, 0, 0.02f), flags: HapticProtocol.Inkwell));
            stylus.Tick(0.02f);
            action.Step(0.02f);
            Expect("docking while in the body takes the needle out", action.Current == NeedleAction.State.Outside);

            // ---- extracting with the stylus: the drip stays, the needle does not take itself out ----
            Tissue(true); // the force graph below records this run
            Hold(new Vector3(0, 0, -0.06f)); // out and undocked
            stylus.Inject(State(new Vector3(0, 0, -0.06f), buttons: HapticProtocol.Button2));
            stylus.Tick(0.02f);
            stylus.Inject(State(new Vector3(0, 0, -0.06f)));
            stylus.Tick(0.02f);
            model.pathTarget.position = new Vector3(0, 0, 0.05f);
            Hold(new Vector3(0, 0, 0.01f));
            for (float z = 0.013f; z < 0.0501f; z += 0.003f) Hold(new Vector3(0, 0, z));
            Hold(new Vector3(0, 0, 0.05f));
            Expect("at the target it says so", action.Current == NeedleAction.State.Reached);
            action.Toggle(); // what button 1 does
            for (int i = 0; i < 30; i++) Hold(new Vector3(0, 0, 0.05f));
            Expect("button 1 starts the extraction: the head is out and the fluid runs", action.Current == NeedleAction.State.Extracting && action.HeadOut >= 1);
            Expect("the stylus is held where it was: no force while it stays there", stylus.SentForce.magnitude < 0.05f);
            Hold(new Vector3(0.002f, 0, 0.05f));
            Expect($"2 mm away it is pulled back to where it was with 0.6 N (x {stylus.SentForce.x:F2}), and the needle stays", Is(stylus.SentForce.x, -0.6f, 0.05f) && action.Current == NeedleAction.State.Extracting);
            Hold(new Vector3(0.01f, 0, 0.03f));
            Expect($"far from it the hold is limited to 1 N, still pulling back", Is(stylus.SentForce.magnitude, 1f, 0.02f) && stylus.SentForce.x < 0 && action.Current == NeedleAction.State.Extracting);
            action.Toggle();
            float firstForce = -1;
            for (int i = 0; i < 40; i++)
            {
                Hold(new Vector3(0.01f, 0, 0.03f)); // the hand is 1 cm off the track and 2 cm back
                if (firstForce < 0 && !action.Extracting) firstForce = stylus.SentForce.magnitude;
            }
            Expect("button 1 again closes the head and gives the needle back: still at the target, not withdrawing", !action.Extracting && action.Current == NeedleAction.State.Reached);
            Expect($"and it does not take itself out of the body (no animation, not hidden)", body.GetComponent<Renderer>().enabled);
            for (int i = 0; i < 150; i++) Hold(new Vector3(0.01f, 0, 0.03f));
            Expect("not even after a long time", action.Current == NeedleAction.State.Reached && body.GetComponent<Renderer>().enabled);
            Expect($"the force comes back gradually, not at once ({firstForce:F2} N on the first frame, {stylus.SentForce.magnitude:F2} N later)", firstForce >= 0 && firstForce < 0.3f && stylus.SentForce.magnitude > 0.9f);
            Hold(new Vector3(0, 0, -0.06f));
            Expect("the user takes the needle out with the stylus", action.Current == NeedleAction.State.Outside);

            // ---- the force graph: the run just done, in to the target and the fluid out, with the forces on ----
            var outside = new Vector3(0, 0, -0.06f);
            void Press2(Vector3 at) // button 2: arms or disarms the forces
            {
                stylus.Inject(State(at, buttons: HapticProtocol.Button2));
                stylus.Tick(0.02f);
                graph.Tick(0.02f);
                stylus.Inject(State(at));
                stylus.Tick(0.02f);
                graph.Tick(0.02f);
            }
            Expect($"the run is recorded and waits for the forces to go off ({graph.Samples.Count} points)", graph.State == ForceGraph.Mode.Done && graph.Samples.Count > 40 && !graph.Visible && !graph.card.gameObject.activeSelf);
            int recorded = graph.Samples.Count;
            Hold(new Vector3(0, 0, 0.01f)); // the tip skims the skin on its way out: in again for a moment, with the forces still on
            Expect("the needle in again a moment after it left is not a new puncture: the complete run waits", graph.State == ForceGraph.Mode.Done && graph.Samples.Count == recorded);
            Hold(outside);
            Expect("and out again, it still waits", graph.State == ForceGraph.Mode.Done && graph.Samples.Count == recorded && !graph.Visible);
            bool fluid = false;
            float depthMax = 0, peak = 0, last = 0;
            foreach (var s in graph.Samples) { fluid |= s.fluid; depthMax = Mathf.Max(depthMax, s.depth); peak = Mathf.Max(peak, s.along); last = s.t; }
            Expect("it goes from the skin down to the target (5 cm) and into the fluid extraction", fluid && Is(depthMax, 0.05f, 0.002f) && graph.Samples[0].depth < 0.02f && graph.Samples[0].t < 0.05f);
            Expect($"time runs at 0.02 s a frame ({last:F1} s)", Is(last, 0.02f * graph.Samples.Count, 0.001f));
            Expect($"the force is the one asked of the stylus: against the advance in the tissue, up to its 1 N limit (peak {peak:F2} N)", peak > 0.5f && peak <= 1.0001f);
            // the run is saved as soon as it is complete: a CSV on the headset (and on the PC, if the link is there; none here)
            var files = Directory.Exists(runsDir) ? Directory.GetFiles(runsDir, "run-*.csv") : new string[0];
            string[] lines = files.Length == 1 ? File.ReadAllText(files[0]).Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries) : new string[0];
            Expect($"the complete run is saved as a CSV ({files.Length} file, {lines.Length - 1} rows for {graph.Samples.Count} samples)", files.Length == 1 && lines.Length == graph.Samples.Count + 1 && File.ReadAllText(files[0]) == graph.Csv());
            Expect($"with a header and one row per frame: {(lines.Length > 1 ? lines[0] + " | " + lines[1] : "")}",
                lines.Length > 1 && lines[0] == "time_s,depth_mm,tissue,phase,formula_N,along_N,side_N" && lines[1].Split(',').Length == 7 && lines[1].Split(',')[3] == "insertion" && lines[1].Split(',')[2] == "a");
            Expect("the extraction rows say so", lines.Length > 1 && lines[lines.Length - 1].Split(',')[3] == "fluid" && lines[lines.Length - 1].Split(',')[2] == "b");
            Press2(outside);
            Expect("the needle is out and the forces are off: the graph is shown on its card", !stylus.ForcesOn && graph.Visible && graph.card.gameObject.activeSelf);
            if (bridge != null)
            {
                for (int i = 0; i < 200 && !graph.saved.text.StartsWith("Saved on the PC"); i++) { System.Threading.Thread.Sleep(20); graph.Tick(0.02f); } // the bridge answers
                var pcFiles = Directory.Exists(pcDir) ? Directory.GetFiles(pcDir, "run-*.csv") : new string[0];
                Expect($"with the link up the PC saves the run and the card says so ({graph.saved.text}, {pcFiles.Length} file there)",
                    graph.saved.text.StartsWith("Saved on the PC: run-") && pcFiles.Length == 1 && File.ReadAllText(pcFiles[0]) == graph.Csv());
                Expect("and the graph stays up while the answer comes", graph.Visible && graph.card.gameObject.activeSelf);
            }
            else
            {
                Expect($"the card says where it is saved: on the headset, and the PC is being waited for ({graph.saved.text})",
                    graph.saved.text.Contains("Saved on the headset: run-") && graph.saved.text.Contains("Sending it to the PC"));
                stylus.Inject(State(outside), live: false);
                graph.Tick(0.02f);
                Expect("with no link it says the PC is not connected and it will go when the bridge is back", graph.saved.text.Contains("not connected"));
                stylus.Inject(State(outside));
                graph.Tick(0.02f);
            }
            var pixels = graph.Texture.GetPixels32();
            int yellow = 0, banded = 0;
            foreach (var p in pixels)
            {
                if (p.r > 200 && p.g > 150 && p.b < 80 && p.a > 200) yellow++;
                if (p.a > 90) banded++;
            }
            Expect($"its texture has the curve ({yellow} px of the line) and the bands under it", graph.Texture.width == graph.width && graph.Texture.height == graph.height && yellow > 300 && banded > pixels.Length / 2);
            int labels = 0;
            bool unitN = false, unitS = false;
            foreach (Transform c in graph.plot.transform) if (c.gameObject.activeSelf && c != graph.tick.transform) { labels++; var text = c.GetComponent<TMP_Text>().text; unitN |= text.EndsWith(" N"); unitS |= text.EndsWith(" s"); }
            Expect($"the axes have their values ({labels} labels), the units on the last ones", labels >= 6 && unitN && unitS);
            Expect("the legend names the tissues it went through (a, b here) and the fluid, in their colours; the summary gives the peak",
                graph.legend.text.Contains(">a</color>") && graph.legend.text.Contains(">b</color>") && graph.legend.text.Contains(">Fluid extraction</color>") && graph.summary.text.Contains("Peak"));
            action.inMenu = true;
            graph.Tick(0.02f);
            Expect("the card steps aside while the menu is open", graph.Visible && !graph.card.gameObject.activeSelf);
            action.inMenu = false;
            graph.Tick(0.02f);
            Expect("and comes back", graph.card.gameObject.activeSelf);
            action.Toggle(); // A, or the stylus' button 1
            graph.Tick(0.02f);
            Expect("A closes it and the run is forgotten", !graph.Visible && graph.State == ForceGraph.Mode.Idle && graph.Samples.Count == 0 && !graph.card.gameObject.activeSelf);

            Hold(new Vector3(0, 0, 0.01f));
            Expect("with the forces off, going in records nothing", graph.State == ForceGraph.Mode.Idle && graph.Samples.Count == 0);
            Hold(outside);
            Press2(outside);
            Expect("forces armed again", stylus.ForcesOn);
            Hold(new Vector3(0, 0, 0.01f));
            Hold(new Vector3(0, 0, 0.02f));
            Expect("armed, going in starts a run", graph.State == ForceGraph.Mode.Recording && graph.Samples.Count == 2);
            Hold(outside);
            Expect("taking the needle out before the end drops it: no graph", graph.State == ForceGraph.Mode.Idle && graph.Samples.Count == 0);
            Hold(new Vector3(0, 0, 0.01f));
            Hold(new Vector3(0, 0, 0.02f));
            Press2(new Vector3(0, 0, 0.02f));
            Expect("so does turning the forces off before the end", !stylus.ForcesOn && graph.State == ForceGraph.Mode.Idle && graph.Samples.Count == 0);
            Hold(outside);
            Expect("and nothing is shown", !graph.Visible && !graph.card.gameObject.activeSelf);
            Press2(outside);
            Expect("armed again, as the next checks have it", stylus.ForcesOn);
            Tissue(false);

            // ---- help mode: the ideal path is z from 0 to 85 mm; the stylus is judged near its start ----
            model.pathTarget.position = new Vector3(0, 0, 0.085f);
            action.guided = true;
            Hold(new Vector3(0, 0, -0.1f));
            Expect("help mode, far from the path: nothing is judged, no indicator, no ghost", action.GuideBand == NeedleAction.Band.None && !action.MessageShown && !action.ghost.gameObject.activeSelf);
            // the user's case: the stylus stretched out as far as it goes, where they start: 36 mm before the entry, 10 mm off, the pen pitched down 25 degrees
            Hold(new Vector3(0.0095f, 0, -0.036f), tilt: -25);
            Expect("the stylus at its far point, off the path and tilted, is not judged: no indicator, no 'force is off'", action.GuideBand == NeedleAction.Band.None && !action.MessageShown && !action.ghost.gameObject.activeSelf);
            Hold(new Vector3(0.0095f, 0, -0.02f), tilt: -25);
            Expect("and it is still not judged 2 cm before the entry", action.GuideBand == NeedleAction.Band.None && !action.MessageShown);
            Hold(new Vector3(0.06f, 0, -0.01f));
            Expect("near the start but 6 cm off the path: still not judged", action.GuideBand == NeedleAction.Band.None);
            Hold(new Vector3(0, 0, -0.01f));
            Expect("on the path and straight, it says so", action.GuideBand == NeedleAction.Band.Good && action.MessageShown && action.message.text.Contains("On the path"));
            Hold(new Vector3(0, 0, -0.02f));
            Expect("judging goes on a little farther out than it began (no flicker)", action.GuideBand == NeedleAction.Band.Good);
            Hold(new Vector3(0, 0, -0.03f));
            Expect("and stops beyond 25 mm", action.GuideBand == NeedleAction.Band.None && !action.MessageShown);
            Hold(new Vector3(0, 0, -0.01f));
            Hold(new Vector3(0.005f, 0, -0.01f));
            Expect("5 mm off: adjust", action.GuideBand == NeedleAction.Band.Close && action.message.text.Contains("Adjust"));
            Hold(new Vector3(0.010f, 0, -0.01f));
            Expect("10 mm off: off the path, the force is still there", action.GuideBand == NeedleAction.Band.Off && action.message.text.Contains("Off the path") && !action.message.text.Contains("Force is off"));
            Hold(new Vector3(0.014f, 0, -0.01f));
            Expect("14 mm off: lost, the force is off", action.GuideBand == NeedleAction.Band.Lost && action.message.text.Contains("Force is off"));
            Hold(new Vector3(0, 0, 0.01f));
            Expect("back on the path it stays lost: no force, the needle is not in", action.GuideBand == NeedleAction.Band.Lost && action.Current == NeedleAction.State.Outside && stylus.SentForce == Vector3.zero);
            Hold(new Vector3(0, 0, -0.08f));
            Expect("going back out of the approach starts over", action.GuideBand == NeedleAction.Band.None && !action.MessageShown);
            Hold(new Vector3(0.014f, 0, -0.01f));
            Hold(new Vector3(0.02f, 0, -0.01f));
            Expect("(lost again)", action.GuideBand == NeedleAction.Band.Lost);
            Hold(new Vector3(0, 0, 0.03f), flags: HapticProtocol.Inkwell);
            Expect("so does going back to the dock", action.GuideBand == NeedleAction.Band.None);
            Hold(new Vector3(0, 0, 0.03f));
            Hold(new Vector3(0, 0, -0.06f)); // out of the body: the needle shows again
            Expect("docking had disarmed the forces", !stylus.ForcesOn);
            stylus.Inject(State(new Vector3(0, 0, -0.06f), buttons: HapticProtocol.Button2)); // arm them again
            stylus.Tick(0.02f);
            stylus.Inject(State(new Vector3(0, 0, -0.06f)));
            stylus.Tick(0.02f);
            Hold(new Vector3(0, 0, 0.002f));
            Expect("straight into the body past the start, without the approach, is wrong", action.GuideBand == NeedleAction.Band.Lost && action.Current == NeedleAction.State.Outside);
            Hold(new Vector3(0, 0, -0.08f));
            Hold(new Vector3(0, 0, -0.01f));
            Expect("engaged again on the path", action.GuideBand == NeedleAction.Band.Good);
            Vector3 Shown() => action.transform.position + action.transform.forward * action.tipLength; // where the needle's tip is drawn
            Vector3 Ghost() => action.ghost.position + action.ghost.forward * action.tipLength;         // and the ghost's
            Hold(new Vector3(0.006f, 0, -0.01f));
            Expect($"the needle goes onto the path by easing, not by a jump (x {Shown().x * 1000:F1} mm of 6 at the stylus)", Shown().x > 0.003f && Shown().x < 0.006f);
            for (int i = 0; i < 12; i++) Hold(new Vector3(0.006f, 0, -0.01f));
            Expect("then it is on the path, 6 mm from where the stylus is", Mathf.Abs(Shown().x) < 1e-4f && Is(Shown().z, -0.01f, 1e-4f));
            Expect("and the ghost is where the stylus is", action.ghost.gameObject.activeSelf && Near(Ghost(), new Vector3(0.006f, 0, -0.01f)) && Near(action.ghost.forward, Vector3.forward));
            Hold(new Vector3(0.006f, 0, 0.002f));
            Expect("through the entry the needle is on the path and in", action.Current == NeedleAction.State.Inside && Mathf.Abs(Shown().x) < 1e-4f);
            Expect("the hand is pulled toward the path, gently at first", stylus.SentForce.x < -0.05f && stylus.SentForce.x > -0.3f);
            Hold(new Vector3(0.006f, 0, 0.02f));
            Expect("1.8 cm in it is on the path, whatever the hand does, and the ghost stays with the stylus", Mathf.Abs(Shown().x) < 1e-4f && Is(Shown().z, 0.02f, 1e-4f) && Near(Ghost(), new Vector3(0.006f, 0, 0.02f)));
            Expect("and the hand is pulled back to the path (0.9 N)", Is(stylus.SentForce.x, -0.9f, 0.15f));
            Hold(new Vector3(0, 0, 0.1f));
            Expect("past the target the needle stops at it and says so", Is(Shown().z, 0.085f, 1e-4f) && action.Current == NeedleAction.State.Reached);
            Expect("and the stylus is pushed back (the force is limited to 1 N, toward the user)", Is(stylus.SentForce.z, 1f, 0.02f));

            // extracting in help mode: the needle stays on the path, the ghost is the stylus, and the stylus is held where it was
            Hold(new Vector3(0.003f, 0, 0.085f));
            Expect("at the target, 3 mm off the path", action.Current == NeedleAction.State.Reached);
            action.Toggle();
            for (int i = 0; i < 30; i++) Hold(new Vector3(0.003f, 0, 0.085f));
            Expect("the extraction runs, the needle on the path and the ghost at the stylus", action.Current == NeedleAction.State.Extracting && Mathf.Abs(Shown().x) < 1e-4f && Near(Ghost(), new Vector3(0.003f, 0, 0.085f)));
            Hold(new Vector3(0.005f, 0, 0.085f));
            Expect($"the stylus is held where it was (2 mm away, {stylus.SentForce.x:F2} N back); the needle has not moved, the ghost has", Is(stylus.SentForce.x, -0.6f, 0.05f) && Mathf.Abs(Shown().x) < 1e-4f && Near(Ghost(), new Vector3(0.005f, 0, 0.085f)));
            action.Toggle();
            for (int i = 0; i < 40; i++) Hold(new Vector3(0.003f, 0, 0.085f));
            Expect("stopping gives the needle back, on the path", !action.Extracting && action.Current == NeedleAction.State.Reached && Mathf.Abs(Shown().x) < 1e-4f);
            Hold(new Vector3(0, 0, -0.02f));
            Expect("pulling the stylus back out takes the needle out", action.Current == NeedleAction.State.Outside);
            action.guided = false;
            Hold(new Vector3(0, 0, -0.02f));
            Expect("without help mode there is no indicator and no ghost", action.GuideBand == NeedleAction.Band.None && !action.MessageShown && !action.ghost.gameObject.activeSelf);

            // ---- the layer tester with the stylus: the cube replaces the model, on the ideal path, and pushes back ----
            var part = Make("model part");
            model.groups = new[] { new ModelTarget.Group { name = "part", objects = new[] { part }, visible = true } };
            var cubeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(cubeObject.GetComponent<Collider>());
            cubeObject.transform.localScale = Vector3.one * 0.1f;
            made.Add(cubeObject);
            var tester = Make("tester", active: false).AddComponent<LayerTester>();
            cubeObject.transform.SetParent(tester.transform, false);
            var cubeMaterial = cubeObject.GetComponent<Renderer>().sharedMaterial;
            tester.needle = action;
            tester.cube = cubeObject.GetComponent<Renderer>();
            tester.materials = new[] { cubeMaterial, cubeMaterial, cubeMaterial };
            tester.haptics = Make("tester haptics", active: false).AddComponent<NeedleHaptics>();
            action.gameObject.SetActive(true); // LayerTester only feels a needle that is on
            if (!stylus.ForcesOn)
            {
                stylus.Inject(State(new Vector3(0, 0, -0.06f), buttons: HapticProtocol.Button2));
                stylus.Tick(0.02f);
                stylus.Inject(State(new Vector3(0, 0, -0.06f)));
                stylus.Tick(0.02f);
            }
            void Poke(Vector3 tip, float tilt = 0) // what LandmarkAligner and the tester's LateUpdate do each frame
            {
                stylus.Inject(State(tip, 0, 0, tilt));
                var p = stylus.NeedlePose(action.tipLength);
                action.transform.SetPositionAndRotation(p.position, p.rotation);
                stylus.Tick(0.02f);
                tester.Feel(0.02f);
                stylus.Flush();
            }
            Tissue(true); // the cube takes the needle's layers, constants and all
            tester.OpenOnPath();
            Expect("the cube is on the ideal path, its face at the entry (10 cm: its middle 5 cm down the path), turned along it",
                Near(tester.transform.position, new Vector3(0, 0, 0.05f)) && Near(tester.transform.forward, Vector3.forward));
            Expect("the model is hidden and the needle's body test stands down", !part.activeSelf && !action.enabled && tester.gameObject.activeSelf);
            Poke(new Vector3(0, 0, -0.03f));
            Expect("outside the cube, no force", stylus.SentForce == Vector3.zero && tester.haptics.silent);
            Poke(new Vector3(0, 0, 0.005f));
            for (int i = 0; i < 20; i++) Poke(new Vector3(0, 0, 0.005f + 0.0003f * (i + 1))); // 15 mm/s: full speed
            Expect($"pushing into the first layer at full speed, the stylus is pushed back toward the user with the table's force (z {stylus.SentForce.z:F3})",
                stylus.SentEnabled && stylus.SentForce.z > 0.1f && Is(stylus.SentForce.z, -tester.haptics.Axial * action.TableScale, 1e-4f) && Mathf.Abs(stylus.SentForce.x) < 0.01f);
            Poke(new Vector3(0.006f, 0, 0.011f));
            Expect($"the layer grips the needle on the line it went in on: 6 mm across it, pulled back (x {stylus.SentForce.x:F2})", Is(stylus.SentForce.x, -0.9f, 0.15f));
            Poke(new Vector3(0, 0, -0.03f));
            tester.Select(1); // the second layer pops on entry
            Poke(new Vector3(0, 0, 0.005f));
            Expect($"the kick is off by default: 5 mm into the strong tissue only its spring pushes back (200 N/m · 5 mm · 0.4 = 0.4 N; z {stylus.SentForce.z:F2})", Is(stylus.SentForce.z, 0.4f, 0.01f));
            Poke(new Vector3(0, 0, -0.03f));
            action.popForce = 2; // turned on: 1.2 N forward against the 0.4 N of the spring
            Poke(new Vector3(0, 0, 0.005f));
            Expect($"turned on, the second layer pops as the tip goes in: a kick forward (z {stylus.SentForce.z:F2})", stylus.SentForce.z < -0.3f);
            action.popForce = 0;
            Poke(new Vector3(0, 0, -0.03f));
            tester.Select(2); // bone
            Poke(new Vector3(0, 0, 0.01f));
            Expect($"bone is a wall: 1 cm into it, pushed back as hard as it goes (z {stylus.SentForce.z:F2})", Is(stylus.SentForce.z, 1f, 0.02f));
            Poke(new Vector3(0, 0, -0.03f));
            Expect("out of the cube again, no force", stylus.SentForce == Vector3.zero);
            tester.Close();
            Expect("closing brings the model and the needle's body test back", part.activeSelf && action.enabled && !tester.gameObject.activeSelf);
            tester.Open(new Vector3(5, 5, 5), Quaternion.identity);
            Expect("opened beside the panel (the controller's way) the model stays", part.activeSelf);
            tester.Close();
            action.gameObject.SetActive(false);
            typeof(NeedleAction).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(action, null);
        }
        finally
        {
            if (linked != null) stylusOnDisable.Invoke(linked, null); // closes the link
            if (bridge != null)
            {
                if (!bridge.HasExited) bridge.Kill();
                bridge.Dispose();
            }
            foreach (var go in made) if (go != null) Object.DestroyImmediate(go);
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "HapticStylusCheck*-*")) Directory.Delete(dir, true);
        }
        report.Append(failed == 0 ? "all passed" : $"{failed} failed");
        return report.ToString();
    }
}
