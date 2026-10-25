using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The force graph of a puncture done with the Touch stylus. It records what the stylus is pushed with, from the frame the needle goes
/// through the skin to the moment the fluid extraction is stopped, and only while the forces are armed: if they are turned off, or the
/// needle comes out of the model, before the extraction is over, the run is dropped and nothing is drawn. Once it is complete, the graph
/// appears when the user has taken the needle out of the model and turned the forces off, on a card in front of them: the force along the
/// needle (+ = against the advance) and the sideways one against time, over bands that say which tissue the tip was in, then the fluid.
/// A (or the stylus' button 1, see NeedleAction.Toggle) closes it; a new puncture with the forces on starts over. The card is out of the
/// way while the menu is open. All UI text is English.
/// Each complete run is also saved as a CSV (Csv): on the PC by the bridge, which the headset sends it to over the link (HapticStylus.SendRun;
/// it goes again after a drop until the bridge answers), and a copy on the headset (runsDir). The card says where it is saved.
/// </summary>
[DefaultExecutionOrder(20)] // after HapticStylus.Flush (10) has this frame's force
public class ForceGraph : MonoBehaviour
{
    public struct Sample
    {
        public float t;      // s since the needle went in
        public float depth;  // m of needle under the skin
        public float along;  // N along the needle, + = against the advance
        public float side;   // N across the needle
        public float table;  // N the tissue's formula gives along the needle before scaling to the device, + = against the advance
        public bool fluid;   // the fluid is being extracted
    }

    public enum Mode { Idle, Recording, Done, Shown }

    public NeedleAction needle;
    public HapticStylus stylus;
    public Transform card;       // the world-space card; shown when the graph is ready (null in the checks)
    public RawImage plot;        // the curves go here
    public TMP_Text tick;        // an inactive child of plot: the template of the axis labels
    public TMP_Text legend;      // the tissues, in their colours
    public TMP_Text summary;     // the peak, the time, what the lines are
    public TMP_Text saved;       // where the run is saved (on the PC, on the headset)
    public string runsDir;       // the headset's copy of each run goes here; empty = Application.persistentDataPath/runs
    public int width = 1032, height = 390; // the texture, in pixels (its shape is the plot's, 860 x 325 units)
    public float distance = 0.75f;         // m in front of the user's head where the card appears

    public const int MaxSamples = 40000;   // about 8 minutes at 72 Hz; a longer run is cut here
    public const float ReentrySeconds = 1; // a needle that was out of the model for less than this is not starting a new puncture after a complete run

    // The tissues' colours (the bands under the lines and the legend), then the fluid's.
    static readonly Color32[] Palette =
    {
        new(255, 179, 140, 255), new(200, 205, 214, 255), new(128, 217, 148, 255), new(102, 204, 224, 255),
        new(168, 158, 255, 255), new(242, 148, 209, 255), new(255, 128, 128, 255), new(89, 166, 255, 255),
    };
    static readonly Color32 AlongColor = new(255, 212, 0, 255), SideColor = new(239, 244, 250, 255);
    const int Fluid = -2; // the band of the fluid extraction; the tissues' are their index in the table, -1 = before the first one

    public Mode State { get; private set; }
    public bool Visible => State == Mode.Shown;
    public IReadOnlyList<Sample> Samples => _samples;
    public Texture2D Texture => _tex;

    readonly List<Sample> _samples = new();
    readonly List<TMP_Text> _labels = new();
    Texture2D _tex;
    Color32[] _pixels;
    float[] _cov;
    float _t;
    bool _wasIn;
    float _outFor;         // s the needle has been out of the model
    long _runId;           // the complete run's id (what the PC knows it by) and its file on the headset
    string _runFile;
    bool _ackShown, _liveShown;

    /// <summary>Where the run is saved, in words (the card shows it).</summary>
    public string SavedText
    {
        get
        {
            if (stylus != null && stylus.RunSaved(_runId, out var name)) return $"Saved on the PC: {name}";
            string here = _runFile != null ? $"Saved on the headset: {Path.GetFileName(_runFile)}" : "Not saved";
            return stylus != null && stylus.Live ? here + ". Sending it to the PC..." : here + ". The PC is not connected: it goes when the bridge is back.";
        }
    }

    void LateUpdate() { Tick(Time.deltaTime); }

