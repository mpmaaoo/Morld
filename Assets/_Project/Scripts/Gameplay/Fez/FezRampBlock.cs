using System.Collections.Generic;
using UnityEngine;

/// <summary>Samples the visible upper outline of an editable, solid ramp mesh.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshCollider), typeof(FezOneWayPlatform))]
public sealed class FezRampBlock : MonoBehaviour
{
    private static readonly List<FezRampBlock> Registered = new List<FezRampBlock>();
    public static IReadOnlyList<FezRampBlock> ActiveRamps => Registered;
    private MeshCollider body;
    private Mesh cachedMesh;
    private Vector3[] vertices;
    private int[] triangles;
    public MeshCollider Body => body != null ? body : body = GetComponent<MeshCollider>();
    public FezOneWayPlatform Platform => GetComponent<FezOneWayPlatform>();
    private void OnEnable() { if (!Registered.Contains(this)) Registered.Add(this); }
    private void OnDisable() { Registered.Remove(this); }
    private void OnValidate() { cachedMesh = null; }
    private void Reset() { Platform.ConfigureStandaloneBody(Body); }

    public bool TrySample(Vector3 foot, Vector3 depthAxis, float lateralMargin,
        out Vector3 point, out Vector3 normal)
    {
        point = default; normal = Vector3.up;
        if (!isActiveAndEnabled || Body == null || !Body.enabled || Body.isTrigger || Body.sharedMesh == null) return false;
        if (cachedMesh != Body.sharedMesh)
        {
            cachedMesh = Body.sharedMesh;
            vertices = cachedMesh.vertices;
            triangles = cachedMesh.triangles;
        }
        return TrySampleMesh(vertices, triangles, Body.transform.localToWorldMatrix, Body.bounds,
            foot, depthAxis, lateralMargin, out point, out normal);
    }

