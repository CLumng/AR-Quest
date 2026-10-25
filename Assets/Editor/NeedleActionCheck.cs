using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks NeedleAction on a synthetic body (skin = the plane z = 0, a 2 cm bone block under it, the target 85 mm down at the side):
/// the needle follows the hand outside, goes in on a semi-straight track (sideways drag ignored, bends at a limited rate and angle),
/// a bone stops it and buzzes at 95 %, pulling back and angling walks off the bone, and reaching the target is reported. At the
/// target A leaves the needle in the model and the fluid runs; A again gives the needle back to the controller.
/// </summary>
public static class NeedleActionCheck
{
    [MenuItem("Tools/Model Target/Self-check Needle Action")]
    public static void Run()
    {
        var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var made = new System.Collections.Generic.List<GameObject>();
        MeshCollider Block(string name, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name);
            made.Add(go);
            go.transform.position = center;
            go.transform.localScale = size;
            var c = go.AddComponent<MeshCollider>();
            c.sharedMesh = cube;
            return c;
        }
        var skin = Block("check skin", new Vector3(0, 0, -0.2f), new Vector3(1, 1, 0.4f));    // top face at z = 0
        var bone = Block("check bone", new Vector3(0, 0, -0.03f), new Vector3(0.02f, 0.02f, 0.02f)); // x, y +-0.01, z -0.04 .. -0.02
        Physics.SyncTransforms();

        var entry = new GameObject("check entry").transform;
        var aim = new GameObject("check target").transform;
        aim.position = new Vector3(0.05f, 0.05f, -0.085f);
        var model = new GameObject("check model").AddComponent<ModelTarget>();
        model.pathEntry = entry;
        model.pathTarget = aim;
        var action = new GameObject("check needle").AddComponent<NeedleAction>();
        action.model = model;
        action.bones = new Collider[] { bone };
        action.skin = skin;
        action.message = new GameObject("check message").AddComponent<TextMeshPro>();
        action.card = action.message.transform; // one object stands for the card and its text here
        action.hint = new GameObject("check hint");
        action.hintKey = new GameObject("check key").AddComponent<TextMeshPro>();
        action.hintLabel = new GameObject("check label").AddComponent<TextMeshPro>();
        var haptics = new GameObject("check haptics").AddComponent<NeedleHaptics>();
        haptics.layers = new[] { new NeedleHaptics.Layer { name = "check", start = 0.001f, resistance = 0.5f, pop = 0.5f } };
        action.haptics = haptics;
        var source = new GameObject("check source").transform;
        source.position = new Vector3(0, 1, 0);
        var flow = new GameObject("check flow").AddComponent<FluidFlow>();
        flow.source = source;
        flow.drops = new Transform[3];
        for (int i = 0; i < flow.drops.Length; i++)
        {
            var drop = new GameObject("check drop " + i);
            drop.SetActive(false);
            made.Add(drop);
            flow.drops[i] = drop.transform;
        }
        action.flow = flow;
        var head = new GameObject("check head").transform; // the stylet head, a child of the needle like the model's own
        head.SetParent(action.transform, false);
        action.head = head;
        var body = new GameObject("check body").AddComponent<MeshRenderer>(); // what is hidden while the needle is gone
        body.transform.SetParent(action.transform, false);
        made.AddRange(new[] { entry.gameObject, aim.gameObject, model.gameObject, action.message.gameObject, action.hint, action.hintKey.gameObject, action.hintLabel.gameObject, haptics.gameObject, action.gameObject, source.gameObject, flow.gameObject });