    /// <summary>One frame. Public for the checks, which drive it after HapticStylus.Flush.</summary>
    public void Tick(float dt)
    {
        if (needle == null || stylus == null) return;
        bool armed = stylus.Active && stylus.ForcesOn;
        bool inBody = needle.Current != NeedleAction.State.Outside;
        float away = _outFor; // how long the needle has been out of the model, before this frame
        _outFor = inBody ? 0 : _outFor + dt;
        if (armed && inBody && !_wasIn)
        {
            // Going through the skin with the forces on starts a run, over whatever was there. Not a complete run that waits to be shown, though,
            // if the needle was out for a moment only: the tip skimming the skin on its way out is not a new puncture.
            if (State == Mode.Done && away < ReentrySeconds) Debug.Log($"ForceGraph: the needle went in again after {away:F2} s out: the complete run still waits");
            else Begin();
        }
        _wasIn = inBody;

        switch (State)
        {
            case Mode.Recording:
                if (!armed || !inBody) Reset(!armed ? "the forces went off" : "the needle came out");   // before the end of the extraction: no graph
                else if (needle.Current == NeedleAction.State.Withdrawing) { State = Mode.Done; Save(); Debug.Log($"ForceGraph: run complete, {_samples.Count} samples, {_t:F1} s; it waits for the needle to be out and the forces off"); } // the extraction is stopped
                else Record(dt);
                break;
            case Mode.Done:
                if (!inBody && !armed) Show(); // the needle is out and the forces are off
                break;
            case Mode.Shown:
                if (saved != null && (stylus.RunSaved(_runId, out _) != _ackShown || stylus.Live != _liveShown)) ShowSaved(); // the PC's answer, or the link, changed
                break;
        }
        if (card != null) card.gameObject.SetActive(State == Mode.Shown && !needle.inMenu);
    }

    /// <summary>
    /// The run as CSV text, one row per frame: time (s), depth (mm), tissue the tip is in, phase (insertion or fluid), the force the tissue's
    /// formula gives along the needle before scaling (N), the force sent to the stylus along the needle (N, + = against the advance) and
    /// across it (N).
    /// </summary>
    public string Csv()
    {
        var sb = new StringBuilder("time_s,depth_mm,tissue,phase,formula_N,along_N,side_N\r\n");
        var layers = needle != null && needle.haptics != null ? needle.haptics.layers : null;
        foreach (var s in _samples)
        {
            int layer = LayerOf(s.depth);
            string tissue = layer < 0 ? "" : layers[layer].name;
            if (tissue.IndexOfAny(new[] { ',', '"' }) >= 0) tissue = "\"" + tissue.Replace("\"", "\"\"") + "\"";
            sb.Append(Num(s.t, "0.####")).Append(',').Append(Num(s.depth * 1000, "0.###")).Append(',').Append(tissue).Append(',').Append(s.fluid ? "fluid" : "insertion").Append(',')
              .Append(Num(s.table, "0.#####")).Append(',').Append(Num(s.along, "0.#####")).Append(',').Append(Num(s.side, "0.#####")).Append("\r\n");
        }
        return sb.ToString();
    }

    // The run is complete: a copy on the headset, and the same text to the PC (it keeps trying until the bridge says it is saved).
    void Save()
    {
        string csv = Csv();
        _runId = System.DateTime.UtcNow.Ticks;
        _runFile = null;
        try
        {
            string dir = string.IsNullOrEmpty(runsDir) ? Path.Combine(Application.persistentDataPath, "runs") : runsDir;
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"run-{System.DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(file, csv, new UTF8Encoding(false));
            _runFile = file;
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException) { Debug.LogWarning($"ForceGraph: the run could not be saved on the headset: {e.Message}"); }
        stylus.SendRun(_runId, csv);
    }

    void ShowSaved()
    {
        _ackShown = stylus.RunSaved(_runId, out _);
        _liveShown = stylus.Live;
        if (saved != null) saved.text = SavedText;
    }

    /// <summary>Closes the graph and forgets the run.</summary>
    public void Hide() { Reset("closed"); }

    /// <summary>One more point of the curve (Tick does it each frame of a run).</summary>
    public void Add(Sample s)
    {
        if (_samples.Count < MaxSamples) _samples.Add(s);
    }

    void Begin()
    {
        _samples.Clear();
        _t = 0;
        State = Mode.Recording;
        Debug.Log("ForceGraph: run started");
    }

    void Reset(string why)
    {
        Debug.Log($"ForceGraph: {State} -> nothing ({why}, {_samples.Count} samples)");
        _samples.Clear();
        State = Mode.Idle;
    }

    void Record(float dt)
    {
        _t += dt;
        var force = stylus.SentWorld;
        var dir = needle.Dir;
        float along = Vector3.Dot(force, dir);
        Add(new Sample
        {
            t = _t, depth = needle.Depth, along = -along, side = (force - dir * along).magnitude,
            table = needle.haptics != null ? -needle.haptics.Axial : 0, fluid = needle.Current == NeedleAction.State.Extracting,
        });
    }

