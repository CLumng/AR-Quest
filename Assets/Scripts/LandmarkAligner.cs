using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Guided landmark alignment. The user touches each landmark on the physical model with the tip of the right
/// controller; a least-squares rigid fit then places the virtual model over the physical one.
/// Controls: TRIGGER or A confirms, B goes back. Once the model is locked the panel hides itself, and the right GRIP
/// opens a menu (thumbstick moves, TRIGGER or A selects, B goes back one page, and closes the menu from the main one) to fade the
/// soft tissue (the slider row: thumbstick left / right), test the layers or show/hide parts of the model.
/// "Settings" opens a second page: calibrate again, adjust by eye, the size of the cards, the size of their text and their opacity
/// (GlassSettings, kept between sessions), and the stylus' help mode and force strength. "Layer tester" lists the layers in two columns.
/// Adjust: the left thumbstick moves the model, the right one turns it and lifts it, all by eye against the passthrough.
/// While recalibrating or adjusting, GRIP or B cancels and brings back the previous pose. All UI text is English.
/// </summary>
public class LandmarkAligner : MonoBehaviour
{
    enum Step { Intro, Capture, Review, Done, Menu, Adjust }

    const int Actions = 3; // main menu entries before the element groups: settings, transparency slider, layer tester

    public ModelTarget model;
    public Transform trackingSpace; // parent of the camera (Camera Offset): controller poses are relative to it
    public Transform rig;           // follows the head; holds the panel and the guide
    public GameObject panel;        // the Canvas (GlassPanel on it): a card as tall as what it holds
    public Transform guideAnchor;   // where the model sits while it serves as a guide
    public Renderer tip;           // ball drawn at the controller tip
    public Transform needle;        // follows the right controller (tip along its forward axis); shown once the model is locked
    public TissueFade fade;         // transparency of the soft tissue, set from the menu slider
    public GlassPanel view;         // draws what Describe puts in the model
    public LayerTester tester;      // the cube of the menu's "Layer tester" page
    public HapticStylus stylus;     // the Touch stylus: the needle follows it while it sends data (null or silent: the right controller holds it)
    public Vector3 tipOffset = new(0, 0, 0.05f); // controller aim pose -> ball, in controller space
    public float goodMm = 4, okMm = 8;
    public float guideScale = 1.5f; // the guide is drawn larger than life so the yellow dot is easy to place
    public float moveSpeed = 0.06f; // adjust: m/s at full stick
    public float turnSpeed = 30;    // adjust: degrees/s at full stick
    public float fadeSpeed = 0.8f;  // slider: whole range per second at full stick

    readonly InputAction _pos = new("pos", binding: "<XRController>{RightHand}/pointerPosition");
    readonly InputAction _rot = new("rot", binding: "<XRController>{RightHand}/pointerRotation");
    readonly InputAction _tracked = new("tracked", InputActionType.Button, "<XRController>{RightHand}/isTracked");
    readonly InputAction _confirm = new("confirm", InputActionType.Button, "<XRController>{RightHand}/{TriggerButton}");
    readonly InputAction _accept = new("accept", InputActionType.Button, "<XRController>{RightHand}/{PrimaryButton}"); // A: confirms like the trigger
    readonly InputAction _back = new("back", InputActionType.Button, "<XRController>{RightHand}/{SecondaryButton}");
    readonly InputAction _menu = new("menu", InputActionType.Button, "<XRController>{RightHand}/{GripButton}");
    readonly InputAction _stick = new("stick", InputActionType.Value, "<XRController>{RightHand}/{Primary2DAxis}");
    readonly InputAction _moveStick = new("moveStick", InputActionType.Value, "<XRController>{LeftHand}/{Primary2DAxis}");

