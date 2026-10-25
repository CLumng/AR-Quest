using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Adds the lumbar puncture guide to the ModelTarget: the ideal needle path (entry disc, line, target ball) and the
/// recommended skin area. Everything is measured on the anatomy meshes, so it follows the model if the prefab changes.
/// It is one menu group ("Puncture guide"), hidden while aligning. Model space: +Y head, +Z back.
/// </summary>
public static class PunctureGuideBuilder
{
    const string GroupName = "Puncture guide", ObjectName = "PunctureGuide";
    // The ideal path goes through this interspace (upper, lower vertebra). L3-L4 is the usual first choice: the cord ends at L1-L2.
    const string Upper = "L3", Lower = "L4";
    // The recommended skin area runs from the spinous tip of AreaTop to AreaBottom: the L3-L4 and L4-L5 interspaces.
    const string AreaTop = "L3", AreaBottom = "L5";
    const float HalfWidth = 0.012f; // area half width around the midline
    const int MaxTiltDeg = 25;      // the needle may head this far cephalad
    const float Lift = 0.001f;      // the area floats this far over the skin so the two do not flicker against each other
    const float Approach = 0.05f;   // the path line goes on this far outside the skin

    [MenuItem("Tools/Model Target/Build Puncture Guide")]
    static void Rebuild()
    {
        var target = Object.FindFirstObjectByType<ModelTarget>();
        ModelTargetSceneBuilder.Refit(target); // everything measured on the model goes together
        Add(target);
        // Scenes built before the needle action existed get it (and anything it gained later) here; a full rebuild adds it with the needle.
        var needle = Object.FindFirstObjectByType<LandmarkAligner>()?.needle;
        if (needle != null) ModelTargetSceneBuilder.AddNeedleAction(needle, target);
        EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
    }

