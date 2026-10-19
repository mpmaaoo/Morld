using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)]
public sealed class FezPlayerDepthSnapper : MonoBehaviour
{
    [Header("深度軸來源")]
    [SerializeField] private FezDepthAxisSource depthAxisSource = FezDepthAxisSource.WorldViewState;
    [SerializeField] private Transform explicitAxisTransform;
    [SerializeField] private Camera explicitCamera;

    [Header("踩踏偵測")]
    [SerializeField] private float groundProbeDistance = 0.15f;
    [SerializeField] private float groundProbeRadius = 0.25f;
    [SerializeField] private LayerMask depthProxyMask = ~0;
    [SerializeField] private bool includeTriggerDepthProxy = true;
    [SerializeField] private string requiredDepthProxyTag = "DepthProxy";

    [Header("對齊設定")]
    [SerializeField] private float snapSpeed = 0f;
    [SerializeField] private float snapEpsilon = 0.001f;
    [SerializeField] private float maxSnapDepthStep = 1.5f;
    [SerializeField] private bool resolveShallowOnlyWhenOverlapping = true;
    [Tooltip("停用時不會因玩家卡入牆／平台而自動沿深度軸推到淺側。")]
    [SerializeField] private bool allowAutomaticOverlapShallowCorrection;
    [SerializeField] private LayerMask overlapResolveMask = ~0;
    [SerializeField] private float shallowOverlapBias = 0.05f;

    [Header("遮擋停用（旋轉後）")]
    [SerializeField] private FezRotationOcclusionGate occlusionGate;

