using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Checks the force profile built from the scene's anatomy: layer order, which tissue is hardest, the pops, the speed scaling.</summary>
public static class NeedleHapticsCheck
{
    [MenuItem("Tools/Model Target/Self-check Needle Haptics")]
    public static void Run()
    {
        var h = Object.FindFirstObjectByType<NeedleAction>(FindObjectsInactive.Include).haptics; // the needle's, not the layer tester's
        var model = Object.FindFirstObjectByType<ModelTarget>();
        int failed = 0;
        void Expect(string what, bool ok)
        {
            Debug.Log($"NeedleHaptics: {what} -> {(ok ? "PASS" : "FAIL")}");
            if (!ok) failed++;
        }
        NeedleHaptics.Layer Named(string name) => h.layers.First(l => l.name == name);

        Expect("seven layers, entered in order of depth", h.layers.Length == 7 && h.layers.Zip(h.layers.Skip(1), (a, b) => a.start < b.start).All(x => x));
        Expect("the skin comes first and clicks when it gives way", h.layers[0].name == "Skin" && h.layers[0].pop >= 0.8f);
        Expect("the flavum is the firmest tissue", h.layers.All(l => l == Named("Deep ligament (flavum)") || l.resistance < Named("Deep ligament (flavum)").resistance));
        Expect("fat is softer than the ligaments", Named("Subcutaneous fat").resistance < Named("Supraspinous ligament").resistance);
        Expect("the epidural space is nearly free and pops when entered (loss of resistance)",
            Named("Epidural space").resistance < 0.15f && Named("Epidural space").pop > 0.3f);
        Expect("the dura pops", Named("Dura and fluid").pop > 0.3f);
        Expect("every layer starts inside the path to the target",
            h.layers.All(l => l.start > 0 && l.start < (model.pathTarget.position - model.pathEntry.position).magnitude));

        var flavum = Named("Deep ligament (flavum)");
        float inside = flavum.start + 0.001f;
        Expect("nothing is felt before the skin", h.Amplitude(h.layers[0].start - 0.0005f, 0.05f) == 0);
        Expect("moving fast in the flavum feels its full resistance", Mathf.Abs(h.Amplitude(inside, h.fullSpeed * 2) - flavum.resistance) < 1e-5f);
        Expect("held still it only feels a share of it", Mathf.Abs(h.Amplitude(inside, 0) - flavum.resistance * h.restShare) < 1e-5f);
        Expect("pulling back drags like pushing", Mathf.Abs(h.Amplitude(inside, -h.fullSpeed) - h.Amplitude(inside, h.fullSpeed)) < 1e-5f);

        var epidural = Named("Epidural space");
        Expect("going deeper through the epidural space gives its pop", h.Pop(epidural.start - 0.001f, epidural.start + 0.001f) == epidural.pop);
        Expect("coming back out gives no pop", h.Pop(epidural.start + 0.001f, epidural.start - 0.001f) == 0);
        Expect("the first frame in counts the skin as just entered", h.Pop(-1, h.layers[0].start + 0.0005f) == h.layers[0].pop);

        // The stylus' force: the user's table (k N/m, rho N·s/m, friction N, thickness mm), supraspinous + interspinous as one tissue of 30 mm.
        bool Is(NeedleHaptics.Layer l, float k, float rho, float mu, float mm) =>
            Mathf.Abs(l.stiffness - k) < 1e-4f && Mathf.Abs(l.viscosity - rho) < 1e-4f && Mathf.Abs(l.friction - mu) < 1e-4f && Mathf.Abs(l.thickness * 1000 - mm) < 1e-4f;
        Expect("the table's constants are the user's",
            Is(Named("Skin"), 327, 3.21f, 0.5f, 3) && Is(Named("Subcutaneous fat"), 0, 2.98f, 0.5f, 6) && Is(Named("Supraspinous ligament"), 72, 4.06f, 0.8f, 30) &&
            Is(Named("Interspinous ligament"), 72, 4.06f, 0.8f, 30) && Is(flavum, 920, 4.21f, 1.1f, 3) && Is(epidural, 0, 2.98f, 0.5f, 6) && Is(Named("Dura and fluid"), 920, 4.21f, 1.1f, 6));
        Expect("the two ligaments are one tissue", Named("Interspinous ligament").continues && !Named("Supraspinous ligament").continues);
        Expect("the strongest steady force is the dura's spring at its stretch plus its friction (6.62 N)", Mathf.Abs(h.Peak - (920 * 0.006f + 1.1f)) < 1e-4f);
        var supra = Named("Supraspinous ligament");
        Expect("2 mm into the skin, at rest, only the spring pushes back: k·dx = 327 · 0.002",
            Mathf.Abs(h.Force(h.layers[0].start + 0.002f, 0) + 327 * 0.002f) < 1e-4f);
        Expect("the flavum's spring stops stretching at its 3 mm: 920 · 0.003 = 2.76 N, however deep", Mathf.Abs(h.Force(flavum.start + 0.01f, 0) + 920 * 0.003f) < 1e-4f);
        Expect("the epidural space has no spring: only the damper and the friction, 2.98 · 0.005 + 0.5 = 0.515 N at 5 mm/s",
            Mathf.Abs(h.Force(epidural.start + 0.001f, 0.005f) + (2.98f * 0.005f + 0.5f)) < 1e-4f);
        Expect("the interspinous ligament goes on stretching the supraspinous spring from where that one began",
            Mathf.Abs(h.Force(Named("Interspinous ligament").start + 0.001f, 0) + 72 * (Named("Interspinous ligament").start + 0.001f - supra.start)) < 1e-4f);

        typeof(NeedleHaptics).GetField("_next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(h, float.MinValue); // a buzz may go out now
        h.Hit(flavum.start + 0.001f);
        Expect("a bone buzzes at 95 %", Mathf.Abs(h.LastSent - 0.95f) < 1e-5f && h.boneAmplitude == 0.95f);
        h.Release();
        if (failed > 0) throw new System.Exception($"NeedleHaptics self-check failed ({failed})");
    }
}
