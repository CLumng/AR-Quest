using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds Assets/Scenes/HapticLinkTest.unity: the Quest passthrough rig of the other test scene plus the Touch stylus' workspace
/// (HapticLinkTest) and its status card. Puts the scene first in Build Settings (that is the one the headset starts), allows the
/// Internet permission on Android (a TCP socket needs it, even to localhost), and is safe to run again: it rebuilds the scene.
/// </summary>
public static class HapticLinkSceneBuilder
{
    const string ScenePath = "Assets/Scenes/HapticLinkTest.unity";

    [MenuItem("Tools/Haptic Link/Build Test Scene")]
    public static void Build()
    {
        var cam = ModelTargetSceneBuilder.NewRigScene();
        PlayerSettings.Android.forceInternetPermission = true;

        var test = new GameObject("HapticLink").AddComponent<HapticLinkTest>();
        test.trackingSpace = cam.transform.parent; // Camera Offset: controller poses live in this space

        // The workspace: the ball in the middle, and the stylus as the bridge reports it (a 8 mm tip and a pen behind it).
        var workspace = new GameObject("Workspace").transform;
        workspace.position = new Vector3(0, 0.9f, 0.45f); // until HapticLinkTest puts it in front of the head
        test.workspace = workspace;

        test.ballIdle = Overlay("HapticBall", new Color(0.45f, 0.8f, 1f, 0.30f));
        test.ballTouched = Overlay("HapticBallTouched", new Color(1f, 0.55f, 0.15f, 0.55f));
        test.tipIdle = Overlay("HapticTip", new Color(1f, 1f, 1f, 0.95f));
        test.tipButton1 = Overlay("HapticTipButton1", new Color(0.3f, 1f, 0.5f, 1f));
        test.tipButton2 = Overlay("HapticTipButton2", new Color(1f, 0.4f, 0.4f, 1f));
        var pen = Overlay("HapticPen", new Color(0.9f, 0.92f, 0.95f, 0.75f));

        test.ballRenderer = Shape("Ball", PrimitiveType.Sphere, workspace, Vector3.zero, Vector3.one * 0.05f, test.ballIdle); // resized by HapticLinkTest
        test.tip = new GameObject("Stylus").transform;
        test.tip.SetParent(workspace, false);
        test.tipRenderer = Shape("Tip", PrimitiveType.Sphere, test.tip, Vector3.zero, Vector3.one * 0.008f, test.tipIdle);
        var body = Shape("Pen", PrimitiveType.Cylinder, test.tip, new Vector3(0, 0, -0.06f), new Vector3(0.007f, 0.06f, 0.007f), pen);
        body.transform.localRotation = Quaternion.Euler(90, 0, 0); // a cylinder stands along Y: lay it along Z, behind the tip

        (test.panel, test.status) = GlassPanelBuilder.AddCard("HapticLinkPanel", 0.0007f, 760, 32);
        test.status.text = "Haptic link";

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        scenes.RemoveAll(s => s.path == ScenePath);
        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"Built {ScenePath} (first in Build Settings)");
    }

    static Material Overlay(string name, Color color)
    {
        var mat = ModelTargetSceneBuilder.MakeMaterial($"Assets/Materials/{name}.mat", "ModelTarget/GuideOverlay");
        mat.SetColor("_Color", color);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Renderer Shape(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material mat)
    {
        var s = GameObject.CreatePrimitive(type);
        s.name = name;
        Object.DestroyImmediate(s.GetComponent<Collider>());
        s.transform.SetParent(parent, false);
        s.transform.localPosition = position;
        s.transform.localScale = scale;
        var r = s.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }
}
