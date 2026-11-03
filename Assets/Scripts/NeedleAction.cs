using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The needle in the body. Outside the skin it simply follows the hand. Once the tip is through the skin the needle is committed
/// to a semi-straight track: it goes on the way it went in (so the entry point and the angle are the decision), and only the
/// hand's advance along the needle moves it. Dragging the hand sideways does nothing. It can bend a little: it turns towards where
/// the controller points, at most turnPerMm degrees per mm it moves and never more than maxBend away from its entry direction, so
/// a bone is walked off by pulling back a little and angling, not by dragging the needle around. Pulling out of the skin hands the
/// needle back to the hand.
/// Two things are reported: the tip reaches the target (the ball in the dural sac), or it runs into a bone. A bone stops the needle
/// while the hand pushes on, the controller buzzes at almost full strength (NeedleHaptics.Hit) and a message shows, until the hand
/// pulls back. Only the tip collides, not the shaft.
/// At the target, A leaves the needle where it is (the controller is free): the stylet head slides out of the hub, and then fluid
/// drips from it. A again stops the fluid, the head slides back in, and the needle comes out of the body along its track; it
/// stays hidden until the controller is out of the body too, then it is the controller's again.
/// With the Touch stylus holding the needle (HapticStylus) the tissue pushes back through it (StylusForce); the extraction is the same
/// except that the stylus is held where it was while it lasts, and the user takes the needle out: it is the stylus' again once the head is
/// home. In help mode (guided) the needle is held on the ideal path while an indicator says if the stylus is on it, and a ghost needle
/// shows where the stylus really is.
/// Runs in LateUpdate, after LandmarkAligner has placed the needle on the controller. All UI text is English.
/// </summary>
public class NeedleAction : MonoBehaviour
{
    public enum State { Outside, Inside, Blocked, Reached, Extracting, Withdrawing }

    // What the needle is doing once it is left in the model.
    enum Phase { None, Opening, Flowing, Closing, Withdrawing }

    public ModelTarget model;         // pathEntry (where the message floats) and pathTarget (the goal)
    public Transform card;            // the message's card: floats beside the entry point, facing the user (GlassPanelBuilder.AddMessage)
    public float extractingShift = 0.09f; // m further to the user's left while the fluid is extracted: that message stays up a while, off the view of the work
    public TMP_Text message;          // its text: the first line takes the state's colour
    public Graphic edge;              // its border, in the same colour
    public GameObject hint;           // under the text: a key chip and what it does
    public TMP_Text hintKey, hintLabel;
    public NeedleHaptics haptics;     // the tissue's force, and the buzz on bone
    public FluidFlow flow;            // the fluid that drips from the hub while extracting
    public Transform head;            // the stylet head at the back of the hub (and its rod): slides out to let the fluid flow
    public Collider[] bones;          // the obstacles
    public Collider skin;             // where the needle goes in
    public GameObject colliders;      // holds bones and skin; on only while the needle is, so moving the model costs no physics
    public float tipLength = 0.1025f; // hub to tip along the needle's forward axis (spinalNeedle1.fbx)
    public float reachRadius = 0.005f; // how close the tip must get to the centre of the target
    public float entryDepth = 0.002f;  // the needle counts as in once this much of it is under the skin
    public float maxBend = 10;         // degrees the track may turn away from the entry direction, in all
    public float turnPerMm = 0.5f;     // degrees it may turn for every mm the needle moves
    public float pullDistance = 0.06f; // how far the head slides out
    public float pullSeconds = 0.4f;   // how long it takes, each way
    public float withdrawSpeed = 0.1f; // m/s the needle comes out of the body by itself
    public HapticStylus stylus;        // when it holds the needle, the tissue pushes back through it (see StylusForce)
    public bool parked;                // set by the stylus while it is docked: the needle rests where it is and goes nowhere
    public float sideStiffness = 150f; // N/m: how hard the stylus is pulled back to the needle's track
    public float wallStiffness = 300f; // N/m: how hard a bone pushes back
    public float popForce = 0f;        // N: the kick forward as the tip pops into a layer (negative: a bump against it); off, the formula has none
    public bool guided;                // help mode (stylus only): the needle follows the ideal path and an indicator says if the stylus is on it
    public float guideLateral = 0.008f; // m the stylus' tip may be off the path, and ...
    public float guideAngle = 15f;      // ... degrees the pen may be off its direction: the indicator is full at either
    public float guideLost = 1.6f;      // times those tolerances: past this the force is off until the stylus goes back
    public float guideApproach = 0.015f; // m before the path's start where the stylus starts to be judged (if it is within guideCapture of the path):
                                         // short on purpose, the stylus' far point (arm stretched out) is where the user starts and must not be judged
    public float guideReset = 0.025f;    // m: farther out than this it is not judged any more and a lost try starts over; more than guideApproach, so it does not flicker.
                                         // Also how far the help mode's box reaches past the entry and past the target
    public float guideCapture = 0.03f;   // m: the box's half width (and how near the path the stylus must be to start being judged)
    public float guideEase = 8f;        // per second: how fast the needle goes onto the path when the stylus is judged, and back to the hand when it is not
    public Transform ghost;             // help mode: a see-through needle where the stylus really is, while the needle is held on the path
    public ForceGraph graph;            // the graph of a puncture with the forces on: A (or the stylus' button 1) closes it once it is shown
    public float lockStiffness = 300f;  // N/m: how hard the stylus is held where it was while the fluid is extracted

