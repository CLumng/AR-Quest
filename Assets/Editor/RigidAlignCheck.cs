using UnityEditor;
using UnityEngine;

/// <summary>Checks RigidAlign recovers a known pose, including from coplanar points.</summary>
public static class RigidAlignCheck
{
    [MenuItem("Tools/Model Target/Self-check Rigid Alignment")]
    public static void Run()
    {
        var rng = new System.Random(7);
        float R() => (float)(rng.NextDouble() - 0.5);
        foreach (bool coplanar in new[] { false, true })
        {
            var truth = new Pose(new Vector3(R(), R(), R()), Quaternion.Euler(R() * 360, R() * 360, R() * 360));
            var src = new Vector3[5];
            var dst = new Vector3[5];
            for (int i = 0; i < src.Length; i++)
            {
                src[i] = new Vector3(R() * 0.3f, coplanar ? 0 : R() * 0.2f, R() * 0.1f);
                dst[i] = truth.rotation * src[i] + truth.position;
            }
            var pose = RigidAlign.Solve(src, dst, out float rms, out _);
            float angle = Quaternion.Angle(pose.rotation, truth.rotation);
            float shift = (pose.position - truth.position).magnitude;
            bool ok = rms < 1e-5f && angle < 0.01f && shift < 1e-5f;
            Debug.Log($"RigidAlign {(coplanar ? "coplanar" : "3D")}: rms={rms:E1} angle={angle:E1} deg shift={shift:E1} -> {(ok ? "PASS" : "FAIL")}");
            if (!ok) throw new System.Exception("RigidAlign self-check failed");
        }
    }
}
