using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The alignment panel as a world-space Canvas: a dark translucent card with a thin border, as tall as what it holds (the top edge stays put).
/// Inside: title and progress dots, a badge, a text, menu rows (action, slider, toggle switch) and key chips. Every part exists as a pool
/// that GlassPanel switches on and off. Safe to run again: it replaces the panel under the rig.
/// </summary>
public static class GlassPanelBuilder
{
    const string Folder = "Assets/UI/Sprites";
    const float Width = 900;      // canvas units are millimetres: the canvas is scaled 0.001, so this is a 0.9 m panel
    const float WideWidth = 1200; // the card of a page with two columns
    const float CompactWidth = 600, Padding = 34;
    const float Top = 0.42f;      // the panel's top edge above the rig, in metres
    const float MessageScale = 0.0007f; // the needle's message: its title (40 units) is 2.8 cm tall; it is read from about half a metre
    const int Radius = 28;
    const int Rows = 16, Chips = 6, Dots = 6; // the main menu is 3 actions, a header and 9 toggles

    static readonly Color Card = new(0.05f, 0.07f, 0.10f, 0.90f);
    static readonly Color Border = new(0.62f, 0.83f, 1f, 0.35f);
    static readonly Color Primary = new(0.945f, 0.957f, 0.973f), Secondary = new(0.84f, 0.87f, 0.90f), Muted = new(0.60f, 0.64f, 0.70f);
    static readonly Color Accent = new(1f, 0.83f, 0f), Info = new(0.62f, 0.83f, 1f), Track = new(0.23f, 0.27f, 0.31f);
    static readonly Color Selected = new(1f, 0.83f, 0f, 0.16f), KeyBg = new(0.18f, 0.22f, 0.27f);

    [MenuItem("Tools/Model Target/Build Glass Panel")]
    static void Menu()
    {
        var aligner = Object.FindFirstObjectByType<LandmarkAligner>();
        Apply(aligner);
        EditorSceneManager.MarkSceneDirty(aligner.gameObject.scene);
    }

    /// <summary>Replaces the aligner's panel with a new one and wires it.</summary>
    public static void Apply(LandmarkAligner aligner)
    {
        var old = aligner.rig.Find("Panel");
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var (panel, view) = Build(aligner.rig);
        panel.transform.SetSiblingIndex(0);
        aligner.panel = panel;
        aligner.view = view;
        EditorUtility.SetDirty(aligner);
        AddMessage(aligner.needle.GetComponent<NeedleAction>());
    }

    /// <summary>
    /// Replaces the needle's message with a small card in the panel's style: it floats beside the entry point (NeedleAction places it)
    /// and its first line and border take the colour of the state. Also run by ModelTargetSceneBuilder.AddNeedleAction.
    /// </summary>
    public static void AddMessage(NeedleAction action)
    {
        var needle = action.transform;
        var old = needle.Find("NeedleMessage");
        if (old != null) Object.DestroyImmediate(old.gameObject);

        var kit = MakeKit();
        var root = kit.Node("NeedleMessage", needle);
        var canvas = root.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = TissueFadeBuilder.OverlayOrder + 10;
        root.pivot = new Vector2(0.5f, 0.5f);
        root.localScale = Vector3.one * MessageScale;

        var bg = root.gameObject.AddComponent<Image>();
        bg.sprite = kit.round;
        bg.type = Image.Type.Sliced;
        bg.color = Card;
        bg.material = kit.card;
        bg.raycastTarget = false;

        var look = root.gameObject.AddComponent<GlassLook>();
        look.card = bg;
        look.baseScale = MessageScale;

        var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(30, 30, 18, 18);
        layout.spacing = 6;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        var fit = root.gameObject.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var text = kit.Label("Text", root, 40, Primary, FontStyles.Normal, TextAlignmentOptions.Left);
        var hint = kit.Node("Hint", root); // a key chip, then what it does, like the panel's
        Kit.Row(hint, 12, 0, 0, TextAnchor.MiddleLeft);
        var keyBg = kit.Pic("KeyBg", hint, kit.pill, KeyBg);
        Kit.Row(keyBg.rectTransform, 0, 14, 3, TextAnchor.MiddleCenter);
        var key = kit.Label("Key", keyBg.transform, 26, Info, FontStyles.Bold, TextAlignmentOptions.Center);
        var what = kit.Label("Label", hint, 28, Secondary, FontStyles.Normal, TextAlignmentOptions.Left);
        look.texts = new TMP_Text[] { text, key, what }; // no GlassPanel here: the card's own look follows the user's text size
        look.sizes = new[] { 40f, 26f, 28f };
        hint.gameObject.SetActive(false);
        var edge = kit.Pic("Border", root, kit.ring, Border);
        edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        Kit.Stretch(edge.rectTransform);

        root.gameObject.SetActive(false); // NeedleAction shows it
        action.card = root;
        action.message = text;
        action.edge = edge;
        action.hint = hint.gameObject;
        action.hintKey = key;
        action.hintLabel = what;
        EditorUtility.SetDirty(action);
    }

