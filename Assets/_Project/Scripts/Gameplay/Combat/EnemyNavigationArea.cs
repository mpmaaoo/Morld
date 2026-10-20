using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Layered physical floor graph. Multiple floors may occupy the same XZ cell.</summary>
public sealed class EnemyNavigationArea : MonoBehaviour
{
    [Serializable] private sealed class Node
    {
        public Vector3 position;
        public List<int> neighbours = new List<int>();
    }
    [InspectorName("區域大小（世界軸向）")] public Vector3 size = new Vector3(12f, 6f, 12f);
    [InspectorName("格子間距"), Min(0.2f)] public float cellSize = 0.5f;
    [InspectorName("加密窄通道採樣")] public bool refineNarrowPassages = true;
    public float SamplingSpacing => Mathf.Max(0.2f, cellSize * (refineNarrowPassages ? 0.5f : 1f));
    [InspectorName("敵人半徑"), Min(0.1f)] public float agentRadius = 0.3f;
    [InspectorName("敵人高度"), Min(0.2f)] public float agentHeight = 1f;
    [InspectorName("相鄰格最大高度差"), Range(0.01f, 0.3f)] public float maxRise = 0.15f;
    [InspectorName("實體地形圖層")] public LayerMask terrainMask = ~0;
    [InspectorName("顯示路網")] public bool showGraph = true;
    [SerializeField, HideInInspector] private List<Node> nodes = new List<Node>();
    [SerializeField, HideInInspector] private Vector3 builtOrigin;
    [SerializeField, HideInInspector] private Vector3 builtSize;
    [SerializeField, HideInInspector] private Vector4 builtAgent;
    [SerializeField, HideInInspector] private int builtMask;
    [SerializeField, HideInInspector] private int builtVersion;
    [SerializeField, HideInInspector] private float builtSpacing;
    private readonly List<int> indices = new List<int>();
    private struct NearestCandidate { public int index; public float distance; }
    private readonly List<NearestCandidate> nearestCandidates = new List<NearestCandidate>(64);
    private static readonly Comparison<NearestCandidate> NearestOrder = (a, b) =>
    {
        int comparison = a.distance.CompareTo(b.distance);
        return comparison != 0 ? comparison : a.index.CompareTo(b.index);
    };
    public int NodeCount => nodes.Count;
    public string LastPathStatus { get; private set; }
    public bool IsBuilt => builtVersion == 2 && builtSpacing == SamplingSpacing && nodes.Count > 0 && builtOrigin == transform.position && builtSize == size
        && builtAgent == new Vector4(cellSize, agentRadius, agentHeight, maxRise) && builtMask == terrainMask.value;
    private const int MaxCells = 4096;

    private void Start() { if (!IsBuilt) Rebuild(); }

    [ContextMenu("重建實體尋路網格")]
    public void Rebuild()
    {
        nodes.Clear();
        builtOrigin = transform.position;
        builtSize = size;
        builtAgent = new Vector4(cellSize, agentRadius, agentHeight, maxRise);
        builtMask = terrainMask.value;
        builtVersion = 2;
        builtSpacing = SamplingSpacing;
        float spacing = SamplingSpacing;
        int width = Mathf.CeilToInt(Mathf.Max(0f, size.x) / spacing);
        int depth = Mathf.CeilToInt(Mathf.Max(0f, size.z) / spacing);
        if ((long)width * depth > MaxCells || width == 0 || depth == 0)
        {
            Debug.LogWarning("[Morld] 尋路區域過大或為空；請縮小區域，最多 4096 個 XZ 格。", this);
            return;
        }
        Physics.SyncTransforms();
        var cells = new Dictionary<Vector2Int, List<int>>();
        Vector3 minimum = builtOrigin - size * 0.5f;
        for (int x = 0; x < width; x++) for (int z = 0; z < depth; z++)
        {
            Vector3 origin = new Vector3(minimum.x + (x + 0.5f) * spacing, minimum.y + size.y, minimum.z + (z + 0.5f) * spacing);
            var hits = Physics.RaycastAll(origin, Vector3.down, Mathf.Max(0f, size.y), terrainMask, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (!IsTerrain(hit.collider) || hit.normal.y < 0.75f || !HasClearance(hit.point)) continue;
                var cell = new Vector2Int(x, z);
                if (!cells.TryGetValue(cell, out var list)) { list = new List<int>(); cells.Add(cell, list); }
                bool duplicate = false;
                foreach (int existing in list) if (Mathf.Abs(nodes[existing].position.y - hit.point.y) < 0.05f) duplicate = true;
                if (duplicate) continue;
                if (nodes.Count >= 8192) { nodes.Clear(); Debug.LogWarning("[Morld] 尋路層數過多，請縮小區域。", this); return; }
                list.Add(nodes.Count);
                nodes.Add(new Node { position = hit.point });
            }
        }
        Vector2Int[] directions = { Vector2Int.right, Vector2Int.up };
        foreach (var cell in cells) foreach (var direction in directions)
        {
            if (!cells.TryGetValue(cell.Key + direction, out var adjacent)) continue;
            foreach (int a in cell.Value) foreach (int b in adjacent)
            {
                if (!CanTraverse(nodes[a].position, nodes[b].position)) continue;
                nodes[a].neighbours.Add(b);
                nodes[b].neighbours.Add(a);
            }
        }
    }