    public static bool TrySampleMesh(Vector3[] vertices, int[] triangles, Matrix4x4 matrix,
        Bounds bounds, Vector3 foot, Vector3 depthAxis, float lateralMargin,
        out Vector3 point, out Vector3 normal)
    {
        point = default; normal = Vector3.up;
        Vector3 right = Vector3.Cross(Vector3.up, depthAxis).normalized;
        float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
        float side = Vector3.Dot(foot - bounds.center, right);
        if (Mathf.Abs(side) > half + lateralMargin + 0.0001f) return false;
        float inset = Mathf.Min(0.001f, half * 0.1f);
        float coordinate = Vector3.Dot(bounds.center, right) + Mathf.Clamp(side, -half + inset, half - inset);
        float top = float.NegativeInfinity;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = matrix.MultiplyPoint3x4(vertices[triangles[i]]);
            Vector3 b = matrix.MultiplyPoint3x4(vertices[triangles[i + 1]]);
            Vector3 c = matrix.MultiplyPoint3x4(vertices[triangles[i + 2]]);
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            if (n.y < 0.7f) continue;
            // Intersect every upward triangle with the vertical view column. Its highest
            // edge becomes a flat top when the ramp rises along the camera depth axis.
            ConsiderEdge(a, b, right, depthAxis, coordinate, foot, n, ref top, ref point, ref normal);
            ConsiderEdge(b, c, right, depthAxis, coordinate, foot, n, ref top, ref point, ref normal);
            ConsiderEdge(c, a, right, depthAxis, coordinate, foot, n, ref top, ref point, ref normal);
        }
        return !float.IsNegativeInfinity(top);
    }

    private static void ConsiderEdge(Vector3 a, Vector3 b, Vector3 right, Vector3 depth,
        float coordinate, Vector3 foot, Vector3 n, ref float top, ref Vector3 point, ref Vector3 normal)
    {
        float x = Vector3.Dot(a, right), dx = Vector3.Dot(b - a, right);
        if (Mathf.Abs(dx) < 0.000001f) return;
        float t = (coordinate - x) / dx;
        if (t < -0.0001f || t > 1.0001f) return;
        Vector3 hit = Vector3.Lerp(a, b, Mathf.Clamp01(t));
        if (hit.y < top - 0.0001f) return;
        if (Mathf.Abs(hit.y - top) <= 0.0001f)
        {
            float oldDepth = Vector3.Dot(point, depth), newDepth = Vector3.Dot(hit, depth);
            float wanted = Mathf.Clamp(Vector3.Dot(foot, depth), Mathf.Min(oldDepth, newDepth), Mathf.Max(oldDepth, newDepth));
            point += depth * (wanted - oldDepth);
            return;
        }
        top = hit.y; point = hit; normal = n;
    }

    public bool TryGetSupport(CharacterController player, Vector3 feet, Vector3 axis,
        float margin, out FezOneWayPlatform.SupportHit hit)
    {
        hit = default;
        if (Body == null || !Body.enabled) return false;
        Vector3 column = feet + axis * Vector3.Dot(Body.bounds.center - feet, axis);
        if (!TrySample(column, axis, margin, out Vector3 point, out Vector3 normal)) return false;
        Vector3 right = Vector3.Cross(Vector3.up, axis).normalized;
        float gradient = Mathf.Abs(Vector3.Dot(normal, right)) / Mathf.Max(0.001f, normal.y);
        if (Mathf.Atan(gradient) * Mathf.Rad2Deg > player.slopeLimit + 0.1f) return false;
        float radius = player.radius * Mathf.Max(Mathf.Abs(player.transform.lossyScale.x), Mathf.Abs(player.transform.lossyScale.z));
        // A capsule's bottom is above a tilted plane when its rounded base touches it.
        float signedGradient = -Vector3.Dot(normal, right) / normal.y;
        Bounds bounds = Body.bounds;
        float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
        float side = Vector3.Dot(feet - bounds.center, right);
        float contactSide = Mathf.Clamp(side + radius * signedGradient / Mathf.Sqrt(1f + gradient * gradient), -half, half);
        Vector3 contactColumn = column + right * (contactSide - side);
        if (!TrySample(contactColumn, axis, 0f, out var contact, out _)) return false;
        float distance = Vector3.Dot(contact - feet, right);
        if (Mathf.Abs(distance) > radius + 0.002f) return false;
        // Use the finite top edge, rather than extending the slope beyond its crest.
        // This lets the rounded capsule join a flat block without a hop or extra lift.
        float height = contact.y + Mathf.Sqrt(Mathf.Max(0f, radius * radius - distance * distance)) - radius;
        point = contact + right * Vector3.Dot(feet - contact, right);
        point.y = height + player.skinWidth;
        hit = new FezOneWayPlatform.SupportHit { platform = Platform, collider = Body, point = point };
        return hit.platform != null && hit.platform.isActiveAndEnabled;
    }

    // Sample along the real slope instead of its flat screen silhouette.
    public bool TryGetDepthSupport(CharacterController player, Vector3 feet, Vector3 viewAxis,
        out FezOneWayPlatform.SupportHit hit, out float uphillSign)
    {
        hit = default; uphillSign = 0f;
        Vector3 right = Vector3.Cross(Vector3.up, viewAxis).normalized;
        float radius = player.radius * Mathf.Max(Mathf.Abs(player.transform.lossyScale.x), Mathf.Abs(player.transform.lossyScale.z));
        if (!TrySample(feet, viewAxis, radius, out _, out var normal)) return false;
        float depthGrade = -Vector3.Dot(normal, viewAxis);
        if (Mathf.Abs(depthGrade) < 0.01f || Mathf.Abs(depthGrade) < Mathf.Abs(Vector3.Dot(normal, right))) return false;
        Vector3 samplingAxis = Vector3.Cross(viewAxis, Vector3.up).normalized;
        if (!TryGetSupport(player, feet, samplingAxis, radius, out hit)) return false;
        hit.point += samplingAxis * Vector3.Dot(feet - hit.point, samplingAxis);
        uphillSign = Mathf.Sign(depthGrade);
        return true;
    }

    public bool ContinuesSurface(FezRampBlock other, Vector3 samplingAxis, bool requireDepthOverlap)
    {
        if (other == null || other == this) return other == this;
        Bounds a = Body.bounds, b = other.Body.bounds;
        Vector3 right = Vector3.Cross(Vector3.up, samplingAxis).normalized;
        float halfA = Mathf.Abs(right.x) * a.extents.x + Mathf.Abs(right.z) * a.extents.z;
        float halfB = Mathf.Abs(right.x) * b.extents.x + Mathf.Abs(right.z) * b.extents.z;
        float offset = Vector3.Dot(b.center - a.center, right);
        if (Mathf.Abs(Mathf.Abs(offset) - halfA - halfB) > 0.025f) return false;
        if (requireDepthOverlap)
        {
            float depthA = Mathf.Abs(samplingAxis.x) * a.extents.x + Mathf.Abs(samplingAxis.z) * a.extents.z;
            float depthB = Mathf.Abs(samplingAxis.x) * b.extents.x + Mathf.Abs(samplingAxis.z) * b.extents.z;
            if (Mathf.Abs(Vector3.Dot(b.center - a.center, samplingAxis)) >= depthA + depthB - 0.001f) return false;
        }
        Vector3 seam = a.center + right * (Mathf.Sign(offset) * halfA);
        return TrySample(seam, samplingAxis, 0.025f, out var topA, out var normalA) &&
            other.TrySample(seam, samplingAxis, 0.025f, out var topB, out var normalB) &&
            Mathf.Abs(topA.y - topB.y) <= 0.025f &&
            Vector3.Dot(Vector3.ProjectOnPlane(normalA, Vector3.up).normalized,
                Vector3.ProjectOnPlane(normalB, Vector3.up).normalized) > 0.995f;
    }

    public bool TryGetProjectedTipJoin(FezRampBlock other, Vector3 axis, out float side, out float height)
    {
        side = height = 0f;
        if (other == null || other == this) return false;
        Vector3 right = Vector3.Cross(Vector3.up, axis).normalized;
        Bounds a = Body.bounds, b = other.Body.bounds;
        float halfA = Mathf.Abs(right.x) * a.extents.x + Mathf.Abs(right.z) * a.extents.z;
        float halfB = Mathf.Abs(right.x) * b.extents.x + Mathf.Abs(right.z) * b.extents.z;
        float centerA = Vector3.Dot(a.center, right), centerB = Vector3.Dot(b.center, right);
        for (int i = -1; i <= 1; i += 2)
        {
            float edge = centerA + i * halfA;
            if (Mathf.Abs(Mathf.Abs(edge - centerB) - halfB) > 0.025f) continue;
            Vector3 seam = a.center + right * (edge - centerA);
            if (!TrySample(seam, axis, 0.025f, out var topA, out var normalA) ||
                !other.TrySample(seam, axis, 0.025f, out var topB, out var normalB) ||
                Mathf.Abs(topA.y - topB.y) > 0.025f ||
                Vector3.Dot(normalA, right) * Vector3.Dot(normalB, right) >= -0.0001f) continue;
            side = edge; height = (topA.y + topB.y) * 0.5f;
            return true;
        }
        return false;
    }
}
