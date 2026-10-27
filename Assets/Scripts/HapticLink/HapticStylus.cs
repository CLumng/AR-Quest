using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The Touch 3D Stylus holding the needle in the main scene. The stylus and the physical model are not tied to each other, so the
/// stylus is not placed in the room: its frame is tied to the anatomy through the inkwell. `home` is a point on the model (a child of it)
/// where the tip sits when the stylus is docked, and its rotation says how the device's axes lie in the model. Everything the stylus does
/// is measured from the dock: the needle's tip is home.TransformPoint(tip - dock), and it turns with the pen. So the virtual needle meets
/// the anatomy's colliders wherever the real stylus is on the desk.
/// While the stylus is docked the needle rests at home (NeedleAction.parked: it goes nowhere and no force is asked), and the sticks move
/// home: LEFT stick across the model (relative to where the user looks), RIGHT stick turns it and lifts it; it is kept between sessions.
/// When it leaves the dock the needle stays hidden until its axis is out of the body (see NeedleAction).
/// Button 1 does what A does on the controller (NeedleAction.Toggle: extract fluid at the target, stop; the user takes the needle out).
/// Button 2 arms and disarms the forces, so the hand can always leave the anatomy freely: they start disarmed, and docking or losing the
/// link disarms them. The force itself comes from NeedleAction (Push, in the world's frame); it is limited, turned into the device's frame
/// and sent in LateUpdate. The state, the help mode and the strength are on the menu's Settings page (LandmarkAligner).
/// </summary>
[DefaultExecutionOrder(10)] // LateUpdate sends the force NeedleAction has just worked out (NeedleAction runs at 0)
public class HapticStylus : MonoBehaviour
{
    public string host = "127.0.0.1"; // USB: the bridge's port is reached through adb reverse; over Wi-Fi, the PC's address
    public int port = HapticProtocol.Port;
    public Vector3 stylusAxis = Vector3.back; // where the pen points, in the device's end-effector frame (measured: the pen points along -Z)
    public Vector3 dock = new(0, -0.0979f, 0.0856f); // the tip in the inkwell, in Unity's device frame (m): (0, -97.9, -85.6) mm on this unit
    public Transform home;            // on the model: where the docked tip is, and how the device's axes lie in the model
    public NeedleAction needle;
    public LandmarkAligner aligner;   // its menu's Settings page has the stylus (state, help mode, force strength)
    public float maxForce = 1f;       // N, the most that is ever asked (the bridge limits it too)
    public float strength = 1f;       // 0..1, on every force and on the limit
    public float damping = 0.04f;     // s, time constant of the dampening of the force sent (0 = off): about 3 frames at 72 Hz
    public float moveSpeed = 0.05f;   // m/s of home at full stick
    public float turnSpeed = 30;      // degrees/s of home at full stick

    public Action onButton1;          // by default NeedleAction.Toggle, the controller's A
    public bool Live { get; private set; }   // the bridge has sent a state lately
    public bool Active => Live;              // the needle follows the stylus
    public bool Docked => Live && (_s.flags & HapticProtocol.Inkwell) != 0;
    public bool ForcesOn => _forcesOn;
    public Vector3 SentForce { get; private set; } // what went to the bridge last: the device's frame, N
    public Vector3 SentWorld { get; private set; } // the same force in the world's frame, before the device's flip (the force graph records it)
    public bool SentEnabled { get; private set; }

    readonly HapticProtocol.StateFrame _s = new();
    HapticLinkClient _link;
    InputAction _move, _turn;
    bool _manual, _b1, _b2, _forcesOn, _dirty;
    int _frame = -1;
    Vector3 _pending;                // the force NeedleAction asked this frame, in the world's frame
    Vector3 _damped;                 // what the dampening let through last frame (world's frame)
    Vector3 _defaultPos;
    float _defaultYaw;

    [Serializable] class Saved { public Vector3 defaultPos, pos; public float defaultYaw, yaw; }
    const string Key = "stylus.home";

    void Awake() { if (needle != null) onButton1 = needle.Toggle; }

    void OnEnable()
    {
        _move = new InputAction("move", binding: "<XRController>{LeftHand}/{Primary2DAxis}");
        _turn = new InputAction("turn", binding: "<XRController>{RightHand}/{Primary2DAxis}");
        _move.Enable();
        _turn.Enable();
        _link = new HapticLinkClient(host, port);
    }

    void OnDisable()
    {
        Save();
        _link?.Dispose();
        _link = null;
        _move?.Dispose();
        _turn?.Dispose();
    }

    void Start()
    {
        // The builder's home is the default; what the user moved it to is kept unless the default itself has changed since.
        _defaultPos = home.localPosition;
        _defaultYaw = home.localEulerAngles.y;
        var json = PlayerPrefs.GetString(Key, "");
        if (json == "") return;
        var d = JsonUtility.FromJson<Saved>(json);
        if ((d.defaultPos - _defaultPos).sqrMagnitude > 1e-8f || Mathf.Abs(Mathf.DeltaAngle(d.defaultYaw, _defaultYaw)) > 0.01f) return;
        home.localPosition = d.pos;
        home.localRotation = Quaternion.Euler(0, d.yaw, 0);
    }

    void Update() { Tick(Time.deltaTime); }
    void LateUpdate() { Flush(); }

    /// <summary>The needle's pose (its hub, with the tip along its forward axis) for the stylus' tip, through home.</summary>
    public Pose NeedlePose(float tipLength)
    {
        Poll();
        ToUnity(_s.m, stylusAxis, out var p, out var q);
        var rotation = home.rotation * q;
        return new Pose(home.TransformPoint(p - dock) - rotation * Vector3.forward * tipLength, rotation);
    }

