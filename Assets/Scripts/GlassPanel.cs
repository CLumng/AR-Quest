using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>What the panel shows. The aligner fills one every frame; GlassPanel only redraws when it differs from the last one.</summary>
public class PanelModel
{
    public enum Kind { Action, Slider, Toggle, Header }

    public struct Row
    {
        public Kind kind;
        public string label;
        public bool selected, on; // selected = the menu cursor; on = a toggle's state
        public float value;       // slider, 0..1 (the bar)
        public string valueText;  // on the right of a row; a slider without one shows its value as a percentage
    }

    public struct Key { public string key, label; }

    public string title = "", body = "", badge = ""; // badge = a coloured pill under the title ("Good  3.1 mm")
    public Color badgeColor;
    public int step, steps; // progress dots: step of steps, 1-based; steps 0 = none
    public bool compact;    // only the title and the keys, in a narrower card
    public int columnsFrom, columnsCount; // rows[columnsFrom ..< columnsFrom + columnsCount] go in two columns (the first half down the left one) in a wider card; the others span it
    public readonly List<Row> rows = new();
    public readonly List<Key> keys = new();

    /// <summary>How many of n rows in columns go in the left one.</summary>
    public static int Half(int n) => (n + 1) / 2;

    public void Clear()
    {
        title = body = badge = "";
        badgeColor = default;
        step = steps = columnsFrom = columnsCount = 0;
        compact = false;
        rows.Clear();
        keys.Clear();
    }

    public void Add(Kind kind, string label, bool selected = false, bool on = false, float value = 0, string valueText = null) =>
        rows.Add(new Row { kind = kind, label = label, selected = selected, on = on, value = value, valueText = valueText });

    public void Hint(string key, string label) => keys.Add(new Key { key = key, label = label });

    public bool Same(PanelModel o)
    {
        if (title != o.title || body != o.body || badge != o.badge || badgeColor != o.badgeColor || step != o.step || steps != o.steps ||
            compact != o.compact || columnsFrom != o.columnsFrom || columnsCount != o.columnsCount || rows.Count != o.rows.Count || keys.Count != o.keys.Count) return false;
        for (int i = 0; i < rows.Count; i++)
        {
            Row a = rows[i], b = o.rows[i];
            if (a.kind != b.kind || a.label != b.label || a.selected != b.selected || a.on != b.on || a.value != b.value || a.valueText != b.valueText) return false;
        }
        for (int i = 0; i < keys.Count; i++)
            if (keys[i].key != o.keys[i].key || keys[i].label != o.keys[i].label) return false;
        return true;
    }

    public void CopyFrom(PanelModel o)
    {
        title = o.title; body = o.body; badge = o.badge; badgeColor = o.badgeColor;
        step = o.step; steps = o.steps; compact = o.compact; columnsFrom = o.columnsFrom; columnsCount = o.columnsCount;
        rows.Clear(); rows.AddRange(o.rows);
        keys.Clear(); keys.AddRange(o.keys);
    }
}

/// <summary>
/// The alignment panel: a card with a title, progress dots, a badge, a text, menu rows (action, slider, toggle switch)
/// and a line of key chips. Built by GlassPanelBuilder with pools of each part; Show switches on what the model needs.
/// </summary>
public class GlassPanel : MonoBehaviour
{
    [System.Serializable]
    public class RowView
    {
        public GameObject root, slider, toggle;
        public Image bg, track;
        public LayoutElement layout, sliderLayout, valueLayout;
        public TMP_Text label, value;
        public RectTransform fill, knob;
    }

    [System.Serializable]
    public class ChipView
    {
        public GameObject root;
        public RectTransform rect, keyBg, label;
        public TMP_Text key, labelText;
    }

    public TMP_Text title, body, badgeText;
    public Image badgeBg;
    public GameObject badge, divider, rowsRoot; // badge = its whole row
    public RectTransform columns, left, right;  // inside rowsRoot: the two columns that the rows of a model's columns move into
    public Image[] dots;
    public RowView[] rows;
    public ChipView[] chips;
    public LayoutElement chipsLayout;
    public float width = 900, wideWidth = 1200, compactWidth = 600, padding = 34;

    static readonly Color Accent = new(1f, 0.83f, 0f), Primary = new(0.945f, 0.957f, 0.973f), Muted = new(0.60f, 0.64f, 0.70f),
        Todo = new(0.34f, 0.38f, 0.44f), Off = new(0.34f, 0.38f, 0.44f), On = new(0.30f, 0.85f, 0.40f);

    [System.NonSerialized] PanelModel _shown;

    // The text size is the user's (GlassSettings): when it changes the same model is drawn again with the new font sizes.
    void OnEnable()
    {
        GlassSettings.Changed += Refresh;
        Refresh();
    }

    void OnDisable() => GlassSettings.Changed -= Refresh;

    public void Refresh()
    {
        if (_shown != null) Apply(_shown);
    }

    public void Show(PanelModel m)
    {
        if (_shown != null && _shown.Same(m)) return;
        (_shown ??= new PanelModel()).CopyFrom(m);
        Apply(_shown);
    }

