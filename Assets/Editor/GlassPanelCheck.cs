using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Self-check of the alignment panel: each state of the panel is laid out and must fit a metre, keep every text inside its box,
/// keep the chips inside the card, fit the pools of rows / chips / dots and use only glyphs the font has.
/// With a folder, each state is also rendered to a PNG (a camera 1.3 m in front, grey background).
/// It drives the aligner's private state by reflection and puts it back; it never runs the aligner itself.
/// </summary>
public static class GlassPanelCheck
{
    const int Layer = 29; // free layer: only the panel is shown to the check camera
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [MenuItem("Tools/Model Target/Self-check Glass Panel")]
    static void Menu() => Debug.Log(Run(null));

    /// <summary>Every state of the panel. textT = a position of the text-size slider to check at (0 = smallest, 1 = largest); the user's own value is put back.</summary>
    public static string Run(string pngDir, float textT = -1)
    {
        var aligner = UnityEngine.Object.FindFirstObjectByType<LandmarkAligner>();
        var type = typeof(LandmarkAligner);
        var stepType = type.GetNestedType("Step", BindingFlags.NonPublic);
        T Get<T>(string field) => (T)type.GetField(field, Any).GetValue(aligner);
        void Set(string field, object v) => type.GetField(field, Any).SetValue(aligner, v);
        var describe = type.GetMethod("Describe", Any);

        var saved = new object[] { Get<object>("_step"), Get<int>("_index"), Get<int>("_sel"), Get<float>("_rms"), Get<float[]>("_residuals"), Get<int>("_worst"), Get<Pose?>("_locked"), Get<int>("_page") };
        var groups = aligner.model.groups;
        var visible = new bool[groups.Length];
        for (int i = 0; i < groups.Length; i++) visible[i] = groups[i].visible;
        var view = aligner.view;
        var panel = (RectTransform)aligner.panel.transform;
        var font = TMP_Settings.defaultFontAsset;
        var report = new StringBuilder();
        int failed = 0;
        float textBefore = GlassSettings.TextSizeT;
        if (textT >= 0) GlassSettings.TextSizeT = textT;
        int percent = Mathf.RoundToInt(GlassSettings.TextSize * 100);

        // name, step, index, sel, rms (m), tracked
        var states = new (string name, string step, int index, int sel, float rms, bool tracked)[]
        {
            ("intro", "Intro", 0, 0, 0, true), ("capture", "Capture", 1, 0, 0, true), ("capture, not tracked", "Capture", 1, 0, 0, false),
            ("review good", "Review", 0, 0, 0.0031f, true), ("review acceptable", "Review", 0, 0, 0.0062f, true), ("review poor", "Review", 0, 0, 0.0098f, true),
            ("adjust", "Adjust", 0, 0, 0, true), ("done", "Done", 0, 0, 0, true),
            ("menu, action", "Menu", 0, 0, 0, true), ("menu, slider", "Menu", 0, 1, 0, true), ("menu, tester row", "Menu", 0, 2, 0, true), ("menu, last row", "Menu", 0, groups.Length + 2, 0, true),
            ("settings, calibrate", "Menu", 0, 0, 0, true), ("settings, size", "Menu", 0, 2, 0, true), ("settings, text size", "Menu", 0, 3, 0, true), // names that start with "settings" are the second menu page
            ("settings, reset", "Menu", 0, 5, 0, true), ("settings, help mode", "Menu", 0, 6, 0, true), ("settings, strength", "Menu", 0, 7, 0, true),
            ("tester, first layer", "Menu", 0, 1, 0, true), ("tester, long name", "Menu", 0, 5, 0, true), ("tester, bone", "Menu", 0, 8, 0, true), // "tester": the third page, with the layers in two columns (sel - 1 = the layer on the cube)
        };
        try
        {
            Set("_locked", (Pose?)new Pose()); // the longest key lines: with "GRIP: cancel"
            Set("_residuals", new[] { 0.003f, 0.002f, 0.004f, 0.003f });
            Set("_worst", 2);
            for (int i = 0; i < groups.Length; i++) groups[i].visible = i % 2 == 0; // some switches on, some off
            foreach (var s in states)
            {
                Set("_step", Enum.Parse(stepType, s.step));
                Set("_index", s.index);
                Set("_sel", s.sel);
                Set("_rms", s.rms);
                Set("_page", s.name.StartsWith("settings") ? 1 : s.name.StartsWith("tester") ? 2 : 0);
                if (s.name.StartsWith("tester")) aligner.tester.Select(Math.Max(s.sel - 1, 0));
                var m = new PanelModel();
                describe.Invoke(aligner, new object[] { s.tracked, m });
                if (s.name == "menu, slider") { var r = m.rows[1]; r.value = 0.6f; m.rows[1] = r; } // the bar part way, as the scene's fade is at 0
                view.Show(m);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(panel);

                var problems = new List<string>();
                float height = panel.rect.height, room = panel.rect.width - 2 * view.padding;
                float cap = 1100 * Mathf.Max(1, GlassSettings.TextSize); // a sanity bound (the menu is 0.95 m at 100 %): bigger text makes the same rows taller
                if (height < 100 || height > cap) problems.Add($"height {height:F0} mm out of 100..{cap:F0}");
                if (m.rows.Count > view.rows.Length) problems.Add($"{m.rows.Count} rows for a pool of {view.rows.Length}");
                if (m.keys.Count > view.chips.Length) problems.Add($"{m.keys.Count} keys for a pool of {view.chips.Length}");
                if (m.steps > view.dots.Length) problems.Add($"{m.steps} dots for a pool of {view.dots.Length}");
                int inLeft = view.left.Cast<Transform>().Count(c => c.gameObject.activeSelf), inRight = view.right.Cast<Transform>().Count(c => c.gameObject.activeSelf);
                int half = PanelModel.Half(m.columnsCount);
                if (inLeft != half || inRight != m.columnsCount - half) problems.Add($"the columns hold {inLeft} + {inRight} rows, the model has {m.columnsCount} for them");
                if (Mathf.Abs(panel.rect.width - (m.columnsCount > 0 ? view.wideWidth : m.compact ? view.compactWidth : view.width)) > 0.5f) problems.Add($"card {panel.rect.width:F0} wide for {(m.columnsCount > 0 ? "two columns" : "one")}");
                foreach (var t in panel.GetComponentsInChildren<TMP_Text>(false))
                {
                    t.ForceMeshUpdate();
                    font.HasCharacters(Regex.Replace(t.text, "<[^>]*>", ""), out uint[] missing);
                    if (missing != null && missing.Length > 0) problems.Add($"{t.name}: missing glyphs {string.Join(",", missing)}");
                    if (t.renderedWidth > t.rectTransform.rect.width + 4) problems.Add($"{t.name} is wider than its box ({t.renderedWidth:F0} > {t.rectTransform.rect.width:F0})");
                }
                foreach (var c in view.chips)
                {
                    if (c.root.activeSelf && c.rect.anchoredPosition.x + c.rect.sizeDelta.x > room + 1) problems.Add("a chip leaves the card");
                    if (c.root.activeSelf && c.rect.sizeDelta.y < 34) problems.Add("a chip is too short for its pill");
                }
                if (problems.Count > 0) failed++;
                report.AppendLine($"{(problems.Count == 0 ? "ok  " : "FAIL")} {s.name}: {height:F0} mm tall {string.Join("; ", problems)}");
                bool picture = textT < 0 || s.name == "capture" || s.name == "menu, action" || s.name == "settings, text size" || s.name == "tester, long name"; // away from the default only a few
                if (pngDir != null && picture) Render(panel, Path.Combine(pngDir, s.name.Replace(", ", "-").Replace(' ', '-') + (textT >= 0 ? $"-text{percent}" : "") + ".png"), 1.3f);
            }
        }
        finally
        {
            Set("_step", saved[0]); Set("_index", saved[1]); Set("_sel", saved[2]); Set("_rms", saved[3]);
            Set("_residuals", saved[4]); Set("_worst", saved[5]); Set("_locked", saved[6]); Set("_page", saved[7]);
            for (int i = 0; i < groups.Length; i++) groups[i].visible = visible[i];
            GlassSettings.TextSizeT = textBefore;
            view.Show(new PanelModel()); // nothing on: the state the scene is saved in
            aligner.tester.Select(0);
        }
        return $"{(failed == 0 ? "PASS" : "FAILED")} glass panel at text {percent} %: {states.Length - failed}/{states.Length} states ok\n{report}";
    }