    Step _step;
    int _index, _worst;
    int _sel;               // menu selection, counting only the rows that can be selected (not the headers): see Describe for what each page has
    int _page;              // menu page: 0 = the main menu, 1 = settings (calibrate, adjust, size / text size / opacity of the cards, reset, stylus help mode and force strength; B goes back), 2 = layer tester (row 0 = back, then one row per layer, in two columns)
    bool _stickLatched;     // one thumbstick flick moves the selection one step
    bool _sideLatched;      // the same for left / right between the two columns
    float _hintUntil;       // how long the "model locked" hint stays on screen
    Pose? _locked;          // the accepted pose, restored if a recalibration is cancelled
    Vector3[] _touched;
    float _rms;
    float[] _residuals;
    readonly PanelModel _model = new();
    Vector3 _dir = Vector3.forward; // flat direction from the head to the panel
    Camera _cam;
    NeedleAction _needleAction;
    NeedleAction Action => _needleAction != null ? _needleAction : _needleAction = needle.GetComponent<NeedleAction>(); // also before Start (the checks)

    // Aligning = the bones are shown over the physical model and GRIP cancels back to the locked pose.
    bool Aligning => _step == Step.Intro || _step == Step.Capture || _step == Step.Review || _step == Step.Adjust;
    // The white ball is the pointer, so the needle is hidden.
    bool Pointing => Aligning;

    /// <summary>The model is locked and the menu is closed: the sticks are free (the stylus uses them to move its home point).</summary>
    public bool Idle => _step == Step.Done;

    void OnEnable() { _pos.Enable(); _rot.Enable(); _tracked.Enable(); _confirm.Enable(); _accept.Enable(); _back.Enable(); _menu.Enable(); _stick.Enable(); _moveStick.Enable(); }
    void OnDisable() { _pos.Disable(); _rot.Disable(); _tracked.Disable(); _confirm.Disable(); _accept.Disable(); _back.Disable(); _menu.Disable(); _stick.Disable(); _moveStick.Disable(); }

    void Start()
    {
        _cam = Camera.main;
        _needleAction = needle.GetComponent<NeedleAction>();
        _touched = new Vector3[model.landmarks.Length];
        Go(Step.Intro);
    }

    void Go(Step step, int index = 0)
    {
        _step = step;
        _index = index;
        model.SetAligning(Aligning);
        tip.gameObject.SetActive(step == Step.Intro || step == Step.Capture);
        foreach (var l in model.landmarks) l.marker.gameObject.SetActive(Aligning);
        if (step == Step.Menu) _sel = _page = 0;
        else GlassSettings.Save(); // the menu is closed: keep what the user tuned
    }

