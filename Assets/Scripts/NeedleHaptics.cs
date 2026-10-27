using System;
using UnityEngine;
using UnityEngine.InputSystem.XR;

/// <summary>
/// The force of the needle going through the body, as vibration on the right controller (a controller cannot push back).
/// A table of layers (measured along the ideal path on the anatomy meshes, see NeedleHapticsBuilder) says how hard each tissue
/// is. While the needle moves, the vibration follows the resistance of the layer the tip is in, scaled by how fast it moves;
/// entering some layers gives a short pop (the skin giving way, the loss of resistance in the epidural space, the dura).
/// The table is read by depth under the skin, so it is exact on the ideal path and a typical sequence anywhere else.
/// A bone is the exception: the tip on bone buzzes at boneAmplitude, whatever the layer.
/// NeedleAction feeds it every frame: Feel with the tip's depth under the skin, Hit on bone, Release when the tip is out.
/// For a force device (the Touch stylus) the same table also holds the physical constants of each tissue and Force works out the needle's
/// force along its axis as the user's model does (Python/IdealCurve.py): F = F_tip + F_friction, F_tip = -(k·dx + rho·v), F_friction =
/// -(sum of mu of every tissue the tip has gone into)·sign(v) (dx: how far the tip is past where its tissue starts, not capped; v: the tip's
/// speed, + = in). The sign is against the motion, so every term opposes it (IdealCurve.py subtracts rho·v; at these speeds it is ~0.01 N).
/// A tissue with scaleTo has its whole force scaled so its peak is that (every tissue above 3 N, to 3 N): the curve keeps its shape.
/// </summary>
public class NeedleHaptics : MonoBehaviour
{
    [Serializable]
    public class Layer
    {
        public string name;
        public float start;      // depth where the tip enters it: metres from the entry point, along the path
        public float resistance; // 0..1: vibration while moving through it
        public float pop;        // 0..1: short impulse when the tip enters it
        public float stiffness;  // N/m: the tissue's spring, pushing the tip back by this much per metre it is past the start
        public float viscosity;  // N·s/m: its damper, against the tip's speed
        public float friction;   // N: kinetic friction of the shaft in this tissue, against the motion; it stays once the tip is deeper
        public float thickness;  // m: the tissue's width in the model, to the next tissue (the last one to the target); only Peak reads it
        public bool continues;   // one tissue with the layer before: the spring goes on from where that one started
        public float scaleTo;    // N: the force in this layer is scaled so its peak (TissuePeak) is this; 0 = as the formula gives it
    }

    public Layer[] layers;           // sorted by start; each one runs until the next starts, the last until the target
    public float fullSpeed = 0.015f; // m/s of needle travel that feels the full resistance
    public float restShare = 0.2f;   // share of the resistance still felt while the needle is held in the tissue
    public float interval = 0.03f;   // seconds between impulses; each lasts twice that, so they run together into a buzz
    public float popSeconds = 0.08f;
    public float boneAmplitude = 0.95f; // the buzz on bone: nearly all the controller can give
    public float frictionSpeed = 0.003f; // m/s at which the kinetic friction is whole; slower it fades out, so the hand's tremor does not flip it
    public bool silent;              // the stylus holds the needle: no buzz on the controller (the amplitudes are still worked out)

    float _depth = -1; // tip depth last frame; negative = the needle was not in
    float _speed;      // smoothed travel speed, m/s
    float _next;       // when the next buzz impulse may go out
    float _pop;        // the pop of the layer just entered, running down over popSeconds

    public bool InTissue => _depth >= 0; // fed this frame: the needle is in
    public float LastSent { get; private set; } // amplitude of the last impulse sent (the Editor has no rumble, so checks read this)

    // For a force device (the stylus), which can push back along the needle: the same table as numbers.
    public float Axial { get; private set; }    // N along the needle as the table has it (NeedleAction.TableScale brings it to the device): negative = against the advance
    public float PopPulse { get; private set; } // 0..1: the pop of the layer just entered, running down

    /// <summary>The strongest steady force of the table (N): the highest tissue peak, after scaling.</summary>
    public float Peak
    {
        get
        {
            float peak = 0;
            for (int i = 0; i < layers.Length; i++) peak = Mathf.Max(peak, layers[i].scaleTo > 0 ? layers[i].scaleTo : TissuePeak(i));
            return peak;
        }
    }