    void Show()
    {
        State = Mode.Shown;
        Debug.Log($"ForceGraph: showing the graph of {_samples.Count} samples");
        var cam = Camera.main;
        if (card != null && cam != null) // first in front of the user, so whatever goes wrong in the drawing, the card is not left at the origin
        {
            var forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward; // looking straight up or down
            var pos = cam.transform.position + forward.normalized * distance + Vector3.down * 0.05f;
            card.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - cam.transform.position));
        }
        Draw();
        ShowSaved();
    }

    int BandOf(in Sample s) => s.fluid ? Fluid : LayerOf(s.depth);

    // The tissue the tip is in at this depth: its index in the table, -1 before the first one.
    int LayerOf(float depth)
    {
        var layers = needle != null && needle.haptics != null ? needle.haptics.layers : null;
        if (layers == null) return -1;
        int found = -1;
        for (int i = 0; i < layers.Length; i++) if (layers[i].start <= depth) found = i;
        return found;
    }

    static Color32 BandColor(int band) => band == Fluid ? Palette[7] : Palette[band % 7];

    string BandName(int band) => band == Fluid ? "Fluid extraction" : needle.haptics.layers[band].name;

    /// <summary>Draws the run into the texture (and the card's labels, if it has them).</summary>
    public void Draw()
    {
        int n = _samples.Count;
        if (n < 2) return;
        int W = width, H = height;
        if (_tex == null || _tex.width != W || _tex.height != H)
        {
            if (_tex != null) DestroyObject(_tex);
            _tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ForceGraph" };
            _pixels = new Color32[W * H];
            _cov = new float[W * H];
        }

        // The axes: time from 0 to the end of the run, force from the lowest to the highest of either line, in round steps.
        float T = Mathf.Max(_samples[n - 1].t, 0.1f);
        float lo = 0, hi = 0;
        foreach (var s in _samples)
        {
            lo = Mathf.Min(lo, Mathf.Min(s.along, s.side));
            hi = Mathf.Max(hi, Mathf.Max(s.along, s.side));
        }
        float ys = Nice(Mathf.Max(hi - lo, 0.1f) / 7);
        float ymin = lo > -0.1f * ys ? 0 : Mathf.Floor(lo / ys) * ys; // a whisper below zero does not earn a row of the axis (the margin shows it)
        float ymax = Mathf.Max(Mathf.Ceil(hi / ys) * ys, ymin + ys);
        float xs = Nice(T / 6);
        float ylo = ymin - 0.04f * (ymax - ymin), yhi = ymax + 0.04f * (ymax - ymin), xlo = -0.015f * T, xhi = 1.015f * T; // a margin, so the lines are not cut at the edge
        float X(float t) => (t - xlo) / (xhi - xlo) * W;
        float Y(float v) => (v - ylo) / (yhi - ylo) * H;

        System.Array.Fill(_pixels, new Color32(0, 0, 0, 90));

        // Bands: which tissue the tip was in (the first one runs from the left edge, the last to the right).
        var bands = new List<int>();
        int runStart = 0, runBand = BandOf(_samples[0]);
        for (int i = 1; i <= n; i++)
        {
            int b = i < n ? BandOf(_samples[i]) : int.MinValue;
            if (b == runBand) continue;
            int x0 = runStart == 0 ? 0 : Mathf.Clamp(Mathf.RoundToInt(X(_samples[runStart].t)), 0, W);
            int x1 = i == n ? W : Mathf.Clamp(Mathf.RoundToInt(X(_samples[i].t)), 0, W);
            if (runBand != -1)
            {
                if (!bands.Contains(runBand)) bands.Add(runBand);
                var color = BandColor(runBand);
                for (int y = 0; y < H; y++)
                    for (int x = x0; x < x1; x++) Over(ref _pixels[y * W + x], color, 0.2f);
            }
            runStart = i;
            runBand = b;
        }

        // Grid: a line at each round value of force (the zero line brighter) and of time.
        var white = new Color32(255, 255, 255, 255);
        for (float v = ymin; v <= ymax + ys * 0.01f; v += ys)
        {
            int row = Mathf.RoundToInt(Y(v));
            float a = Mathf.Abs(v) < ys * 0.01f ? 0.4f : 0.13f;
            for (int y = Mathf.Max(0, row - 1); y <= Mathf.Min(H - 1, row); y++)
                for (int x = 0; x < W; x++) Over(ref _pixels[y * W + x], white, a);
        }
        for (float t = 0; t <= T + xs * 0.01f; t += xs)
        {
            int col = Mathf.RoundToInt(X(t));
            for (int x = Mathf.Max(0, col - 1); x <= Mathf.Min(W - 1, col); x++)
                for (int y = 0; y < H; y++) Over(ref _pixels[y * W + x], white, 0.07f);
        }

        Stroke(false, 1.2f, SideColor, X, Y);
        Stroke(true, 2.4f, AlongColor, X, Y);

        _tex.SetPixelData(_pixels, 0);
        _tex.Apply(false);
        if (plot != null) plot.texture = _tex;

        // Labels: the values at the grid lines (the unit goes with the last one), then the texts.
        int used = 0;
        for (float v = ymin; v <= ymax + ys * 0.01f; v += ys)
            Label(used++, Num(v, "0.##") + (v + ys > ymax + ys * 0.01f ? " N" : ""), new Vector2(0, (Y(v)) / H), new Vector2(1, 0.5f), new Vector2(-8, 0));
        for (float t = 0; t <= T + xs * 0.01f; t += xs)
            Label(used++, Num(t, "0.#") + (t + xs > T + xs * 0.01f ? " s" : ""), new Vector2(X(t) / W, 0), new Vector2(0.5f, 1), new Vector2(0, -6));
        for (int i = used; i < _labels.Count; i++) _labels[i].gameObject.SetActive(false);

        if (legend != null)
        {
            var sb = new StringBuilder();
            foreach (int b in bands)
            {
                var c = BandColor(b);
                sb.Append($"<color=#{c.r:X2}{c.g:X2}{c.b:X2}>{BandName(b)}</color>   ");
            }
            legend.text = sb.ToString().TrimEnd();
        }
        if (summary != null)
        {
            int peak = 0;
            for (int i = 1; i < n; i++) if (_samples[i].along > _samples[peak].along) peak = i;
            int where = BandOf(_samples[peak]);
            summary.text = $"Peak <b>{Num(_samples[peak].along, "0.00")} N</b> in {(where < 0 ? "the skin" : BandName(where))}   ·   {Num(T, "0")} s in all\n" +
                "<color=#FFD400>Along the needle</color> (+ pushes against the advance)   <color=#EFF4FA>Sideways</color>";
        }
    }

    // The label i of the pool, at this place in the plot (anchors are fractions of it).
    void Label(int i, string text, Vector2 anchor, Vector2 pivot, Vector2 offset)
    {
        if (tick == null) return;
        while (_labels.Count <= i) _labels.Add(Instantiate(tick, tick.transform.parent));
        var l = _labels[i];
        l.gameObject.SetActive(true);
        l.text = text;
        l.alignment = pivot.x >= 1 ? TextAlignmentOptions.Right : TextAlignmentOptions.Center; // the values on the left hug the axis
        var rt = l.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = offset;
    }

    // One line through the samples: its distance to each pixel gives the coverage (soft edges), the strongest one counts, then it is laid over.
    void Stroke(bool along, float r, Color32 color, System.Func<float, float> X, System.Func<float, float> Y)
    {
        System.Array.Clear(_cov, 0, _cov.Length);
        Vector2 a = Point(0, along, X, Y);
        for (int i = 1; i < _samples.Count; i++)
        {
            var b = Point(i, along, X, Y);
            Segment(a, b, r);
            a = b;
        }
        for (int k = 0; k < _cov.Length; k++) if (_cov[k] > 0) Over(ref _pixels[k], color, _cov[k]);
    }

    Vector2 Point(int i, bool along, System.Func<float, float> X, System.Func<float, float> Y) =>
        new(X(_samples[i].t), Y(along ? _samples[i].along : _samples[i].side));

    void Segment(Vector2 a, Vector2 b, float r)
    {
        int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - r - 1)), x1 = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + r + 1));
        int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - r - 1)), y1 = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + r + 1));
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float t = len2 > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0;
                float c = Mathf.Clamp01(r + 0.5f - (p - (a + ab * t)).magnitude);
                int k = y * width + x;
                if (c > _cov[k]) _cov[k] = c;
            }
    }

    // `s` over `d`, with this coverage.
    static void Over(ref Color32 d, Color32 s, float coverage)
    {
        float sa = coverage * s.a / 255f, da = d.a / 255f, oa = sa + da * (1 - sa);
        if (oa <= 0) return;
        d.r = (byte)((s.r * sa + d.r * da * (1 - sa)) / oa + 0.5f);
        d.g = (byte)((s.g * sa + d.g * da * (1 - sa)) / oa + 0.5f);
        d.b = (byte)((s.b * sa + d.b * da * (1 - sa)) / oa + 0.5f);
        d.a = (byte)(oa * 255 + 0.5f);
    }

    // A round step (1, 2, 5 times a power of ten) at least as big as this.
    static float Nice(float raw)
    {
        float pow = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(raw)));
        float m = raw / pow;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * pow;
    }

    static string Num(float v, string format) => (Mathf.Abs(v) < 1e-6f ? 0 : v).ToString(format, CultureInfo.InvariantCulture);

    static void DestroyObject(Object o)
    {
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    void OnDestroy() { if (_tex != null) DestroyObject(_tex); }
}