    void Update()
    {
        bool tracked = _tracked.IsPressed();
        var ctrl = new Pose(trackingSpace.TransformPoint(_pos.ReadValue<Vector3>()),
                            trackingSpace.rotation * _rot.ReadValue<Quaternion>());
        tip.transform.position = ctrl.position + ctrl.rotation * tipOffset;
        bool onStylus = stylus != null && stylus.Active;
        var hand = onStylus ? stylus.NeedlePose(_needleAction.tipLength) : ctrl; // the stylus' tip is the needle's tip, placed through its home point on the model; otherwise the controller holds it
        needle.SetPositionAndRotation(hand.position, hand.rotation);
        // Not while the ball is the pointer, and not at a stale pose; a needle left in the model for extraction stays, whatever the hand does.
        needle.gameObject.SetActive(_locked.HasValue && !Pointing && (tracked || onStylus || _needleAction.Extracting));

        Action.inMenu = _step == Step.Menu; // A selects in the menu: the needle does not extract with it
        bool confirm = _confirm.WasPressedThisFrame() || _accept.WasPressedThisFrame(), back = _back.WasPressedThisFrame();
        if (Aligning && _locked.HasValue && _menu.WasPressedThisFrame()) Cancel();
        else switch (_step)
        {
            case Step.Intro:
                if (confirm) Go(Step.Capture);
                else if (back && _locked.HasValue) Cancel();
                break;
            case Step.Capture:
                // ponytail: the point is sampled on the trigger press, which can jitter a few mm; average a short window if needed
                if (confirm && tracked)
                {
                    _touched[_index] = tip.transform.position;
                    if (_index + 1 < _touched.Length) Go(Step.Capture, _index + 1); else Fit();
                }
                else if (back) Go(_index == 0 ? Step.Intro : Step.Capture, Mathf.Max(_index - 1, 0));
                break;
            case Step.Review:
                if (confirm) Lock();
                else if (back) Go(Step.Capture);
                break;
            case Step.Adjust:
                if (confirm) Lock();
                else if (back) Cancel();
                else Nudge(Time.deltaTime);
                break;
            case Step.Done:
                if (_menu.WasPressedThisFrame()) Go(Step.Menu);
                break;
            case Step.Menu:
                if (_menu.WasPressedThisFrame()) Go(Step.Done);
                else if (confirm) MenuSelect();
                else if (back) MenuBack();
                else
                {
                    var stick = _stick.ReadValue<Vector2>();
                    MoveSelection(stick);
                    float slide = Curve(new Vector2(stick.x, 0)).x * fadeSpeed * Time.deltaTime; // left / right on a slider row
                    if (_page == 0 && _sel == 1) fade.Set(fade.transparency + slide);
                    else if (_page == 1 && _sel == 2) GlassSettings.SizeT += slide;
                    else if (_page == 1 && _sel == 3) GlassSettings.TextSizeT += slide;
                    else if (_page == 1 && _sel == 4) GlassSettings.OpacityT += slide;
                    else if (_page == 1 && _sel == 7 && stylus != null) stylus.strength = Mathf.Clamp01(stylus.strength + slide);
                }
                break;
        }

        bool testing = _step == Step.Menu && _page == 2; // the cube is out only on the tester's page, wherever the menu is left from
        if (testing != tester.gameObject.activeSelf)
        {
            if (!testing) tester.Close();
            else if (stylus != null && stylus.Active) tester.OpenOnPath(); // with the stylus the cube replaces the model, where the stylus can reach it
            else tester.Open(_cam.transform.position + _dir * 0.45f + Vector3.Cross(Vector3.up, _dir) * 0.28f + Vector3.down * 0.15f, Quaternion.LookRotation(_dir)); // beside the panel, in reach of the right hand
        }
        if (testing && _sel > 0) tester.Select(_sel - 1);

        panel.SetActive(_step != Step.Done || Time.time < _hintUntil);
        Describe(tracked, _model);
        view.Show(_model);
        UpdateMarkers();
    }

    void LateUpdate()
    {
        // Panel and guide move with the user, and only turn when the user looks more than 45 degrees away,
        // so looking at the guide does not make it run away. They snap into place during the first 1.5 s.
        var head = _cam.transform;
        var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (flat.sqrMagnitude > 0.01f && (Time.time < 1.5f || Vector3.Angle(flat, _dir) > 45)) _dir = flat.normalized;
        float k = Time.time < 1.5f ? 1 : 1 - Mathf.Exp(-4 * Time.deltaTime);
        rig.position = Vector3.Lerp(rig.position, head.position + _dir + Vector3.down * 0.1f, k);
        var toRig = Vector3.ProjectOnPlane(rig.position - head.position, Vector3.up);
        if (toRig.sqrMagnitude > 1e-4f) rig.rotation = Quaternion.LookRotation(toRig);

        if (_step == Step.Intro || _step == Step.Capture)
        {
            model.transform.SetPositionAndRotation(guideAnchor.position, guideAnchor.rotation);
            model.transform.localScale = Vector3.one * guideScale;
        }
    }

    void Lock()
    {
        _locked = new Pose(model.transform.position, model.transform.rotation);
        _hintUntil = Time.time + 5;
        Go(Step.Done);
    }

    // Moves the model relative to where the user is looking, so "up on the stick" is always "away from me".
    // Only yaw and translation: the model rests on a level surface, so the 4-point fit already sets its tilt.
    // The turn pivots on the landmark centroid so the model spins in place over the physical one.
    void Nudge(float dt)
    {
        var fwd = Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) return; // looking straight up or down
        fwd.Normalize();
        var right = Vector3.Cross(Vector3.up, fwd);
        Vector2 m = Curve(_moveStick.ReadValue<Vector2>()), r = Curve(_stick.ReadValue<Vector2>());

        var pivot = Vector3.zero;
        foreach (var l in model.landmarks) pivot += l.point.position;
        pivot /= model.landmarks.Length;

