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
/// appears when the user has taken the needle out of the model and turned the forces off, on a card in front of them. It is drawn like the
/// ideal curve (Python/IdealCurve.py): force against depth, over a band per tissue of the table with a dotted line where each one starts;
/// the ideal curve of the tissues' formula (white: an even advance, a function of depth alone, NeedleHaptics.Ideal; the CSV's formula_N is
/// the formula at the hand's real speed) and the force sent to the stylus along the needle (yellow), + = against the advance. Only the
/// insertion is drawn: during the fluid extraction the needle stands still (the CSV has it all).
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
        public NeedleAction.State state;
        public Vector3 tip;      // m, the needle's tip in the model's frame: the tip's trajectory
        public float off;        // m, how far the tip is from the ideal path's line
        public float toTarget;   // m, from the tip to the target
        public float angle;      // degrees between the needle and the ideal path
        public float rtt, age, frame; // ms: command to acknowledgement over the link, how old the stylus' pose was when read, the frame's length
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
    public const int IdealPoints = 400;    // the ideal curve is drawn from this many depths between the skin and the deepest point
    public const float ReentrySeconds = 1; // a needle that was out of the model for less than this is not starting a new puncture after a complete run

    // The tissues' colours (the bands under the lines and the legend), by their index in the table.
    static readonly Color32[] Palette =
    {
        new(255, 179, 140, 255), new(200, 205, 214, 255), new(128, 217, 148, 255), new(102, 204, 224, 255),
        new(168, 158, 255, 255), new(242, 148, 209, 255), new(255, 128, 128, 255), new(89, 166, 255, 255),
    };
    static readonly Color32 AlongColor = new(255, 212, 0, 255), FormulaColor = new(239, 244, 250, 255), EdgeColor = new(170, 176, 186, 255);

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
    /// across it (N). For the system's characterization (Python/Characterization.py) each row also has the needle's state, its tip in the
    /// model's frame (mm: the trajectory), the tip's distance to the ideal path's line and to the target (mm), the angle to the path
    /// (degrees), and the link's timing (ms): round trip, age of the stylus' pose, frame length. The last six columns (meta_) are
    /// constants of the run and only the first row has them: see Meta.
    /// </summary>
    public string Csv()
    {
        var sb = new StringBuilder("time_s;depth_mm;tissue;phase;formula_N;along_N;side_N;state;tip_x_mm;tip_y_mm;tip_z_mm;off_mm;target_mm;angle_deg;rtt_ms;age_ms;frame_ms;" +
            "meta_reg_res_mm;meta_reg_moved_mm;meta_reg_turned_deg;meta_landmarks_mm;meta_entry_mm;meta_target_mm\r\n");
        var layers = needle != null && needle.haptics != null ? needle.haptics.layers : null;
        string[] meta = Meta();
        for (int i = 0; i < _samples.Count; i++)
        {
            var s = _samples[i];
            int layer = LayerOf(s.depth);
            string tissue = layer < 0 ? "" : layers[layer].name;
            if (tissue.IndexOfAny(new[] { ';', '"' }) >= 0) tissue = "\"" + tissue.Replace("\"", "\"\"") + "\"";
            sb.Append(Num(s.t, "0.####")).Append(';').Append(Num(s.depth * 1000, "0.###")).Append(';').Append(tissue).Append(';').Append(s.fluid ? "fluid" : "insertion").Append(';')
              .Append(Num(s.table, "0.#####")).Append(';').Append(Num(s.along, "0.#####")).Append(';').Append(Num(s.side, "0.#####")).Append(';')
              .Append(s.state).Append(';').Append(Num(s.tip.x * 1000, "0.###")).Append(';').Append(Num(s.tip.y * 1000, "0.###")).Append(';').Append(Num(s.tip.z * 1000, "0.###")).Append(';')
              .Append(Num(s.off * 1000, "0.###")).Append(';').Append(Num(s.toTarget * 1000, "0.###")).Append(';').Append(Num(s.angle, "0.##")).Append(';')
              .Append(Num(s.rtt, "0.##")).Append(';').Append(Num(s.age, "0.##")).Append(';').Append(Num(s.frame, "0.##"))
              .Append(i == 0 ? ";" + string.Join(";", meta) : ";;;;;;").Append("\r\n");
        }
        return sb.ToString();
    }

    // The run's constants, for the first row: the residual of each landmark in the last fit (mm, space separated), how far the model has been
    // moved since that fit (mm, degrees), and, in the model's frame (mm), the landmarks (x y z, separated by |), the ideal path's entry and the
    // target. They are what the analysis needs to estimate the registration error at the target. Empty where they are not known.
    string[] Meta()
    {
        var meta = new[] { "", "", "", "", "", "" };
        var aligner = stylus != null ? stylus.aligner : null;
        if (aligner != null && aligner.Registration(out var residuals, out float moved, out float turned))
        {
            meta[0] = string.Join(" ", System.Array.ConvertAll(residuals, r => Num(r * 1000, "0.###")));
            meta[1] = Num(moved * 1000, "0.###");
            meta[2] = Num(turned, "0.###");
        }
        var model = needle != null ? needle.model : null;
        if (model == null) return meta;
        string P(Vector3 world) { var v = model.transform.InverseTransformPoint(world) * 1000; return Num(v.x, "0.###") + " " + Num(v.y, "0.###") + " " + Num(v.z, "0.###"); }
        if (model.landmarks != null) meta[3] = string.Join("|", System.Array.ConvertAll(model.landmarks, l => P(l.point.position)));
        if (model.pathEntry != null) meta[4] = P(model.pathEntry.position);
        if (model.pathTarget != null) meta[5] = P(model.pathTarget.position);
        return meta;
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
        var s = new Sample
        {
            t = _t, depth = needle.Depth, along = -along, side = (force - dir * along).magnitude,
            table = needle.haptics != null ? -needle.haptics.Axial : 0, fluid = needle.Current == NeedleAction.State.Extracting,
            state = needle.Current, rtt = stylus.RoundTripMs, age = stylus.StateAgeMs, frame = dt * 1000,
        };
        var model = needle.model;
        if (model != null && model.pathEntry != null && model.pathTarget != null)
        {
            Vector3 tip = needle.Tip, entry = model.pathEntry.position, goal = model.pathTarget.position, axis = (goal - entry).normalized;
            s.tip = model.transform.InverseTransformPoint(tip);
            s.off = Vector3.Cross(tip - entry, axis).magnitude;
            s.toTarget = (tip - goal).magnitude;
            s.angle = Vector3.Angle(dir, axis);
        }
        Add(s);
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

    // The tissue the tip is in at this depth: its index in the table, -1 before the first one.
    int LayerOf(float depth)
    {
        var layers = needle != null && needle.haptics != null ? needle.haptics.layers : null;
        if (layers == null) return -1;
        int found = -1;
        for (int i = 0; i < layers.Length; i++) if (layers[i].start <= depth) found = i;
        return found;
    }

    static Color32 BandColor(int layer) => Palette[layer % Palette.Length];

    /// <summary>Draws the run into the texture (and the card's labels, if it has them).</summary>
    public void Draw()
    {
        var run = _samples.FindAll(s => !s.fluid);
        int n = run.Count;
        if (n < 2) return;
        var layers = needle != null && needle.haptics != null && needle.haptics.layers != null ? needle.haptics.layers : new NeedleHaptics.Layer[0];
        int W = width, H = height;
        if (_tex == null || _tex.width != W || _tex.height != H)
        {
            if (_tex != null) DestroyObject(_tex);
            _tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ForceGraph" };
            _pixels = new Color32[W * H];
            _cov = new float[W * H];
        }

        // The axes: depth from 0 to the deepest the tip went, force from the lowest to the highest of either line, in round steps.
        float D = 0, lo = 0, hi = 0;
        foreach (var s in run)
        {
            D = Mathf.Max(D, s.depth);
            lo = Mathf.Min(lo, s.along);
            hi = Mathf.Max(hi, s.along);
        }
        D = Mathf.Max(D, 0.001f);
        // The white line is the ideal curve: the formula at an even speed as a function of depth alone (NeedleHaptics.Ideal), not the
        // formula at the hand's speed of each frame, which carries the hand's steps and the friction flipping with them.
        var curve = run;
        if (layers.Length > 0)
        {
            curve = new List<Sample>(IdealPoints + 1);
            for (int k = 0; k <= IdealPoints; k++) curve.Add(new Sample { depth = D * k / IdealPoints, table = -needle.haptics.Ideal(D * k / IdealPoints) });
        }
        foreach (var s in curve)
        {
            lo = Mathf.Min(lo, s.table);
            hi = Mathf.Max(hi, s.table);
        }
        float ys = Nice(Mathf.Max(hi - lo, 0.1f) / 7);
        float ymin = lo > -0.1f * ys ? 0 : Mathf.Floor(lo / ys) * ys; // a whisper below zero does not earn a row of the axis (the margin shows it)
        float ymax = Mathf.Max(Mathf.Ceil(hi / ys) * ys, ymin + ys);
        float xs = Nice(D * 1000 / 8) / 1000; // m, labelled in mm
        float ylo = ymin - 0.04f * (ymax - ymin), yhi = ymax + 0.04f * (ymax - ymin), xlo = -0.015f * D, xhi = 1.015f * D; // a margin, so the lines are not cut at the edge
        float X(float d) => (d - xlo) / (xhi - xlo) * W;
        float Y(float v) => (v - ylo) / (yhi - ylo) * H;

        System.Array.Fill(_pixels, new Color32(0, 0, 0, 90));

        // Bands: each tissue of the table from where it starts to where the next one does (the last one to the right edge), and a dotted
        // line at each start but the first, as in the Python graph. The table is sorted by start.
        int shown = 0;
        for (int i = 0; i < layers.Length && layers[i].start < D; i++, shown++)
        {
            int x0 = Mathf.Clamp(Mathf.RoundToInt(X(layers[i].start)), 0, W);
            int x1 = i + 1 < layers.Length && layers[i + 1].start < D ? Mathf.Clamp(Mathf.RoundToInt(X(layers[i + 1].start)), 0, W) : W;
            var color = BandColor(i);
            for (int y = 0; y < H; y++)
                for (int x = x0; x < x1; x++) Over(ref _pixels[y * W + x], color, 0.2f);
            if (i > 0 && x0 < W)
                for (int y = 0; y < H; y++) if (y / 5 % 2 == 0) Over(ref _pixels[y * W + x0], EdgeColor, 0.8f);
        }

        // Grid: a line at each round value of force (the zero line brighter) and of depth.
        var white = new Color32(255, 255, 255, 255);
        for (float v = ymin; v <= ymax + ys * 0.01f; v += ys)
        {
            int row = Mathf.RoundToInt(Y(v));
            float a = Mathf.Abs(v) < ys * 0.01f ? 0.4f : 0.13f;
            for (int y = Mathf.Max(0, row - 1); y <= Mathf.Min(H - 1, row); y++)
                for (int x = 0; x < W; x++) Over(ref _pixels[y * W + x], white, a);
        }
        for (float d = 0; d <= D + xs * 0.01f; d += xs)
        {
            int col = Mathf.RoundToInt(X(d));
            for (int x = Mathf.Max(0, col - 1); x <= Mathf.Min(W - 1, col); x++)
                for (int y = 0; y < H; y++) Over(ref _pixels[y * W + x], white, 0.07f);
        }

        Stroke(curve, s => s.table, 2.4f, FormulaColor, X, Y);
        Stroke(run, s => s.along, 1.4f, AlongColor, X, Y); // thinner, on top: where the two are the same (scale 1) both still show

        _tex.SetPixelData(_pixels, 0);
        _tex.Apply(false);
        if (plot != null) plot.texture = _tex;

        // Labels: the values at the grid lines (the unit goes with the last one), then the texts.
        int used = 0;
        for (float v = ymin; v <= ymax + ys * 0.01f; v += ys)
            Label(used++, Num(v, "0.##") + (v + ys > ymax + ys * 0.01f ? " N" : ""), new Vector2(0, (Y(v)) / H), new Vector2(1, 0.5f), new Vector2(-8, 0));
        for (float d = 0; d <= D + xs * 0.01f; d += xs)
            Label(used++, Num(d * 1000, "0.#") + (d + xs > D + xs * 0.01f ? " mm" : ""), new Vector2(X(d) / W, 0), new Vector2(0.5f, 1), new Vector2(0, -6));
        for (int i = used; i < _labels.Count; i++) _labels[i].gameObject.SetActive(false);

        if (legend != null)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                var c = BandColor(i);
                sb.Append($"<color=#{c.r:X2}{c.g:X2}{c.b:X2}>{layers[i].name}</color>   ");
            }
            legend.text = sb.ToString().TrimEnd();
        }
        if (summary != null)
        {
            int peak = 0;
            float sent = 0;
            for (int i = 0; i < curve.Count; i++) if (curve[i].table > curve[peak].table) peak = i;
            for (int i = 0; i < n; i++) sent = Mathf.Max(sent, run[i].along);
            int where = LayerOf(curve[peak].depth);
            summary.text = $"Peak <b>{Num(curve[peak].table, "0.00")} N</b> in {(where < 0 ? "the skin" : layers[where].name)} at {Num(curve[peak].depth * 1000, "0.#")} mm   ·   {Num(_samples[_samples.Count - 1].t, "0")} s in all\n" +
                $"<color=#EFF4FA>Ideal needle force</color> (the tissues' formula)   <color=#FFD400>Sent to the stylus</color> (peak {Num(sent, "0.00")} N)";
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

    // One line through the samples (depth, value): its distance to each pixel gives the coverage (soft edges), the strongest one counts, then it is laid over.
    void Stroke(List<Sample> run, System.Func<Sample, float> value, float r, Color32 color, System.Func<float, float> X, System.Func<float, float> Y)
    {
        System.Array.Clear(_cov, 0, _cov.Length);
        Vector2 a = new(X(run[0].depth), Y(value(run[0])));
        for (int i = 1; i < run.Count; i++)
        {
            Vector2 b = new(X(run[i].depth), Y(value(run[i])));
            Segment(a, b, r);
            a = b;
        }
        for (int k = 0; k < _cov.Length; k++) if (_cov[k] > 0) Over(ref _pixels[k], color, _cov[k]);
    }

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
