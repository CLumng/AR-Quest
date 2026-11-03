using System;
using UnityEngine;

/// <summary>
/// The Touch 3D Stylus holding the needle in the main scene. The stylus and the physical model are not tied to each other, so the
/// stylus is not placed in the room: its frame is tied to the anatomy through the inkwell. `home` is a point on the model (a child of it)
/// where the tip sits when the stylus is docked, and its rotation says how the device's axes lie in the model. Everything the stylus does
/// is measured from the dock: the needle's tip is home.TransformPoint(tip - dock), and it turns with the pen. So the virtual needle meets
/// the anatomy's colliders wherever the real stylus is on the desk.
/// While the stylus is docked the needle rests at home (NeedleAction.parked: it goes nowhere and no force is asked). Home moves only from the
/// menu's "Move stylus" page (LandmarkAligner calls Move / ResetHome): across the model on the pad and up / down on the height meter, in the
/// user's frame, never out of padRange / levelRange from where it started, and only with the stylus in its inkwell or without data; it is kept
/// between sessions.
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
    public float padRange = 0.15f;    // m, how far home can go from where it started, each way across the model (X and Z)
    public float levelRange = 0.10f;  // m, the same up and down (Y)

    public Action onButton1;          // by default NeedleAction.Toggle, the controller's A
    public bool Live { get; private set; }   // the bridge has sent a state lately
    public bool Active => Live;              // the needle follows the stylus
    public bool Docked => Live && (_s.flags & HapticProtocol.Inkwell) != 0;
    public bool ForcesOn => _forcesOn;
    public bool CanMove => !Live || Docked;  // out of the inkwell the needle is in the hand: moving home would drag it
    public Vector3 SentForce { get; private set; } // what went to the bridge last: the device's frame, N
    public Vector3 SentWorld { get; private set; } // the same force in the world's frame, before the device's flip (the force graph records it)
    public bool SentEnabled { get; private set; }
    public float StateAgeMs { get; private set; }  // how old the stylus' pose was when this frame read it (ms; the force graph's CSV records it)
    public float RoundTripMs => _link != null ? _link.RoundTripMs : 0; // command out to the state that acknowledges it (ms)

    readonly HapticProtocol.StateFrame _s = new();
    HapticLinkClient _link;
    bool _manual, _b1, _b2, _forcesOn, _dirty, _moving, _hasDefault;
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
        _link = new HapticLinkClient(host, port);
    }

    void OnDisable()
    {
        Save();
        _link?.Dispose();
        _link = null;
    }

    void Start()
    {
        // The builder's home is the default; what the user moved it to is kept unless the default itself has changed since.
        Defaults();
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

    /// <summary>One frame: the buttons, the dock, saving the home point. Public for the checks, which drive it.</summary>
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
        if (_moving) _moving = false; // Move runs from the menu each frame the stick is held: saved once it has stopped
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
        float age = 0;
        Live = _link != null && _link.Latest(_s, out age) && age < 300;
        StateAgeMs = Live ? age : 0;
    }

    // Where home started (the builder's, or the default the saved one was made against), taken once: before Start in the Editor's checks.
    void Defaults()
    {
        if (_hasDefault) return;
        _hasDefault = true;
        _defaultPos = home.localPosition;
        _defaultYaw = home.localEulerAngles.y;
    }

    Vector3 StartWorld => home.parent != null ? home.parent.TransformPoint(_defaultPos) : _defaultPos;

    /// <summary>How far home is from where it started in the user's frame (right and forward are horizontal, perpendicular, unit): x to their right, y up, z away from them (m).</summary>
    public Vector3 Shift(Vector3 right, Vector3 forward)
    {
        Defaults();
        var o = home.position - StartWorld;
        return new Vector3(Vector3.Dot(o, right), o.y, Vector3.Dot(o, forward));
    }

    /// <summary>
    /// Moves home across the model by `across` (x right, y away from the user) and up by `lift` (m), in the user's frame. Across is the XZ
    /// plane and lift the Y axis, each held within padRange / levelRange of where home started. Nothing moves, and it says so, with the stylus
    /// out of its inkwell.
    /// </summary>
    public bool Move(Vector2 across, float lift, Vector3 right, Vector3 forward)
    {
        if (!CanMove) return false;
        var s = Shift(right, forward);
        float x = Mathf.Clamp(s.x + across.x, -padRange, padRange), y = Mathf.Clamp(s.y + lift, -levelRange, levelRange), z = Mathf.Clamp(s.z + across.y, -padRange, padRange);
        home.position = StartWorld + right * x + Vector3.up * y + forward * z;
        _moving = _dirty = true;
        return true;
    }

    /// <summary>Home back to where it started (and as it was turned). False, and nothing moves, with the stylus out of its inkwell.</summary>
    public bool ResetHome()
    {
        if (!CanMove) return false;
        Defaults();
        home.localPosition = _defaultPos;
        home.localRotation = Quaternion.Euler(0, _defaultYaw, 0);
        _dirty = true;
        return true;
    }

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