        model.transform.position += (right * m.x + fwd * m.y + Vector3.up * r.y) * (moveSpeed * dt);
        model.transform.RotateAround(pivot, Vector3.up, r.x * turnSpeed * dt);
    }

    // Dead zone, then quadratic: a small flick gives a slow, fine correction and only a full push is fast.
    static Vector2 Curve(Vector2 v) => v.magnitude < 0.15f ? Vector2.zero : v * v.magnitude;

    void Cancel()
    {
        model.transform.SetPositionAndRotation(_locked.Value.position, _locked.Value.rotation);
        model.transform.localScale = Vector3.one;
        _hintUntil = Time.time + 5;
        Go(Step.Done);
    }

    // TRIGGER or A on the selected row. The slider rows have nothing to select: they are moved with the stick. On the tester's page the
    // layer rows have nothing to select either: the cursor is the layer, so the same hand can poke the cube and change layer.
    void MenuSelect()
    {
        if (_page == 2 && _sel == 0) MenuBack();
        else if (_page == 1)
        {
            if (_sel == 0) Go(Step.Intro);
            else if (_sel == 1) Go(Step.Adjust);
            else if (_sel == 5) GlassSettings.Reset();
            else if (_sel == 6) Action.guided = !Action.guided;
        }
        else if (_page == 0)
        {
            if (_sel == 0) { _page = 1; _sel = 0; }
            else if (_sel == 2) { _page = 2; _sel = 1; } // on the first layer: the cube is out with a material at once
            else if (_sel >= Actions) model.Toggle(_sel - Actions);
        }
    }

    // B, or a page's Back row: up to the main menu, on the row that opened the page. From the main menu it closes the menu.
    void MenuBack()
    {
        if (_page == 0) { Go(Step.Done); return; }
        _sel = _page == 1 ? 0 : 2; // settings is the main menu's first row, the layer tester its third
        _page = 0;
    }

    // The stick up / down moves the cursor a row (down at the end wraps to the first); on the tester's page left / right goes to the
    // other column, to the same place in it (the layers are rows 1..Count, the left column holds the first half).
    void MoveSelection(Vector2 stick)
    {
        if (Mathf.Abs(stick.y) < 0.3f) _stickLatched = false;
        else if (!_stickLatched && Mathf.Abs(stick.y) > 0.6f)
        {
            _stickLatched = true;
            int n = _page == 1 ? 8 : _page == 2 ? tester.Count + 1 : model.groups.Length + Actions;
            _sel = (_sel + (stick.y < 0 ? 1 : n - 1)) % n;
        }

        if (Mathf.Abs(stick.x) < 0.3f) _sideLatched = false;
        else if (!_sideLatched && Mathf.Abs(stick.x) > 0.6f && _page == 2 && _sel > 0)
        {
            _sideLatched = true;
            int half = PanelModel.Half(tester.Count);
            if (stick.x > 0 && _sel <= half) _sel = Mathf.Min(_sel + half, tester.Count);
            else if (stick.x < 0 && _sel > half) _sel -= half;
        }
    }

    void Fit()
    {
        var local = new Vector3[_touched.Length];
        for (int i = 0; i < local.Length; i++)
            local[i] = model.transform.InverseTransformPoint(model.landmarks[i].point.position);
        var pose = RigidAlign.Solve(local, _touched, out _rms, out _residuals);
        _worst = System.Array.IndexOf(_residuals, Mathf.Max(_residuals));
        model.transform.SetPositionAndRotation(pose.position, pose.rotation);
        model.transform.localScale = Vector3.one;
        Go(Step.Review);
    }

    void UpdateMarkers()
    {
        for (int i = 0; i < model.landmarks.Length; i++)
        {
            var m = model.landmarks[i].marker;
            bool current = _step == Step.Capture && i == _index;
            bool done = _step == Step.Review || _step == Step.Adjust || (_step == Step.Capture && i < _index);
            m.material.color = current ? Color.yellow : done ? Color.green : new Color(0.75f, 0.75f, 0.75f);
            m.transform.localScale = Vector3.one * (current ? 0.016f * (1 + 0.2f * Mathf.Sin(Time.time * 6)) : 0.008f);
        }
    }

    // Fills the model with what the panel shows in the current step. The panel redraws only when it differs from the last one.
    void Describe(bool tracked, PanelModel m)
    {
        m.Clear();
        int n = model.landmarks.Length;
        switch (_step)
        {
            case Step.Capture:
                var l = model.landmarks[_index];
                m.title = $"Landmark {_index + 1} of {n}";
                m.step = _index + 1;
                m.steps = n;
                m.body = $"<color=#ffd400><b>{l.title}</b></color>\n{l.hint}\n\n" +
                    "Touch this point on the physical model with the white ball, then press TRIGGER." +
                    (tracked ? "" : "\n\n<color=#ff5a4f>Right controller not tracked. Turn it on and hold it in view.</color>");
                m.Hint("TRIGGER / A", "Confirm point");
                m.Hint("B", "Back");
                AddCancel(m);
                break;
            case Step.Review:
                float mm = _rms * 1000, worstMm = _residuals[_worst] * 1000;
                string f(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
                bool poor = mm >= okMm;
                m.title = "Check the alignment";
                m.badge = $"{(mm < goodMm ? "Good" : poor ? "Poor" : "Acceptable")}  {f(mm)} mm";
                m.badgeColor = mm < goodMm ? new Color(0.30f, 0.85f, 0.40f) : poor ? new Color(1f, 0.35f, 0.31f) : new Color(1f, 0.83f, 0f);
                m.body = $"Largest at: {model.landmarks[_worst].title} ({f(worstMm)} mm).\n\n" +
                    "The 3D bones are now placed over the physical model. Walk around it and check that they overlap." +
                    (poor ? "\n\nThe error is high. Press B and touch each point more carefully." : "");
                m.Hint("TRIGGER / A", "Accept");
                m.Hint("B", "Redo");
                AddCancel(m);
                break;
            case Step.Adjust:
                m.title = "Adjust alignment";
                m.body = "Look at the 3D bones over the physical model. Nudge them until they overlap, checking from more than one side.";
                m.Hint("LEFT STICK", "Move");
                m.Hint("RIGHT STICK", "Turn, up / down");
                m.Hint("TRIGGER / A", "Accept");
                AddCancel(m, true);
                break;
            case Step.Done:
                m.compact = true;
                m.title = "Model locked";
                m.Hint("GRIP", "Open menu");
                break;
            case Step.Menu:
                if (_page == 1)
                {
                    // the stylus' state is in the badge and the text under the title: "Stylus: ..." says what they are about
                    bool live = stylus != null && stylus.Live;
                    m.title = "Settings";
                    if (!live)
                    {
                        m.badge = "Stylus: No data";
                        m.badgeColor = new Color(1f, 0.35f, 0.31f);
                        m.body = "Start the bridge on the PC and plug the headset in by USB.";
                    }
                    else if (stylus.Docked)
                    {
                        m.badge = "Stylus: In the inkwell";
                        m.badgeColor = new Color(1f, 0.83f, 0f);
                        m.body = "The needle rests at its home point. With the menu closed the sticks move it: left across the model, right turns and lifts it. Then take the stylus out.";
                    }
                    else
                    {
                        m.badge = stylus.ForcesOn ? "Stylus: Forces on" : "Stylus: Forces off";
                        m.badgeColor = stylus.ForcesOn ? new Color(0.30f, 0.85f, 0.40f) : new Color(1f, 0.83f, 0f);
                        m.body = "Button 1 extracts fluid, as A. Button 2 switches the forces on and off. With the forces on, a puncture ends in a force graph: extract the fluid, take the needle out and switch the forces off." +
                            (Action.guided ? "\n\nHelp mode: follow the ideal path. The indicator by the entry point says if the stylus is on it, in the last 15 mm before it and inside." + StylusWhere() : "");
                    }
                    m.Add(PanelModel.Kind.Action, "Calibrate model", _sel == 0);
                    m.Add(PanelModel.Kind.Action, "Adjust alignment", _sel == 1);
                    m.Add(PanelModel.Kind.Header, "Interface");
                    m.Add(PanelModel.Kind.Slider, "Interface size", _sel == 2, value: GlassSettings.SizeT, valueText: Percent(GlassSettings.Size));
                    m.Add(PanelModel.Kind.Slider, "Text size", _sel == 3, value: GlassSettings.TextSizeT, valueText: Percent(GlassSettings.TextSize));
                    m.Add(PanelModel.Kind.Slider, "Panel opacity", _sel == 4, value: GlassSettings.OpacityT, valueText: Percent(GlassSettings.Opacity));
                    m.Add(PanelModel.Kind.Action, "Reset to defaults", _sel == 5);
                    m.Add(PanelModel.Kind.Header, "Stylus");
                    m.Add(PanelModel.Kind.Toggle, "Help mode", _sel == 6, Action.guided);
                    float strength = stylus != null ? stylus.strength : 1;
                    m.Add(PanelModel.Kind.Slider, "Force strength", _sel == 7, value: strength, valueText: Percent(strength));
                }
                else if (_page == 2)
                {
                    var (layer, resistance, pop) = tester.Layer(tester.Selected);
                    m.title = "Layer tester";
                    m.badge = layer;
                    m.badgeColor = new Color(0.55f, 0.85f, 1f);
                    m.body = "Push the needle tip into the cube, then move it in and out. The stick changes the layer." +
                        (stylus != null && stylus.Live ? "\n\nThe cube replaces the model. Stylus: button 2 turns the forces on." : "");
                    m.Add(PanelModel.Kind.Action, "Back", _sel == 0);
                    m.columnsFrom = m.rows.Count;
                    for (int i = 0; i < tester.Count; i++) m.Add(PanelModel.Kind.Action, tester.Layer(i).name, _sel == i + 1);
                    m.columnsCount = m.rows.Count - m.columnsFrom;
                    m.Add(PanelModel.Kind.Header, "Properties");
                    m.Add(PanelModel.Kind.Slider, "Resistance", value: resistance, valueText: Percent(resistance));
                    m.Add(PanelModel.Kind.Slider, "Pop on entry", value: pop, valueText: Percent(pop));
                }
                else
                {
                    m.title = "Menu";
                    m.Add(PanelModel.Kind.Action, "Settings", _sel == 0, valueText: ">");
                    m.Add(PanelModel.Kind.Slider, "Tissue transparency", _sel == 1, value: fade.transparency);
                    m.Add(PanelModel.Kind.Action, "Layer tester", _sel == 2, valueText: ">");
                    m.Add(PanelModel.Kind.Header, "Show / hide elements");
                    for (int i = 0; i < model.groups.Length; i++)
                        m.Add(PanelModel.Kind.Toggle, model.groups[i].name, _sel == i + Actions, model.groups[i].visible);
                }
                m.Hint("STICK", "Move / adjust");
                m.Hint("TRIGGER / A", "Select");
                m.Hint("B", "Back");
                m.Hint("GRIP", "Close");
                break;
            default:
                m.title = "Spine model alignment";
                m.body = "Stand the physical spine model upright on its flat bottom, on a stable surface, with its back facing you.\n\n" +
                    $"You will touch {n} landmarks on it. Each one is shown as a yellow dot on the 3D guide at the right.\n\n" +
                    "Move the RIGHT controller until the white ball at its tip touches the same spot on the physical model, then press TRIGGER.\n\n" +
                    "Tip: check the ball from two angles. Depth is easy to misjudge.";
                m.Hint("TRIGGER / A", "Start");
                AddCancel(m, true);
                break;
        }
    }

    static string Percent(float v) => Mathf.RoundToInt(v * 100) + "%";

    // Where the stylus' tip is against the ideal path, for the Settings page in help mode: what to tune the start point by.
    string StylusWhere()
    {
        var n = Action;
        int along = Mathf.RoundToInt(n.GuideAlong * 1000);
        return $"\nTip: {(along < 0 ? $"{-along} mm before the entry" : $"{along} mm in")}, {Mathf.RoundToInt(n.GuideOff * 1000)} mm off the path, pen {Mathf.RoundToInt(n.GuideTilt)}° off.";
    }

    // Offered only when there is a previous pose to go back to. withB: B cancels too (where it has no other job).
    void AddCancel(PanelModel m, bool withB = false)
    {
        if (_locked.HasValue) m.Hint(withB ? "B / GRIP" : "GRIP", "Cancel");
    }
}
