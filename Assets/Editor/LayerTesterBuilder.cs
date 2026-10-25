using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the cube of the menu's "Layer tester" page (LayerTester) and gives it, for each layer of the needle's haptics, the material
/// of the tissue in the anatomy. Idempotent: the old cube is replaced. The cube is in world space (not under the model or the rig,
/// which move) and off until the menu page opens it.
/// </summary>
public static class LayerTesterBuilder
{
    public const float Size = 0.1f; // metres: about the length of the needle, so it can be pushed all the way in

    // Which tissue of the anatomy gives each layer's material: (group in ModelTarget.groups, part of it or null for the first).
    // The names are the layers NeedleHapticsBuilder makes; an unknown one is left grey and reported.
    static (string group, string part)? Source(string layer) => layer switch
    {
        "Skin" => ("Skin", null),
        "Subcutaneous fat" => ("Subcutaneous fat", null),
        "Supraspinous ligament" => ("Ligaments", "Supraspinous Ligament"),
        "Interspinous ligament" or "Deep ligament (flavum)" => ("Ligaments", "Ligaments"),
        "Epidural space" => ("Epidural space", null),
        "Dura and fluid" => ("Spinal cord", null),
        "Bone" => ("Bones", "L3"),
        _ => null,
    };

    [MenuItem("Tools/Model Target/Build Layer Tester")]
    static void Menu()
    {
        var aligner = Object.FindFirstObjectByType<LandmarkAligner>();
        Add(aligner);
        EditorSceneManager.MarkSceneDirty(aligner.gameObject.scene);
    }

    public static LayerTester Add(LandmarkAligner aligner)
    {
        var needle = aligner.needle.GetComponent<NeedleAction>();
        var old = aligner.tester != null ? aligner.tester.gameObject : GameObject.Find("LayerTester");
        if (old != null) Object.DestroyImmediate(old);

        var root = new GameObject("LayerTester");
        var tester = root.AddComponent<LayerTester>();
        tester.needle = needle;
        tester.haptics = root.AddComponent<NeedleHaptics>();

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Cube";
        Object.DestroyImmediate(cube.GetComponent<Collider>()); // the tip is tested by LayerTester.Depth, not by physics
        cube.transform.SetParent(root.transform, false);
        cube.transform.localScale = Vector3.one * Size;
        var renderer = cube.GetComponent<Renderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        tester.cube = renderer;

        var plain = renderer.sharedMaterial; // the primitive's own, for a layer with no tissue to copy
        var names = needle.haptics.layers.Select(l => l.name).Append("Bone");
        tester.materials = names.Select(n => MaterialOf(aligner.model, n) ?? plain).ToArray();
        renderer.sharedMaterial = tester.materials[0];

        root.SetActive(false);
        aligner.tester = tester;
        return tester;
    }

    static Material MaterialOf(ModelTarget model, string layer)
    {
        var s = Source(layer);
        var part = s.HasValue ? model.groups.FirstOrDefault(g => g.name == s.Value.group)?.objects.FirstOrDefault(o => s.Value.part == null || o.name == s.Value.part) : null;
        var material = part != null ? part.GetComponentInChildren<Renderer>(true)?.sharedMaterial : null;
        if (material == null) Debug.LogWarning($"Layer tester: no material found for \"{layer}\", the cube stays grey for it.");
        return material;
    }
}
