using UnityEngine;

/// <summary>
/// A cube to feel each layer of the needle's haptics on its own, away from the model. The menu's "Layer tester" page opens it beside
/// the panel: the stick picks a layer (the needle's own NeedleHaptics.layers, then bone), and the cube takes that tissue's material
/// and its force. Push the needle's tip into the cube from any side: the right controller does what it does in the body, a pop as
/// the tip enters, then the layer's resistance scaled by how fast the tip moves; bone buzzes steadily at boneAmplitude.
/// The needle's tuning (speed, intervals, bone strength) is the needle's own, so what is felt here is what is felt in the model.
/// While the cube is out NeedleAction stands down, so the body behind it cannot add its own vibration. All UI text is English.
/// With the Touch stylus holding the needle the cube replaces the model (OpenOnPath: it sits on the ideal path with its face at the entry
/// point, so the stylus meets it the way it meets the body, and the model is hidden until Close) and pushes back through the stylus as the
/// body does (NeedleAction.StylusForce): the layer grips the needle on the line it went in on, resists its advance, pops as the tip enters,
/// and bone is a wall. The forces still need arming with the stylus' button 2.
/// </summary>
public class LayerTester : MonoBehaviour
{
    public NeedleAction needle;   // the needle that pokes: its pose, its length and the tissue table (needle.haptics)
    public Renderer cube;         // a unit cube scaled to its size (uniform), the cube's material is the layer's
    public Material[] materials;  // one per layer of needle.haptics, then one for bone
    public NeedleHaptics haptics; // this cube's own: one layer, whichever is selected

    public int Selected { get; private set; }
    public int Count => needle.haptics.layers.Length + 1;
    bool _bone;
    bool _modelHidden;
    bool _inside;                     // the tip was in the cube last frame
    Vector3 _entryPoint, _entryDir;   // where the tip went in, and which way: the line the layer grips the needle on

    public (string name, float resistance, float pop) Layer(int i)
    {
        var h = needle.haptics;
        if (i >= h.layers.Length) return ("Bone", h.boneAmplitude, 0);
        return (h.layers[i].name, h.layers[i].resistance, h.layers[i].pop);
    }

    /// <summary>Puts the cube out at this place and takes over the controller from the needle's body test.</summary>
    public void Open(Vector3 position, Quaternion rotation)
    {
        transform.SetPositionAndRotation(position, rotation);
        needle.card.gameObject.SetActive(false);
        needle.enabled = false;
        _inside = false;
        gameObject.SetActive(true);
        Apply();
    }

    /// <summary>
    /// Replaces the model with the cube: it sits on the ideal path with one face at the entry point (perpendicular to the path, the cube
    /// reaching down it), so the stylus, which is tied to the model, meets it where it would meet the skin. The model is hidden until Close.
    /// </summary>
    public void OpenOnPath()
    {
        var model = needle.model;
        var entry = model.pathEntry.position;
        var along = (model.pathTarget.position - entry).normalized;
        Open(entry + along * (cube.transform.lossyScale.x / 2), Quaternion.LookRotation(along, model.transform.up));
        model.SetHidden(true);
        _modelHidden = true;
    }

    public void Close()
    {
        gameObject.SetActive(false);
        needle.enabled = true;
        if (_modelHidden)
        {
            needle.model.SetHidden(false);
            _modelHidden = false;
        }
    }

    public void Select(int i)
    {
        i = Mathf.Clamp(i, 0, Count - 1);
        if (i == Selected) return;
        Selected = i;
        Apply();
    }

    // The cube becomes the layer: its material, and a table of one layer that starts at the surface. Everything else about the
    // feel (speed scaling, intervals, bone) is copied from the needle, which is what the user will meet in the model.
    void Apply()
    {
        Selected = Mathf.Clamp(Selected, 0, Count - 1);
        _bone = Selected >= needle.haptics.layers.Length;
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(needle.haptics), haptics);
        haptics.layers = _bone ? new NeedleHaptics.Layer[0] : new[] { Surface(needle.haptics.layers[Selected]) };
        if (Selected < materials.Length) cube.sharedMaterial = materials[Selected];
    }

    // A copy of the needle's layer with everything it has (the table's constants too), starting at the cube's surface and on its own.
    static NeedleHaptics.Layer Surface(NeedleHaptics.Layer layer)
    {
        var copy = JsonUtility.FromJson<NeedleHaptics.Layer>(JsonUtility.ToJson(layer));
        copy.start = 0;
        copy.continues = false;
        return copy;
    }

    void LateUpdate() => Feel(Time.deltaTime); // after LandmarkAligner has put the needle on the controller

    /// <summary>One frame of the needle's tip against the cube.</summary>
    public void Feel(float dt)
    {
        var n = needle.transform;
        var stylus = needle.stylus;
        bool byStylus = stylus != null && stylus.Active;
        haptics.silent = byStylus; // the stylus pushes back, so the controller does not buzz as well
        var tip = n.position + n.forward * needle.tipLength;
        float depth = 0;
        bool inside = n.gameObject.activeInHierarchy && Depth(tip, n.forward, out depth);
        if (inside)
        {
            if (_bone) haptics.Hit(depth); else haptics.Feel(depth, dt);
            if (!_inside) { _entryPoint = tip - n.forward * depth; _entryDir = n.forward; }
        }
        else haptics.Release();
        _inside = inside;
        // The layer grips the needle on the line it went in on (a spring across it), resists its advance, pops as the tip goes in; bone is a wall.
        if (byStylus)
            stylus.Push(inside
                ? NeedleAction.StylusForce(tip - _entryPoint, _entryDir, _bone, depth, haptics.Axial * needle.TableScale, haptics.PopPulse, needle.sideStiffness, needle.wallStiffness, needle.popForce)
                : Vector3.zero);
    }

    /// <summary>Whether the tip is inside the cube, and how deep: back along the needle to the face it came through (the cube is uniform in scale).</summary>
    public bool Depth(Vector3 tip, Vector3 forward, out float depth)
    {
        var t = cube.transform;
        Vector3 p = t.InverseTransformPoint(tip), d = t.InverseTransformDirection(forward);
        depth = 0;
        if (Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z)) > 0.5f) return false;
        float back = float.MaxValue; // how far back along -d a face is crossed, in cube units
        for (int i = 0; i < 3; i++)
            if (Mathf.Abs(d[i]) > 1e-6f) back = Mathf.Min(back, (0.5f + p[i] * Mathf.Sign(d[i])) / Mathf.Abs(d[i]));
        depth = back * t.lossyScale.x;
        return true;
    }
}
