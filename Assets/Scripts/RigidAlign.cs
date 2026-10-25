using System;
using UnityEngine;

/// <summary>Least-squares rigid transform between two matched point sets (Horn 1987, quaternion method).</summary>
public static class RigidAlign
{
    /// <summary>Finds the pose that maps <paramref name="src"/> onto <paramref name="dst"/>. Needs at least 3 non-collinear points.</summary>
    public static Pose Solve(Vector3[] src, Vector3[] dst, out float rms, out float[] residuals)
    {
        int n = src.Length;
        Vector3 cs = Vector3.zero, cd = Vector3.zero;
        for (int i = 0; i < n; i++) { cs += src[i]; cd += dst[i]; }
        cs /= n; cd /= n;

        // S[a,b] = sum over points of src_a * dst_b, both centered
        var s = new double[3, 3];
        for (int i = 0; i < n; i++)
        {
            var p = src[i] - cs; var q = dst[i] - cd;
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    s[a, b] += p[a] * q[b];
        }

        var m = new double[4, 4]
        {
            { s[0,0] + s[1,1] + s[2,2], s[1,2] - s[2,1],            s[2,0] - s[0,2],            s[0,1] - s[1,0] },
            { s[1,2] - s[2,1],          s[0,0] - s[1,1] - s[2,2],   s[0,1] + s[1,0],            s[2,0] + s[0,2] },
            { s[2,0] - s[0,2],          s[0,1] + s[1,0],           -s[0,0] + s[1,1] - s[2,2],   s[1,2] + s[2,1] },
            { s[0,1] - s[1,0],          s[2,0] + s[0,2],            s[1,2] + s[2,1],           -s[0,0] - s[1,1] + s[2,2] },
        };
        var v = MaxEigenvector(m);
        var rot = new Quaternion((float)v[1], (float)v[2], (float)v[3], (float)v[0]).normalized;
        var pose = new Pose(cd - rot * cs, rot);

        residuals = new float[n];
        float sum = 0;
        for (int i = 0; i < n; i++)
        {
            residuals[i] = (pose.rotation * src[i] + pose.position - dst[i]).magnitude;
            sum += residuals[i] * residuals[i];
        }
        rms = Mathf.Sqrt(sum / n);
        return pose;
    }

    // Cyclic Jacobi on a symmetric 4x4; returns the eigenvector of the largest eigenvalue.
    static double[] MaxEigenvector(double[,] a)
    {
        var v = new double[4, 4];
        for (int i = 0; i < 4; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = 0;
            for (int p = 0; p < 3; p++) for (int q = p + 1; q < 4; q++) off += a[p, q] * a[p, q];
            if (off < 1e-24) break;
            for (int p = 0; p < 3; p++)
                for (int q = p + 1; q < 4; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-30) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                    for (int k = 0; k < 4; k++)
                    {
                        double x = a[k, p], y = a[k, q];
                        a[k, p] = c * x - sn * y; a[k, q] = sn * x + c * y;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double x = a[p, k], y = a[q, k];
                        a[p, k] = c * x - sn * y; a[q, k] = sn * x + c * y;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double x = v[k, p], y = v[k, q];
                        v[k, p] = c * x - sn * y; v[k, q] = sn * x + c * y;
                    }
                }
        }
        int best = 0;
        for (int i = 1; i < 4; i++) if (a[i, i] > a[best, best]) best = i;
        return new[] { v[0, best], v[1, best], v[2, best], v[3, best] };
    }
}