    public static void Add(ModelTarget target)
    {
        var root = target.transform;
        var old = root.Find(ObjectName);
        if (old != null) Object.DestroyImmediate(old.gameObject);
        target.groups = target.groups.Where(g => g.name != GroupName).ToArray();

        // Midpoint between the spinous tips: on the midline, where the needle passes between the two processes.
        var tipUp = ModelTargetSceneBuilder.ExtremePoint(root, Upper, Vector3.forward);
        var tipLow = ModelTargetSceneBuilder.ExtremePoint(root, Lower, Vector3.forward);
        var mid = (tipUp + tipLow) / 2;
        float y0 = ModelTargetSceneBuilder.ExtremePoint(root, AreaBottom, Vector3.forward).y;
        float y1 = ModelTargetSceneBuilder.ExtremePoint(root, AreaTop, Vector3.forward).y;

        // Target: the middle of the dural sac at this level. Its back wall is where the bone stops mattering.
        var sac = Verts(root, "Spinal Cord").Where(p => Mathf.Abs(p.y - mid.y) < 0.003f).ToArray();
        float zTarget = sac.Average(p => p.z), zDura = sac.Max(p => p.z);

        // Ideal path: the line to the sac that stays farthest from the bone (spinous processes and laminae) on the midline.
        // Candidates cross the depth of the spinous tips between the two tips. The needle heads up (cephalad) as it goes in,
        // so the line leaves the body backwards and down.
        var bone = new[] { "Sacrum", "S1", "L5", "L4", "L3", "L2", "L1" }.SelectMany(n => Verts(root, n))
            .Where(p => Mathf.Abs(p.x - mid.x) < 0.012f && p.z > zDura).ToArray();
        float zTop = bone.Max(p => p.z), best = -1, yLow = Mathf.Min(tipUp.y, tipLow.y);
        int bestTilt = 0, steps = Mathf.RoundToInt(Mathf.Abs(tipUp.y - tipLow.y) / 0.0005f);
        Vector3 aim = default, dir = default;
        for (int tilt = 0; tilt <= MaxTiltDeg; tilt++)
        {
            var d = new Vector3(0, -Mathf.Sin(tilt * Mathf.Deg2Rad), Mathf.Cos(tilt * Mathf.Deg2Rad));
            for (int i = 0; i <= steps; i++) // height between the tips where the line crosses their depth, 0.5 mm steps
            {
                var tgt = new Vector3(mid.x, yLow + i * 0.0005f + (mid.z - zTarget) * d.y / -d.z, zTarget);
                var start = tgt + d * ((zDura - zTarget) / d.z);
                float len = (zTop - zDura) / d.z, near = float.MaxValue;
                foreach (var v in bone)
                    near = Mathf.Min(near, (v - (start + d * Mathf.Clamp(Vector3.Dot(v - start, d), 0, len))).sqrMagnitude);
                if (near > best) { best = near; bestTilt = tilt; aim = tgt; dir = d; }
            }
        }

        // Entry point: where that line leaves the skin. The area follows the skin surface between the two spinous tips.
        var skin = Triangles(root, "Skin", mid.x - 0.03f, y0 - 0.03f, mid.x + 0.03f, y1 + 0.03f);
        var far = aim + dir * 0.15f;
        float? hit = Hit(skin, far, -dir);
        if (hit == null) { Debug.LogError("Puncture guide: the path does not reach the skin."); return; }
        var entry = far - dir * hit.Value;

        const int nx = 8, ny = 25;
        var grid = new Vector3[(nx + 1) * (ny + 1)];
        for (int j = 0, k = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++, k++)
            {
                float x = mid.x + HalfWidth * (2f * i / nx - 1), y = Mathf.Lerp(y0, y1, (float)j / ny);
                float? h = Hit(skin, new Vector3(x, y, 0.2f), Vector3.back);
                grid[k] = new Vector3(x, y, h.HasValue ? 0.2f - h.Value + Lift : entry.z);
            }
        var tris = new List<int>();
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1;
                tris.AddRange(new[] { a, c, b, b, c, c + 1 });
            }
        var mesh = new Mesh { name = "RecommendedArea", vertices = grid, triangles = tris.ToArray() };
        mesh.RecalculateBounds();

        var guide = new GameObject(ObjectName).transform;
        guide.SetParent(root, false);
        var areaMat = GuideMaterial("Assets/Materials/GuideArea.mat", new Color(0.2f, 0.85f, 0.4f, 0.35f), CompareFunction.LessEqual, 3005); // after the translucent tissue, so it stays over the skin
        var pathMat = GuideMaterial("Assets/Materials/GuidePath.mat", new Color(0.1f, 0.8f, 1, 0.9f), CompareFunction.Always, 3010); // over the skin: always readable
        var tubeMat = GuideMaterial("Assets/Materials/GuideTube.mat", new Color(0.1f, 0.8f, 1, 0.15f), CompareFunction.Always, 3010); // very see-through: the needle is seen clearly inside it

        var outside = entry + dir * Approach;
        var along = Quaternion.FromToRotation(Vector3.up, dir);
        Part(guide, "RecommendedArea", mesh, Vector3.zero, Quaternion.identity, Vector3.one, areaMat);
        Part(guide, "IdealPath", Resources.GetBuiltinResource<Mesh>("Cylinder.fbx"), (aim + outside) / 2, along, new Vector3(0.003f, Vector3.Distance(aim, outside) / 2, 0.003f), tubeMat);
        // A ring, so the skin point stays visible. Only a suggestion: the needle may go in anywhere, and keeps to the way it goes in.
        target.pathEntry = Part(guide, "EntryRing", Ring(0.005f, 0.008f), entry + dir * Lift, along, Vector3.one, pathMat);
        target.pathTarget = Part(guide, "Target", Resources.GetBuiltinResource<Mesh>("Sphere.fbx"), aim, Quaternion.identity, Vector3.one * 0.007f, pathMat);

