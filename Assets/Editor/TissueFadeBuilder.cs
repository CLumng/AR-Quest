using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Adds TissueFade to the ModelTarget: one entry per renderer of the groups that fade (everything but the bones), and a
/// blending copy for each material on an opaque shader. Re-run it after changing the groups or the tissue materials.
/// </summary>
public static class TissueFadeBuilder
{
    // The tissue shaders have an opaque and a transparent version with the same properties (_Color, _Transparency).
    const string TransparentShader = "Shader Graphs/TissueShader";

    // Sorting order beats the material's render queue, and the tissue layers use 1..7: everything that must show over the
    // tissue (guide, needle, messages) takes this order or higher, or the tissue is drawn over it like an underlay.
    public const int OverlayOrder = 100;

    [MenuItem("Tools/Model Target/Build Tissue Fade")]
    static void Rebuild()
    {
        var target = Object.FindFirstObjectByType<ModelTarget>();
        // Scenes built before the flag existed: take it from the builder's table.
        foreach (var g in target.groups) g.fades = ModelTargetSceneBuilder.Groups.Any(x => x.name == g.name && x.fades);
        var fade = Add(target);
        var aligner = Object.FindFirstObjectByType<LandmarkAligner>();
        if (aligner != null) aligner.fade = fade;
        EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
    }

    public static TissueFade Add(ModelTarget target)
    {
        var old = target.GetComponent<TissueFade>();
        if (old != null)
        {
            old.Set(0); // back to the prefab's own materials before they are read again
            Object.DestroyImmediate(old);
        }
        var fade = target.gameObject.AddComponent<TissueFade>();

        // Groups run outermost first. With depth writes on, translucent layers only blend right if they are drawn from the
        // inside out, so the outer ones get a higher sorting order (the guide area and path are on later render queues).
        var layers = target.groups.Where(g => g.fades).ToArray();
        var entries = new List<TissueFade.Entry>();
        for (int i = 0; i < layers.Length; i++)
            foreach (var go in layers[i].objects)
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    r.sortingOrder = layers.Length - i;
                    var solid = r.sharedMaterial;
                    entries.Add(new TissueFade.Entry { renderer = r, solid = solid, fade = BlendingCopy(solid), opacity = solid.GetFloat("_Transparency") });
                }
        fade.entries = entries.ToArray();
        Debug.Log($"Tissue fade: {entries.Count} renderers in {layers.Length} groups, {entries.Count(e => e.fade != null)} with a blending copy");
        return fade;
    }

    // Opaque materials cannot blend: a copy of the material on the transparent shader, used while the slider is above zero.
    static Material BlendingCopy(Material solid)
    {
        if (solid.renderQueue >= (int)RenderQueue.AlphaTest) return null; // already blends
        var copy = ModelTargetSceneBuilder.MakeMaterial($"Assets/Materials/Fade_{solid.name}.mat", TransparentShader);
        copy.SetColor("_Color", solid.GetColor("_Color"));
        copy.SetFloat("_Transparency", 1);
        EditorUtility.SetDirty(copy);
        return copy;
    }
}