    // What the user tunes from the menu: the sliders' ends, the change event, and that both cards take size, text size and opacity from it.
    [MenuItem("Tools/Model Target/Self-check Interface Settings")]
    static void SettingsMenu() => Debug.Log(RunSettings());

    public static string RunSettings()
    {
        var looks = new[]
        {
            UnityEngine.Object.FindFirstObjectByType<LandmarkAligner>().panel.GetComponent<GlassLook>(),
            UnityEngine.Object.FindFirstObjectByType<NeedleAction>(FindObjectsInactive.Include).card.GetComponent<GlassLook>(),
        };
        var report = new StringBuilder();
        int failed = 0, changes = 0;
        void Expect(string what, bool ok) { if (!ok) failed++; report.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
        bool Is(float a, float b) => Mathf.Abs(a - b) < 1e-4f;
        Action count = () => changes++;
        float size = GlassSettings.SizeT, text = GlassSettings.TextSizeT, opacity = GlassSettings.OpacityT; // the user's own, put back at the end
        var view = UnityEngine.Object.FindFirstObjectByType<LandmarkAligner>().view;
        var fonts = looks[1].texts.Select(t => t.fontSize).ToArray();
        var scales = new Vector3[looks.Length];
        var colors = new Color[looks.Length];
        for (int i = 0; i < looks.Length; i++) { scales[i] = looks[i].transform.localScale; colors[i] = looks[i].card.color; }
        GlassSettings.Changed += count;
        var menu = new MenuProbe(UnityEngine.Object.FindFirstObjectByType<LandmarkAligner>());
        try
        {
            GlassSettings.Reset();
            Expect("the defaults are size 100 %, text 100 % and opacity 90 %", Is(GlassSettings.Size, 1) && Is(GlassSettings.TextSize, 1) && Is(GlassSettings.Opacity, 0.9f));
            changes = 0;
            GlassSettings.SizeT = 1;
            Expect("the slider's far end is the largest size, announced once", Is(GlassSettings.Size, GlassSettings.MaxSize) && changes == 1);
            GlassSettings.SizeT = 1;
            Expect("the same value announces nothing", changes == 1);
            GlassSettings.SizeT = -5;
            Expect("past the start it stays at the smallest size", Is(GlassSettings.Size, GlassSettings.MinSize));
            GlassSettings.OpacityT = 7;
            Expect("past the end the opacity stays at 100 %", Is(GlassSettings.Opacity, GlassSettings.MaxOpacity));
            GlassSettings.OpacityT = 0;
            Expect("the start of its slider is the lowest opacity", Is(GlassSettings.Opacity, GlassSettings.MinOpacity));

            GlassSettings.SizeT = 1;
            GlassSettings.OpacityT = 0.5f; // 70 %
            foreach (var look in looks)
            {
                var before = look.card.color;
                look.Apply();
                Expect($"{look.name} is scaled by the size and its background takes the opacity, colour kept",
                    Is(look.transform.localScale.x, look.baseScale * GlassSettings.MaxSize) && Is(look.card.color.a, 0.7f) &&
                    Is(look.card.color.r, before.r) && Is(look.card.color.g, before.g) && Is(look.card.color.b, before.b));
            }
            GlassSettings.Reset();
            foreach (var look in looks) look.Apply();
            Expect("reset gives the cards their normal size and opacity back", looks[0].transform.localScale.x > 0 &&
                Is(looks[0].transform.localScale.x, looks[0].baseScale) && Is(looks[1].transform.localScale.x, looks[1].baseScale) && Is(looks[0].card.color.a, 0.9f));

            // the text size is its own setting: the cards keep their size, only the fonts move
            GlassSettings.TextSizeT = 1;
            Expect("the far end of the text slider is the largest text", Is(GlassSettings.TextSize, GlassSettings.MaxText));
            GlassSettings.TextSizeT = -3;
            Expect("past the start the text stays at the smallest", Is(GlassSettings.TextSize, GlassSettings.MinText));
            GlassSettings.TextSizeT = 1;
            foreach (var look in looks) look.Apply();
            Expect("the cards keep their size when only the text size changes", Is(looks[0].transform.localScale.x, looks[0].baseScale) && Is(looks[1].transform.localScale.x, looks[1].baseScale));
            Expect("the message card's texts follow the text size", looks[1].texts.Select((t, i) => Is(t.fontSize, looks[1].sizes[i] * GlassSettings.MaxText)).All(ok => ok));
            var shown = new PanelModel { title = "Check" };
            shown.Add(PanelModel.Kind.Action, "A row");
            shown.Hint("GRIP", "Close");
            view.Show(shown);
            var onEnable = typeof(GlassPanel).GetMethod("OnEnable", Any);
            var onDisable = typeof(GlassPanel).GetMethod("OnDisable", Any);
            Expect("the panel draws its title at the text size (52 x 130 %)", Is(view.title.fontSize, 52 * GlassSettings.MaxText));
            onEnable.Invoke(view, null); // what Unity does when the panel is switched on
            GlassSettings.TextSizeT = 0;
            Expect("while it is on, the panel redraws itself when the text size changes (52 x 80 %, chips 35 tall)",
                Is(view.title.fontSize, 52 * GlassSettings.MinText) && Is(view.chips[0].rect.sizeDelta.y, Mathf.Round(44 * GlassSettings.MinText)));
            onDisable.Invoke(view, null);
            GlassSettings.TextSizeT = 1;
            Expect("and it stops listening when it is switched off", Is(view.title.fontSize, 52 * GlassSettings.MinText));
            view.Refresh();
            Expect("Refresh draws it again at the current size", Is(view.title.fontSize, 52 * GlassSettings.MaxText));
            GlassSettings.Reset();

            // the menu's pages, driven like the controller would (TRIGGER or A on a row, B, the stick up / down), never on the model's own rows
            menu.Set(page: 0, sel: 0);
            menu.Select();
            Expect("TRIGGER on \"Settings\" opens the second page on its first row", menu.Page == 1 && menu.Sel == 0);
            menu.Set(page: 1, sel: 2);
            menu.Select();
            Expect("TRIGGER on a slider row does nothing", menu.Page == 1 && menu.Sel == 2 && menu.Step == "Menu");
            menu.Set(page: 1, sel: 4);
            menu.Back();
            Expect("B returns to the main menu, on the row that opened the page", menu.Page == 0 && menu.Sel == 0);
            menu.Set(page: 0, sel: 2);
            menu.Back();
            Expect("B on the main menu closes it", menu.Step == "Done");
            GlassSettings.SizeT = 1;
            GlassSettings.TextSizeT = 1;
            GlassSettings.OpacityT = 0;
            menu.Set(page: 1, sel: 5);
            menu.Select();
            Expect("\"Reset to defaults\" puts size, text size and opacity back",
                Is(GlassSettings.Size, GlassSettings.DefaultSize) && Is(GlassSettings.TextSize, GlassSettings.DefaultText) && Is(GlassSettings.Opacity, GlassSettings.DefaultOpacity) && menu.Page == 1);
            menu.Set(page: 1, sel: 0);
            menu.Select();
            Expect("\"Calibrate model\", the first row, starts the calibration", menu.Step == "Intro");
            menu.Set(page: 1, sel: 1);
            menu.Select();
            Expect("\"Adjust alignment\" starts the adjustment", menu.Step == "Adjust");
            menu.Set(page: 1, sel: 7);
            menu.Move(-1);
            Expect("the cursor wraps from the last row of the settings page to its first", menu.Sel == 0);
            menu.Move(1);
            Expect("and from the first row to the last", menu.Sel == 7);
            menu.Set(page: 0, sel: 0);
            menu.Move(-1);
            Expect("the main page moves one row down", menu.Sel == 1);
            menu.Set(page: 0, sel: 1);
            menu.Move(0, 1);
            Expect("left / right on the main page moves nothing", menu.Sel == 1);
        }
        finally
        {
            GlassSettings.Changed -= count;
            menu.Restore();
            GlassSettings.SizeT = size;
            GlassSettings.TextSizeT = text;
            GlassSettings.OpacityT = opacity;
            for (int i = 0; i < fonts.Length; i++) looks[1].texts[i].fontSize = fonts[i];
            view.Show(new PanelModel()); // nothing on, as the scene is saved
            for (int i = 0; i < looks.Length; i++) { looks[i].transform.localScale = scales[i]; looks[i].card.color = colors[i]; } // the scene as it was
        }
        return $"{(failed == 0 ? "PASS" : "FAILED")} interface settings\n{report}";
    }

    // Reads and drives the aligner's private menu state, and puts it back.
    internal sealed class MenuProbe
    {
        readonly LandmarkAligner _a;
        readonly object _step;
        readonly int _page, _sel;
        readonly bool _latched, _sideLatched;
        readonly int _index;
        readonly GameObject[] _objects; // what Go switches on and off (the parts of the model, the markers, the ball), as the scene has them
        readonly bool[] _active;

        public MenuProbe(LandmarkAligner a)
        {
            _a = a;
            _index = (int)Field("_index").GetValue(a);
            _objects = a.model.groups.SelectMany(g => g.objects).Concat(a.model.landmarks.Select(l => l.marker.gameObject)).Append(a.tip.gameObject).ToArray();
            _active = _objects.Select(o => o.activeSelf).ToArray();
            _step = Field("_step").GetValue(a);
            _page = (int)Field("_page").GetValue(a);
            _sel = (int)Field("_sel").GetValue(a);
            _latched = (bool)Field("_stickLatched").GetValue(a);
            _sideLatched = (bool)Field("_sideLatched").GetValue(a);
        }

        static FieldInfo Field(string name) => typeof(LandmarkAligner).GetField(name, Any);
        static MethodInfo Method(string name) => typeof(LandmarkAligner).GetMethod(name, Any);

        public string Step => Field("_step").GetValue(_a).ToString();
        public int Page => (int)Field("_page").GetValue(_a);
        public int Sel => (int)Field("_sel").GetValue(_a);

        public void Set(int page, int sel)
        {
            Field("_step").SetValue(_a, Enum.Parse(typeof(LandmarkAligner).GetNestedType("Step", BindingFlags.NonPublic), "Menu"));
            Field("_page").SetValue(_a, page);
            Field("_sel").SetValue(_a, sel);
        }

        public void Select() => Method("MenuSelect").Invoke(_a, null);
        public void Back() => Method("MenuBack").Invoke(_a, null);

        // y < 0 = stick down, x > 0 = right. A flick has to come back to the middle first, so the cursor is let go before it is pushed.
        public void Move(float y, float x = 0)
        {
            Method("MoveSelection").Invoke(_a, new object[] { Vector2.zero });
            Method("MoveSelection").Invoke(_a, new object[] { new Vector2(x, y) });
        }

        public void Restore()
        {
            Field("_step").SetValue(_a, _step);
            Field("_page").SetValue(_a, _page);
            Field("_sel").SetValue(_a, _sel);
            Field("_stickLatched").SetValue(_a, _latched);
            Field("_sideLatched").SetValue(_a, _sideLatched);
            Field("_index").SetValue(_a, _index);
            _a.model.SetAligning(false); // a step that was started (calibrate, adjust, close) has switched the parts of the model: back to the scene's own
            for (int i = 0; i < _objects.Length; i++) _objects[i].SetActive(_active[i]);
        }
    }

    // The needle's message card in each state it shows (hit a bone, target reached, extracting): sized sensibly, text inside its box, glyphs in the font.
    [MenuItem("Tools/Model Target/Self-check Needle Message")]
    static void MessageMenu() => Debug.Log(RunMessage(null));

    public static string RunMessage(string pngDir)
    {
        var action = UnityEngine.Object.FindFirstObjectByType<NeedleAction>(FindObjectsInactive.Include);
        var go = typeof(NeedleAction).GetMethod("Go", Any);
        var card = (RectTransform)action.card;
        var parent = card.parent;
        int index = card.GetSiblingIndex();
        Vector3 position = card.localPosition, scale = card.localScale;
        Quaternion rotation = card.localRotation;
        var font = TMP_Settings.defaultFontAsset;
        var report = new StringBuilder();
        int failed = 0;
        var states = new (string name, NeedleAction.State state, string text)[]
        {
            ("hit a bone", NeedleAction.State.Blocked, null), ("target reached", NeedleAction.State.Reached, null),
            ("extracting", NeedleAction.State.Extracting, null), ("extracting 3.2 mL", NeedleAction.State.Extracting, NeedleAction.ExtractText(32)),
        };
        card.SetParent(null, true); // the needle is switched off in the scene, and a Canvas under it would not draw
        try
        {
            foreach (var s in states)
            {
                go.Invoke(action, new object[] { NeedleAction.State.Outside }); // so the next state is a change
                go.Invoke(action, new object[] { s.state });
                if (s.text != null) action.message.text = s.text;
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(card);
                var problems = new List<string>();
                float w = card.rect.width, h = card.rect.height;
                if (w < 200 || w > 700 || h < 60 || h > 300) problems.Add($"size {w:F0} x {h:F0} units out of range");
                if (!action.MessageShown) problems.Add("not shown");
                foreach (var t in card.GetComponentsInChildren<TMP_Text>(false)) // the text, and the chip when it shows
                {
                    t.ForceMeshUpdate();
                    font.HasCharacters(Regex.Replace(t.text, "<[^>]*>", ""), out uint[] missing);
                    if (missing != null && missing.Length > 0) problems.Add($"{t.name}: missing glyphs {string.Join(",", missing)}");
                    if (t.renderedWidth > t.rectTransform.rect.width + 4) problems.Add($"{t.name} wider than its box");
                }
                if (action.hint.activeSelf != (s.state != NeedleAction.State.Blocked)) problems.Add("the key chip shows only with a key to press");
                if (problems.Count > 0) failed++;
                report.AppendLine($"{(problems.Count == 0 ? "ok  " : "FAIL")} {s.name}: {w:F0} x {h:F0} units {string.Join("; ", problems)}");
                if (pngDir != null) Render(card, Path.Combine(pngDir, "message-" + s.name.Replace(' ', '-') + ".png"), 0.5f);
            }
        }
        finally
        {
            go.Invoke(action, new object[] { NeedleAction.State.Outside }); // hides the card again
            action.message.text = "";
            card.SetParent(parent, false);
            card.SetSiblingIndex(index);
            card.localPosition = position;
            card.localRotation = rotation;
            card.localScale = scale;
        }
        return $"{(failed == 0 ? "PASS" : "FAILED")} needle message: {states.Length - failed}/{states.Length} states ok\n{report}";
    }

    // meters = how far in front of the card the camera stands, looking at its centre.
    public static void Render(RectTransform panel, string file, float meters)
    {
        var all = panel.GetComponentsInChildren<Transform>(true);
        var layers = new int[all.Length];
        for (int i = 0; i < all.Length; i++) { layers[i] = all[i].gameObject.layer; all[i].gameObject.layer = Layer; }
        var cam = new GameObject("check camera").AddComponent<Camera>();
        var rt = RenderTexture.GetTemporary(1600, 1000, 24);
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        try
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.23f, 0.25f, 0.28f);
            cam.cullingMask = 1 << Layer;
            cam.fieldOfView = 45;
            cam.nearClipPlane = 0.02f;
            cam.targetTexture = rt;
            cam.transform.SetPositionAndRotation(panel.TransformPoint(panel.rect.center) - panel.forward * meters, panel.rotation);
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, tex.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            for (int i = 0; i < all.Length; i++) all[i].gameObject.layer = layers[i];
        }
    }
}