    // The help mode's indicator, on the message card.
    public enum Band { None, Good, Close, Off, Lost, Far }
    enum Guide { Free, Engaged, Lost }

    const float Clearance = 0.0005f; // a needle stopped by a bone stays this far off its surface, so the next test does not start on it
    const float Release = 0.002f;    // the hand must come this far back from a bone to let go, so tremor at the surface does not flicker
    const float RampSeconds = 0.5f;  // the stylus' force comes back over this long after an extraction

    readonly InputAction _extract = new("extract", InputActionType.Button, "<XRController>{RightHand}/{PrimaryButton}");

    /// <summary>The menu is open (LandmarkAligner sets it): A selects there, so it does not also extract.</summary>
    [System.NonSerialized] public bool inMenu;

    public State Current { get; private set; }
    public bool MessageShown => card.gameObject.activeSelf;
    public bool Extracting => _phase != Phase.None; // the needle is left in the model: LandmarkAligner keeps it on even if the controller is not tracked
    public float HeadOut => _pull;                  // 0 = the head is home, 1 = all the way out
    public bool ByStylus => stylus != null && stylus.Active;
    public float Depth => _s;      // how far the needle is in, m along its track
    public Vector3 Dir => _dir;    // the way it is going (the track's direction)
    public Vector3 Tip => _tip;    // where the tip is shown (world); only meaningful while the needle is in
    /// <summary>N at the stylus per N of the tissue's table: the table's strongest steady force is the stylus' limit, so the layers keep their proportions.</summary>
    public float TableScale { get { float peak = haptics != null ? haptics.Peak : 0; return peak > 0 && stylus != null ? stylus.maxForce / peak : 0; } }
    // Where the stylus is against the ideal path, whether or not it is being judged (the menu's Settings page shows it): along the path from
    // the entry (m, negative = before it), the tip's distance from the path (m) and the pen's angle to it (degrees). Help mode only.
    public float GuideAlong => _along;
    public float GuideOff => _lat;
    public float GuideTilt => _ang;
    bool PathReady => model != null && model.pathEntry != null && model.pathTarget != null;
    public bool Guided => guided && ByStylus && PathReady;
    // The stylus is out of the dock and has not been pulled back behind the path's start yet (help mode or not): the card says to move it back as far as it goes.
    bool FarHint => ByStylus && PathReady && !parked && !_far && !_in;
    public Band GuideBand => !Guided || _guide == Guide.Free ? (FarHint ? Band.Far : Band.None) : _guide == Guide.Lost ? Band.Lost : _deviation < 0.6f ? Band.Good : _deviation < 1 ? Band.Close : Band.Off;
    bool _in, _blocked;
    bool _wasParked, _wasGuided;
    Guide _guide;
    bool _begun;                  // help mode: this try started properly (judged on the way in), so a lost or left one can be picked up again
    bool _far;                    // the stylus has been behind the help box (pulled back as far as it goes) since it left the dock; until then the card says to
    float _deviation, _lat, _ang; // the stylus against the ideal path: the larger of its two errors in tolerances, the tip's distance (m), the pen's angle
    float _along;                 // how far along the path the stylus' tip is from the entry (m; negative = before it)
    float _over;                  // how far the stylus has gone on past the target (m): the needle stops there
    float _ramp = 1;              // the stylus' force, coming back gradually after the extraction
    float _w;                     // help mode: how far the shown needle is onto the path (0 = at the hand, 1 = on the path)
    bool _drawn;                  // help mode: the needle is drawn at _tip/_dir even though it is not in yet
    Vector3 _lock, _handTip;      // where the stylus is held during an extraction, and the hand's tip last frame
    bool _guideShown;
    int _guideKey;
    bool _gone;        // the needle came out by itself and is hidden until the controller is out of the body as well
    Phase _phase;
    float _pull;        // how far out the head is, 0..1
    Vector3 _headHome, _headBack; // the head's own position, and the way out in the head's parent's space
    Renderer[] _renderers;        // what is hidden while the needle is gone
    int _tenths;        // the volume shown in the message, in tenths of a mL
    Vector3 _up;        // the needle's roll while it is left in the model
    Vector3 _d0, _dir;  // the direction it went in, and the direction it is going now
    Vector3 _tip;       // where the needle's tip is shown
    Vector3 _handPrev;  // the hand's tip last frame
    float _s, _h;       // how far the needle is in, and how far the hand would have taken it (more than _s while a bone holds it), metres along the track
    Camera _cam;