        var onEnable = typeof(NeedleAction).GetMethod("OnEnable", BindingFlags.NonPublic | BindingFlags.Instance);
        var under = typeof(NeedleAction).GetMethod("Under", BindingFlags.NonPublic | BindingFlags.Instance);
        var onDisable = typeof(NeedleAction).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance);
        var next = typeof(NeedleHaptics).GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance);
        int failed = 0;

        // The hand holds the needle with its tip here, tilted this many degrees off straight in (towards +Y).
        void Hold(Vector3 tip, float tilt = 0)
        {
            var forward = Quaternion.AngleAxis(tilt, Vector3.right) * Vector3.back;
            action.transform.SetPositionAndRotation(tip - forward * action.tipLength, Quaternion.LookRotation(forward));
            action.Step(0.02f);
        }
        Vector3 Shown() => action.transform.position + action.transform.forward * action.tipLength;
        float Bend() => Vector3.Angle(action.transform.forward, Vector3.back); // how far the needle is turned from straight in
        bool At(Vector3 p) => (Shown() - p).magnitude < 1e-4f;
        float DepthOf(Vector3 tip, float tilt)
        {
            var args = new object[] { tip, Quaternion.AngleAxis(tilt, Vector3.right) * Vector3.back, 0f, Vector3.zero };
            return (bool)under.Invoke(action, args) ? (float)args[2] : -1;
        }
        void Expect(string what, bool ok)
        {
            Debug.Log($"NeedleAction: {what} -> {(ok ? "PASS" : "FAIL")}");
            if (!ok) failed++;
        }
        void Away() => Hold(new Vector3(0.3f, 0.3f, 0.05f)); // out in the air, far from everything

        try
        {
            flow.Begin();
            flow.Step(0.05f);
            Expect("the fluid starts with a drop at once, and counts 0.15 mL per second", flow.Flowing && flow.drops[0].gameObject.activeSelf && Mathf.Abs(flow.Volume - 0.0075f) < 1e-5f);
            flow.Step(0.1f);
            Expect("the drop falls", flow.drops[0].position.y < 1 - 0.001f);
            flow.Stop();
            float volume = flow.Volume;
            flow.Step(0.2f);
            Expect("stopped: the volume stops growing and no new drop comes out", flow.Volume == volume && !flow.drops[1].gameObject.activeSelf);
            for (int i = 0; i < 40; i++) flow.Step(0.05f);
            Expect("a drop that has fallen far enough is taken out of play", !flow.drops[0].gameObject.activeSelf);

            onEnable.Invoke(action, null);
            Expect("the depth is the needle's length under the skin: 20 mm straight in", Mathf.Abs(DepthOf(new Vector3(0.1f, 0.1f, -0.02f), 0) - 0.02f) < 1e-4f);
            Expect("slanted 60 degrees the same tip has 40 mm of needle under the skin", Mathf.Abs(DepthOf(new Vector3(0.1f, 0.1f, -0.02f), 60) - 0.04f) < 1e-4f);

            Hold(new Vector3(0.1f, 0.1f, 0.05f));
            Expect("outside the skin the needle is the hand's: nothing felt, no message",
                action.Current == NeedleAction.State.Outside && At(new Vector3(0.1f, 0.1f, 0.05f)) && !haptics.InTissue && !action.MessageShown);

            Hold(new Vector3(0.1f, 0.1f, -0.01f));
            Expect("through the skin it is inside, still where the hand holds it, and the tissue is felt",
                action.Current == NeedleAction.State.Inside && At(new Vector3(0.1f, 0.1f, -0.01f)) && haptics.InTissue);

            Hold(new Vector3(0.13f, 0.1f, -0.03f));
            Expect("dragging the hand 30 mm sideways does not move the needle, only its 20 mm advance counts", At(new Vector3(0.1f, 0.1f, -0.03f)));

            Hold(new Vector3(0.1f, 0.1f, -0.04f), 5);
            Expect("it bends towards where the controller points, at 0.5 degrees per mm: 5 degrees after 10 mm", Mathf.Abs(Bend() - 5) < 0.05f);
            Hold(new Vector3(0.1f, 0.1f, -0.06f), 40);
            Expect("but never more than 10 degrees from the way it went in", Mathf.Abs(Bend() - 10) < 0.05f);

            Hold(new Vector3(0.1f, 0.1f, 0.02f));
            Expect("pulled out of the skin it is the hand's again", action.Current == NeedleAction.State.Outside && At(new Vector3(0.1f, 0.1f, 0.02f)) && !haptics.InTissue);

            // A bone on the track.
            Away();
            Hold(new Vector3(0, 0, 0.05f));
            Hold(new Vector3(0, 0, -0.01f));
            next.SetValue(haptics, float.MinValue); // a buzz may go out right now
            Hold(new Vector3(0, 0, -0.025f));
            Expect("pushing on into a bone stops the needle at its surface", action.Current == NeedleAction.State.Blocked && At(new Vector3(0, 0, -0.0195f)));
            Expect("the controller buzzes at 95 %", Mathf.Abs(haptics.LastSent - 0.95f) < 1e-5f);
            Expect("the message says a bone was hit", action.MessageShown && action.message.text.Contains("bone"));

            Hold(new Vector3(0, 0, -0.06f), 10);
            Expect("pushing harder stays blocked, and a needle that cannot move does not turn", action.Current == NeedleAction.State.Blocked && At(new Vector3(0, 0, -0.0195f)) && Bend() < 0.01f);
            Hold(new Vector3(0.03f, 0, -0.06f));
            Expect("dragging the hand sideways does not walk it off the bone", action.Current == NeedleAction.State.Blocked && At(new Vector3(0, 0, -0.0195f)));

            Hold(new Vector3(0, 0, -0.0185f));
            Expect("pulling back 1 mm is still held (no flicker at the surface)", action.Current == NeedleAction.State.Blocked);
            Hold(new Vector3(0, 0, -0.016f));
            Expect("3 mm back lets go, and the needle follows the hand again", action.Current == NeedleAction.State.Inside && At(new Vector3(0, 0, -0.016f)) && !action.MessageShown);
            Hold(new Vector3(0, 0, -0.03f));
            Expect("pushing in again meets the same bone", action.Current == NeedleAction.State.Blocked);

            // Walking off a bone: pull back a little and angle (a faster, wider bend than the default, to keep the check short).
            Away();
            action.turnPerMm = 2;
            action.maxBend = 20;
            Hold(new Vector3(0, 0.0095f, 0.05f));
            Hold(new Vector3(0, 0.0095f, -0.005f));
            Hold(new Vector3(0, 0.0095f, -0.03f));
            Expect("an entry 0.5 mm inside the bone's edge runs into it", action.Current == NeedleAction.State.Blocked);
            Hold(new Vector3(0, 0.0095f, -0.005f));
            for (float z = -0.009f; z > -0.0375f; z -= 0.004f) Hold(new Vector3(0, 0.0095f, z), 20);
            Expect("pulling back and angling takes it past the edge of the bone", action.Current == NeedleAction.State.Inside && Shown().y > 0.0105f && Shown().z < -0.03f);
            action.turnPerMm = 0.5f;
            action.maxBend = 10;

            // A stab, and a controller that reappears deep in the body.
            Away();
            Hold(new Vector3(0, 0, 0.05f));
            Hold(new Vector3(0, 0, -0.1f));
            Expect("a stab through the whole bone in one frame is stopped at its surface", action.Current == NeedleAction.State.Blocked && At(new Vector3(0, 0, -0.0195f)));
            Away();
            onEnable.Invoke(action, null);
            Hold(new Vector3(0, 0, -0.1f));
            Expect("a controller that comes back deep in the body goes in along its own axis, and a bone in the way stops it",
                action.Current == NeedleAction.State.Blocked && At(new Vector3(0, 0, -0.0195f)));

            // The target.
            Away();
            Hold(new Vector3(0.05f, 0.05f, 0.05f));
            Hold(new Vector3(0.05f, 0.05f, -0.01f));
            Hold(new Vector3(0.05f, 0.05f, -0.082f));
            Expect("3 mm from the target is reached, and the message says what A does", action.Current == NeedleAction.State.Reached && action.message.text.Contains("Target reached") && action.hint.activeSelf && action.hintKey.text == "A" && action.hintLabel.text.Contains("Extract fluid"));
            Hold(new Vector3(0.05f, 0.05f, -0.07f));
            Expect("15 mm from the target is not, and the message goes", action.Current == NeedleAction.State.Inside && !action.MessageShown);
            action.Toggle();
            Hold(new Vector3(0.05f, 0.05f, -0.07f));
            Expect("A away from the target does nothing", action.Current == NeedleAction.State.Inside && !flow.Flowing && !action.Extracting);

            // Extracting fluid: the stylet head slides out of the hub, then the fluid runs.
            Hold(new Vector3(0.05f, 0.05f, -0.082f));
            var left = Shown();
            Expect("the head starts at home", action.HeadOut == 0 && head.localPosition == Vector3.zero);
            action.Toggle();
            Hold(new Vector3(0.3f, 0.3f, 0.2f), 30);
            Expect("A at the target leaves the needle where it is, whatever the controller does",
                action.Current == NeedleAction.State.Extracting && action.Extracting && At(left) && action.message.text.Contains("Extracting"));
            Expect("the head is on its way out and the fluid waits for it", action.HeadOut > 0 && action.HeadOut < 1 && !flow.Flowing);
            for (int i = 0; i < 25; i++) Hold(new Vector3(0.3f, 0.3f, 0.2f), 30); // 0.5 s
            Expect("the head is out, 6 cm back along the needle, and then the fluid runs",
                action.HeadOut == 1 && flow.Flowing && (head.localPosition - new Vector3(0, 0, -0.06f)).magnitude < 1e-4f && At(left));
            Expect("nothing of the tissue is felt while it is left in", !haptics.InTissue);
            flow.Step(2.1f); // 0.315 mL
            Hold(new Vector3(-0.2f, 0.1f, 0.3f));
            Expect("the volume is on the message and the needle has not moved", action.message.text.Contains("0.3 mL") && At(left));

            // A again: the fluid stops, the head goes back in, and the needle comes out of the body along its track.
            action.Toggle();
            var inside = new Vector3(0.3f, 0.3f, -0.05f); // the controller stays where it is: under the skin line
            Hold(inside);
            Expect("A again stops the fluid at once; the needle is still in, and the card is gone", action.Current == NeedleAction.State.Withdrawing && !flow.Flowing && At(left) && !action.MessageShown);
            int frames = 0;
            var lastIn = left;
            while (action.Current == NeedleAction.State.Withdrawing && frames++ < 300)
            {
                Hold(inside);
                if (action.Current == NeedleAction.State.Withdrawing) lastIn = Shown();
                if (frames == 10) Expect("the head slides back in first, with the needle still where it was", action.HeadOut < 1 && action.HeadOut > 0 && At(left));
            }
            Expect("the head is home again", action.HeadOut == 0 && head.localPosition == Vector3.zero);
            Expect("the needle came out of the body along its track, to the skin, at 0.1 m/s (about 1.3 s with the head)", frames > 50 && frames < 80 && lastIn.z > -0.003f && Mathf.Abs(lastIn.x - 0.05f) < 1e-3f);
            Expect("it is out, hidden, and the extraction is over", action.Current == NeedleAction.State.Outside && !action.Extracting && !body.enabled);
            Hold(inside);
            Expect("it stays hidden while the controller is still under the skin line", !body.enabled);
            Hold(new Vector3(0.3f, 0.3f, 0.05f));
            Expect("once the controller is out of the body the needle is its again", body.enabled && At(new Vector3(0.3f, 0.3f, 0.05f)) && action.Current == NeedleAction.State.Outside);
        }
        finally
        {
            onDisable.Invoke(action, null);
            foreach (var go in made) Object.DestroyImmediate(go);
        }
        if (failed > 0) throw new System.Exception($"NeedleAction self-check failed ({failed})");
    }
}