        target.groups = new[] { new ModelTarget.Group { name = GroupName, objects = new[] { guide.gameObject } } }.Concat(target.groups).ToArray();
        Debug.Log($"Puncture guide {Upper}-{Lower}: tilt {bestTilt} deg, {Mathf.Sqrt(best) * 1000:F1} mm clear of bone, " +
                  $"{(entry - aim).magnitude * 1000:F0} mm from skin to target, entry {entry:F4}, target {aim:F4}");
    }

    internal static Material GuideMaterial(string path, Color color, CompareFunction ztest, int queue)
    {
        var mat = ModelTargetSceneBuilder.MakeMaterial(path, "ModelTarget/GuideOverlay");
        mat.color = color;
        mat.SetFloat("_ZTest", (float)ztest);
        mat.renderQueue = queue;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    internal static Transform Part(Transform parent, string name, Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = rot;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.sortingOrder = TissueFadeBuilder.OverlayOrder; // drawn after the translucent tissue, never under it
        return go.transform;
    }

    // Flat ring facing +Y, like the cap of a cylinder, so it can be turned to face along the path.
    static Mesh Ring(float inner, float outer)
    {
        const int n = 32;
        var v = new Vector3[2 * n];
        var t = new int[6 * n];
        for (int i = 0; i < n; i++)
        {
            float a = 2 * Mathf.PI * i / n;
            var radial = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            v[i] = radial * inner;
            v[n + i] = radial * outer;
            int j = (i + 1) % n;
            new[] { i, n + i, j, j, n + i, n + j }.CopyTo(t, 6 * i);
        }
        return new Mesh { name = "EntryRing", vertices = v, triangles = t };
    }

    internal static Vector3[] Verts(Transform root, string meshName)
    {
        var mf = root.GetComponentsInChildren<MeshFilter>(true).First(m => m.name == meshName);
        return mf.sharedMesh.vertices.Select(v => root.InverseTransformPoint(mf.transform.TransformPoint(v))).ToArray();
    }

    // Only the triangles of a mesh that touch the box (x, y): the skin has 65 thousand and the guide needs a few hundred rays.
    internal static List<(Vector3 a, Vector3 b, Vector3 c)> Triangles(Transform root, string meshName, float xMin, float yMin, float xMax, float yMax)
    {
        var v = Verts(root, meshName);
        var t = root.GetComponentsInChildren<MeshFilter>(true).First(m => m.name == meshName).sharedMesh.triangles;
        var list = new List<(Vector3, Vector3, Vector3)>();
        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            if (Mathf.Max(a.x, b.x, c.x) < xMin || Mathf.Min(a.x, b.x, c.x) > xMax || Mathf.Max(a.y, b.y, c.y) < yMin || Mathf.Min(a.y, b.y, c.y) > yMax) continue;
            list.Add((a, b, c));
        }
        return list;
    }

    // Distance along the ray to the nearest triangle in front of it.
    static float? Hit(List<(Vector3 a, Vector3 b, Vector3 c)> tris, Vector3 origin, Vector3 dir)
    {
        float? nearest = null;
        foreach (float t in Crossings(tris, origin, dir)) if (nearest == null || t < nearest) nearest = t;
        return nearest;
    }

    // Moller-Trumbore: the distance along the ray to every triangle in front of it (a closed mesh gives them in pairs: in, out).
    internal static IEnumerable<float> Crossings(List<(Vector3 a, Vector3 b, Vector3 c)> tris, Vector3 origin, Vector3 dir)
    {
        foreach (var (a, b, c) in tris)
        {
            Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(dir, e2), s = origin - a;
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) continue;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0 || u > 1) continue;
            Vector3 q = Vector3.Cross(s, e1);
            float w = Vector3.Dot(dir, q) / det;
            if (w < 0 || u + w > 1) continue;
            float t = Vector3.Dot(e2, q) / det;
            if (t > 0) yield return t;
        }
    }
}