    private CharacterController _cc;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        if (occlusionGate == null)
            occlusionGate = GetComponent<FezRotationOcclusionGate>();
    }

    private void LateUpdate()
    {
        if (TryGetComponent<PlayerController>(out var motor) && motor.isActiveAndEnabled) return;
        if (_cc == null)
            return;

        // 下穿中：禁止吸回平台本體深度，也不做穿模淺解算
        if (PlayerController.IsDropThroughActiveStatic)
        {
            // 下穿時也不要讓 DepthProxy 跟到玩家腳下，否則會再被當成承載面
            return;
        }

        var viewForward = FezDepthAxis.GetViewForward(depthAxisSource, explicitAxisTransform, explicitCamera);

        if (_cc.isGrounded && !IsOcclusionBlocked())
        {
            var playerDepth = FezDepthAxis.DepthOf(transform.position, viewForward);
            if (TryGetShallowestPlatformDepthUnderFeet(viewForward, out var bestDepth))
            {
                var deltaDepth = Mathf.Clamp(bestDepth - playerDepth, -Mathf.Max(0.01f, maxSnapDepthStep), Mathf.Max(0.01f, maxSnapDepthStep));
                if (Mathf.Abs(deltaDepth) >= snapEpsilon)
                {
                    var desiredMove = viewForward * deltaDepth;
                    if (snapSpeed <= 0f)
                        _cc.Move(desiredMove);
                    else
                        _cc.Move(desiredMove * (1f - Mathf.Exp(-snapSpeed * Time.deltaTime)));
                }
            }
        }

        FezDepthProxyFollower.SyncAllAfterPlayerDepthSnap(Time.deltaTime);

        if (allowAutomaticOverlapShallowCorrection
            && resolveShallowOnlyWhenOverlapping
            && !IsOcclusionBlocked())
            ResolveOverlapShallow(viewForward);
    }

    private bool IsOcclusionBlocked() =>
        occlusionGate != null && occlusionGate.IsPlayerDepthMovementBlocked;

    internal void CollectFootPlatformSurfaces(List<FezDepthProxySurface> results)
    {
        results.Clear();
        if (_cc == null)
            return;

        var b = _cc.bounds;
        var foot = new Vector3(b.center.x, b.min.y + 0.02f, b.center.z);
        var probeCenter = foot + Vector3.down * Mathf.Max(0.01f, groundProbeDistance);

        var overlaps = Physics.OverlapSphere(
            probeCenter,
            Mathf.Max(0.01f, groundProbeRadius),
            depthProxyMask,
            includeTriggerDepthProxy ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);

        for (var i = 0; i < overlaps.Length; i++)
        {
            var col = overlaps[i];
            if (col == null || !IsDepthProxyCollider(col))
                continue;
            var surface = col.GetComponentInParent<FezDepthProxySurface>();
            if (surface == null || results.Contains(surface))
                continue;
            results.Add(surface);
        }
    }

    internal bool TryGetShallowestPlatformDepthUnderFeet(Vector3 viewForward, out float platformDepth)
    {
        platformDepth = float.PositiveInfinity;
        var found = false;

        var b = _cc.bounds;
        var foot = new Vector3(b.center.x, b.min.y + 0.02f, b.center.z);
        var probeCenter = foot + Vector3.down * Mathf.Max(0.01f, groundProbeDistance);

        var overlaps = Physics.OverlapSphere(
            probeCenter,
            Mathf.Max(0.01f, groundProbeRadius),
            depthProxyMask,
            includeTriggerDepthProxy ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);

        for (var i = 0; i < overlaps.Length; i++)
        {
            var col = overlaps[i];
            if (col == null || !IsDepthProxyCollider(col))
                continue;
            var surface = col.GetComponentInParent<FezDepthProxySurface>();
            if (surface == null)
                continue;

            var depth = FezDepthAxis.DepthOf(surface.GetPlatformDepthWorldPos(), viewForward);
            if (!found || depth < platformDepth)
            {
                platformDepth = depth;
                found = true;
            }
        }

        return found;
    }

    private bool TryGetOverlapShallowDepth(Vector3 viewForward, out float shallowDepth)
    {
        shallowDepth = float.PositiveInfinity;
        var foundOverlap = false;

        var b = _cc.bounds;
        var overlaps = Physics.OverlapBox(
            b.center, b.extents * 0.98f, Quaternion.identity, overlapResolveMask, QueryTriggerInteraction.Collide);

        for (var i = 0; i < overlaps.Length; i++)
        {
            var col = overlaps[i];
            if (col == null || col == _cc || !IsDepthProxyCollider(col))
                continue;

            var surface = col.GetComponentInParent<FezDepthProxySurface>();
            if (surface == null)
                continue;

            Vector3 dir;
            float distance;
            var penetrating = Physics.ComputePenetration(
                _cc, _cc.transform.position, _cc.transform.rotation,
                col, col.transform.position, col.transform.rotation,
                out dir, out distance);
            if (!penetrating || distance <= 0.0001f)
                continue;

            var surfaceDepth = FezDepthAxis.DepthOf(surface.GetPlatformDepthWorldPos(), viewForward);
            var proxyShallow = GetBoundsShallowDepth(col.bounds, viewForward);
            var colShallow = Mathf.Min(surfaceDepth, proxyShallow) - Mathf.Max(0f, shallowOverlapBias);
            if (colShallow < shallowDepth)
                shallowDepth = colShallow;
            foundOverlap = true;
        }

        return foundOverlap;
    }

    private void ResolveOverlapShallow(Vector3 viewForward)
    {
        if (!TryGetOverlapShallowDepth(viewForward, out var overlapShallowDepth))
            return;

        var playerDepth = FezDepthAxis.DepthOf(transform.position, viewForward);
        var deltaDepth = overlapShallowDepth - playerDepth;
        if (Mathf.Abs(deltaDepth) < snapEpsilon)
            return;

        deltaDepth = Mathf.Clamp(deltaDepth, -Mathf.Max(0.01f, maxSnapDepthStep), Mathf.Max(0.01f, maxSnapDepthStep));
        _cc.Move(viewForward * deltaDepth);
    }

    private bool IsDepthProxyCollider(Collider col)
    {
        if ((depthProxyMask.value & (1 << col.gameObject.layer)) == 0)
            return false;
        if (!string.IsNullOrEmpty(requiredDepthProxyTag) && col.tag != requiredDepthProxyTag)
            return false;
        return true;
    }

    private static float GetBoundsShallowDepth(Bounds b, Vector3 vf)
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
}