    /// <summary>
    /// A card in the panel's style with one rich text (a world-space Canvas at the scene root, 1 unit = scale metres), as tall as its text.
    /// Follows the user's size and opacity settings like the others. For the haptic link test scene.
    /// </summary>
    internal static (Transform card, TMP_Text text) AddCard(string name, float scale, float width, float textSize)
    {
        var kit = MakeKit();
        var root = kit.Node(name, null);
        root.gameObject.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        root.sizeDelta = new Vector2(width, 100);
        root.pivot = new Vector2(0.5f, 0.5f);
        root.localScale = Vector3.one * scale;

        var bg = root.gameObject.AddComponent<Image>();
        bg.sprite = kit.round;
        bg.type = Image.Type.Sliced;
        bg.color = Card;
        bg.material = kit.card;
        bg.raycastTarget = false;
        var look = root.gameObject.AddComponent<GlassLook>();
        look.card = bg;
        look.baseScale = scale;

        var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(34, 34, 24, 24);
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        root.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var text = kit.Label("Text", root, textSize, Primary, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);
        var edge = kit.Pic("Border", root, kit.ring, Border);
        edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        Kit.Stretch(edge.rectTransform);
        return (root, text);
    }

    /// <summary>
    /// The force graph's card (ForceGraph draws into it): a title, the plot (a RawImage with its axis labels around it, whose first child
    /// is the template of those labels), the tissues' legend, a summary and how to close it. A world-space Canvas under `parent`, off until shown.
    /// </summary>
    internal static (Transform card, RawImage plot, TMP_Text tick, TMP_Text legend, TMP_Text summary, TMP_Text saved) AddGraph(Transform parent)
    {
        const float scale = 0.0008f; // 1010 units: a card 0.8 m wide, read from about 0.75 m
        var kit = MakeKit();
        var root = kit.Node("GraphCard", parent);
        var canvas = root.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = TissueFadeBuilder.OverlayOrder + 10;
        root.sizeDelta = new Vector2(1010, 100);
        root.pivot = new Vector2(0.5f, 0.5f);
        root.localScale = Vector3.one * scale;

        var bg = root.gameObject.AddComponent<Image>();
        bg.sprite = kit.round;
        bg.type = Image.Type.Sliced;
        bg.color = Card;
        bg.material = kit.card;
        bg.raycastTarget = false;
        var look = root.gameObject.AddComponent<GlassLook>();
        look.card = bg;
        look.baseScale = scale;

        var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(34, 34, 24, 24);
        layout.spacing = 10;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        root.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var title = kit.Label("Title", root, 44, Primary, FontStyles.Bold, TextAlignmentOptions.TopLeft);
        title.text = "Force during the puncture";

        // The plot is 860 x 325 units inside a box that leaves room for the values on the left and below.
        var box = kit.Node("PlotBox", root);
        Kit.Elem(box, prefH: 377);
        var plot = kit.Node("Plot", box).gameObject.AddComponent<RawImage>();
        plot.material = kit.card;
        plot.raycastTarget = false;
        Kit.Stretch(plot.rectTransform);
        plot.rectTransform.offsetMin = new Vector2(74, 44);
        plot.rectTransform.offsetMax = new Vector2(-8, -8);
        var tick = kit.Label("Tick", plot.rectTransform, 22, Muted, FontStyles.Normal, TextAlignmentOptions.Center);
        tick.rectTransform.sizeDelta = new Vector2(90, 30);
        tick.gameObject.SetActive(false);

        var legend = kit.Label("Legend", root, 28, Secondary, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);
        var summary = kit.Label("Summary", root, 28, Secondary, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);
        var saved = kit.Label("Saved", root, 26, Info, FontStyles.Normal, TextAlignmentOptions.TopLeft, true); // where the run is saved
        var hint = kit.Label("Hint", root, 26, Muted, FontStyles.Normal, TextAlignmentOptions.TopLeft);
        hint.text = "A, or the stylus' button 1: close";
        look.texts = new TMP_Text[] { title, legend, summary, saved, hint };
        look.sizes = new[] { 44f, 28f, 28f, 26f, 26f };
        var edge = kit.Pic("Border", root, kit.ring, Border);
        edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        Kit.Stretch(edge.rectTransform);
        root.gameObject.SetActive(false); // ForceGraph shows it
        return (root, plot, tick, legend, summary, saved);
    }

