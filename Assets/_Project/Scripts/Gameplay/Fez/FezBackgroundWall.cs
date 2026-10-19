using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 背景牆本體。深度與遮擋用牆自己的碰撞器，不再另掛移動用的觸發箱。
/// </summary>
[DisallowMultipleComponent]
public sealed class FezBackgroundWall : MonoBehaviour
{
    private static readonly List<FezBackgroundWall> Registered = new List<FezBackgroundWall>(32);
    internal static IReadOnlyList<FezBackgroundWall> ActiveWalls => Registered;

    [Tooltip("牆本體碰撞器（算淺面深度、遮擋）。")]
    [SerializeField] private Collider[] wallBodyColliders;

    [Tooltip("繞行目標：本體淺面再淺這麼多（沿深度軸，通常 1 格）。")]
    [SerializeField] private float shallowRouteGridStep = 1f;

    [Tooltip("牆方塊幾何中心（例如 WallCenter）；留空則嘗試 Find(\"WallCenter\")。")]
    [SerializeField] private Transform depthReference;

    public float ShallowRouteGridStep => shallowRouteGridStep;

    /// <summary>牆方塊的幾何中心。</summary>
    public Transform DepthReference
    {
        get
        {
            if (depthReference != null)
                return depthReference;
            return transform.Find("WallCenter");
        }
    }

    private void OnEnable()
    {
        if (!Registered.Contains(this))
            Registered.Add(this);
    }

    private void OnDisable()
    {
        Registered.Remove(this);
    }

    public Bounds GetBodyWorldBounds()
    {
        var has = false;
        var b = new Bounds(transform.position, Vector3.zero);
        var cols = GetBodyColliders();
        for (var i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled)
                continue;
            if (!has)
            {
                b = c.bounds;
                has = true;
            }
            else
                b.Encapsulate(c.bounds);
        }

        return has ? b : new Bounds(transform.position, Vector3.one * 0.01f);
    }

    public float GetBodyShallowDepth(Vector3 viewForward) =>
        ProjectBoundsShallowDepth(GetBodyWorldBounds(), viewForward);

    public float GetBodyDeepDepth(Vector3 viewForward) =>
        ProjectBoundsDeepDepth(GetBodyWorldBounds(), viewForward);

    public float GetPlayerRouteTargetDepth(Vector3 viewForward) =>
        GetBodyShallowDepth(viewForward) - Mathf.Max(0.001f, shallowRouteGridStep);

    /// <summary>
    /// 繞行查詢用：與牆本體同尺寸 1×1×1（不再用 8m 高柱，避免「平台疊在牆上」時 Y 軸誤判）。
    /// </summary>
    public Bounds GetRoutingColumnBounds()
    {
        var body = GetBodyWorldBounds();
        var center = DepthReference != null ? DepthReference.position : body.center;
        return new Bounds(center, body.size);
    }

    public bool IntersectsRoutingColumn(Bounds queryBounds)
    {
        var column = GetRoutingColumnBounds();
        if (queryBounds.Intersects(column))
            return true;

        return OverlapsXZ(queryBounds, column);
    }

    internal static bool OverlapsViewColumn(Bounds player, Bounds wall, Vector3 viewForward)
    {
        viewForward.y = 0f;
        if (viewForward.sqrMagnitude < 0.0001f)
            return OverlapsXZ(player, wall);

        viewForward.Normalize();
        var lateral = Vector3.Cross(Vector3.up, viewForward);
        if (lateral.sqrMagnitude < 0.0001f)
            return OverlapsXZ(player, wall);

        lateral.Normalize();
        return ProjectIntervalOverlaps(player, wall, lateral);
    }

    private static bool ProjectIntervalOverlaps(Bounds a, Bounds b, Vector3 axis)
    {
        ProjectBoundsOntoAxis(a, axis, out var aMin, out var aMax);
        ProjectBoundsOntoAxis(b, axis, out var bMin, out var bMax);
        return aMax > bMin && aMin < bMax;
    }

    private static void ProjectBoundsOntoAxis(Bounds bounds, Vector3 axis, out float min, out float max)
    {
        var c = bounds.min;
        var s = bounds.size;
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        for (var xi = 0; xi <= 1; xi++)
        {
            for (var yi = 0; yi <= 1; yi++)
            {
                for (var zi = 0; zi <= 1; zi++)
                {
                    var p = new Vector3(c.x + s.x * xi, c.y + s.y * yi, c.z + s.z * zi);
                    var d = Vector3.Dot(p, axis);
                    if (d < min) min = d;
                    if (d > max) max = d;
                }
            }
        }
    }

    internal static bool OverlapsXZ(Bounds a, Bounds b)
    {
        return a.min.x < b.max.x && a.max.x > b.min.x
            && a.min.z < b.max.z && a.max.z > b.min.z;
    }

    public Collider[] GetBodyColliders()
    {
        if (wallBodyColliders != null && wallBodyColliders.Length > 0)
            return wallBodyColliders;

        var self = GetComponent<Collider>();
        return self != null ? new[] { self } : System.Array.Empty<Collider>();
    }

    internal static float ProjectBoundsShallowDepth(Bounds b, Vector3 vf)
    {
        var min = b.min;
        var max = b.max;
        var d0 = Vector3.Dot(new Vector3(min.x, min.y, min.z), vf);
        var d1 = Vector3.Dot(new Vector3(max.x, min.y, min.z), vf);
        var d2 = Vector3.Dot(new Vector3(min.x, max.y, min.z), vf);
        var d3 = Vector3.Dot(new Vector3(max.x, max.y, min.z), vf);
        var d4 = Vector3.Dot(new Vector3(min.x, min.y, max.z), vf);
        var d5 = Vector3.Dot(new Vector3(max.x, min.y, max.z), vf);
        var d6 = Vector3.Dot(new Vector3(min.x, max.y, max.z), vf);
        var d7 = Vector3.Dot(new Vector3(max.x, max.y, max.z), vf);
        return Mathf.Min(d0, d1, d2, d3, d4, d5, d6, d7);
    }

    internal static float ProjectBoundsDeepDepth(Bounds b, Vector3 vf)
    {
        var min = b.min;
        var max = b.max;
        var d0 = Vector3.Dot(new Vector3(min.x, min.y, min.z), vf);
        var d1 = Vector3.Dot(new Vector3(max.x, min.y, min.z), vf);
        var d2 = Vector3.Dot(new Vector3(min.x, max.y, min.z), vf);
        var d3 = Vector3.Dot(new Vector3(max.x, max.y, min.z), vf);
        var d4 = Vector3.Dot(new Vector3(min.x, min.y, max.z), vf);
        var d5 = Vector3.Dot(new Vector3(max.x, min.y, max.z), vf);
        var d6 = Vector3.Dot(new Vector3(min.x, max.y, max.z), vf);
        var d7 = Vector3.Dot(new Vector3(max.x, max.y, max.z), vf);
        return Mathf.Max(d0, d1, d2, d3, d4, d5, d6, d7);
    }
}
