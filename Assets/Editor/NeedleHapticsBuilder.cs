using System.Linq;
using UnityEngine;

/// <summary>
/// Fills NeedleHaptics.layers from the anatomy meshes: how deep the ideal path goes into each tissue it crosses, measured from
/// the entry point. Needs the puncture guide (the path). Re-run it after rebuilding the guide or changing the model.
/// </summary>
public static class NeedleHapticsBuilder
{
    // resistance and pop are qualitative and only drive the controller's buzz: the skin gives way with a click, fat is soft, ligaments
    // are firm and the flavum is the firmest, the epidural space has almost no resistance (the "loss of resistance" that tells the
    // needle is in), and the dura gives one last small pop before the fluid. Tune on the headset.
    // The stylus' force is the user's table (stiffness N/m, viscosity N·s/m, friction N) over the model's widths, as Python/IdealCurve.py
    // (layers_model) has it: see NeedleHaptics.Force. Its supraspinous + interspinous "ligaments" are one tissue, so the interspinous layer
    // continues the supraspinous one's spring. Every tissue whose peak passes Ceiling is scaled down to it, keeping its shape: the ligaments
    // (3.6 N), the flavum (19 N), the epidural space (3.4 N) and the dura (11 N); the skin and the fat stay as they are.
    const float Ceiling = 3; // N

    static NeedleHaptics.Layer Layer(string name, float start, float resistance, float pop, float stiffness, float viscosity, float friction, bool continues = false) =>
        new NeedleHaptics.Layer
        {
            name = name, start = start, resistance = resistance, pop = pop,
            stiffness = stiffness, viscosity = viscosity, friction = friction, continues = continues,
        };

    public static void Fill(NeedleHaptics haptics, ModelTarget target)
    {
        haptics.layers = new NeedleHaptics.Layer[0];
        if (target.pathEntry == null || target.pathTarget == null) { Debug.LogWarning("Needle haptics: no puncture guide yet, build it first."); return; }

        var root = target.transform;
        var entry = root.InverseTransformPoint(target.pathEntry.position);
        var axis = (root.InverseTransformPoint(target.pathTarget.position) - entry).normalized;
        const float behind = 0.05f; // the ray starts this far outside the entry point, clear of the skin
        float[] Crossings(string mesh) =>
            PunctureGuideBuilder.Crossings(PunctureGuideBuilder.Triangles(root, mesh, -1, -1, 1, 1), entry - axis * behind, axis)
                .Select(t => t - behind).OrderBy(t => t).ToArray();

        // A closed mesh is crossed in pairs (in, out). The ligament mesh holds two stretches on some paths (interspinous, then the
        // deep one) and a single merged one on others; then the deep ligament is taken to start half way through it.
        float[] skin = Crossings("Skin"), supraspinous = Crossings("Supraspinous Ligament"), ligaments = Crossings("Ligaments"),
                epidural = Crossings("Epidural Space"), dura = Crossings("Spinal Cord");
        if (skin.Length < 2 || supraspinous.Length < 1 || ligaments.Length < 2 || epidural.Length < 1 || dura.Length < 1)
        {
            Debug.LogError($"Needle haptics: the path does not cross the tissue as expected (skin {skin.Length}, supraspinous {supraspinous.Length}, ligaments {ligaments.Length}, epidural {epidural.Length}, dura {dura.Length}).");
            return;
        }

        haptics.layers = new[]
        {
            Layer("Skin", skin[0], 0.6f, 1.0f, 327, 3.21f, 0.5f),
            Layer("Subcutaneous fat", skin[1], 0.2f, 0, 0, 2.98f, 0.5f),
            Layer("Supraspinous ligament", supraspinous[0], 0.45f, 0.2f, 72, 4.06f, 0.8f),
            Layer("Interspinous ligament", ligaments[0], 0.5f, 0, 72, 4.06f, 0.8f, continues: true),
            Layer("Deep ligament (flavum)", ligaments.Length >= 3 ? ligaments[2] : (ligaments[0] + ligaments[1]) / 2, 0.85f, 0.2f, 920, 4.21f, 1.1f),
            Layer("Epidural space", epidural[0], 0.08f, 0.6f, 0, 2.98f, 0.5f),
            Layer("Dura and fluid", dura[0], 0.03f, 0.7f, 920, 4.21f, 1.1f),
        }.OrderBy(l => l.start).ToArray();
        // Each tissue's width: to where the next tissue starts, the last one to the target (a layer that continues a tissue runs to its end too).
        float end = (root.InverseTransformPoint(target.pathTarget.position) - entry).magnitude;
        for (int i = haptics.layers.Length - 1; i >= 0; i--)
        {
            haptics.layers[i].thickness = end - haptics.layers[i].start;
            if (!haptics.layers[i].continues) end = haptics.layers[i].start;
        }
        for (int i = 0; i < haptics.layers.Length; i++) if (haptics.TissuePeak(i) > Ceiling) haptics.layers[i].scaleTo = Ceiling;
        Debug.Log("Needle haptics layers (mm from the entry point, width, scaled to): " + string.Join(", ", haptics.layers.Select(l => $"{l.name} {l.start * 1000:F1} ({l.thickness * 1000:F1}, {l.scaleTo})")));
    }
}
