using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Registered real platform geometry. The motor samples its projected top at the player's feet.
/// Neither this component nor a proxy moves the player or toggles a scene collider.
/// </summary>
[RequireComponent(typeof(Collider))]
[DisallowMultipleComponent]
public sealed class FezOneWayPlatform : MonoBehaviour
{
    private static readonly List<FezOneWayPlatform> Registered = new List<FezOneWayPlatform>(64);
    internal static IReadOnlyList<FezOneWayPlatform> ActivePlatforms => Registered;
    private static readonly Dictionary<Collider, List<FezOneWayPlatform>> BodyOwners =
        new Dictionary<Collider, List<FezOneWayPlatform>>();
    private static readonly Dictionary<Collider, List<FezOneWayPlatform>> AllColliderOwners =
        new Dictionary<Collider, List<FezOneWayPlatform>>();
    private static readonly HashSet<FezOneWayPlatform> CandidateSet = new HashSet<FezOneWayPlatform>();
    private static readonly List<FezOneWayPlatform> CandidatePlatforms = new List<FezOneWayPlatform>(64);
    private static Collider[] CandidateColliders = new Collider[128];
    private const float CandidateHalfWidth = 0.025f;
    private const float CandidateDepthReach = 10000f;
    private static float MaxWorldTopOffset;
    private static int _nextRegistrationOrder;
    private static readonly Dictionary<CharacterController, HashSet<Collider>> Ignored =
        new Dictionary<CharacterController, HashSet<Collider>>();
    private static readonly HashSet<FezOneWayPlatform> DirtyPlatforms = new HashSet<FezOneWayPlatform>();
    private static readonly HashSet<CharacterController> PreparedPlayers = new HashSet<CharacterController>();
    [SerializeField] private bool autoIncludeParentCollider = true;
    [SerializeField] private Collider[] platformBodyColliders;
    [SerializeField] private Collider topSurfaceReference;
    [SerializeField] private float worldTopYOffset;
    private Collider _proxy;
    private Collider[] _bodies, _all;
    private int _registrationOrder;

    public struct SupportHit
    {
        public FezOneWayPlatform platform;
        public Collider collider;
        public Vector3 point;
    }

    private void Awake()
    {
        _proxy = GetComponent<Collider>();
        var bodies = new List<Collider>();
        if (platformBodyColliders != null)
            foreach (var col in platformBodyColliders)
                if (col != null && col != _proxy && !bodies.Contains(col)) bodies.Add(col);
        if (bodies.Count == 0 && autoIncludeParentCollider && transform.parent != null)
            foreach (var col in transform.parent.GetComponentsInChildren<Collider>(true))
                if (col != _proxy && !col.isTrigger &&
                    col.GetComponentInParent<FezDepthProxyFollower>() == null &&
                    !bodies.Contains(col)) bodies.Add(col);
        if (topSurfaceReference != null && !bodies.Contains(topSurfaceReference))
            bodies.Add(topSurfaceReference);
        // Standalone authored one-way colliders remain usable too.
        if (bodies.Count == 0 && _proxy != null) bodies.Add(_proxy);
        _bodies = bodies.ToArray();
        if (_proxy != null && !bodies.Contains(_proxy)) bodies.Add(_proxy);
        // Explicit top references need not enumerate every duplicate body collider (e.g. ProBuilder).
        if (autoIncludeParentCollider && transform.parent != null)
            foreach (var col in transform.parent.GetComponentsInChildren<Collider>(true))
                if (!col.isTrigger && !bodies.Contains(col)) bodies.Add(col);
        _all = bodies.ToArray();
        MaxWorldTopOffset = Mathf.Max(MaxWorldTopOffset, Mathf.Abs(worldTopYOffset));
        for (int i = 0; i < _all.Length; i++)
        {
            var col = _all[i];
            if (col == null) continue;
            if (!AllColliderOwners.TryGetValue(col, out var owners))
            {
                owners = new List<FezOneWayPlatform>(1);
                AllColliderOwners.Add(col, owners);
            }
            if (!owners.Contains(this)) owners.Add(this);
        }
        for (int i = 0; i < _bodies.Length; i++)
        {
            var body = _bodies[i];
            if (body == null) continue;
            if (!BodyOwners.TryGetValue(body, out var owners))
            {
                owners = new List<FezOneWayPlatform>(1);
                BodyOwners.Add(body, owners);
            }
            if (!owners.Contains(this)) owners.Add(this);
        }
        foreach (var col in _all)
        {
            if (col == null) continue;
            var relay = col.GetComponent<FezPlatformCollisionLifecycle>();
            if (relay == null) relay = col.gameObject.AddComponent<FezPlatformCollisionLifecycle>();
            relay.Register(this);
        }
    }

