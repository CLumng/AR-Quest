using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Meta;

/// <summary>Rebuilds Assets/Scenes/ModelTargetTest.unity and enables the OpenXR features it needs on Android.</summary>
public static class ModelTargetSceneBuilder
{
    const string ScenePath = "Assets/Scenes/ModelTargetTest.unity";
    const string ModelPath = "Assets/Geometry/Anatomy.prefab";
    const string NeedlePath = "Assets/Geometry/spinalNeedle1.fbx"; // metres, hub at the origin, tip at +Z (10.25 cm)

    // Landmarks are the model's extremes, found on the mesh as the extreme point along dir (model space: +Y head, +Z back).
    // The model is cut flat at both ends, so the wings and the top are the bottom back corners and the middle of the top face.
    // The midline one is the L5 spinous tip, not the sacrum's bottom edge: that edge sits on the table and is hard to reach.
    // Listed left to right as the user sees them from behind, then the top. They are plain child transforms of ModelTarget
    // afterwards, so they can be nudged in the scene.
    static readonly (string title, string hint, string mesh, Vector3 dir)[] Landmarks =
    {
        ("Left wing, bottom back corner", "The left wing of the pelvis, on its flat bottom: where the bottom edge ends at the back, next to the sacrum.", "Left hip", new Vector3(0, -1, 1)),
        ("L5 spinous process", "The tip of the bony ridge at the back of vertebra L5, the first vertebra above the sacrum.", "L5", Vector3.forward),
        ("Right wing, bottom back corner", "The right wing of the pelvis, on its flat bottom: where the bottom edge ends at the back, next to the sacrum.", "Right hip", new Vector3(0, -1, 1)),
        ("Top of the spine", "The middle of the flat face on top of the highest vertebra.", "T11", Vector3.up),
    };

    // Parts the user can show or hide from the menu, outermost first. The physical model is bones and discs,
    // so those two are the only ones shown while aligning. The prefab ships with the bones off, hence a whole group toggles at once.
    // fades = follows the transparency slider (TissueFade): everything but the bones. The order also sets how the layers blend.
    internal static readonly (string name, string[] parts, bool showWhileAligning, bool fades)[] Groups =
    {
        ("Skin", new[] { "Skin" }, false, true),
        ("Subcutaneous fat", new[] { "SubcutaneousGroup" }, false, true),
        ("Deep back muscles", new[] { "Right deep back muscle", "Left deep back muscle" }, false, true),
        ("Ligaments", new[] { "Ligaments", "Supraspinous Ligament" }, false, true),
        ("Epidural space", new[] { "Epidural Space" }, false, true),
        ("Spinal cord", new[] { "Spinal Cord" }, false, true),
        ("Intervertebral discs", new[] { "Intervertebral Discs" }, true, true),
        ("Bones", new[] { "Sacrum", "S1", "L5", "L4", "L3", "L2", "L1", "T12", "T11", "Left hip", "Right hip" }, true, false),
    };