    static Kit MakeKit()
    {
        var kit = new Kit
        {
            font = TMP_Settings.defaultFontAsset,
            round = MakeSprite("GlassRound", 128, Radius * 2, false),
            ring = MakeSprite("GlassRing", 128, Radius * 2, true),
            pill = MakeSprite("GlassPill", 72, 34, false),       // 17 units: the 34-unit toggle, 44-unit chips and 48-unit rows
            small = MakeSprite("GlassPillSmall", 32, 14, false), // 7 units: the slider's 14-unit bar
            card = Mat("Assets/Materials/GlassPanel.mat", "ModelTarget/GlassUI", null),
        };
        kit.text = Mat("Assets/Materials/GlassText.mat", "TextMeshPro/Mobile/Distance Field Overlay", kit.font.material);
        return kit;
    }

    internal static (GameObject panel, GlassPanel view) Build(Transform rig)
    {
        var kit = MakeKit();
        var root = kit.Node("Panel", rig);
        var canvas = root.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = TissueFadeBuilder.OverlayOrder + 10; // over the tissue, like the needle's message
        root.sizeDelta = new Vector2(Width, 100);
        root.pivot = new Vector2(0.5f, 1);
        root.localPosition = new Vector3(0, Top, 0);
        root.localScale = Vector3.one * 0.001f;

        var bg = root.gameObject.AddComponent<Image>();
        bg.sprite = kit.round;
        bg.type = Image.Type.Sliced;
        bg.color = Card;
        bg.material = kit.card;
        bg.raycastTarget = false;

        var look = root.gameObject.AddComponent<GlassLook>(); // the user's size and opacity from the menu
        look.card = bg;
        look.baseScale = 0.001f;

        var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset((int)Padding, (int)Padding, 28, 28);
        layout.spacing = 16;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        root.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var view = root.gameObject.AddComponent<GlassPanel>();
        view.width = Width;
        view.wideWidth = WideWidth;
        view.compactWidth = CompactWidth;
        view.padding = Padding;

        // title + progress dots
        var header = kit.Node("Header", root);
        Kit.Row(header, 12, 0, 0, TextAnchor.MiddleLeft);
        view.title = kit.Label("Title", header, 52, Primary, FontStyles.Bold, TextAlignmentOptions.Left, true);
        Kit.Elem(view.title, flexW: 1);
        var dots = kit.Node("Dots", header);
        var dotLayout = Kit.Row(dots, 10, 0, 0, TextAnchor.MiddleRight);
        dotLayout.childControlWidth = dotLayout.childControlHeight = false; // the dots keep their own size
        view.dots = Enumerable.Range(1, Dots).Select(i =>
        {
            var dot = kit.Pic("Dot " + i, dots, kit.pill, Track, false);
            dot.rectTransform.sizeDelta = new Vector2(18, 18);
            dot.gameObject.SetActive(false);
            return dot;
        }).ToArray();

        // badge: a pill as wide as its text
        var badgeRow = kit.Node("BadgeRow", root);
        Kit.Row(badgeRow, 0, 0, 0, TextAnchor.MiddleLeft);
        view.badge = badgeRow.gameObject;
        view.badgeBg = kit.Pic("Badge", badgeRow, kit.pill, Color.white);
        Kit.Row(view.badgeBg.rectTransform, 0, 26, 8, TextAnchor.MiddleCenter);
        view.badgeText = kit.Label("BadgeText", view.badgeBg.transform, 34, Primary, FontStyles.Bold, TextAlignmentOptions.Left);
        badgeRow.gameObject.SetActive(false);

        view.body = kit.Label("Body", root, 34, Primary, FontStyles.Normal, TextAlignmentOptions.TopLeft, true);

        // menu rows
        var rows = Kit.Stack(kit.Node("Rows", root));
        view.rowsRoot = rows.gameObject;
        view.rows = Enumerable.Range(1, Rows).Select(i => BuildRow(kit, rows, i)).ToArray();

        // two columns for the rows that a page lists in them (GlassPanel moves those rows in); each takes half the card
        view.columns = kit.Node("Columns", rows);
        var columnsLayout = Kit.Row(view.columns, 28, 0, 0, TextAnchor.UpperLeft);
        columnsLayout.childForceExpandWidth = true;
        view.left = Kit.Stack(kit.Node("Left", view.columns));
        view.right = Kit.Stack(kit.Node("Right", view.columns));
        Kit.Elem(view.left, prefW: 0, flexW: 1);
        Kit.Elem(view.right, prefW: 0, flexW: 1);
        view.columns.gameObject.SetActive(false);
        rows.gameObject.SetActive(false);

        // key chips, placed by hand (see GlassPanel.LayoutChips)
        var divider = kit.Node("Divider", root).gameObject.AddComponent<Image>();
        divider.color = new Color(1, 1, 1, 0.12f);
        divider.material = kit.card;
        divider.raycastTarget = false;
        Kit.Elem(divider, prefH: 2);
        view.divider = divider.gameObject;
        divider.gameObject.SetActive(false);

        var chips = kit.Node("Chips", root);
        view.chipsLayout = chips.gameObject.AddComponent<LayoutElement>();
        view.chips = Enumerable.Range(1, Chips).Select(i => BuildChip(kit, chips, i)).ToArray();
        chips.gameObject.SetActive(false);

        var edge = kit.Pic("Border", root, kit.ring, Border);
        edge.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        Kit.Stretch(edge.rectTransform);

        view.Show(new PanelModel()); // nothing on: a clean state to save
        return (root.gameObject, view);
    }