    private void OnEnable()
    {
        if (!Registered.Contains(this)) Registered.Add(this);
        _registrationOrder = _nextRegistrationOrder++;
        NotifyCollisionStateChanged();
    }

    private void OnDisable()
    {
        Registered.Remove(this);
        DirtyPlatforms.Remove(this);
    }

    private void OnDestroy()
    {
        if (_bodies != null)
        foreach (var body in _bodies)
        {
            if (body == null || !BodyOwners.TryGetValue(body, out var owners)) continue;
            owners.Remove(this);
            if (owners.Count == 0) BodyOwners.Remove(body);
        }
        if (_all != null)
        foreach (var col in _all)
        {
            if (col == null || !AllColliderOwners.TryGetValue(col, out var owners)) continue;
            owners.Remove(this);
            if (owners.Count == 0) AllColliderOwners.Remove(col);
        }
        if (_all == null) return;
        foreach (var col in _all)
        {
            if (col == null) continue;
            var relay = col.GetComponent<FezPlatformCollisionLifecycle>();
            if (relay != null) relay.Unregister(this);
        }
    }

    // Call after changing Collider.enabled or Rigidbody collision participation directly.
    // GameObject activation is reported automatically by the lifecycle relay.
    public void NotifyCollisionStateChanged()
    {
        if (isActiveAndEnabled) DirtyPlatforms.Add(this);
    }

    // A disabled/recreated CharacterController can lose its native ignore-pair state.
    public static void InvalidatePlayerCollisionState(CharacterController player)
    {
        if (player != null) PreparedPlayers.Remove(player);
    }

    public static void PreparePlayer(CharacterController player)
    {
        if (player == null) return;
        if (!player.enabled || !player.gameObject.activeInHierarchy)
        {
            PreparedPlayers.Remove(player);
            return;
        }
        if (!Ignored.TryGetValue(player, out var set))
        {
            set = new HashSet<Collider>();
            Ignored.Add(player, set);
        }
        bool initializedNow = PreparedPlayers.Add(player);
        if (initializedNow)
        {
            foreach (var platform in Registered) PreparePlatform(player, set, platform);
        }
        // Steady state: no iteration over platforms or collider pairs.
        if (DirtyPlatforms.Count == 0) return;
        foreach (var entry in Ignored)
        {
            var otherPlayer = entry.Key;
            if (initializedNow && otherPlayer == player) continue;
            if (otherPlayer == null || !otherPlayer.enabled || !otherPlayer.gameObject.activeInHierarchy)
            {
                PreparedPlayers.Remove(otherPlayer);
                continue;
            }
            foreach (var platform in DirtyPlatforms) PreparePlatform(otherPlayer, entry.Value, platform);
        }
        DirtyPlatforms.Clear();
    }

    private static void PreparePlatform(CharacterController player, HashSet<Collider> set,
        FezOneWayPlatform platform)
    {
        if (platform == null || !platform.isActiveAndEnabled || platform._all == null) return;
        foreach (var col in platform._all)
        {
            if (col == null || col == player || !col.enabled || !col.gameObject.activeInHierarchy) continue;
            // A shared authoring parent must never turn a solid routing wall into a one-way body.
            if (col.GetComponentInParent<FezBackgroundWall>() != null) continue;
            // Only the moving depth proxy is query geometry. The authored block remains solid.
            if (col.GetComponentInParent<FezDepthProxyFollower>() == null ||
                System.Array.IndexOf(platform._bodies, col) >= 0)
            {
                if (set.Remove(col)) Physics.IgnoreCollision(player, col, false);
                continue;
            }
            // Query only on registration/invalidation. Preserve ignores owned by other systems.
            if (Physics.GetIgnoreCollision(player, col)) continue;
            Physics.IgnoreCollision(player, col, true);
            set.Add(col);
        }
    }

