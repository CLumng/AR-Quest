using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Checks the transparency slider on the open scene: as designed at zero, blending in between, gone at one, bones untouched.</summary>
public static class TissueFadeCheck
{
    [MenuItem("Tools/Model Target/Self-check Tissue Fade")]
    public static void Run()
    {
        var fade = Object.FindFirstObjectByType<TissueFade>();
        var model = Object.FindFirstObjectByType<ModelTarget>();
        int failed = 0;
        void Expect(string what, bool ok)
        {
            Debug.Log($"TissueFade: {what} -> {(ok ? "PASS" : "FAIL")}");
            if (!ok) failed++;
        }
        float? Alpha(Renderer r)
        {
            var block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            return block.HasFloat("_Transparency") ? block.GetFloat("_Transparency") : null;
        }
        float Close(float? a, float b) => a.HasValue ? Mathf.Abs(a.Value - b) : float.MaxValue;

        try
        {
            fade.Set(0);
            Expect("at 0 every renderer has its own material and no override",
                fade.entries.All(e => e.renderer.sharedMaterial == e.solid && Alpha(e.renderer) == null && e.renderer.enabled));

            fade.Set(0.5f);
            Expect("at 0.5 the alpha is the material's own opacity halved",
                fade.entries.All(e => Close(Alpha(e.renderer), e.opacity * 0.5f) < 1e-5f && e.renderer.enabled));
            Expect("at 0.5 materials on an opaque shader are swapped for the blending copy",
                fade.entries.All(e => e.renderer.sharedMaterial == (e.fade != null ? e.fade : e.solid)) && fade.entries.Any(e => e.fade != null));
            Expect("the blending copies are really transparent", fade.entries.Where(e => e.fade != null).All(e => e.fade.renderQueue >= 3000));

            fade.Set(1);
            Expect("at 1 nothing is drawn", fade.entries.All(e => !e.renderer.enabled));

            var bones = model.groups.First(g => g.name == "Bones").objects.SelectMany(o => o.GetComponentsInChildren<Renderer>(true)).ToArray();
            Expect("bones never take part", bones.Length > 0 && bones.All(r => r.enabled && Alpha(r) == null && fade.entries.All(e => e.renderer != r)));

            fade.Set(0.25f);
            Expect("inner layers are drawn before outer ones",
                model.groups.Where(g => g.fades).Select(g => g.objects[0].GetComponentInChildren<Renderer>(true).sortingOrder).SequenceEqual(
                    model.groups.Where(g => g.fades).Select(g => g.objects[0].GetComponentInChildren<Renderer>(true).sortingOrder).OrderByDescending(x => x)));
        }
        finally { fade.Set(0); }
        if (failed > 0) throw new System.Exception($"TissueFade self-check failed ({failed})");
    }
}