    /// <summary>A finished run as CSV text, for the bridge to save on the PC (see HapticLinkClient.SendRun). Nothing happens without a link.</summary>
    public void SendRun(long id, string csv) { _link?.SendRun(id, csv); }

    /// <summary>True once the PC has saved the run; `name` is its file there.</summary>
    public bool RunSaved(long id, out string name)
    {
        name = null;
        return _link != null && _link.RunSaved(id, out name);
    }

    /// <summary>The force NeedleAction wants on the stylus now, in the world's frame (N). Sent at the end of the frame, if the forces are armed.</summary>
    public void Push(Vector3 worldForce) { _pending = worldForce; }

    /// <summary>One frame: the buttons, the dock, the home point. Public for the checks, which drive it.</summary>
    public void Tick(float dt)
    {
        Poll();
        bool b1 = Live && (_s.buttons & HapticProtocol.Button1) != 0, b2 = Live && (_s.buttons & HapticProtocol.Button2) != 0;
        if (b1 && !_b1) onButton1?.Invoke();
        if (b2 && !_b2) _forcesOn = !_forcesOn;
        _b1 = b1;
        _b2 = b2;
        if (!Live || Docked) _forcesOn = false; // a dropped link, or the stylus back in its inkwell, disarms the forces

        if (needle != null) needle.parked = Docked;
        bool nudging = Docked && aligner != null && aligner.Idle;
        if (nudging) Nudge(dt);
        else if (_dirty) Save();
    }

    /// <summary>End of the frame: the force is limited, dampened, put in the device's frame and handed to the link.</summary>
    public void Flush() { Flush(Time.deltaTime); }

    /// <summary>
    /// The dampening is a low-pass on the limited force (the formula and its friction are untouched): a jump in what NeedleAction asks, like the
    /// friction flipping sign between frames, reaches the device as a ramp of about `damping` seconds, and since it only mixes forces that are
    /// already inside the limit it can never leave it. Disarming, a lost link or a NaN cut the force at once, with no tail.
    /// </summary>
    public void Flush(float dt)
    {
        float limit = maxForce * strength;
        bool on = _forcesOn && Live && float.IsFinite(_pending.x + _pending.y + _pending.z);
        var target = on ? Vector3.ClampMagnitude(_pending, maxForce) * strength : Vector3.zero;
        _damped = !on || damping <= 0 ? target : Vector3.ClampMagnitude(Vector3.Lerp(_damped, target, 1 - Mathf.Exp(-dt / damping)), limit);
        var f = _damped;
        _pending = Vector3.zero;
        SentWorld = f;
        var local =Quaternion.Inverse(home.rotation) * f; // the model's frame, then the device's: Z flips (the device's +Z is toward the user)
        SentForce = new Vector3(local.x, local.y, -local.z);
        SentEnabled = _forcesOn && Live;
        _link?.SetForce(SentForce.x, SentForce.y, SentForce.z, SentEnabled);
    }

    /// <summary>For the checks: use this state instead of the bridge's.</summary>
    public void Inject(HapticProtocol.StateFrame state, bool live = true)
    {
        _manual = true;
        _s.CopyFrom(state);
        Live = live;
    }

    void Poll()
    {
        if (_manual || Time.frameCount == _frame) return;
        _frame = Time.frameCount;
        Live = _link != null && _link.Latest(_s, out float age) && age < 300;
    }

    // Home moves with the sticks, relative to where the user looks (up on the stick is away from the user), and turns about its own point.
    void Nudge(float dt)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) return; // looking straight up or down
        fwd.Normalize();
        var right = Vector3.Cross(Vector3.up, fwd);
        Vector2 m = Curve(_move.ReadValue<Vector2>()), r = Curve(_turn.ReadValue<Vector2>());
        if (m == Vector2.zero && r == Vector2.zero) return;
        home.position += (right * m.x + fwd * m.y + Vector3.up * r.y) * (moveSpeed * dt);
        home.Rotate(Vector3.up, r.x * turnSpeed * dt, Space.World);
        _dirty = true;
    }

    // Dead zone, then quadratic: a small flick is a slow, fine correction.
    static Vector2 Curve(Vector2 v) => v.magnitude < 0.15f ? Vector2.zero : v * v.magnitude;

    void Save()
    {
        if (!_dirty || home == null) return;
        _dirty = false;
        PlayerPrefs.SetString(Key, JsonUtility.ToJson(new Saved { defaultPos = _defaultPos, defaultYaw = _defaultYaw, pos = home.localPosition, yaw = home.localEulerAngles.y }));
        PlayerPrefs.Save();
    }

    /// <summary>The device's transform (column major, mm, right handed, +Z toward the user) as a pose in Unity's frame (m, +Z away from the user).</summary>
    public static void ToUnity(float[] m, Vector3 axis, out Vector3 position, out Quaternion rotation)
    {
        position = new Vector3(m[12], m[13], -m[14]) * 0.001f;
        Vector3 c0 = new(m[0], m[1], m[2]), c1 = new(m[4], m[5], m[6]), c2 = new(m[8], m[9], m[10]);
        Vector3 pen = axis.x * c0 + axis.y * c1 + axis.z * c2;
        rotation = Quaternion.LookRotation(Flip(pen), Flip(c1));
    }

    static Vector3 Flip(Vector3 v) => new(v.x, v.y, -v.z);
}