    /// <summary>The formula's steady force at the end of layer i (N): its tissue's spring stretched over the tissue's width plus the friction of every tissue so far.</summary>
    public float TissuePeak(int i) => layers[i].stiffness * layers[First(i)].thickness + Friction(i);

    void OnDisable() { Release(); }

    /// <summary>Vibration amplitude (0..1) with the tip at this depth, moving at this speed (either way).</summary>
    public float Amplitude(float depth, float speed)
    {
        var layer = LayerAt(depth);
        return layer == null ? 0 : layer.resistance * (restShare + (1 - restShare) * Mathf.Clamp01(Mathf.Abs(speed) / fullSpeed));
    }

    /// <summary>The strongest pop among the layers the tip enters going deeper from one depth to another (none when it comes out).</summary>
    public float Pop(float from, float to)
    {
        float pop = 0;
        foreach (var l in layers) if (l.start > from && l.start <= to) pop = Mathf.Max(pop, l.pop);
        return pop;
    }

    public Layer LayerAt(float depth)
    {
        int i = IndexAt(depth);
        return i < 0 ? null : layers[i];
    }

    int IndexAt(float depth)
    {
        int found = -1;
        for (int i = 0; i < layers.Length; i++) if (layers[i].start <= depth) found = i;
        return found;
    }

    int First(int i) // the layer a tissue's spring starts from
    {
        while (i > 0 && layers[i].continues) i--;
        return i;
    }

    /// <summary>The needle's force along its axis (N, negative = against the advance) with the tip at this depth moving at this speed (m/s, + = in).</summary>
    public float Force(float depth, float speed)
    {
        int i = IndexAt(depth);
        if (i < 0) return 0;
        var l = layers[i];
        float dx = depth - layers[First(i)].start;
        float force = -(l.stiffness * dx + l.viscosity * speed) - Friction(i) * Mathf.Clamp(speed / frictionSpeed, -1, 1);
        float peak = l.scaleTo > 0 ? TissuePeak(i) : 0;
        return peak > 0 ? force * l.scaleTo / peak : force;
    }

    // The shaft's kinetic friction with the tip in layer i: every tissue it has gone into, each once (a layer that continues one is that tissue).
    float Friction(int i)
    {
        float mu = 0;
        for (int j = 0; j <= i; j++) if (!layers[j].continues) mu += layers[j].friction;
        return mu;
    }

    /// <summary>The needle is in the tissue with its tip at this depth. Call every frame.</summary>
    public void Feel(float depth, float dt)
    {
        float from = _depth < 0 ? -1 : _depth; // first frame in: everything up to here counts as just entered
        float speed = _depth < 0 ? 0 : (depth - _depth) / Mathf.Max(dt, 1e-4f);
        _speed = Mathf.Lerp(_speed, speed, 1 - Mathf.Exp(-10 * dt)); // the hand's tremor is not a push
        _depth = depth;

        float pop = Pop(from, depth);
        Axial = Force(depth, _speed);
        _pop = Mathf.Max(Mathf.MoveTowards(_pop, 0, dt / popSeconds), pop);
        PopPulse = _pop;
        if (pop > 0)
        {
            Send(pop, popSeconds);
            _next = Time.time + popSeconds;
        }
        else if (Time.time >= _next)
        {
            float amplitude = Amplitude(depth, _speed);
            if (amplitude > 0.01f) Send(amplitude, interval * 2);
            _next = Time.time + interval;
        }
    }

    /// <summary>The tip is on bone, at this depth: a steady buzz at boneAmplitude. Call every frame. The tissue's pops are not replayed when it comes free.</summary>
    public void Hit(float depth)
    {
        _depth = depth;
        _speed = 0;
        Axial = 0;
        PopPulse = _pop = 0;
        if (Time.time >= _next)
        {
            Send(boneAmplitude, interval * 2);
            _next = Time.time + interval;
        }
    }

    /// <summary>The needle is out of the body.</summary>
    public void Release()
    {
        _depth = -1;
        _speed = 0;
        Axial = 0;
        PopPulse = _pop = 0;
    }

    void Send(float amplitude, float seconds)
    {
        LastSent = Mathf.Clamp01(amplitude);
        if (!silent) (XRController.rightHand as XRControllerWithRumble)?.SendImpulse(LastSent, seconds);
    }
}
