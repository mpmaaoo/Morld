using UnityEngine;

/// <summary>
/// 掛在 DepthProxy（或平台）上：從相機對「本體中心」「頂面參考點」做視線檢查。
/// 若兩點皆被其他遮擋物（非本平台）擋住，<see cref="IsViewOccluded"/> 為 true，
/// 供 <see cref="FezDepthProxyFollower"/> 停止跟隨玩家深度、只留在本體中心。
/// </summary>
[DisallowMultipleComponent]
public sealed class FezPlatformViewOcclusion : MonoBehaviour
{
    private static RaycastHit[] _footHits = new RaycastHit[32];

    /// <summary>
    /// Returns true only when at least the requested fraction of the player's silhouette is
    /// visible from the committed view. Sampling is kept in the view plane so depth alignment
    /// cannot make a mostly hidden player pop onto a foreground platform.
    /// </summary>
    public static bool IsCharacterMostlyVisible(CharacterController controller, Vector3 axis,
        float requiredVisibleFraction = 0.6f)
    {
        if (controller == null || !controller.enabled) return false;
        axis = axis.sqrMagnitude > 0.0001f ? axis.normalized : Vector3.forward;
        Bounds bounds = controller.bounds;
        Vector3 right = Vector3.Cross(Vector3.up, axis).normalized;
        if (right.sqrMagnitude < 0.5f) right = Vector3.right;

        const int columns = 5;
        const int rows = 6;
        int visible = 0;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            float x = Mathf.Lerp(-0.82f, 0.82f, (column + 0.5f) / columns);
            float y = Mathf.Lerp(-0.88f, 0.88f, (row + 0.5f) / rows);
            Vector3 point = bounds.center + right * (bounds.extents.x * x)
                + Vector3.up * (bounds.extents.y * y);
            if (!IsPointOccludedFromView(controller.transform, point, axis)) visible++;
        }
        return visible / (float)(columns * rows) >= Mathf.Clamp01(requiredVisibleFraction);
    }

    private static bool IsPointOccludedFromView(Transform player, Vector3 point, Vector3 axis)
    {
        const float distance = 2000f;
        Vector3 origin = point - axis * distance;
        int count;
        while (true)
        {
            count = Physics.RaycastNonAlloc(origin, axis, _footHits, distance,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            if (count < _footHits.Length) break;
            if (_footHits.Length >= 4096) return true;
            System.Array.Resize(ref _footHits, _footHits.Length * 2);
        }
        for (int i = 0; i < count; i++)
        {
            var col = _footHits[i].collider;
            if (col == null || col.transform == player || col.transform.IsChildOf(player)) continue;
            if (IsNonVisualMoveCollider(col) || col.GetComponentInParent<PlayerController>() != null) continue;
            if (col.GetComponentInParent<FezPlatformDepthSnapSuppressZone>() != null ||
                col.GetComponentInParent<FezPlayerDepthSnapSuppressZone>() != null) continue;
            // Trigger volumes describe interaction, not visible geometry.
            if (col.isTrigger && col.GetComponent<Renderer>() == null &&
                col.GetComponentInParent<Renderer>() == null) continue;
            if (_footHits[i].distance < distance - 0.025f) return true;
        }
        return false;
    }

    /// <summary>Visibility at the actual foot segment in the committed orthographic view.</summary>
    public static bool IsFootVisible(Transform player, Vector3 point, Vector3 axis, Collider[] own)
    {
        // Just inside the top edge: a foreground block at the same height hides the rear block.
        point.y -= 0.005f;
        const float distance = 2000f;
        Vector3 origin = point - axis * distance;
        int count;
        while (true)
        {
            count = Physics.RaycastNonAlloc(origin, axis, _footHits, distance,
                Physics.AllLayers, QueryTriggerInteraction.Collide);
            if (count < _footHits.Length) break;
            if (_footHits.Length >= 4096) return false;
            System.Array.Resize(ref _footHits, _footHits.Length * 2);
        }
        // Visibility ends at the candidate's camera-facing surface, not at its depth center.
        // Coincident platform copies otherwise occlude each other and both reject landing.
        float surfaceDistance = distance;
        bool foundSurface = false;
        for (int i = 0; i < count; i++)
        {
            var col = _footHits[i].collider;
            if (col == null || IsNonVisualMoveCollider(col)) continue;
            for (int j = 0; j < own.Length; j++)
                if (own[j] == col)
                {
                    surfaceDistance = Mathf.Min(surfaceDistance, _footHits[i].distance);
                    foundSurface = true;
                    break;
                }
        }
        float occluderLimit = surfaceDistance - (foundSurface ? 0.001f : 0.025f);
        for (int i = 0; i < count; i++)
        {
            var col = _footHits[i].collider;
            if (col == null || col.transform.IsChildOf(player) || col.transform == player) continue;
            bool isOwn = false;
            for (int j = 0; j < own.Length; j++)
                if (own[j] == col) { isOwn = true; break; }
            if (isOwn || IsNonVisualMoveCollider(col)) continue;
            if (col.GetComponentInParent<PlayerController>() != null) continue;
            if (col.GetComponentInParent<FezPlatformDepthSnapSuppressZone>() != null ||
                col.GetComponentInParent<FezPlayerDepthSnapSuppressZone>() != null) continue;
            // Match the silhouette query: invisible interaction volumes cannot hide a floor.
            if (col.isTrigger && col.GetComponent<Renderer>() == null &&
                col.GetComponentInParent<Renderer>() == null) continue;
            // Equal-depth surfaces remain visible. A genuinely nearer wall or block still hides
            // the candidate, including overlapping geometry whose front face is nearer.
            if (_footHits[i].distance >= occluderLimit) continue;
            return false;
        }
        return true;
    }
    [Header("遮擋來源")]
    [Tooltip("會擋住視線的 Layer（通常與玩家 FezRotationOcclusionGate 相同：平台本體等）。")]
    [SerializeField] private LayerMask occluderMask;
    [SerializeField] private bool includeTriggerOccluders = true;
    [SerializeField] private float raycastDistancePadding = 0.02f;

    [Header("採樣點")]
    [Tooltip("本體深度參考；留空則用同物件或父層的 FezDepthProxySurface。")]
    [SerializeField] private FezDepthProxySurface bodySurface;
    [Tooltip("頂面參考點；留空則用本物件 Collider 的 bounds.center。")]
    [SerializeField] private Collider topSurfaceCollider;

    [Header("平台歸屬")]
    [Tooltip("判定「自己的碰撞器」用；留空則為 FezDepthProxySurface 所在物件，否則為本物件父層。")]
    [SerializeField] private Transform platformRoot;

    [Header("相機")]
    [SerializeField] private Camera explicitCamera;

    [Header("行為")]
    [Tooltip("關閉時不檢查，IsViewOccluded 恆為 false。")]
    [SerializeField] private bool occlusionCheckEnabled = true;

    private bool _isViewOccluded;

    /// <summary>本體與頂面採樣點皆被非本平台遮擋物擋住視線時為 true。</summary>
    public bool IsViewOccluded => occlusionCheckEnabled && _isViewOccluded;

    private void Awake()
    {
        if (bodySurface == null)
            bodySurface = GetComponent<FezDepthProxySurface>();
        if (bodySurface == null)
            bodySurface = GetComponentInParent<FezDepthProxySurface>();
        if (topSurfaceCollider == null)
            topSurfaceCollider = GetComponent<Collider>();
        if (platformRoot == null)
            platformRoot = bodySurface != null ? bodySurface.transform : transform.parent;
        if (platformRoot == null)
            platformRoot = transform;
    }

    /// <summary>立即刷新（<see cref="FezDepthProxyFollower"/> 每幀 Tick 前會呼叫）。</summary>
    public void RefreshNow()
    {
        if (!occlusionCheckEnabled)
        {
            _isViewOccluded = false;
            return;
        }

        var cam = explicitCamera != null ? explicitCamera : Camera.main;
        if (cam == null)
        {
            _isViewOccluded = false;
            return;
        }

        var bodyPoint = GetBodySampleWorldPos();
        var topPoint = GetTopSampleWorldPos();
        var camPos = cam.transform.position;

        _isViewOccluded = IsPointOccludedFromCamera(camPos, bodyPoint)
            && IsPointOccludedFromCamera(camPos, topPoint);
    }

    private Vector3 GetBodySampleWorldPos()
    {
        if (bodySurface != null)
            return bodySurface.GetPlatformDepthWorldPos();
        if (platformRoot != null)
            return platformRoot.position;
        return transform.position;
    }

    private Vector3 GetTopSampleWorldPos()
    {
        if (topSurfaceCollider != null)
            return topSurfaceCollider.bounds.center;
        return transform.position;
    }

    private bool IsPointOccludedFromCamera(Vector3 cameraPos, Vector3 worldPoint)
    {
        var dir = worldPoint - cameraPos;
        var len = dir.magnitude;
        if (len <= 0.0001f)
            return false;

        dir /= len;
        var triggerMode = includeTriggerOccluders
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;

        var hits = Physics.RaycastAll(
            cameraPos,
            dir,
            len + raycastDistancePadding,
            occluderMask,
            triggerMode
        );

        if (hits == null || hits.Length == 0)
            return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        for (var i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h.distance >= len - 0.001f)
                break;

            if (h.collider != null && IsOwnPlatformCollider(h.collider))
                continue;

            // 跟隨玩家深度的隱形碰撞箱不算視線遮擋。
            if (IsNonVisualMoveCollider(h.collider))
                continue;

            return true;
        }

        return false;
    }

    private bool IsOwnPlatformCollider(Collider col)
    {
        if (col == null || platformRoot == null)
            return false;
        var t = col.transform;
        return t == platformRoot || t.IsChildOf(platformRoot);
    }

    /// <summary>
    /// 跟隨玩家深度的隱形碰撞箱：不應參與「是否看得到平台」的判定。
    /// </summary>
    private static bool IsNonVisualMoveCollider(Collider col)
    {
        if (col == null)
            return true;

        // A disabled visual with a live collider still participates in physics, but cannot hide a floor or silhouette.
        var visual = col.GetComponentInParent<Renderer>();
        if (visual != null && (!visual.enabled || visual.forceRenderingOff))
            return true;

        // 其他平台的 DepthProxy 同樣不可見；本平台已由 IsOwnPlatformCollider 略過
        if (col.GetComponentInParent<FezDepthProxyFollower>() != null)
            return true;

        return false;
    }
}

