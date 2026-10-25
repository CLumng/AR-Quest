using System;
using UnityEngine;

/// <summary>
/// Transparency of the soft tissue (every ModelTarget group that is not bone), driven by the menu slider.
/// The tissue shaders take their alpha from _Transparency (1 = opaque), so the slider only lowers that, per renderer,
/// without touching the project's materials. Materials on an opaque shader (cord, discs) cannot blend: while the slider
/// is above zero they are swapped for a copy on the transparent sibling of the shader; at zero everything is as designed.
/// </summary>
public class TissueFade : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public Renderer renderer;
        public Material solid;  // the material as the prefab has it
        public Material fade;   // blending copy; null when the solid material already blends
        public float opacity;   // the material's own _Transparency (the fat is 0.6 by design)
    }

    static readonly int Alpha = Shader.PropertyToID("_Transparency");

    public Entry[] entries;
    public float transparency; // 0 = as designed, 1 = invisible

    MaterialPropertyBlock _block;

    void Awake() { Set(transparency); }

    public void Set(float value)
    {
        transparency = Mathf.Clamp01(value);
        _block ??= new MaterialPropertyBlock();
        bool faded = transparency > 0.001f;
        foreach (var e in entries)
        {
            if (e.renderer == null) continue;
            float alpha = e.opacity * (1 - transparency);
            e.renderer.sharedMaterial = faded && e.fade != null ? e.fade : e.solid;
            if (faded) { _block.SetFloat(Alpha, alpha); e.renderer.SetPropertyBlock(_block); }
            else e.renderer.SetPropertyBlock(null);
            e.renderer.enabled = alpha > 0.003f; // fully faded: not drawn at all
        }
    }
}