    public static void ReleasePlayer(CharacterController player)
    {
        PreparedPlayers.Remove(player);
        if (player == null || !Ignored.TryGetValue(player, out var set)) return;
        foreach (var col in set)
            if (col != null) Physics.IgnoreCollision(player, col, false);
        Ignored.Remove(player);
    }

    public static bool FindFloor(CharacterController player, Vector3 startFeet, Vector3 endFeet,
        Vector3 depthAxis, float tolerance, out SupportHit best, float ignoredTop = float.PositiveInfinity,
        bool characterMostlyVisible = true, float lateralMargin = 0f, bool preserveDepth = false,
        System.Predicate<SupportHit> reachable = null)
    {
        best = default;
        float top = float.NegativeInfinity;
        if (FindProjectedCandidates(endFeet, depthAxis, endFeet.y - tolerance,
                startFeet.y + tolerance, lateralMargin))
        {
            for (int i = 0; i < CandidatePlatforms.Count; i++)
                ConsiderProjectedFloor(player, CandidatePlatforms[i], startFeet, endFeet,
                    depthAxis, tolerance, ignoredTop, characterMostlyVisible, lateralMargin, preserveDepth,
                    ref top, ref best, reachable);
        }
        else
        {
            // A saturated or empty broad-phase result is not trusted; retain the original
            // complete search as a correctness fallback for unusual geometry/world scales.
            foreach (var platform in Registered)
                ConsiderProjectedFloor(player, platform, startFeet, endFeet, depthAxis,
                    tolerance, ignoredTop, characterMostlyVisible, lateralMargin, preserveDepth,
                    ref top, ref best, reachable);
        }
        return best.platform != null;
    }

    private static void ConsiderProjectedFloor(CharacterController player, FezOneWayPlatform platform,
        Vector3 startFeet, Vector3 endFeet, Vector3 depthAxis, float tolerance, float ignoredTop,
        bool characterMostlyVisible, float lateralMargin, bool preserveDepth, ref float top, ref SupportHit best,
        System.Predicate<SupportHit> reachable)
    {
        if (platform == null || !platform.isActiveAndEnabled ||
            !platform.TrySample(endFeet, depthAxis, out var hit,
                endFeet.y - tolerance, startFeet.y + tolerance, lateralMargin, preserveDepth)) return;
        if (hit.point.y > startFeet.y + tolerance || hit.point.y < endFeet.y - tolerance) return;
        if (hit.point.y >= ignoredTop - tolerance || hit.point.y < top) return;
        float requiredDepthShift = Vector3.Dot(hit.point - endFeet, depthAxis);
        if (!characterMostlyVisible && Mathf.Abs(requiredDepthShift) > tolerance) return;
        if (!FezPlatformViewOcclusion.IsFootVisible(player.transform, hit.point,
                depthAxis, platform._all)) return;
        // A blocked best-looking floor must not suppress another reachable landing.
        if (reachable != null && !reachable(hit)) return;
        if (Mathf.Abs(hit.point.y - top) < 0.0001f && best.platform != null)
        {
            float candidateDepth = preserveDepth ? Mathf.Abs(requiredDepthShift) : Vector3.Dot(hit.point, depthAxis);
            float previousDepth = preserveDepth
                ? Mathf.Abs(Vector3.Dot(best.point - endFeet, depthAxis)) : Vector3.Dot(best.point, depthAxis);
            if (candidateDepth >= previousDepth) return;
        }
        top = hit.point.y;
        best = hit;
    }

    /// <summary>
    /// Finds real platform geometry directly below the player's current world-space foot point.
    /// This is the hidden-world support path: it does not require camera visibility and never
    /// projects the player along the depth axis onto a different plane.
    /// </summary>
    public static bool FindFloorAtCurrentDepth(Vector3 startFeet, Vector3 endFeet,
        float tolerance, out SupportHit best, float ignoredTop = float.PositiveInfinity)
    {
        return FindFloorAtCurrentDepth(startFeet, endFeet, tolerance, out best,
            Vector3.forward, 0f, ignoredTop);
    }