    // Font sizes are written here at 100 % and multiplied by the user's text size; heights that hold text grow with it.
    void Apply(PanelModel m)
    {
        float t = GlassSettings.TextSize;
        var rt = (RectTransform)transform;
        bool split = !m.compact && m.columnsCount > 0;
        float cardWidth = m.compact ? compactWidth : split ? wideWidth : width;
        rt.sizeDelta = new Vector2(cardWidth, rt.sizeDelta.y);

        title.text = m.title;
        title.fontSize = (m.compact ? 40 : 52) * t;
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].gameObject.SetActive(i < m.steps);
            dots[i].color = i + 1 < m.step ? new Color(Accent.r, Accent.g, Accent.b, 0.55f) : i + 1 == m.step ? Accent : Todo;
        }

        badge.SetActive(m.badge != "" && !m.compact);
        badgeText.text = m.badge;
        badgeText.fontSize = 34 * t;
        badgeText.color = m.badgeColor;
        badgeBg.color = new Color(m.badgeColor.r, m.badgeColor.g, m.badgeColor.b, 0.18f);

        body.gameObject.SetActive(m.body != "" && !m.compact);
        body.text = m.body;
        body.fontSize = 34 * t;

        rowsRoot.SetActive(!m.compact && m.rows.Count > 0);
        columns.gameObject.SetActive(split);
        int half = PanelModel.Half(m.columnsCount), at = 0; // at = the place in rowsRoot of the next row that spans the card
        for (int i = 0; i < rows.Length; i++)
        {
            // the pool's rows move into the columns when the model has them, and back out when it does not
            int c = i - m.columnsFrom;
            bool inColumns = split && c >= 0 && c < m.columnsCount;
            var tr = rows[i].root.transform;
            Transform parent = !inColumns ? rowsRoot.transform : c < half ? left : right;
            if (tr.parent != parent) tr.SetParent(parent, false);
            if (split && i == m.columnsFrom) columns.SetSiblingIndex(at++);
            if (!inColumns) tr.SetSiblingIndex(at++);

            bool shown = !m.compact && i < m.rows.Count;
            rows[i].root.SetActive(shown);
            if (shown) Fill(rows[i], m.rows[i], t);
        }

        divider.SetActive(m.keys.Count > 0 && !m.compact);
        chipsLayout.gameObject.SetActive(m.keys.Count > 0);
        LayoutChips(m.keys, cardWidth - 2 * padding, t);
    }

    static void Fill(RowView v, PanelModel.Row r, float t)
    {
        bool header = r.kind == PanelModel.Kind.Header;
        v.label.text = r.label;
        v.label.fontSize = (header ? 28 : 34) * t;
        v.label.color = header ? Muted : r.selected ? Accent : Primary;
        v.layout.minHeight = (header ? 40 : 48) * t;
        v.bg.enabled = r.selected;

        bool slider = r.kind == PanelModel.Kind.Slider, showValue = slider || r.valueText != null;
        v.slider.SetActive(slider);
        v.value.gameObject.SetActive(showValue);
        float value = Mathf.Clamp01(r.value);
        if (slider)
        {
            v.sliderLayout.preferredWidth = Mathf.Round(300 * (2 - t)); // the label needs the room that bigger text takes: 210 at 130 %
            v.fill.gameObject.SetActive(value > 0.03f); // a sliced pill needs its two end caps
            v.fill.anchorMax = new Vector2(value, 1);
        }
        if (showValue)
        {
            v.value.text = r.valueText ?? Mathf.RoundToInt(value * 100) + "%";
            v.value.fontSize = 30 * t;
            v.valueLayout.preferredWidth = Mathf.Round(90 * t); // "100%" fits at any text size
            v.value.color = r.selected ? Accent : Muted;
        }

        v.toggle.SetActive(r.kind == PanelModel.Kind.Toggle);
        if (r.kind == PanelModel.Kind.Toggle)
        {
            v.track.color = r.on ? On : Off;
            v.knob.anchoredPosition = new Vector2(r.on ? 48 : 16, 0); // track is 64 wide, the knob 26
        }
    }

    // Chips flow left to right and wrap when the next one does not fit. uGUI has no wrapping layout, so they are placed by hand.
    void LayoutChips(List<PanelModel.Key> keys, float room, float t)
    {
        float height = Mathf.Round(44 * t); // the pill is 34 units at least, hence the text size has a floor
        const float keyPad = 14, gapKeyLabel = 12, gap = 28, rowGap = 10;
        float x = 0, y = 0;
        for (int i = 0; i < chips.Length; i++)
        {
            var c = chips[i];
            c.root.SetActive(i < keys.Count);
            if (i >= keys.Count) continue;
            c.key.text = keys[i].key;
            c.labelText.text = keys[i].label;
            c.key.fontSize = 26 * t;
            c.labelText.fontSize = 28 * t;
            float kw = c.key.GetPreferredValues(keys[i].key).x + 2 * keyPad, lw = c.labelText.GetPreferredValues(keys[i].label).x + 4;
            float w = kw + gapKeyLabel + lw;
            if (x > 0 && x + w > room) { x = 0; y += height + rowGap; }
            c.rect.anchoredPosition = new Vector2(x, -y);
            c.rect.sizeDelta = new Vector2(w, height);
            c.keyBg.sizeDelta = new Vector2(kw, height);
            c.label.anchoredPosition = new Vector2(kw + gapKeyLabel, 0);
            c.label.sizeDelta = new Vector2(lw, height);
            x += w + gap;
        }
        chipsLayout.preferredHeight = keys.Count == 0 ? 0 : y + height;
    }
}
