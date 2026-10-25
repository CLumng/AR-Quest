using UnityEngine;

/// <summary>
/// Fluid dripping from the needle hub while it is being extracted, and the volume so far. The drops are a small pool of
/// spheres in world space, on a root of their own (not on the needle), so a drop keeps falling after the needle goes away.
/// NeedleAction calls Begin and Stop.
/// </summary>
public class FluidFlow : MonoBehaviour
{
    public Transform source;        // the hub opening: drops come out here, a little backwards along the needle
    public Transform[] drops;       // the pool; each one is off until it falls
    public float dropsPerSecond = 3;
    public float mlPerDrop = 0.05f; // about 0.15 mL/s: quicker than a real puncture, so the test does not take minutes
    public float fallLimit = 0.5f;  // a drop is taken out of play this far below where it came out

    public bool Flowing { get; private set; }
    public float Volume { get; private set; } // mL since Begin

    float _clock;
    Vector3[] _velocity;
    float[] _floor;

    public void Begin()
    {
        Flowing = true;
        Volume = 0;
        _clock = 1 / dropsPerSecond; // the first drop right away
    }

    public void Stop() { Flowing = false; }

    void Update() { Step(Time.deltaTime); }

    public void Step(float dt)
    {
        _velocity ??= new Vector3[drops.Length];
        _floor ??= new float[drops.Length];

        if (Flowing)
        {
            Volume += dropsPerSecond * mlPerDrop * dt;
            _clock += dt;
            if (_clock >= 1 / dropsPerSecond)
            {
                _clock -= 1 / dropsPerSecond;
                Spawn();
            }
        }
        for (int i = 0; i < drops.Length; i++)
        {
            if (!drops[i].gameObject.activeSelf) continue;
            _velocity[i] += Physics.gravity * dt;
            drops[i].position += _velocity[i] * dt;
            if (drops[i].position.y < _floor[i]) drops[i].gameObject.SetActive(false);
        }
    }

    void Spawn()
    {
        for (int i = 0; i < drops.Length; i++)
        {
            if (drops[i].gameObject.activeSelf) continue;
            drops[i].position = source.position;
            drops[i].gameObject.SetActive(true);
            _velocity[i] = -source.forward * 0.03f; // out of the hub, then it falls
            _floor[i] = source.position.y - fallLimit;
            return;
        }
    }
}