    public static bool FindFloorAtCurrentDepth(Vector3 startFeet, Vector3 endFeet,
        float tolerance, out SupportHit best, Vector3 depthAxis, float lateralMargin = 0f,
        float ignoredTop = float.PositiveInfinity)
    {
        best = default;
        float top = float.NegativeInfinity;
        if (FindAnchoredCandidates(startFeet, endFeet, tolerance, depthAxis, lateralMargin))
        {
            for (int i = 0; i < CandidatePlatforms.Count; i++)
                ConsiderAnchoredFloor(CandidatePlatforms[i], startFeet, endFeet,
                    tolerance, depthAxis, lateralMargin, ignoredTop, ref top, ref best);
        }
        else
        {
            foreach (var platform in Registered)
                ConsiderAnchoredFloor(platform, startFeet, endFeet, tolerance,
                    depthAxis, lateralMargin, ignoredTop, ref top, ref best);
        }
        return best.platform != null;
    }

    private static void ConsiderAnchoredFloor(FezOneWayPlatform platform, Vector3 startFeet,
        Vector3 endFeet, float tolerance, Vector3 depthAxis, float lateralMargin,
        float ignoredTop, ref float top, ref SupportHit best)
    {
        if (platform == null || !platform.isActiveAndEnabled ||
            !platform.TryGetAnchoredFloor(startFeet, endFeet, tolerance, out var hit,
                depthAxis, lateralMargin)) return;
        if (hit.point.y >= ignoredTop - tolerance || hit.point.y < top) return;
        top = hit.point.y;
        best = hit;
    }

    private static bool FindProjectedCandidates(Vector3 foot, Vector3 axis, float minY, float maxY,
        float lateralMargin)
    {
        axis = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.forward;
        return QueryCandidates(foot, axis, minY - MaxWorldTopOffset,
            maxY + MaxWorldTopOffset, CandidateHalfWidth + lateralMargin, CandidateDepthReach);
    }

    private static bool FindAnchoredCandidates(Vector3 startFeet, Vector3 endFeet,
        float tolerance, Vector3 depthAxis, float lateralMargin)
    {
        float minY = endFeet.y - tolerance - MaxWorldTopOffset;
        float maxY = startFeet.y + tolerance + MaxWorldTopOffset;
        Vector3 center = new Vector3(endFeet.x, (minY + maxY) * 0.5f, endFeet.z);
        float halfHeight = Mathf.Max(CandidateHalfWidth, (maxY - minY) * 0.5f + CandidateHalfWidth);
        return QueryCandidates(center, depthAxis, center.y - halfHeight,
            center.y + halfHeight, CandidateHalfWidth + lateralMargin, CandidateHalfWidth);
    }

    private static bool QueryCandidates(Vector3 center, Vector3 depthAxis,
        float minY, float maxY, float halfWidth, float halfDepth)
    {
        CandidateSet.Clear();
        CandidatePlatforms.Clear();
        depthAxis.y = 0f;
        depthAxis = depthAxis.sqrMagnitude > 0.0001f ? depthAxis.normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, depthAxis).normalized;
        float halfHeight = Mathf.Max(CandidateHalfWidth, (maxY - minY) * 0.5f + CandidateHalfWidth);
        center.y = (minY + maxY) * 0.5f;
        Quaternion orientation = Quaternion.LookRotation(depthAxis, Vector3.up);
        int count;
        while (true)
        {
            count = Physics.OverlapBoxNonAlloc(center, new Vector3(halfWidth,
                    halfHeight, halfDepth), CandidateColliders, orientation,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            if (count < CandidateColliders.Length) break;
            if (CandidateColliders.Length >= 16384) return false;
            System.Array.Resize(ref CandidateColliders, CandidateColliders.Length * 2);
        }
        for (int i = 0; i < count; i++)
        {
            var collider = CandidateColliders[i];
            if (collider == null || !BodyOwners.TryGetValue(collider, out var owners)) continue;
            for (int j = 0; j < owners.Count; j++)
            {
                var owner = owners[j];
                if (owner != null && owner.isActiveAndEnabled && CandidateSet.Add(owner))
                    CandidatePlatforms.Add(owner);
            }
        }
        CandidatePlatforms.Sort(CompareRegistrationOrder);
        return CandidatePlatforms.Count > 0;
    }