    [MenuItem("Tools/Model Target/Build Test Scene")]
    public static void Build()
    {
        var cam = NewRigScene();
        var cameraManager = cam.GetComponent<ARCameraManager>();

        var marker = MakeMaterial("Assets/Materials/LandmarkMarker.mat", "Universal Render Pipeline/Unlit");
        var target = BuildModel(marker);
        PunctureGuideBuilder.Add(target);
        TissueFadeBuilder.Add(target);
        BuildAligner(target, cam.transform, marker);
        BuildProbe(cam.transform, cameraManager);

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(s => s.path == ScenePath);
        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"Built {ScenePath} (first in Build Settings)");
    }

    /// <summary>
    /// An empty scene with the Quest passthrough rig: AR session + XR Origin with an ARCameraManager on the camera (clear background so the
    /// passthrough shows), and a light. Also enables the OpenXR features it needs on Android. Returns the camera.
    /// </summary>
    internal static Camera NewRigScene()
    {
        EnableOpenXrFeatures();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Standard AR Foundation rig: session + XR Origin with an ARCameraManager on the camera.
        EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
        EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");
        var cam = Camera.main;
        cam.clearFlags = CameraClearFlags.SolidColor; // passthrough shows through alpha 0
        cam.backgroundColor = Color.clear;
        cam.nearClipPlane = 0.05f;
        Object.DestroyImmediate(cam.GetComponent<ARCameraBackground>()); // no-op on Quest, and needs a URP renderer feature

        var light = new GameObject("Directional Light").AddComponent<Light>();
        light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(50, -30, 0);
        return cam;
    }

    static ModelTarget BuildModel(Material markerMat)
    {
        var root = new GameObject("ModelTarget");
        var target = root.AddComponent<ModelTarget>();
        var instance = ((GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath), root.transform)).transform;

        target.landmarks = Landmarks.Select(l =>
        {
            var point = new GameObject("Landmark " + l.title).transform;
            point.SetParent(root.transform, false);
            point.localPosition = ExtremePoint(root.transform, l.mesh, l.dir);
            return new ModelTarget.Landmark { title = l.title, hint = l.hint, point = point, marker = Sphere(point, markerMat) };
        }).ToArray();

        target.groups = Groups.Select(g => new ModelTarget.Group
        {
            name = g.name,
            objects = Find(instance, g.parts),
            showWhileAligning = g.showWhileAligning,
            fades = g.fades,
        }).ToArray();
        return target;
    }

    // Measures the landmarks on the model again. Run it after the model's scale or position changes in the scene: the landmarks are
    // plain child transforms and do not follow (the puncture guide has its own menu item, which calls this too).
    [MenuItem("Tools/Model Target/Refit Landmarks")]
    static void RefitLandmarks()
    {
        var target = Object.FindFirstObjectByType<ModelTarget>();
        Refit(target);
        EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
    }

    internal static void Refit(ModelTarget target)
    {
        for (int i = 0; i < Landmarks.Length; i++)
        {
            var l = target.landmarks[i];
            l.title = Landmarks[i].title;
            l.hint = Landmarks[i].hint;
            l.point.name = "Landmark " + l.title;
            l.point.localPosition = ExtremePoint(target.transform, Landmarks[i].mesh, Landmarks[i].dir);
        }
    }

    static LandmarkAligner BuildAligner(ModelTarget target, Transform cam, Material markerMat)
    {
        var rig = new GameObject("AlignmentRig").transform;

        var (panel, view) = GlassPanelBuilder.Build(rig); // a Canvas, hidden as a whole while the model is locked

        var anchor = new GameObject("GuideAnchor").transform;
        anchor.SetParent(rig, false);
        anchor.localPosition = new Vector3(0.73f, -0.16f, 0); // model pivot is at its bottom centre; the guide is drawn 1.5x
        anchor.localRotation = Quaternion.Euler(0, 180, 0);  // back of the model faces the user

        var tip = Sphere(null, markerMat);
        tip.name = "ControllerTip";
        tip.transform.localScale = Vector3.one * 0.012f;
        tip.sharedMaterial = new Material(markerMat) { color = Color.white };

        var needle = new GameObject("ControllerNeedle").transform; // LandmarkAligner moves it to the right controller
        PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(NeedlePath), needle);
        AddNeedleAction(needle, target);
        needle.gameObject.SetActive(false); // appears once the model is locked

        var aligner = new GameObject("LandmarkAligner").AddComponent<LandmarkAligner>();
        aligner.model = target;
        aligner.trackingSpace = cam.parent; // Camera Offset: controller poses live in this space
        aligner.rig = rig;
        aligner.panel = panel;
        aligner.guideAnchor = anchor;
        aligner.tip = tip;
        aligner.needle = needle;
        aligner.fade = target.GetComponent<TissueFade>();
        aligner.view = view;
        LayerTesterBuilder.Add(aligner);
        HapticStylusBuilder.Add(aligner);
        return aligner;
    }

    // The needle's behaviour in the body, with what it needs: a message (floats near the entry point, placed by NeedleAction), the
    // tissue's force, the colliders and the fluid. Safe to run again: it only adds what is missing and refreshes the rest.
    public static void AddNeedleAction(Transform needle, ModelTarget target)
    {
        var action = needle.GetComponent<NeedleAction>();
        if (action == null) action = needle.gameObject.AddComponent<NeedleAction>();
        action.model = target;

        GlassPanelBuilder.AddMessage(action);

        // The needle goes in anywhere now, so the dotted rail along the ideal path of earlier scenes has no use.
        var rail = needle.Find("NeedleRail");
        if (rail != null) Object.DestroyImmediate(rail.gameObject);

        // The force of the tissue as controller vibration, from the layers the path crosses in this model.
        var haptics = needle.GetComponent<NeedleHaptics>();
        if (haptics == null) haptics = needle.gameObject.AddComponent<NeedleHaptics>();
        NeedleHapticsBuilder.Fill(haptics, target);
        action.haptics = haptics;
        AddColliders(action, target);
        AddFluid(action, needle);
        AddStylet(action, needle);

        // Over the tissue, not under it (see TissueFadeBuilder.OverlayOrder). The message is a Canvas with its own order, above these.
        foreach (var r in needle.GetComponentsInChildren<Renderer>(true)) r.sortingOrder = TissueFadeBuilder.OverlayOrder;
    }

    // The bones (obstacles) and the skin (for the depth) as colliders of their own. The model's own objects cannot carry them: the
    // bones are switched off most of the time (the prefab ships them off, the user hides them) and a disabled collider is not felt.
    // The holder stays off; NeedleAction turns it on while the needle is shown, so a moving model costs no physics updates.
    static void AddColliders(NeedleAction action, ModelTarget target)
    {
        var root = target.transform;
        var old = root.Find("NeedleColliders");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var holder = new GameObject("NeedleColliders").transform;
        holder.SetParent(root, false);

        MeshCollider Copy(MeshFilter mf)
        {
            var go = new GameObject(mf.name + " collider");
            go.transform.SetParent(holder, false);
            go.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
            go.transform.localScale = mf.transform.lossyScale; // the model's root is at scale 1 when the scene is built
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mf.sharedMesh;
            return collider;
        }
        MeshFilter[] Meshes(string group) =>
            target.groups.First(g => g.name == group).objects.SelectMany(o => o.GetComponentsInChildren<MeshFilter>(true)).ToArray();

        action.bones = Meshes("Bones").Select(Copy).ToArray();
        action.skin = Copy(Meshes("Skin")[0]);
        action.colliders = holder.gameObject;
        holder.gameObject.SetActive(false);
    }

    // The fluid that drips from the hub while extracting: an opening on the needle (just behind the hub, which ends at z = 3 mm in
    // spinalNeedle1.fbx) and a pool of drops on a root of its own, so a drop keeps falling when the needle goes away.
    static void AddFluid(NeedleAction action, Transform needle)
    {
        var source = needle.Find("FluidSource");
        if (source == null) source = new GameObject("FluidSource").transform;
        source.SetParent(needle, false);
        source.localPosition = new Vector3(0, 0, 0.002f);

        if (action.flow != null) Object.DestroyImmediate(action.flow.gameObject);
        var holder = new GameObject("FluidDrops");
        var flow = holder.AddComponent<FluidFlow>();
        flow.source = source;
        var mat = PunctureGuideBuilder.GuideMaterial("Assets/Materials/FluidDrop.mat", new Color(0.7f, 0.88f, 1, 0.9f), UnityEngine.Rendering.CompareFunction.LessEqual, 3020);
        flow.drops = new Transform[12];
        for (int i = 0; i < flow.drops.Length; i++)
        {
            var drop = Sphere(holder.transform, mat);
            drop.name = "Drop " + i;
            drop.transform.localScale = Vector3.one * 0.004f;
            drop.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            drop.receiveShadows = false;
            drop.sortingOrder = TissueFadeBuilder.OverlayOrder;
            drop.gameObject.SetActive(false);
            flow.drops[i] = drop.transform;
        }
        action.flow = flow;
    }

    // The stylet: the needle model's "Head" (the cap at the back of the hub) gets a thin rod that runs forward into the hub, so that
    // when NeedleAction slides the head out it pulls a stylet out of the hub instead of leaving a cube floating behind it.
    static void AddStylet(NeedleAction action, Transform needle)
    {
        var head = needle.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Head");
        if (head == null)
        {
            Debug.LogWarning("The needle model has no part called Head: the stylet will not slide out.");
            return;
        }
        action.head = head;

        var old = head.Find("Stylet");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var rod = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Object.DestroyImmediate(rod.GetComponent<Collider>());
        rod.name = "Stylet";
        rod.transform.SetParent(head, false);
        rod.transform.SetPositionAndRotation(head.position + needle.forward * 0.05f, needle.rotation * Quaternion.Euler(90, 0, 0)); // the cylinder's axis is Y
        rod.transform.localScale = new Vector3(0.0012f, 0.05f, 0.0012f); // 1.2 mm thick and 10 cm long (the cylinder is 2 units tall); the head is at scale 1
        var mat = MakeMaterial("Assets/Materials/Stylet.mat", "Universal Render Pipeline/Lit");
        mat.SetColor("_BaseColor", new Color(0.8f, 0.82f, 0.86f));
        mat.SetFloat("_Metallic", 0.8f);
        mat.SetFloat("_Smoothness", 0.6f);
        EditorUtility.SetDirty(mat);
        var renderer = rod.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    // Camera feed preview from the first test; hidden here so it does not clutter the instructions.
    static void BuildProbe(Transform head, ARCameraManager cameraManager)
    {
        var mat = MakeMaterial("Assets/Materials/FeedPreview.mat", "Universal Render Pipeline/Unlit");

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "FeedPreview";
        Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.GetComponent<Renderer>().sharedMaterial = mat;
        quad.transform.SetParent(head, false);
        quad.transform.localPosition = new Vector3(0.32f, -0.25f, 0.7f);
        quad.transform.localScale = new Vector3(0.26f, 0.2f, 1); // Show() fixes the width from the image aspect

        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var hud = new GameObject("FeedHud").AddComponent<TextMesh>();
        hud.transform.SetParent(head, false);
        hud.transform.localPosition = new Vector3(-0.45f, -0.15f, 0.7f);
        hud.font = font;
        hud.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        hud.anchor = TextAnchor.UpperLeft;
        hud.fontSize = 64;
        hud.characterSize = 0.0035f;

        var probe = new GameObject("FeedProbe").AddComponent<CameraFeedProbe>();
        probe.transform.SetParent(head, false);
        probe.cameraManager = cameraManager;
        probe.preview = quad.GetComponent<Renderer>();
        probe.hud = hud;

        quad.SetActive(false);
        hud.gameObject.SetActive(false);
        probe.gameObject.SetActive(false);
    }

    static GameObject[] Find(Transform parent, string[] names)
    {
        var found = names.Select(n => parent.Find(n)?.gameObject).Where(g => g != null).ToArray();
        if (found.Length != names.Length) Debug.LogWarning($"Some of {string.Join(", ", names)} were not found in {ModelPath}");
        return found;
    }

    internal static Vector3 ExtremePoint(Transform root, string meshName, Vector3 dir)
    {
        var mf = root.GetComponentsInChildren<MeshFilter>(true).First(m => m.name == meshName);
        var pts = mf.sharedMesh.vertices.Select(v => root.InverseTransformPoint(mf.transform.TransformPoint(v))).ToArray();
        dir.Normalize();
        float max = pts.Max(p => Vector3.Dot(p, dir));
        var patch = pts.Where(p => Vector3.Dot(p, dir) >= max - 0.003f).ToArray(); // centre of the 3 mm tip patch
        return patch.Aggregate(Vector3.zero, (a, p) => a + p) / patch.Length;
    }

    static Renderer Sphere(Transform parent, Material mat)
    {
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Object.DestroyImmediate(s.GetComponent<Collider>());
        s.transform.SetParent(parent, false);
        s.GetComponent<Renderer>().sharedMaterial = mat;
        return s.GetComponent<Renderer>();
    }

    internal static Material MakeMaterial(string path, string shader)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(mat, path);
        }
        return mat;
    }

    static void EnableOpenXrFeatures()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        var session = settings.GetFeature<ARSessionFeature>();
        var camera = settings.GetFeature<ARCameraFeature>();
        session.enabled = camera.enabled = true;
        camera.cameraImageSupportEnabled = true; // CPU images, needed by any tracker
        EditorUtility.SetDirty(session);
        EditorUtility.SetDirty(camera);
        AssetDatabase.SaveAssets();
    }
}
