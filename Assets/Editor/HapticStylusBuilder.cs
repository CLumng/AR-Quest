using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Puts the Touch stylus in the main scene (HapticStylus and its home point on the model) and wires it to the needle and the
/// aligner. Idempotent: the old one, if any, is replaced; the model and the needle are not touched beyond their `stylus` field.
/// The home point is a default: it puts the middle of the needle's stroke (the ideal path, entry to target) in the middle of the stylus'
/// workspace, with the device's axes turned so that pushing the stylus away from the user pushes the needle into the model's back (the
/// user stands at the model's back, as the calibration asks). The user moves it from the headset with the sticks while the stylus is docked.
/// </summary>
public static class HapticStylusBuilder
{
    const string NeedlePath = "Assets/Geometry/spinalNeedle1.fbx"; // the needle's model (metres, hub at the origin, tip at +Z)

    // Where the middle of the stroke should fall in the device's workspace (Unity's device frame, m, the workspace's centre is the origin):
    // a little low, where the arm is comfortable and the inkwell (the lowest point) is not far.
    static readonly Vector3 StrokeMiddle = new(0, -0.02f, 0);
    // The device's +Z (away from the user) is the model's -Z (into its back) and its up is the model's: a turn of 180 degrees about Y.
    static readonly Quaternion Turn = Quaternion.Euler(0, 180, 0);

    [MenuItem("Tools/Haptic Link/Add Stylus To Main Scene")]
    static void Menu()
    {
        var aligner = Object.FindFirstObjectByType<LandmarkAligner>();
        var stylus = Add(aligner);
        EditorSceneManager.MarkSceneDirty(aligner.gameObject.scene);
        Debug.Log($"Stylus home point at {stylus.home.localPosition} (model space), turned {stylus.home.localEulerAngles.y:F0} degrees.");
    }

    public static HapticStylus Add(LandmarkAligner aligner)
    {
        var model = aligner.model;
        var needle = aligner.needle.GetComponent<NeedleAction>();
        for (var old = GameObject.Find("HapticStylus"); old != null; old = GameObject.Find("HapticStylus")) Object.DestroyImmediate(old); // also one whose script is gone
        var oldHome = model.transform.Find("StylusHome");
        if (oldHome != null) Object.DestroyImmediate(oldHome.gameObject);
        var oldCard = aligner.rig.Find("StylusCard"); // the always-on status card of an earlier version: the menu's Settings page replaced it
        if (oldCard != null) Object.DestroyImmediate(oldCard.gameObject);

        foreach (var root in aligner.gameObject.scene.GetRootGameObjects()) // the ghost is inactive, so GameObject.Find would not see it
            if (root.name == "StylusGhost") Object.DestroyImmediate(root);

        var stylus = new GameObject("HapticStylus").AddComponent<HapticStylus>();

        var home = new GameObject("StylusHome").transform;
        home.SetParent(model.transform, false);
        var middle = (model.transform.InverseTransformPoint(model.pathEntry.position) + model.transform.InverseTransformPoint(model.pathTarget.position)) / 2;
        home.localRotation = Turn;
        home.localPosition = middle - Turn * (StrokeMiddle - stylus.dock);

        // The ghost: a see-through copy of the needle's model that NeedleAction puts where the stylus really is in the help mode, while the
        // needle itself is held on the path. Drawn over the tissue like the needle (sorting order, and it shows through).
        var ghost = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(NeedlePath));
        ghost.name = "StylusGhost";
        var ghostMaterial = ModelTargetSceneBuilder.MakeMaterial("Assets/Materials/StylusGhost.mat", "ModelTarget/GuideOverlay");
        ghostMaterial.SetColor("_Color", new Color(0.62f, 0.83f, 1f, 0.55f));
        ghostMaterial.SetFloat("_ZTest", 8); // always: it shows through the virtual skin
        EditorUtility.SetDirty(ghostMaterial);
        foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
        {
            var materials = new Material[r.sharedMaterials.Length];
            System.Array.Fill(materials, ghostMaterial);
            r.sharedMaterials = materials;
            r.sortingOrder = TissueFadeBuilder.OverlayOrder;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }
        ghost.SetActive(false); // NeedleAction shows it
        needle.ghost = ghost.transform;

        stylus.home = home;
        stylus.needle = needle;
        stylus.aligner = aligner;
        needle.stylus = stylus;
        aligner.stylus = stylus;
        EditorUtility.SetDirty(stylus);
        EditorUtility.SetDirty(needle);
        EditorUtility.SetDirty(aligner);
        AddGraph(stylus);
        return stylus;
    }

    [MenuItem("Tools/Haptic Link/Add Force Graph")]
    static void GraphMenu()
    {
        var stylus = Object.FindFirstObjectByType<HapticStylus>();
        AddGraph(stylus);
        EditorSceneManager.MarkSceneDirty(stylus.gameObject.scene);
    }

    /// <summary>The graph of a puncture with the forces on (ForceGraph and its card), at the scene root. Replaces the old one.</summary>
    public static ForceGraph AddGraph(HapticStylus stylus)
    {
        for (var old = GameObject.Find("ForceGraph"); old != null; old = GameObject.Find("ForceGraph")) Object.DestroyImmediate(old);
        var graph = new GameObject("ForceGraph").AddComponent<ForceGraph>();
        (graph.card, graph.plot, graph.tick, graph.legend, graph.summary, graph.saved) = GlassPanelBuilder.AddGraph(graph.transform);
        graph.needle = stylus.needle;
        graph.stylus = stylus;
        stylus.needle.graph = graph;
        EditorUtility.SetDirty(graph);
        EditorUtility.SetDirty(stylus.needle);
        return graph;
    }
}