    private static int CompareRegistrationOrder(FezOneWayPlatform left, FezOneWayPlatform right)
    {
        return left._registrationOrder.CompareTo(right._registrationOrder);
    }

    /// <summary>
    /// Finds the highest visible platform below the player's projected foot point that lies
    /// toward the view foreground. Used to choose one stable depth target during an unlocked jump.
    /// </summary>
    public static bool FindVisibleForegroundFloorBelow(CharacterController player, Vector3 feet,
        Vector3 depthAxis, float tolerance, out SupportHit best, float lateralMargin = 0f)
    {
        best = default;
        if (player == null) return false;
        depthAxis = depthAxis.sqrMagnitude > 0.0001f ? depthAxis.normalized : Vector3.forward;
        float bestTop = float.NegativeInfinity;
        float currentDepth = Vector3.Dot(feet, depthAxis);
        float bestDepth = float.PositiveInfinity;
        foreach (var platform in Registered)
        {
            if (platform == null || !platform.isActiveAndEnabled ||
                !platform.TrySample(feet, depthAxis, out var hit,
                    float.NegativeInfinity, feet.y + tolerance, lateralMargin)) continue;
            if (hit.point.y > feet.y + tolerance ||
                Vector3.Dot(hit.point, depthAxis) >= currentDepth - tolerance) continue;
            if (!FezPlatformViewOcclusion.IsFootVisible(player.transform, hit.point,
                    depthAxis, platform._all)) continue;

            float hitDepth = Vector3.Dot(hit.point, depthAxis);
            if (hit.point.y < bestTop - tolerance) continue;
            if (Mathf.Abs(hit.point.y - bestTop) <= tolerance && hitDepth >= bestDepth) continue;
            bestTop = hit.point.y;
            bestDepth = hitDepth;
            best = hit;
        }
        return best.platform != null;
    }

    public bool ContainsFoot(Vector3 foot, Vector3 depthAxis) => TrySample(foot, depthAxis, out _);

    public bool TryGetCenteredLanding(Vector3 startFeet, Vector3 endFeet, float tolerance,
        out SupportHit result, Vector3 depthAxis, float lateralMargin = 0f)
    {
        result = default;
        return isActiveAndEnabled && TrySample(endFeet, depthAxis, out result,
                endFeet.y - tolerance, startFeet.y + tolerance, lateralMargin, preserveDepth: false) &&
            result.point.y <= startFeet.y + tolerance && result.point.y >= endFeet.y - tolerance;
    }

    // Retain an already acquired 2D foothold after a wall route. This samples real geometry
    // for height and moving-platform anchoring without moving the character back in depth.
    public bool TryGetProjectedSupport(Vector3 startFeet, Vector3 endFeet, float tolerance,
        out SupportHit result, Vector3 depthAxis, float lateralMargin = 0f)
    {
        result = default;
        return isActiveAndEnabled && TrySample(endFeet, depthAxis, out result,
                endFeet.y - tolerance, startFeet.y + tolerance, lateralMargin, preserveDepth: true) &&
            result.point.y <= startFeet.y + tolerance && result.point.y >= endFeet.y - tolerance;
    }

    /// <summary>Keep an acquired foothold even if the new view hides it. No projected reacquisition.</summary>
    public bool TryGetAnchoredFloor(Vector3 startFeet, Vector3 endFeet, float tolerance,
        out SupportHit result)
    {
        return TryGetAnchoredFloor(startFeet, endFeet, tolerance, out result,
            Vector3.forward, 0f);
    }

