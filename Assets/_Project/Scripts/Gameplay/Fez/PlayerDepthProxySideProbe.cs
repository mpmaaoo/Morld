using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 掛在玩家子物件（SideProbe）上的側向偵測器：
/// - 只要 SideProbe 觸發到 DepthProxy，就暫時忽略玩家與該 DepthProxy 的碰撞
/// - 離開條件後自動恢復碰撞
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class PlayerDepthProxySideProbe : MonoBehaviour
{
    [Header("玩家")]
    [SerializeField] private CharacterController playerController;

    [Header("DepthProxy 篩選")]
    [SerializeField] private LayerMask depthProxyMask = ~0;
    [SerializeField] private string requiredDepthProxyTag = "DepthProxy";
    [Tooltip("頂面須在腳底附近（公尺）且水平在腳下範圍內，才視為「承載面」不 Ignore；同高度但在側邊的 DepthProxy 仍會 Ignore。")]
    [SerializeField] private float feetSupportTopMargin = 0.12f;
    [Tooltip("與玩家中心水平距離小於此值才視為腳下承載（通常略大於 CharacterController 半徑）。")]
    [SerializeField] private float feetSupportHorizontalRadius = 0.45f;

    private BoxCollider _probe;
    private readonly HashSet<Collider> _ignored = new HashSet<Collider>();

    private void Awake()
    {
        _probe = GetComponent<BoxCollider>();
        if (playerController == null)
            playerController = GetComponentInParent<CharacterController>();
    }

    private void Update()
    {
        var motor = GetComponentInParent<PlayerController>();
        if (motor != null && motor.isActiveAndEnabled) return;
        if (playerController == null || _probe == null)
            return;

        var center = transform.TransformPoint(_probe.center);
        var halfExtents = Vector3.Scale(_probe.size * 0.5f, transform.lossyScale);
        var overlaps = Physics.OverlapBox(
            center,
            halfExtents,
            transform.rotation,
            depthProxyMask,
            QueryTriggerInteraction.Collide
        );

        var keep = new HashSet<Collider>();
        for (var i = 0; i < overlaps.Length; i++)
        {
            var col = overlaps[i];
            if (col == null || col == playerController)
                continue;
            if (!string.IsNullOrEmpty(requiredDepthProxyTag) && col.tag != requiredDepthProxyTag)
                continue;

            keep.Add(col);
            if (_ignored.Contains(col))
                continue;

            if (IsFootSupportDepthProxy(col))
                continue;

            Physics.IgnoreCollision(playerController, col, true);
            _ignored.Add(col);
        }

        if (_ignored.Count == 0)
            return;

        var stale = new List<Collider>();
        foreach (var col in _ignored)
        {
            if (!keep.Contains(col))
                stale.Add(col);
        }

        for (var i = 0; i < stale.Count; i++)
        {
            var col = stale[i];
            if (col != null)
                Physics.IgnoreCollision(playerController, col, false);
            _ignored.Remove(col);
        }
    }

    private void OnDisable()
    {
        RestoreAllIgnores();
    }

    private void RestoreAllIgnores()
    {
        if (playerController == null || _ignored.Count == 0)
            return;

        foreach (var col in _ignored)
        {
            if (col != null)
                Physics.IgnoreCollision(playerController, col, false);
        }
        _ignored.Clear();
    }

    private bool IsFootSupportDepthProxy(Collider col)
    {
        if (playerController == null || col == null)
            return false;

        // 僅保護「真的踩在腳下」的薄片；同高度的側向 DepthProxy 仍走 Ignore
        if (!playerController.isGrounded)
            return false;

        var feetY = playerController.bounds.min.y;
        if (col.bounds.max.y < feetY - Mathf.Max(0.02f, feetSupportTopMargin))
            return false;

        var playerCenter = playerController.bounds.center;
        var colCenter = col.bounds.center;
        var dx = colCenter.x - playerCenter.x;
        var dz = colCenter.z - playerCenter.z;
        var horizontalDist = Mathf.Sqrt(dx * dx + dz * dz);

        var radius = feetSupportHorizontalRadius > 0f
            ? feetSupportHorizontalRadius
            : Mathf.Max(0.35f, playerController.radius * 1.15f);

        return horizontalDist <= radius;
    }
}
