using System.Globalization;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Test scene for the Touch 3D Stylus on the PC talking to the headset: the stylus' tip and pen are drawn from the pose the bridge sends,
/// and a ball in the middle of the workspace pushes back, so both directions of the link can be felt. A card shows the link's health
/// (state rate, round trip, age), the pose, the buttons and the force.
/// Everything is computed in the workspace's own space (Unity axes: the device's +Z, toward the user, becomes -Z), so the workspace can be
/// anywhere in the room; moving it with the controller only changes where that space sits (the real stylus must be where it is drawn).
/// Controls, on the controller of `hand`: X place the workspace so that the drawn tip is at the controller's tip (touch the real stylus tip
/// with it), Y force on/off (starts off; goes off by itself if the link drops), trigger a short push of pulseForce, to check the way back
/// with no geometry in the middle.
/// </summary>
public class HapticLinkTest : MonoBehaviour
{
    public string host = "127.0.0.1";  // USB: the PC's port is reached through adb reverse; over Wi-Fi, the PC's address
    public int port = HapticProtocol.Port;
    public string hand = "LeftHand";   // the controller with the controls: the other hand holds the stylus
    public Vector3 stylusAxis = Vector3.back; // where the pen points, in the device's end-effector frame. If the pen is drawn backwards, try (0, 0, 1)

    public float ballRadius = 0.025f;  // m
    public float stiffness = 100f;     // N/m: 0.1 N/mm. A hard wall over a network with delay shakes; raise it slowly
    public float maxForce = 1f;        // N; the bridge limits it too (--max-force)
    public float pulseForce = 0.5f, pulseSeconds = 0.25f;

    public Transform trackingSpace, workspace, tip, panel;
    public Renderer tipRenderer, ballRenderer;
    public Material tipIdle, tipButton1, tipButton2, ballIdle, ballTouched;
    public TMP_Text status;

    HapticLinkClient _link;
    readonly HapticProtocol.StateFrame _s = new();
    InputAction _place, _toggle, _pulse, _pos, _tracked;
    bool _forceOn, _placed, _live;
    float _pulseUntil, _nextText, _lostSince;
    Vector3 _tip, _force; // in the workspace's space: m, N

    void OnEnable()
    {
        string h = $"<XRController>{{{hand}}}/";
        _place = new InputAction("place", InputActionType.Button, h + "{PrimaryButton}");
        _toggle = new InputAction("toggle", InputActionType.Button, h + "{SecondaryButton}");
        _pulse = new InputAction("pulse", InputActionType.Button, h + "{TriggerButton}");
        _pos = new InputAction("pos", binding: h + "pointerPosition");
        _tracked = new InputAction("tracked", InputActionType.Button, h + "isTracked");
        foreach (var a in new[] { _place, _toggle, _pulse, _pos, _tracked }) a.Enable();
        _link = new HapticLinkClient(host, port);
        ballRenderer.transform.localScale = Vector3.one * (ballRadius * 2);
    }

    void OnDisable()
    {
        _link?.Dispose();
        _link = null;
        foreach (var a in new[] { _place, _toggle, _pulse, _pos, _tracked }) a?.Dispose();
    }

    void Update()
    {
        var cam = Camera.main;
        if (!_placed && cam != null && Time.timeSinceLevelLoad > 1) PlaceInFront(cam.transform);
        if (_toggle.WasPressedThisFrame()) _forceOn = !_forceOn;
        if (_pulse.WasPressedThisFrame()) _pulseUntil = Time.time + pulseSeconds;

        _live = _link.Latest(_s, out float age) && age < 300;
        if (_live)
        {
            HapticStylus.ToUnity(_s.m, stylusAxis, out _tip, out var rot);
            tip.SetLocalPositionAndRotation(_tip, rot);
            _lostSince = 0;
        }
        else if (_lostSince == 0) _lostSince = Time.time;
        else if (Time.time - _lostSince > 0.5f) _forceOn = false; // a link that blinks does not leave the force armed
        tip.gameObject.SetActive(_live);

        if (_place.WasPressedThisFrame() && _tracked.IsPressed() && cam != null) PlaceAtController(cam.transform);

        _force = _live && _forceOn ? Clamp(BallForce(_tip) + (Time.time < _pulseUntil ? Vector3.up * pulseForce : Vector3.zero), maxForce) : Vector3.zero;
        _link.SetForce(_force.x, _force.y, -_force.z, _live && _forceOn);

        bool touched = _live && _tip.magnitude < ballRadius;
        ballRenderer.sharedMaterial = touched ? ballTouched : ballIdle;
        tipRenderer.sharedMaterial = (_s.buttons & HapticProtocol.Button1) != 0 ? tipButton1 : (_s.buttons & HapticProtocol.Button2) != 0 ? tipButton2 : tipIdle;

        if (Time.time >= _nextText)
        {
            _nextText = Time.time + 0.1f;
            status.text = Describe(age);
        }
    }