    public bool TryGetAnchoredFloor(Vector3 startFeet, Vector3 endFeet, float tolerance,
        out SupportHit result, Vector3 depthAxis, float lateralMargin = 0f)
    {
        result = default;
        if (!isActiveAndEnabled || _bodies == null) return false;
        depthAxis = depthAxis.sqrMagnitude > 0.0001f ? depthAxis.normalized : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, depthAxis).normalized;
        float top = float.NegativeInfinity;
        foreach (var body in _bodies)
        {
            if (body == null || !body.enabled || !body.gameObject.activeInHierarchy || body.isTrigger) continue;
            Bounds b = body.bounds;
            float side = Vector3.Dot(endFeet - b.center, right);
            float half = Mathf.Abs(right.x) * b.extents.x + Mathf.Abs(right.z) * b.extents.z;
            if (Mathf.Abs(side) > half + lateralMargin) continue;
            float sampleHalf = Mathf.Max(0f, half - 0.005f);
            float clampedSide = Mathf.Clamp(side, -sampleHalf, sampleHalf);
            Vector3 samplePoint = endFeet + right * (clampedSide - side);
            Vector3 origin = new Vector3(samplePoint.x, b.max.y + 0.1f, samplePoint.z);
            if (!body.Raycast(new Ray(origin, Vector3.down), out var hit, b.size.y + 0.2f)) continue;
            float y = hit.point.y + worldTopYOffset;
            if (hit.normal.y < 0.7f || y > startFeet.y + tolerance || y < endFeet.y - tolerance || y < top) continue;
            top = y;
            result = new SupportHit { platform = this, collider = body, point = hit.point + Vector3.up * worldTopYOffset };
        }
        return result.collider != null;
    }

    private bool TrySample(Vector3 foot, Vector3 axis, out SupportHit result,
        float minY = float.NegativeInfinity, float maxY = float.PositiveInfinity,
        float lateralMargin = 0f, bool preserveDepth = false)
    {
        result = default;
        if (_bodies == null) return false;
        float top = float.NegativeInfinity;
        Vector3 right = Vector3.Cross(Vector3.up, axis);
        foreach (var body in _bodies)
        {
            if (body == null || !body.enabled || !body.gameObject.activeInHierarchy || body.isTrigger) continue;
            Bounds bounds = body.bounds;
            if (bounds.max.y + worldTopYOffset < minY || bounds.min.y + worldTopYOffset > maxY) continue;
            float side = Vector3.Dot(foot - bounds.center, right);
            float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
            if (Mathf.Abs(side) > half + lateralMargin + 0.0001f) continue;
            // Sample inside the top rather than exactly on a shared block/wall seam.
            // Mesh raycasts and visibility rays are ambiguous on coincident edges.
            float sampleHalf = Mathf.Max(0f, half - 0.005f);
            float clampedSide = Mathf.Clamp(side, -sampleHalf, sampleHalf);
            Vector3 samplePoint = foot + right * (clampedSide - side);
            float halfDepth = Mathf.Abs(axis.x) * bounds.extents.x + Mathf.Abs(axis.z) * bounds.extents.z;
            float depth = Vector3.Dot(samplePoint - bounds.center, axis);
            float sampleDepth = preserveDepth
                ? Mathf.Clamp(depth, -Mathf.Max(0f, halfDepth - 0.005f), Mathf.Max(0f, halfDepth - 0.005f))
                : 0f;
            Vector3 origin = samplePoint + axis * (sampleDepth - depth);
            origin.y = bounds.max.y + 0.1f;
            if (!body.Raycast(new Ray(origin, Vector3.down), out var hit, bounds.size.y + 0.2f)) continue;
            if (hit.normal.y < 0.7f || hit.point.y + worldTopYOffset < top) continue;
            top = hit.point.y + worldTopYOffset;
            result = new SupportHit { platform = this, collider = body,
                point = hit.point + Vector3.up * worldTopYOffset };
        }
        return result.collider != null;
    }

    public static bool TryResolveByCollider(Collider col, out FezOneWayPlatform platform)
    {
        if (col != null && AllColliderOwners.TryGetValue(col, out var owners))
            for (int i = 0; i < owners.Count; i++)
                if (owners[i] != null && owners[i].isActiveAndEnabled)
                { platform = owners[i]; return true; }
        platform = null;
        return false;
    }
    public Collider[] GetAllPlatformColliders() => _all;
    public IEnumerable<Collider> GetSolidBodyColliders()
    {
        if (_all == null) yield break;
        foreach (var body in _all)
            if (body != null && !body.isTrigger &&
                body.GetComponentInParent<FezDepthProxyFollower>() == null &&
                body.GetComponentInParent<FezBackgroundWall>() == null) yield return body;
    }
    // Compatibility for older callers: no global enable/disable cycle.
    public static void BeginPlayerMove(CharacterController player, float velocityY) => PreparePlayer(player);
    public static void EndPlayerMove() { }
}