    private static bool IsTerrain(Collider collider) => collider != null
        && collider.GetComponentInParent<FezDepthProxyFollower>() == null
        && collider.GetComponentInParent<CharacterController>() == null
        && collider.GetComponentInParent<PlayerController>() == null
        && collider.GetComponentInParent<StationaryMeleeEnemy>() == null;

    private void Capsule(Vector3 feet, out Vector3 lower, out Vector3 upper, out float radius)
    {
        radius = Mathf.Max(0.05f, agentRadius);
        lower = feet + Vector3.up * (radius + 0.035f);
        upper = feet + Vector3.up * Mathf.Max(radius + 0.035f, agentHeight - radius);
    }
    private bool HasClearance(Vector3 feet)
    {
        Capsule(feet, out var lower, out var upper, out float radius);
        foreach (var other in Physics.OverlapCapsule(lower, upper, radius, terrainMask, QueryTriggerInteraction.Ignore))
            if (IsTerrain(other)) return false;
        return true;
    }
    public bool CanTraverse(Vector3 from, Vector3 to)
    {
        if (Mathf.Abs(to.y - from.y) > maxRise || !HasClearance(to)) return false;
        Vector3 delta = to - from;
        float length = delta.magnitude;
        if (length < 0.001f) return true;
        Capsule(from, out var lower, out var upper, out float radius);
        foreach (var hit in Physics.CapsuleCastAll(lower, upper, radius, delta / length, length, terrainMask, QueryTriggerInteraction.Ignore))
            if (IsTerrain(hit.collider)) return false;
        int samples = Mathf.Max(2, Mathf.CeilToInt(length / 0.1f));
        for (int i = 0; i <= samples; i++)
        {
            Vector3 point = Vector3.Lerp(from, to, i / (float)samples);
            bool floor = false;
            foreach (var hit in Physics.RaycastAll(point + Vector3.up * 0.08f, Vector3.down, 0.16f, terrainMask, QueryTriggerInteraction.Ignore))
                if (IsTerrain(hit.collider) && hit.normal.y >= 0.75f) { floor = true; break; }
            if (!floor) return false;
        }
        return true;
    }
    private int Nearest(Vector3 position)
    {
        nearestCandidates.Clear();
        float connectionDistance = Mathf.Max(1f, cellSize * 2f);
        float maximumDistance = connectionDistance * connectionDistance;
        for (int i = 0; i < nodes.Count; i++)
        {
            Vector3 delta = nodes[i].position - position;
            // A coarse grid must not require actors to stand almost exactly on its centres.
            // Longer connectors still pass the full capsule sweep and continuous floor checks.
            float distance = delta.sqrMagnitude;
            if (Mathf.Abs(delta.y) > 0.35f || distance > maximumDistance) continue;
            nearestCandidates.Add(new NearestCandidate { index = i, distance = distance });
        }
        nearestCandidates.Sort(NearestOrder);
        foreach (var candidate in nearestCandidates)
            if (CanTraverse(position, nodes[candidate.index].position)) return candidate.index;
        return -1;
    }
    public bool TryGroundPosition(Vector3 position, out Vector3 grounded)
    {
        grounded = position;
        float best = float.PositiveInfinity;
        foreach (var hit in Physics.RaycastAll(position + Vector3.up * 0.12f, Vector3.down, 0.42f, terrainMask, QueryTriggerInteraction.Ignore))
        {
            if (!IsTerrain(hit.collider) || hit.normal.y < 0.75f || hit.distance >= best) continue;
            grounded = hit.point;
            best = hit.distance;
        }
        return !float.IsPositiveInfinity(best);
    }
    public bool FindPath(Vector3 from, Vector3 goal, List<Vector3> path)
        => FindPath(from, goal, path, false, out _);

