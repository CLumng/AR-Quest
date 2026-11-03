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

        // The stylus' force: the user's model (Python/IdealCurve.py): their table (k N/m, rho N·s/m, friction N) over the model's widths
        // (layers_model, mm), supraspinous + interspinous as one tissue.
        bool Is(NeedleHaptics.Layer l, float k, float rho, float mu, float mm) =>
            Mathf.Abs(l.stiffness - k) < 1e-4f && Mathf.Abs(l.viscosity - rho) < 1e-4f && Mathf.Abs(l.friction - mu) < 1e-4f && Mathf.Abs(l.thickness * 1000 - mm) < 0.15f;
        var supra = Named("Supraspinous ligament");
        var dura = Named("Dura and fluid");
        Expect("the table's constants are the user's, the widths are layers_model's (2.7, 28.5, 25.3, 17.7, 4.2, 7 mm): " + string.Join(", ", h.layers.Select(l => (l.thickness * 1000).ToString("F2"))),
            Is(Named("Skin"), 327, 3.21f, 0.5f, 2.7f) && Is(Named("Subcutaneous fat"), 0, 2.98f, 0.5f, 28.5f) && Is(supra, 72, 4.06f, 0.8f, 25.3f) &&
            Is(flavum, 920, 4.21f, 1.1f, 17.7f) && Is(epidural, 0, 2.98f, 0.5f, 4.2f) && Is(dura, 920, 4.21f, 1.1f, 7));
        Expect("each width runs to the next tissue, the dura's to the target",
            Mathf.Abs(supra.start + supra.thickness - flavum.start) < 1e-5f && Mathf.Abs(dura.start + dura.thickness - (model.pathTarget.position - model.pathEntry.position).magnitude) < 1e-4f);
        Expect("the two ligaments are one tissue", Named("Interspinous ligament").continues && !Named("Supraspinous ligament").continues);
        // First the formula as IdealCurve.py has it, not scaled; every tissue above 3 N scaled to 3 N after.
        var scaled = h.layers.Where(l => l.scaleTo > 0).ToArray();
        Expect("every tissue whose peak passes 3 N is scaled to it: the two ligaments, the flavum, the epidural space, the dura; not the skin nor the fat",
            scaled.Length == 5 && scaled.All(l => l.scaleTo == 3) && Named("Skin").scaleTo == 0 && Named("Subcutaneous fat").scaleTo == 0);
        foreach (var l in scaled) l.scaleTo = 0;
        const float frictionToFlavum = 0.5f + 0.5f + 0.8f + 1.1f, frictionAll = frictionToFlavum + 0.5f + 1.1f;
        Expect($"the strongest steady force is the flavum's spring over its width plus the friction so far ({h.Peak:F2} N; IdealCurve.py: 19.18)",
            Mathf.Abs(h.Peak - (920 * flavum.thickness + frictionToFlavum)) < 1e-4f && Mathf.Abs(h.Peak - 19.18f) < 0.15f);
        Expect("2 mm into the skin, at rest, only the spring pushes back: k·dx = 327 · 0.002",
            Mathf.Abs(h.Force(h.layers[0].start + 0.002f, 0) + 327 * 0.002f) < 1e-4f);
        Expect("the flavum's spring is not capped: 10 mm in at rest is 920 · 0.01 = 9.2 N", Mathf.Abs(h.Force(flavum.start + 0.01f, 0) + 920 * 0.01f) < 1e-3f);
        Expect("the epidural space has no spring: the damper and the friction of every tissue so far, 2.98 · 0.005 + 3.4 = 3.415 N at 5 mm/s",
            Mathf.Abs(h.Force(epidural.start + 0.001f, 0.005f) + (2.98f * 0.005f + frictionToFlavum + 0.5f)) < 1e-4f);
        Expect("the interspinous ligament goes on stretching the supraspinous spring from where that one began",
            Mathf.Abs(h.Force(Named("Interspinous ligament").start + 0.001f, 0) + 72 * (Named("Interspinous ligament").start + 0.001f - supra.start)) < 1e-4f);
        // Against IdealCurve.py's "Modeled Needle Force" at the end of each tissue (it subtracts rho·v, this adds it: < 0.03 N apart at 3 mm/s).
        float End(NeedleHaptics.Layer l) => -h.Force(l.start + l.thickness - 1e-5f, h.frictionSpeed);
        Expect($"at the end of each tissue, moving in, it is IdealCurve.py's curve: {End(Named("Skin")):F2} / 1.38, {End(Named("Subcutaneous fat")):F2} / 1.00, {End(Named("Interspinous ligament")):F2} / 3.62, {End(flavum):F2} / 19.18, {End(epidural):F2} / 3.40, {End(dura):F2} / 10.93 N",
            Mathf.Abs(End(Named("Skin")) - 1.38f) < 0.1f && Mathf.Abs(End(Named("Subcutaneous fat")) - 1.0f) < 0.1f && Mathf.Abs(End(Named("Interspinous ligament")) - 3.62f) < 0.1f &&
            Mathf.Abs(End(flavum) - 19.18f) < 0.15f && Mathf.Abs(End(epidural) - 3.4f) < 0.1f && Mathf.Abs(End(dura) - 10.93f) < 0.1f && Mathf.Abs(End(dura) - (920 * (dura.thickness - 1e-5f) + 4.21f * h.frictionSpeed + frictionAll)) < 1e-3f);
        // The ideal curve (what the force graph draws in white) is IdealCurve.py's at its even speed: friction whole, a function of depth alone.
        float IdealEnd(NeedleHaptics.Layer l) => -h.Ideal(l.start + l.thickness - 1e-5f);
        Expect($"the ideal curve ends each tissue where IdealCurve.py does: flavum {IdealEnd(flavum):F2} / 19.18, dura {IdealEnd(dura):F2} / 10.93, epidural {IdealEnd(epidural):F2} / 3.40 N",
            Mathf.Abs(IdealEnd(flavum) - 19.18f) < 0.15f && Mathf.Abs(IdealEnd(dura) - 10.93f) < 0.1f && Mathf.Abs(IdealEnd(epidural) - 3.4f) < 0.1f);
        Expect($"it has the friction whole where the hand's real speed, at 1 mm/s, has a third of it: epidural {-h.Ideal(epidural.start + 0.001f):F3} vs {-h.Force(epidural.start + 0.001f, 0.001f):F3} N",
            Mathf.Abs(-h.Ideal(epidural.start + 0.001f) - (frictionToFlavum + 0.5f)) < 0.01f && -h.Force(epidural.start + 0.001f, 0.001f) < frictionToFlavum / 2);
        foreach (var l in scaled) l.scaleTo = 3;
        Expect($"scaled, the ideal curve peaks at 3 N too (flavum {-h.Ideal(flavum.start + flavum.thickness - 1e-5f):F3})", Mathf.Abs(-h.Ideal(flavum.start + flavum.thickness - 1e-5f) - 3) < 0.015f);
        var inter = Named("Interspinous ligament");
        float flavumPeak = h.TissuePeak(System.Array.IndexOf(h.layers, flavum)), duraPeak = h.TissuePeak(System.Array.IndexOf(h.layers, dura));
        Expect($"scaled, every one of them peaks at 3 N, moving in: ligaments {End(inter):F3}, flavum {End(flavum):F3}, epidural {End(epidural):F3}, dura {End(dura):F3} (the formula's 3.62, {flavumPeak:F2}, 3.40, {duraPeak:F2})",
            Mathf.Abs(End(inter) - 3) < 0.015f && Mathf.Abs(End(flavum) - 3) < 0.015f && Mathf.Abs(End(epidural) - 3) < 0.015f && Mathf.Abs(End(dura) - 3) < 0.015f &&
            Mathf.Abs(flavumPeak - 19.18f) < 0.15f && Mathf.Abs(duraPeak - 10.93f) < 0.1f);
        Expect("the two ligament layers are one tissue scaled once: no step where the interspinous begins",
            Mathf.Abs(h.Force(inter.start - 1e-6f, 0) - h.Force(inter.start + 1e-6f, 0)) < 1e-3f);
        Expect("their shape is kept: half way into the flavum at rest is half its scaled spring, and the dura starts at its friction, scaled",
            Mathf.Abs(h.Force(flavum.start + flavum.thickness / 2, 0) + 920 * flavum.thickness / 2 * 3 / flavumPeak) < 1e-4f &&
            Mathf.Abs(h.Force(dura.start + 1e-6f, h.frictionSpeed) + (920 * 1e-6f + 4.21f * h.frictionSpeed + frictionAll) * 3 / duraPeak) < 1e-4f);
        Expect($"the skin and the fat are the formula's: {End(Named("Skin")):F2} / 1.38, {End(Named("Subcutaneous fat")):F2} / 1.00 N",
            Mathf.Abs(End(Named("Skin")) - 1.38f) < 0.1f && Mathf.Abs(End(Named("Subcutaneous fat")) - 1.0f) < 0.1f);
        Expect($"so the table's peak is 3 N and the stylus gets the force 1:1 ({h.Peak:F2} N)", Mathf.Abs(h.Peak - 3) < 1e-4f);

        typeof(NeedleHaptics).GetField("_next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(h, float.MinValue); // a buzz may go out now
        h.Hit(flavum.start + 0.001f);
        Expect("a bone buzzes at 95 %", Mathf.Abs(h.LastSent - 0.95f) < 1e-5f && h.boneAmplitude == 0.95f);
        h.Release();
        if (failed > 0) throw new System.Exception($"NeedleHaptics self-check failed ({failed})");
    }
}