    // label on the left; on the right a slider (bar + percentage) or a toggle switch, whichever the row needs
    static GlassPanel.RowView BuildRow(Kit kit, Transform parent, int n)
    {
        var bg = kit.Pic("Row " + n, parent, kit.pill, Selected);
        bg.enabled = false; // only the selected row is tinted
        var rt = bg.rectTransform;
        Kit.Row(rt, 20, 22, 0, TextAnchor.MiddleLeft);
        var v = new GlassPanel.RowView { root = rt.gameObject, bg = bg, layout = rt.gameObject.AddComponent<LayoutElement>() };
        v.layout.minHeight = 48;

        v.label = kit.Label("Label", rt, 34, Primary, FontStyles.Normal, TextAlignmentOptions.Left);
        Kit.Elem(v.label, flexW: 1);

        var slider = kit.Node("Slider", rt);
        v.sliderLayout = Kit.Elem(slider, prefW: 300, prefH: 14); // GlassPanel narrows it as the text grows
        var bar = kit.Pic("Bar", slider, kit.small, Track);
        Kit.Stretch(bar.rectTransform);
        var fill = kit.Pic("Fill", slider, kit.small, Accent);
        fill.rectTransform.anchorMin = Vector2.zero;
        fill.rectTransform.anchorMax = new Vector2(0.5f, 1); // the right edge follows the value
        fill.rectTransform.offsetMin = fill.rectTransform.offsetMax = Vector2.zero;
        v.slider = slider.gameObject;
        v.fill = fill.rectTransform;

        v.value = kit.Label("Value", rt, 30, Muted, FontStyles.Normal, TextAlignmentOptions.Right);
        v.valueLayout = Kit.Elem(v.value, prefW: 90); // GlassPanel sizes it with the text, so every bar starts at the same place

        var toggle = kit.Node("Toggle", rt);
        Kit.Elem(toggle, prefW: 64, prefH: 34);
        v.track = kit.Pic("Track", toggle, kit.pill, Track);
        Kit.Stretch(v.track.rectTransform);
        var knob = kit.Pic("Knob", toggle, kit.pill, Color.white, false);
        v.knob = knob.rectTransform;
        v.knob.anchorMin = v.knob.anchorMax = new Vector2(0, 0.5f);
        v.knob.sizeDelta = new Vector2(26, 26);
        v.toggle = toggle.gameObject;

        v.slider.SetActive(false);
        v.value.gameObject.SetActive(false);
        v.toggle.SetActive(false);
        v.root.SetActive(false);
        return v;
    }

