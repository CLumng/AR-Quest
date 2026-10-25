using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks the layer tester: where the cube says the tip is, the feel it gives for a layer and for bone (taken from the needle's own table
/// and tuning), the materials, the needle standing down while the cube is out, and the menu page that drives it. Puts the scene back as it was.
/// </summary>
public static class LayerTesterCheck
{
    [MenuItem("Tools/Model Target/Self-check Layer Tester")]
    static void Menu() => Debug.Log(Run());

    public static string Run()
    {
        var aligner = Object.FindFirstObjectByType<LandmarkAligner>();
        var tester = aligner.tester;
        var needle = tester.needle;
        var report = new StringBuilder();
        int failed = 0;
        void Expect(string what, bool ok) { if (!ok) failed++; report.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        bool Is(float a, float b, float eps = 1e-4f) => Mathf.Abs(a - b) < eps;

        var nextField = typeof(NeedleHaptics).GetField("_next", BindingFlags.NonPublic | BindingFlags.Instance);
        var h = needle.haptics;
        void Tip(Vector3 tip, Vector3 forward) => needle.transform.SetPositionAndRotation(tip - forward.normalized * needle.tipLength, Quaternion.LookRotation(forward));
        float Felt(float dt = 0.02f) { nextField.SetValue(tester.haptics, float.MinValue); tester.Feel(dt); return tester.haptics.LastSent; } // a buzz may go out now

        var needlePose = new Pose(needle.transform.position, needle.transform.rotation);
        bool needleActive = needle.gameObject.activeSelf, needleEnabled = needle.enabled;
        float bone = h.boneAmplitude;
        var menu = new GlassPanelCheck.MenuProbe(aligner);
        try
        {
            Expect("the cube has a layer for each of the needle's, and bone", tester.Count == h.layers.Length + 1 && tester.Count == tester.materials.Length && tester.Count == 8);
            Expect("every layer has a material of its tissue (a shader graph), never a fade copy, and bone one of the vertebrae",
                tester.materials.All(m => m != null && !m.name.StartsWith("Fade_")) && tester.materials.Take(h.layers.Length).All(m => m.shader.name.StartsWith("Shader Graphs")) &&
                tester.materials[^1].shader.name == "Universal Render Pipeline/Lit");
            var skin = aligner.model.groups.First(g => g.name == "Skin").objects[0].GetComponentInChildren<Renderer>(true).sharedMaterial;
            Expect("the skin layer is the model's skin material", tester.materials[0] == skin);

            tester.Open(Vector3.zero, Quaternion.identity); // cube 10 cm, faces at +-5 cm
            Expect("opening it puts the needle's body test away and shows the cube", !needle.enabled && tester.gameObject.activeSelf);
            needle.gameObject.SetActive(true);

            // where the tip is
            Tip(new Vector3(0, 0, -0.08f), Vector3.forward);
            Expect("a tip outside is not in", !tester.Depth(needle.transform.position + needle.transform.forward * needle.tipLength, Vector3.forward, out _));
            Tip(new Vector3(0, 0, -0.02f), Vector3.forward);
            Expect("3 cm past the front face is 3 cm deep", tester.Depth(new Vector3(0, 0, -0.02f), Vector3.forward, out float d) && Is(d, 0.03f));
            Expect("through the side, 2 cm in", tester.Depth(new Vector3(0.03f, 0, 0), Vector3.left, out d) && Is(d, 0.02f));
            Expect("on the diagonal it is measured along the needle", tester.Depth(Vector3.zero, new Vector3(1, 0, 1).normalized, out d) && Is(d, 0.05f / Mathf.Cos(Mathf.PI / 4)));
            Expect("out through the back is out again", !tester.Depth(new Vector3(0, 0, 0.06f), Vector3.forward, out _));

            // the feel
            for (int i = 0; i < h.layers.Length; i++)
            {
                tester.Select(i);
                var l = tester.haptics.layers;
                Expect($"{h.layers[i].name}: one layer from the surface with the needle's resistance and pop, its material on the cube",
                    l.Length == 1 && l[0].start == 0 && l[0].resistance == h.layers[i].resistance && l[0].pop == h.layers[i].pop && tester.cube.sharedMaterial == tester.materials[i]);
            }
            tester.Select(0);
            tester.haptics.Release();
            Tip(new Vector3(0, 0, -0.045f), Vector3.forward);
            Expect("entering the skin gives its pop", Is(Felt(), h.layers[0].pop));
            Tip(new Vector3(0, 0, -0.02f), Vector3.forward);
            Expect("moving fast inside gives its full resistance", Is(Felt(), h.layers[0].resistance));
            for (int i = 0; i < 80; i++) Felt();
            Expect("held still it only feels a share of it", Is(Felt(), h.layers[0].resistance * h.restShare, 0.01f));
            Tip(new Vector3(0, 0, -0.08f), Vector3.forward);
            Felt();
            Expect("out of the cube it lets go", !tester.haptics.InTissue);
            Tip(new Vector3(0, 0, -0.045f), Vector3.forward);
            Expect("going in again pops again", Is(Felt(), h.layers[0].pop));

            h.boneAmplitude = 0.5f; // the cube follows the needle's tuning, not its own copy
            tester.Select(h.layers.Length);
            Expect("bone has no table and the needle's strength", tester.haptics.layers.Length == 0 && tester.haptics.boneAmplitude == 0.5f);
            tester.haptics.Release();
            Tip(new Vector3(0, 0, -0.02f), Vector3.forward);
            Expect("bone buzzes steadily at the needle's strength", Is(Felt(), 0.5f) && Is(Felt(), 0.5f));
            Expect("its properties are the strength and no pop", tester.Layer(h.layers.Length) is ("Bone", 0.5f, 0f));
            tester.Select(0);

            // the menu
            menu.Set(page: 0, sel: 2);
            menu.Select();
            Expect("TRIGGER on \"Layer tester\" opens its page on the first layer", menu.Page == 2 && menu.Sel == 1);
            menu.Set(page: 2, sel: 3);
            menu.Select();
            Expect("TRIGGER on a layer does nothing: the cursor is the layer", menu.Page == 2 && menu.Sel == 3);
            menu.Set(page: 2, sel: 0);
            menu.Select();
            Expect("\"Back\" returns to the main menu, on the row that opened the page", menu.Page == 0 && menu.Sel == 2);
            menu.Set(page: 2, sel: 6);
            menu.Back();
            Expect("B does the same from a layer", menu.Page == 0 && menu.Sel == 2);
            menu.Set(page: 2, sel: 0);
            menu.Move(1);
            Expect("the cursor wraps from Back up to the last layer", menu.Sel == tester.Count);
            menu.Move(-1);
            Expect("and from the last layer down to Back", menu.Sel == 0);

            // the layers are in two columns: the left one holds the first half, left / right goes to the same place in the other
            int half = PanelModel.Half(tester.Count);
            menu.Set(page: 2, sel: 1);
            menu.Move(0, 1);
            Expect("right from the first layer goes to the first of the right column", menu.Sel == 1 + half);
            menu.Move(0, 1);
            Expect("right again stays there", menu.Sel == 1 + half);
            menu.Move(0, -1);
            Expect("left goes back to the first of the left column", menu.Sel == 1);
            menu.Set(page: 2, sel: half);
            menu.Move(0, 1);
            Expect("right from the last of the left column goes to the last layer", menu.Sel == tester.Count);
            menu.Set(page: 2, sel: 0);
            menu.Move(0, 1);
            Expect("left / right on Back moves nothing", menu.Sel == 0);

            tester.Select(2);
            var m = new PanelModel();
            menu.Set(page: 2, sel: 3);
            typeof(LandmarkAligner).GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(aligner, new object[] { true, m });
            var (name, resistance, pop) = tester.Layer(2);
            Expect("the page lists Back, every layer and the two properties, the badge names the layer",
                m.rows.Count == 1 + tester.Count + 1 + 2 && m.badge == name && m.rows[3].selected && m.rows[3].label == name &&
                Is(m.rows[^2].value, resistance) && Is(m.rows[^1].value, pop));
            Expect("the layers, and only they, are the rows in two columns", m.columnsFrom == 1 && m.columnsCount == tester.Count);
        }
        finally
        {
            tester.Select(0);
            h.boneAmplitude = bone;
            tester.Close(); // the needle's body test back on
            needle.enabled = needleEnabled;
            needle.gameObject.SetActive(needleActive);
            needle.transform.SetPositionAndRotation(needlePose.position, needlePose.rotation);
            menu.Restore();
        }
        return $"{(failed == 0 ? "PASS" : "FAILED")} layer tester\n{report}";
    }
}