    void OnEnable()
    {
        Current = State.Outside;
        _in = _blocked = false;
        _phase = Phase.None;
        _pull = 0;
        card.gameObject.SetActive(false);
        _extract.Enable();
        if (head != null)
        {
            _headHome = head.localPosition;
            _headBack = head.parent.InverseTransformDirection(-transform.forward); // out of the hub, backwards along the needle
        }
        _renderers = System.Array.FindAll(GetComponentsInChildren<Renderer>(true), r => !r.transform.IsChildOf(card));
        Gone(false);
        if (colliders != null)
        {
            colliders.SetActive(true);
            Physics.SyncTransforms(); // the model may have moved since the colliders were last on
        }
    }

    void OnDisable()
    {
        _extract.Disable();
        _phase = Phase.None; // the needle is off (menu, recalibration): the extraction ends, and the head goes home
        if (ghost != null) ghost.gameObject.SetActive(false);
        if (head != null) head.localPosition = _headHome;
        Gone(false);
        if (flow != null) flow.Stop();
        if (colliders != null) colliders.SetActive(false);
    }

    /// <summary>What A does: at the target, leave the needle in the model and start extracting; while extracting, stop and take the needle out.</summary>
    public void Toggle()
    {
        if (graph != null && graph.Visible) graph.Hide(); // the graph is up: A closes it
        else if (_phase == Phase.Opening || _phase == Phase.Flowing)
        {
            if (flow != null) flow.Stop();
            _phase = Phase.Closing; // the head goes back in, then the needle comes out of the body
        }
        else if (_phase == Phase.None && Current == State.Reached)
        {
            _phase = Phase.Opening; // the head slides out, then the fluid runs
            _up = transform.rotation * Vector3.up;
            _lock = _handTip; // the stylus is held where it is
        }
    }

    void LateUpdate() { Step(Time.deltaTime); }