    /// <summary>The ball's push on a tip at this place (workspace space, m): out along the radius, k times how deep the tip is.</summary>
    public Vector3 BallForce(Vector3 tipLocal)
    {
        float d = tipLocal.magnitude;
        if (d >= ballRadius) return Vector3.zero;
        return (d < 1e-5f ? Vector3.up : tipLocal / d) * ((ballRadius - d) * stiffness);
    }

    public static Vector3 Clamp(Vector3 f, float max) => f.sqrMagnitude > max * max ? f.normalized * max : f;

    // Where the card and the workspace start: ahead of the head, level with the yaw it looks at.
    void PlaceInFront(Transform head)
    {
        _placed = true;
        var yaw = Quaternion.Euler(0, head.eulerAngles.y, 0);
        workspace.SetPositionAndRotation(head.position + yaw * new Vector3(0, -0.30f, 0.45f), yaw);
        panel.position = head.position + yaw * new Vector3(-0.2f, 0.02f, 0.75f);
        panel.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(panel.position - head.position, Vector3.up));
    }

    // The controller's tip is on the real stylus tip: put the workspace so the drawn tip is there too. The yaw comes from where the head looks.
    void PlaceAtController(Transform head)
    {
        var yaw = Quaternion.Euler(0, head.eulerAngles.y, 0);
        var point = trackingSpace.TransformPoint(_pos.ReadValue<Vector3>());
        workspace.SetPositionAndRotation(point - yaw * (_live ? _tip : Vector3.zero), yaw);
        _placed = true;
    }

    string Describe(float age)
    {
        var sb = new StringBuilder(512);
        string N(float v, string f = "F1") => v.ToString(f, CultureInfo.InvariantCulture);
        const string Green = "#7CE08A", Red = "#FF7A7A", Dim = "#9AA3B0", Amber = "#FFD400";
        sb.Append("<b>Haptic link</b>   ");
        if (_link.Connected && _live) sb.Append($"<color={Green}>connected</color>");
        else if (_link.Connected) sb.Append($"<color={Amber}>connected, no data</color>");
        else sb.Append($"<color={Red}>waiting for the bridge</color> <color={Dim}>{host}:{port}</color>");
        sb.Append('\n');
        if (!_live)
        {
            sb.Append($"<color={Dim}>On the PC: open HapticBridge\\HapticBridge.pyw\nOver USB: adb reverse tcp:{port} tcp:{port}</color>");
            return sb.ToString();
        }
        sb.Append($"<color={Dim}>Rate</color> {N(_link.StatesPerSecond, "F0")} Hz    <color={Dim}>Round trip</color> {N(_link.RoundTripMs, "F0")} ms    <color={Dim}>Age</color> {N(age, "F0")} ms\n");
        sb.Append($"<color={Dim}>Tip (mm)</color> {N(_s.m[12])}  {N(_s.m[13])}  {N(_s.m[14])}\n");
        sb.Append($"<color={Dim}>Buttons</color> 1 {(((_s.buttons & HapticProtocol.Button1) != 0) ? $"<color={Green}>ON</color>" : "off")}   2 {(((_s.buttons & HapticProtocol.Button2) != 0) ? $"<color={Green}>ON</color>" : "off")}" +
            ((_s.flags & HapticProtocol.Inkwell) != 0 ? $"   <color={Amber}>in the inkwell</color>" : "") + "\n");
        sb.Append($"<color={Dim}>Force</color> {(_forceOn ? $"<color={Green}>ON</color>" : $"<color={Red}>OFF</color>")}   asked {N(_force.magnitude, "F2")} N   applied {N(new Vector3(_s.fx, _s.fy, _s.fz).magnitude, "F2")} N\n");
        sb.Append($"<color={Dim}>{hand}: X place the workspace at the controller tip · Y force on/off · trigger push</color>");
        return sb.ToString();
    }
}