    // a key in a pill, then what it does
    static GlassPanel.ChipView BuildChip(Kit kit, Transform parent, int n)
    {
        var rect = Kit.TopLeft(kit.Node("Chip " + n, parent));
        var keyBg = Kit.TopLeft(kit.Pic("KeyBg", rect, kit.pill, KeyBg).rectTransform);
        var key = kit.Label("Key", keyBg, 26, Info, FontStyles.Bold, TextAlignmentOptions.Center);
        Kit.Stretch(key.rectTransform);
        var label = kit.Label("Label", rect, 28, Secondary, FontStyles.Normal, TextAlignmentOptions.Left);
        Kit.TopLeft(label.rectTransform);
        rect.gameObject.SetActive(false);
        return new GlassPanel.ChipView { root = rect.gameObject, rect = rect, keyBg = keyBg, key = key, label = label.rectTransform, labelText = label };
    }

    // The things every part is made of, with the font, materials and sprites they share.
    sealed class Kit
    {
        public TMP_FontAsset font;
        public Material card, text;
        public Sprite round, ring, pill, small;

        public RectTransform Node(string name, Transform parent)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        public Image Pic(string name, Transform parent, Sprite sprite, Color color, bool sliced = true)
        {
            var img = Node(name, parent).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.material = card;
            img.raycastTarget = false;
            return img;
        }

        public TextMeshProUGUI Label(string name, Transform parent, float size, Color color, FontStyles style, TextAlignmentOptions align, bool wrap = false)
        {
            var t = Node(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            t.font = font;
            t.fontSharedMaterial = text;
            t.fontSize = size;
            t.color = color;
            t.fontStyle = style;
            t.alignment = align;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            t.richText = true;
            t.raycastTarget = false;
            return t;
        }

        public static HorizontalLayoutGroup Row(RectTransform rt, float spacing, int padX, int padY, TextAnchor align)
        {
            var row = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.spacing = spacing;
            row.padding = new RectOffset(padX, padX, padY, padY);
            row.childAlignment = align;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            return row;
        }

        // Children one under the other, as wide as the parent, as tall as they need (the menu rows).
        public static RectTransform Stack(RectTransform rt)
        {
            var stack = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            stack.spacing = 2;
            stack.childControlWidth = stack.childControlHeight = true;
            stack.childForceExpandWidth = true;
            stack.childForceExpandHeight = false;
            return rt;
        }

        public static LayoutElement Elem(Component c, float prefW = -1, float prefH = -1, float flexW = -1)
        {
            var e = c.gameObject.AddComponent<LayoutElement>();
            if (prefW >= 0) e.preferredWidth = prefW;
            if (prefH >= 0) e.preferredHeight = prefH;
            if (flexW >= 0) e.flexibleWidth = flexW;
            return e;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        public static RectTransform TopLeft(RectTransform rt)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            return rt;
        }
    }

    // A white rounded rectangle (or just its outline) with anti-aliased edges, sliced at the corner radius (r pixels).
    // 200 pixels per unit against the canvas' 100: one sprite pixel is half a canvas unit, so the corner radius is r / 2 units.
    static Sprite MakeSprite(string name, int size, int r, bool outline)
    {
        string path = $"{Folder}/{name}.png";
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (existing != null) return existing;

        const int thickness = 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float half = size / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // signed distance to the rounded box, in pixels: negative inside
                float qx = Mathf.Abs(x + 0.5f - half) - (half - r), qy = Mathf.Abs(y + 0.5f - half) - (half - r);
                float d = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
                float a = Mathf.Clamp01(0.5f - d);
                if (outline) a = Mathf.Min(a, Mathf.Clamp01(d + thickness + 0.5f));
                tex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);

        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 200;
        importer.spriteBorder = new Vector4(r, r, r, r);
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect; // a sliced sprite needs the whole rect
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // The material at path with this shader; "from" copies the values of another material (the font's atlas) when it is made.
    static Material Mat(string path, string shaderName, Material from)
    {
        var shader = Shader.Find(shaderName);
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = from != null ? new Material(from) : new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.shader = shader;
        if (from != null) mat.mainTexture = from.mainTexture; // the atlas of the font in use
        EditorUtility.SetDirty(mat);
        return mat;
    }
}
