using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public static class MorldRampRegression
{
#if UNITY_EDITOR
    [MenuItem("Morld/Tests/Run Ramp Surface Regression")]
    private static void RunFromMenu()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Player/MorldPlayer.prefab");
        var player = prefab != null ? prefab.GetComponent<CharacterController>() : null;
        if (player == null || player.slopeLimit < 45f)
            throw new Exception("MorldPlayer must allow 45-degree slopes; zero slope limit disables ramp support.");
        Debug.Log($"[Morld Ramp] {RunAll()} surface checks and player slope configuration passed.");
    }
#endif
    public static int RunAll()
    {
        Vector3[] vertices = {
            new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0),
            new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1)
        };
        int[] triangles = { 0,2,1, 3,4,5, 0,1,3, 1,4,3, 1,2,4, 2,5,4, 0,3,2, 2,3,5 };
        Bounds bounds = new Bounds(Vector3.one * 0.5f, Vector3.one);
        int count = 0;
        foreach (Vector3 axis in new[] { Vector3.forward, Vector3.back })
        foreach (float side in new[] { 0.1f, 0.25f, 0.5f, 0.75f, 0.9f })
        {
            Check(vertices, triangles, Matrix4x4.identity, bounds, new Vector3(side, 2, 0.5f), axis, side);
            count++;
        }
        foreach (Vector3 axis in new[] { Vector3.right, Vector3.left })
        foreach (float side in new[] { 0.1f, 0.5f, 0.9f })
        {
            Check(vertices, triangles, Matrix4x4.identity, bounds, new Vector3(0.5f, 2, side), axis, 1f);
            count++;
        }
        if (FezRampBlock.TrySampleMesh(vertices, triangles, Matrix4x4.identity, bounds,
            new Vector3(1.1f, 2, 0.5f), Vector3.forward, 0f, out _, out _))
            throw new Exception("Ramp retained support beyond its lateral edge.");
        count++;
        Matrix4x4 rotated = Matrix4x4.identity;
        rotated.m00 = 0; rotated.m02 = 1; rotated.m20 = -1; rotated.m22 = 0; rotated.m23 = 1;
        Check(vertices, triangles, rotated, bounds, new Vector3(0.5f, 2, 0.25f), Vector3.right, 0.75f);
        Check(vertices, triangles, rotated, bounds, new Vector3(0.25f, 2, 0.5f), Vector3.forward, 1f);
        count += 2;
        Matrix4x4 scaled = Matrix4x4.identity; scaled.m00 = 2;
        Check(vertices, triangles, scaled, new Bounds(new Vector3(1, 0.5f, 0.5f), new Vector3(2,1,1)),
            new Vector3(0.5f, 2, 0.5f), Vector3.forward, 0.25f);
        count++;
        Matrix4x4 translated = Matrix4x4.identity; translated.m03 = 3; translated.m13 = 2; translated.m23 = -2;
        Check(vertices, triangles, translated, new Bounds(new Vector3(3.5f, 2.5f, -1.5f), Vector3.one),
            new Vector3(3.25f, 4, -1.5f), Vector3.forward, 2.25f);
        return count + 1;
    }

    private static void Check(Vector3[] vertices, int[] triangles, Matrix4x4 matrix, Bounds bounds,
        Vector3 foot, Vector3 axis, float expectedHeight)
    {
        if (!FezRampBlock.TrySampleMesh(vertices, triangles, matrix, bounds, foot, axis, 0f, out var hit, out _)
            || Mathf.Abs(hit.y - expectedHeight) > 0.005f)
            throw new Exception($"Ramp surface mismatch at {foot}, view {axis}: {hit.y} != {expectedHeight}");
    }
}