    public bool FindPath(Vector3 from, Vector3 goal, List<Vector3> path, bool allowClosest, out bool partial)
    {
        using (PathProfile.Auto()) return FindPathCore(from, goal, path, allowClosest, out partial);
    }

    private static readonly Unity.Profiling.ProfilerMarker PathProfile = new Unity.Profiling.ProfilerMarker("Morld.EnemyPathfinding");
    private bool FindPathCore(Vector3 from, Vector3 goal, List<Vector3> path, bool allowClosest, out bool partial)
    {
        partial = false;
        path.Clear();
        if (!IsBuilt) { LastPathStatus = "路網尚未重建或設定已變更"; return false; }
        if (!TryGroundPosition(from, out from)) { LastPathStatus = "敵人腳下找不到實體地板"; return false; }
        bool targetGrounded = TryGroundPosition(goal, out var groundedGoal);
        if (targetGrounded) goal = groundedGoal;
        if (!targetGrounded && !allowClosest) { LastPathStatus = "玩家不在實體地板上"; return false; }
        int start = Nearest(from), end = targetGrounded ? Nearest(goal) : -1;
        if (start < 0) { LastPathStatus = DescribeUnconnected(from, "敵人"); return false; }
        if (end < 0 && !allowClosest) { LastPathStatus = DescribeUnconnected(goal, "玩家"); return false; }
        bool found = end >= 0 && EnemyAStar.Search(nodes.Count, start, end, i => nodes[i].neighbours,
            (a, b) => Vector3.Distance(nodes[a].position, nodes[b].position),
            i => Vector3.Distance(nodes[i].position, nodes[end].position), indices);
        if (!found && allowClosest)
        {
            found = EnemyAStar.SearchClosest(nodes.Count, start, i => nodes[i].neighbours,
                (a, b) => Vector3.Distance(nodes[a].position, nodes[b].position),
                i => (nodes[i].position - goal).sqrMagnitude, indices);
            partial = found;
        }
        if (!found) { LastPathStatus = "雙方所在路網不連通"; return false; }
        foreach (int index in indices) path.Add(nodes[index].position);
        LastPathStatus = partial ? "前往最接近目標的可達地板" : "路徑已建立";
        return true;
    }
    private string DescribeUnconnected(Vector3 feet, string actor)
    {
        if (!new Bounds(transform.position, size).Contains(feet + Vector3.up * 0.05f))
            return actor + "在尋路區域外，請擴大區域";
        if (!HasClearance(feet)) return actor + "位置淨空不足，請檢查牆距與尋路半徑";
        return actor + "附近無可達節點，請重建並檢查綠色路網";
    }
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(transform.position, size);
        if (!showGraph) return;
        Gizmos.color = IsBuilt ? new Color(0.2f, 1f, 0.4f) : Color.red;
        foreach (var node in nodes)
        {
            Gizmos.DrawWireSphere(node.position + Vector3.up * 0.04f, 0.04f);
            foreach (int index in node.neighbours) if (index >= 0 && index < nodes.Count) Gizmos.DrawLine(node.position, nodes[index].position);
        }
    }
}