    /// <summary>One frame. The checks drive it with a fixed time step.</summary>
    public void Step(float dt)
    {
        if (model == null) return;

        var forward = transform.rotation * Vector3.forward;
        var up = transform.rotation * Vector3.up;
        var hand = transform.position + forward * tipLength; // the tip as the hand holds it
        var handPose = new Pose(transform.position, transform.rotation);
        _handTip = hand;

        if (_extract.WasPressedThisFrame() && !inMenu) Toggle();

        // Docked stylus: the needle rests at its home point, outside the action. Leaving the dock it stays hidden until its axis is out of the body.
        if (parked && _in) Leave();
        if (!parked && _wasParked) Gone(true);
        _wasParked = parked;
        if (ByStylus && PathReady) // behind the box's back face = the arm stretched out, where the device has the whole stroke ahead
            _far = !parked && (_far || Vector3.Dot(hand - model.pathEntry.position, (model.pathTarget.position - model.pathEntry.position).normalized) < -guideReset);
        bool guide = Guided;
        if (guide != _wasGuided) { _wasGuided = guide; Leave(); _guide = Guide.Free; _begun = false; } // changing mode starts the needle over

        float depth = -1;
        Vector3 entry = default;
        bool under = !_in && !parked && Under(hand, forward, out depth, out entry);
        if (_gone)
        {
            if (!under || depth <= 0) Gone(false); // the controller is out of the body too: the needle is the hand's again
        }
        else if (under && depth > entryDepth && !guide)
        {
            // Through the skin. The track starts where the needle crosses it; the depth the hand is at counts as an advance from there.
            _in = true;
            _blocked = false;
            _d0 = _dir = forward;
            _tip = entry;
            _s = 0;
            _h = depth;
            _handPrev = hand;
        }

        // The stylet head slides out while the fluid runs, and back in before the needle leaves the body.
        _pull = Mathf.MoveTowards(_pull, _phase == Phase.Opening || _phase == Phase.Flowing ? 1 : 0, dt / pullSeconds);
        if (head != null) head.localPosition = _headHome + _headBack * (pullDistance * Mathf.SmoothStep(0, 1, _pull));
        if (guide && _phase == Phase.None) GuideStep(hand, forward, dt);
        else if (_in && _phase == Phase.None) Advance(hand, forward); // a needle left in the model stays where it is, whatever the hand does
        else if (_in)
        {
            if (_phase == Phase.Opening && _pull >= 1)
            {
                _phase = Phase.Flowing;
                if (flow != null) flow.Begin();
            }
            else if (_phase == Phase.Closing && _pull <= 0)
            {
                if (ByStylus) Resume(hand); // the user takes the needle out with the stylus: no animation for that
                else _phase = Phase.Withdrawing;
            }
            else if (_phase == Phase.Withdrawing)
            {
                float back = Mathf.Min(_s, withdrawSpeed * dt);
                _s -= back;
                _tip -= _dir * back;
                if (_s <= 0)
                {
                    _in = false;
                    _phase = Phase.None;
                    Gone(true);
                }
            }
        }

        if (!guide) { _drawn = false; _w = 0; }
        if (_in || _drawn) transform.SetPositionAndRotation(_tip - _dir * tipLength, Quaternion.LookRotation(_dir, _phase != Phase.None ? _up : up));
        var tip = _in ? _tip : hand;
        Go(!_in ? State.Outside : _phase == Phase.Closing || _phase == Phase.Withdrawing ? State.Withdrawing : _phase != Phase.None ? State.Extracting
            : _blocked ? State.Blocked
            : model.pathTarget != null && (tip - model.pathTarget.position).magnitude <= reachRadius ? State.Reached : State.Inside);
        if (_phase == Phase.Flowing && flow != null && (int)(flow.Volume * 10) != _tenths)
        {
            _tenths = (int)(flow.Volume * 10);
            message.text = ExtractText(_tenths);
        }

        if (haptics != null)
        {
            if (!_in || _phase != Phase.None) haptics.Release();
            else if (_blocked) haptics.Hit(_s);
            else haptics.Feel(_s, dt);
        }

        if (stylus != null)
        {
            if (haptics != null) haptics.silent = stylus.Active; // the stylus pushes back, so the controller does not buzz as well
            _ramp = Mathf.MoveTowards(_ramp, 1, dt / RampSeconds);
            // In the help mode the needle is held on the path and stops at the target (a wall past it); otherwise it is the bone that stops it.
            // The side spring comes in over the first centimetre inside, so there is no jolt at the skin. While the fluid is extracted the
            // stylus is held where it was (a spring to that point; the stylus' limit makes it soft enough to overpower).
            float side = guide ? sideStiffness * Mathf.SmoothStep(0, 1, _s / 0.01f) : sideStiffness;
            Vector3 push = Vector3.zero;
            if (_in && _phase != Phase.None) push = -(hand - _lock) * lockStiffness;
            else if (_in && haptics != null)
                push = StylusForce(hand - _tip, _dir, guide ? _over > 0 : _blocked, guide ? _over : _h - _s, haptics.Axial * TableScale, haptics.PopPulse, side, wallStiffness, popForce) * _ramp;
            stylus.Push(push); // outside the body or off the path the hand is free
        }
        if (ghost != null)
        {
            bool show = guide && (_drawn || _phase != Phase.None); // the needle is held somewhere else than where the stylus is
            if (ghost.gameObject.activeSelf != show) ghost.gameObject.SetActive(show);
            if (show) ghost.SetPositionAndRotation(handPose.position, handPose.rotation);
        }
        ShowGuide();

        if (card.gameObject.activeSelf && model.pathEntry != null)
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam != null)
            {
                float left = 0.12f + (Current == State.Extracting ? extractingShift : 0);
                var pos = model.pathEntry.position - _cam.transform.right * left + Vector3.up * 0.05f; // beside the work area, not over the needle
                card.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - _cam.transform.position));
            }
        }
    }

    /// <summary>
    /// What the tissue does to the hand that holds the needle, in the world's frame (N, before the stylus' limit). The track is where
    /// the needle is, the hand is where the stylus' tip is: `offset` is hand minus needle tip. Sideways the hand is pulled back to the
    /// track (the needle ignores sideways movement, this is how the hand feels it). Along the track a bone is a wall that pushes back
    /// in proportion to how far the hand has gone on past the stop (`pushed`, m); in tissue the table's force opposes the motion (`axial`, N,
    /// negative = against the advance: NeedleHaptics.Axial times TableScale), and the pop of a layer just entered kicks the hand forward.
    /// </summary>
    public static Vector3 StylusForce(Vector3 offset, Vector3 dir, bool blocked, float pushed, float axial, float pop,
        float sideStiffness, float wallStiffness, float popForce)
    {
        var lateral = offset - dir * Vector3.Dot(offset, dir);
        float along = blocked ? -Mathf.Max(0, pushed) * wallStiffness : axial + pop * popForce;
        return -lateral * sideStiffness + dir * along;
    }

    // The extraction is over and the stylus holds the needle again: it goes on from where it is (what the hand did meanwhile does not
    // count), and the force comes back gradually, since the hand may be far from the track by now.
    void Resume(Vector3 hand)
    {
        _phase = Phase.None;
        _h = _s;
        _handPrev = hand;
        _ramp = 0;
    }

    // Help mode: the track is the ideal path (entry to target). The stylus is judged from the moment its tip is near the path's start,
    // going on the way it came: errors are the tip's distance from the path and the pen's angle to it, in tolerances. Past guideLost the force
    // is off until the stylus is back on the path (within one tolerance) inside the box around it; a try that never began properly (straight
    // into the body) needs a new start instead: out of the box beyond guideReset before the entry, or the dock. While it is judged the needle is held
    // on the path (it eases onto it, and back to the hand when the judging stops, so it does not jump); where the stylus really is, the
    // ghost shows. The needle is in from the entry on and stops at the target.
    void GuideStep(Vector3 hand, Vector3 forward, float dt)
    {
        var entry = model.pathEntry.position;
        var axis = model.pathTarget.position - entry;
        float length = axis.magnitude;
        var d = axis / length;
        var rel = hand - entry;
        float a = Vector3.Dot(rel, d);
        var lateral = rel - d * a;
        _lat = lateral.magnitude;
        _ang = Vector3.Angle(forward, d);
        _deviation = Mathf.Max(_lat / guideLateral, _ang / guideAngle);

        _along = a;
        // The help mode's space is a box around the path: guideReset before the entry to guideReset past the target, guideCapture to each side.
        // Out of it nothing is judged. Back in, a try that was begun goes on: a lost one picks up as soon as the stylus is back on the path.
        var local = Quaternion.Inverse(Quaternion.LookRotation(d, Vector3.up)) * rel;
        bool inBox = a >= -guideReset && a <= length + guideReset && Mathf.Abs(local.x) <= guideCapture && Mathf.Abs(local.y) <= guideCapture;
        if (parked || a < -guideReset) _begun = false; // back at the start, or docked: a new try
        if (parked || !inBox) _guide = Guide.Free;
        else if (_guide == Guide.Free && !_gone && a >= -guideApproach && _lat <= guideCapture) _guide = a <= 0 ? Guide.Engaged : Guide.Lost; // straight into the body, past the start, is wrong
        if (_guide == Guide.Lost && _begun && _deviation <= 1) { _guide = Guide.Engaged; _ramp = 0; } // back on the path: the force comes back gently
        if (_guide == Guide.Engaged)
        {
            _begun = true;
            if (_deviation > guideLost) _guide = Guide.Lost;
        }

        bool engaged = _guide == Guide.Engaged;
        _w = Mathf.MoveTowards(_w, engaged ? 1 : 0, guideEase * dt);
        _drawn = _w > 0;
        if (_drawn)
        {
            float w = Mathf.SmoothStep(0, 1, _w);
            _dir = Vector3.Slerp(forward, d, w);
            _tip = Vector3.Lerp(hand, entry + d * Mathf.Min(a, length), w);
        }
        _in = engaged && a >= 0;
        _blocked = false;
        _d0 = d;
        _s = _h = Mathf.Clamp(a, 0, length);
        _over = engaged ? Mathf.Max(0, a - length) : 0;
    }

    // The indicator, on the message card: on the path, adjust, off the path, lost, or (before the stylus has been behind the box) to move it back.
    // Not while the card says something else.
    void ShowGuide()
    {
        var band = _phase == Phase.None && (Current == State.Outside || Current == State.Inside) ? GuideBand : Band.None;
        if (band == Band.None)
        {
            if (_guideShown) { _guideShown = false; card.gameObject.SetActive(false); }
            return;
        }
        int key = (int)band * 1000000 + (band == Band.Far ? 0 : (int)(_lat * 1000) * 1000 + (int)_ang); // redrawn when the band or the numbers change
        if (_guideShown && key == _guideKey) return;
        _guideShown = true;
        _guideKey = key;
        card.gameObject.SetActive(true);
        string numbers = $"<size=75%><color=#d5dce6>{(_lat * 1000).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} mm   {_ang.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}°</color></size>";
        switch (band)
        {
            case Band.Good: Say($"<b>On the path</b>\n{numbers}", new Color(0.3f, 0.85f, 0.4f)); break;
            case Band.Close: Say($"<b>Adjust</b>\n{numbers}", new Color(1, 0.83f, 0)); break;
            case Band.Off: Say($"<b>Off the path</b>\n{numbers}", new Color(1, 0.5f, 0.3f)); break;
            case Band.Far: Say("<b>Move the stylus back</b>\n<size=75%><color=#d5dce6>As far as it goes, then push in along the path</color></size>", new Color(0.45f, 0.75f, 1)); break;
            default: Say($"<b>Off the path</b>\n<size=75%><color=#d5dce6>Force is off. {(_begun ? "Return to the path" : "Go back to the start point")}</color></size>", new Color(1, 0.35f, 0.3f)); break;
        }
    }

    // The needle goes out of the body where it is (the stylus came back to its dock): the extraction, if any, is over.
    void Leave()
    {
        _in = _blocked = _drawn = false;
        _w = 0;
        _phase = Phase.None;
        _pull = 0;
        if (flow != null) flow.Stop();
        if (head != null) head.localPosition = _headHome;
        if (haptics != null) haptics.Release();
    }

    // The needle that came out of the body by itself is hidden until the controller is out as well: otherwise it would jump back
    // to a controller still held where the needle was, and go straight in again.
    void Gone(bool gone)
    {
        _gone = gone;
        if (_renderers != null) foreach (var r in _renderers) r.enabled = !gone;
    }

    // One frame of the needle in the body: the hand's advance along the track moves it, a bone stops it, and it bends a little.
    void Advance(Vector3 hand, Vector3 forward)
    {
        _h += Vector3.Dot(hand - _handPrev, _dir); // sideways movement of the hand does not count
        _handPrev = hand;

        float moved = 0;
        if (_h > _s) // pushing: the needle goes as far as the first bone ahead (the whole move is tested, so a stab cannot skip a thin process)
        {
            float step = _h - _s;
            _blocked = Cast(_tip, _tip + _dir * step, out var hit);
            moved = _blocked ? Mathf.Max(0, hit.distance - Clearance) : step;
        }
        else if (!_blocked || _h < _s - Release) // pulling back: the needle follows at once, except for the first mm after a bone
        {
            moved = _h - _s;
            _blocked = false;
        }
        _s += moved;
        _tip += _dir * moved;

        // It turns towards where the controller points, within limits; only a needle that moves can turn.
        float angle = Vector3.Angle(forward, _d0);
        var wanted = angle <= maxBend ? forward : Vector3.Slerp(_d0, forward, maxBend / angle);
        _dir = Vector3.RotateTowards(_dir, wanted, turnPerMm * 1000 * Mathf.Abs(moved) * Mathf.Deg2Rad, 0);

        if (_h <= 0) _in = false; // out of the skin
    }

    // The nearest bone the straight move from one point to the other crosses. A bone is only felt from outside, so a tip that
    // starts inside one is not stopped.
    bool Cast(Vector3 from, Vector3 to, out RaycastHit nearest)
    {
        nearest = default;
        var move = to - from;
        float length = move.magnitude;
        if (length < 1e-6f) return false;
        var ray = new Ray(from, move / length);
        bool any = false;
        foreach (var bone in bones)
            if (bone.Raycast(ray, out var h, length)) { nearest = h; length = h.distance; any = true; }
        return any;
    }

    // How much of the needle is under the skin along its own axis, and where that axis crosses the skin. The ray starts behind the
    // hub and goes in, so it meets the skin from outside, the only side a mesh collider answers to.
    bool Under(Vector3 tip, Vector3 forward, out float depth, out Vector3 entry)
    {
        const float behind = 0.15f;
        depth = -1;
        entry = default;
        if (skin == null || !skin.Raycast(new Ray(tip - forward * behind, forward), out var hit, behind + 0.3f)) return false;
        depth = behind - hit.distance;
        entry = hit.point;
        return true;
    }

    void Go(State state)
    {
        if (state == Current) return;
        Current = state;
        _guideShown = false; // the card is redrawn by whoever shows it next
        card.gameObject.SetActive(state == State.Blocked || state == State.Reached || state == State.Extracting);
        if (state == State.Blocked) Say("<b>Hit a bone</b>\n<size=75%><color=#d5dce6>Pull back and redirect</color></size>", new Color(1, 0.35f, 0.3f));
        else if (state == State.Reached) Say("<b>Target reached</b>", new Color(0.3f, 0.85f, 0.4f), "A", "Extract fluid");
        else if (state == State.Extracting)
        {
            _tenths = 0; // the volume joins the text as it grows
            Say(ExtractText(0), new Color(0.55f, 0.85f, 1), "A", "Stop");
        }
    }

    // The first line takes the colour; the rest is lighter and smaller. The border follows the colour.
    // A key and what it does go on a chip under the text; without a key the chip is hidden.
    void Say(string text, Color color, string key = null, string what = null)
    {
        message.text = text;
        message.color = color;
        if (edge != null) edge.color = new Color(color.r, color.g, color.b, 0.8f);
        if (hint == null) return;
        hint.SetActive(key != null);
        if (key == null) return;
        hintKey.text = key;
        hintLabel.text = what;
    }

    public static string ExtractText(int tenths) =>
        $"<b>Extracting fluid</b>\n<size=115%><b>{tenths / 10}.{tenths % 10} mL</b></size>";
}
