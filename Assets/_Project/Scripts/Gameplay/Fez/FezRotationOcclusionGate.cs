using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 視角旋轉後的遮擋閘門。
/// 內圈採樣點：深度 Snap / 穿模解算 / 遮擋主判（平台、牆都算遮擋）。
/// 外圈採樣點：僅在內圈未遮擋時，補充背景牆繞行可見性（忽略背景牆本體）。
/// 內圈優先：內圈一旦遮擋，外圈結果不採用。
/// </summary>
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
[DefaultExecutionOrder(190)]
public sealed class FezRotationOcclusionGate : MonoBehaviour
{
    private const int InitialRaycastBufferSize = 16;
    private const int MaximumRaycastBufferSize = 256;

    [Header("遮擋來源")]
    [Tooltip("停用『所有 Layer』時，僅使用這些 layer 當遮擋物。")]
    [SerializeField] private LayerMask occluderMask;
    [Tooltip("啟用時判定所有 Layer 的 Collider，確保移動物件改變位置後仍參與視線遮擋；玩家自身階層會排除。")]
    [SerializeField] private bool includeAllColliderLayers = true;
    [Tooltip("是否把 Trigger Collider 也算進遮擋（平台本體若是 isTrigger 需開啟）。")]
    [SerializeField] private bool includeTriggerOccluders = true;
    [Tooltip("Raycast 長度緩衝，避免點剛好落在遮擋表面邊界漏判。")]
    [SerializeField] private float raycastDistancePadding = 0.02f;

    [Header("內圈採樣（遮擋 / 深度 Snap）")]
    [Tooltip("靠近玩家本體的四點；任一被 occluder 擋住即封鎖深度修正。")]
    [FormerlySerializedAs("samplePoints")]
    [SerializeField] private Transform[] occlusionSamplePoints;
    [Tooltip("留空時從 Inner 子物件自動收集。")]
    [SerializeField] private Transform innerSampleRoot;
    [Tooltip("自動四點時使用：採樣高度 = bounds.min.y + bounds.size.y * normalized。")]
    [SerializeField] [Range(0f, 1f)] private float autoPointHeightNormalized = 0.55f;
    [SerializeField] private float autoPointInset = 0.05f;

    [Header("外圈採樣（牆繞行）")]
    [Tooltip("較靠深度外側的四點；用於判斷是否允許背景牆繞行。")]
    [SerializeField] private Transform[] wallRoutingSamplePoints;
    [Tooltip("留空時從 Four-point detection 下 LH/RH/RL/LL 自動收集。")]
    [SerializeField] private Transform outerSampleRoot;
    [Tooltip("無外圈 Transform 時，自動四點沿深度軸淺向偏移量。")]
    [SerializeField] private float wallRoutingSampleShallowOffset = 0.5f;

    [Header("相機來源")]
    [SerializeField] private Camera explicitCamera;

    CharacterController _cc;
    RaycastHit[] _raycastHits = new RaycastHit[InitialRaycastBufferSize];
    Transform[] _cachedOcclusionSamplePoints;
    Transform[] _cachedWallRoutingSamplePoints;
    bool _occlusionCheckEnabled;
    bool _isBlockedByOcclusion;
    int _lastRefreshFrame = -1;
    bool _rotationInProgress;
    bool _rotationCompletionPending;
    bool _holdDepthUntilPlayerMoves;
    static int _lastPhysicsSyncFrame = -1;

    public bool IsRotationInProgress => _rotationInProgress;
    public event System.Action ViewRotationSettled;

    /// <summary>
    /// 旋轉進行中、或內圈遮擋時，禁止任何玩家深度修正（Snap／繞行／Proxy 跟隨）。
    /// 同一幀的多次查詢共用結果；LateUpdate 會在相機更新後強制刷新。
    /// </summary>
    public bool IsPlayerDepthMovementBlocked
    {
        get
        {
            if (_rotationInProgress || _holdDepthUntilPlayerMoves)
                return true;

            if (_lastRefreshFrame != Time.frameCount)
                RefreshOcclusionNow();
            return _occlusionCheckEnabled && _isBlockedByOcclusion;
        }
    }

    /// <inheritdoc cref="IsPlayerDepthMovementBlocked"/>
    public bool IsDepthSnapBlocked => IsPlayerDepthMovementBlocked;

    /// <summary>內圈：鏡頭到玩家是否被遮擋來源擋住（主判，優先於外圈）。</summary>
    public bool IsPlayerViewOccludedNow()
    {
        if (_lastRefreshFrame != Time.frameCount)
            RefreshOcclusionNow();
        return _isBlockedByOcclusion;
    }

    /// <summary>
    /// 牆繞行用：內圈遮擋時直接 true；內圈放行後才評估外圈（忽略背景牆本體）。
    /// </summary>
    public bool IsPlayerViewOccludedForWallRouting(Vector3 depthAxis)
    {
        if (IsPlayerViewOccludedNow())
            return true;

        return IsOuterViewOccludedForWallRouting(depthAxis);
    }

    /// <summary>僅外圈；呼叫前應先確認內圈未遮擋。</summary>
    public bool IsOuterViewOccludedForWallRouting(Vector3 depthAxis)
    {
        SyncPhysicsTransformsIfNeeded();
        var cam = ResolveViewCamera();
        if (cam == null)
            return true;

        depthAxis.y = 0f;
        if (depthAxis.sqrMagnitude < 0.0001f)
            return false;

        depthAxis.Normalize();
        return IsAnyPointOccluded(cam.transform.position, GetWallRoutingSamplePoints(), depthAxis, forWallRouting: true);
    }

    void Awake()
    {
        _cc = GetComponent<CharacterController>();
        TryAutoBindSampleRoots();
        _cachedOcclusionSamplePoints = HasAnyPoint(occlusionSamplePoints)
            ? occlusionSamplePoints
            : CollectChildPoints(innerSampleRoot);
        _cachedWallRoutingSamplePoints = HasAnyPoint(wallRoutingSamplePoints)
            ? wallRoutingSamplePoints
            : CollectWallRoutingSamplePoints(outerSampleRoot);
        _occlusionCheckEnabled = true;
    }

    void Start()
    {
        RefreshOcclusionNow();
    }

    public void ScheduleViewRotationCompletion()
    {
        _occlusionCheckEnabled = true;
        // 旋轉後先保留玩家世界位置；只有玩家真的以輸入移動後才恢復深度對齊。
        _holdDepthUntilPlayerMoves = true;
        // 等 Cinemachine 更新完本幀相機後，再刷新遮擋並解鎖深度移動。
        _rotationInProgress = true;
        _rotationCompletionPending = true;
    }

    public void SetRotationInProgress(bool rotating)
    {
        _rotationInProgress = rotating;
        if (rotating)
            _rotationCompletionPending = false;
    }

    /// <summary>玩家經由控制輸入實際移動後，解除轉視角後的位置保護。</summary>
    public void NotifyPlayerMoved()
    {
        _holdDepthUntilPlayerMoves = false;
    }

    void LateUpdate()
    {
        // 在 Cinemachine／Brain 更新相機之後再刷新，供同幀 Router／Snapper 使用。
        _lastRefreshFrame = -1;
        RefreshOcclusionNow();

        if (!_rotationCompletionPending)
            return;

        _rotationCompletionPending = false;
        _rotationInProgress = false;
        ViewRotationSettled?.Invoke();
    }

    public void RefreshOcclusionNow()
    {
        _lastRefreshFrame = Time.frameCount;
        if (!_occlusionCheckEnabled && !_rotationInProgress)
            return;

        SyncPhysicsTransformsIfNeeded();

        var cam = ResolveViewCamera();
        if (cam == null)
        {
            if (_occlusionCheckEnabled || _rotationInProgress)
                _isBlockedByOcclusion = true;
            return;
        }

        _isBlockedByOcclusion = IsInnerViewOccluded(cam.transform.position);
    }

    private static void SyncPhysicsTransformsIfNeeded()
    {
        if (_lastPhysicsSyncFrame == Time.frameCount)
            return;

        Physics.SyncTransforms();
        _lastPhysicsSyncFrame = Time.frameCount;
    }

    Camera ResolveViewCamera()
    {
        if (explicitCamera != null && explicitCamera.isActiveAndEnabled)
            return explicitCamera;

        var main = Camera.main;
        if (main != null && main.isActiveAndEnabled)
            return main;

        return null;
    }

    bool IsInnerViewOccluded(Vector3 cameraPos)
    {
        return IsAnyPointOccluded(cameraPos, GetOcclusionSamplePoints(), Vector3.zero, forWallRouting: false);
    }

    void TryAutoBindSampleRoots()
    {
        if (innerSampleRoot == null)
        {
            var fourPoint = transform.Find("Four-point detection");
            if (fourPoint != null)
                innerSampleRoot = fourPoint.Find("Inner");
        }

        if (outerSampleRoot == null)
            outerSampleRoot = transform.Find("Four-point detection");
    }

    Transform[] GetOcclusionSamplePoints()
    {
        return _cachedOcclusionSamplePoints;
    }

    Transform[] GetWallRoutingSamplePoints()
    {
        return _cachedWallRoutingSamplePoints;
    }

    static Transform[] CollectWallRoutingSamplePoints(Transform root)
    {
        if (root == null)
            return null;

        var named = new Transform[4];
        var found = 0;
        TryAddNamedPoint(root, "LH", named, ref found);
        TryAddNamedPoint(root, "RH", named, ref found);
        TryAddNamedPoint(root, "RL", named, ref found);
        TryAddNamedPoint(root, "LL", named, ref found);
        return found > 0 ? named : CollectChildPoints(root);
    }

    static void TryAddNamedPoint(Transform root, string childName, Transform[] buffer, ref int count)
    {
        if (root == null || count >= buffer.Length)
            return;

        var t = root.Find(childName);
        if (t == null)
            return;

        buffer[count++] = t;
    }

    static Transform[] CollectChildPoints(Transform root)
    {
        if (root == null)
            return null;

        var buffer = new Transform[root.childCount];
        var count = 0;
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child == null || child == root)
                continue;
            if (child.name == "Inner")
                continue;
            buffer[count++] = child;
        }

        if (count == 0)
            return null;

        if (count == buffer.Length)
            return buffer;

        var trimmed = new Transform[count];
        for (var i = 0; i < count; i++)
            trimmed[i] = buffer[i];
        return trimmed;
    }

    static bool HasAnyPoint(Transform[] points)
    {
        if (points == null)
            return false;

        for (var i = 0; i < points.Length; i++)
        {
            if (points[i] != null)
                return true;
        }

        return false;
    }

    bool IsAnyPointOccluded(Vector3 cameraPos, Transform[] points, Vector3 depthAxis, bool forWallRouting)
    {
        if (points != null)
        {
            for (var i = 0; i < points.Length; i++)
            {
                var t = points[i];
                if (t == null)
                    continue;
                if (IsSegmentOccluded(cameraPos, t.position, forWallRouting))
                    return true;
            }

            return false;
        }

        return IsAutoBoundsOccluded(cameraPos, depthAxis, forWallRouting);
    }

    bool IsAutoBoundsOccluded(Vector3 cameraPos, Vector3 depthAxis, bool forWallRouting)
    {
        if (_cc == null)
            return false;

        var b = _cc.bounds;
        var y = b.min.y + b.size.y * autoPointHeightNormalized;
        var minX = b.min.x + autoPointInset;
        var maxX = b.max.x - autoPointInset;
        var minZ = b.min.z + autoPointInset;
        var maxZ = b.max.z - autoPointInset;

        var shallowOffset = Vector3.zero;
        if (forWallRouting)
        {
            depthAxis.y = 0f;
            if (depthAxis.sqrMagnitude > 0.0001f)
                shallowOffset = -depthAxis.normalized * Mathf.Max(0f, wallRoutingSampleShallowOffset);
        }

        var p0 = new Vector3(minX, y, minZ) + shallowOffset;
        var p1 = new Vector3(maxX, y, minZ) + shallowOffset;
        var p2 = new Vector3(minX, y, maxZ) + shallowOffset;
        var p3 = new Vector3(maxX, y, maxZ) + shallowOffset;

        return IsSegmentOccluded(cameraPos, p0, forWallRouting)
            || IsSegmentOccluded(cameraPos, p1, forWallRouting)
            || IsSegmentOccluded(cameraPos, p2, forWallRouting)
            || IsSegmentOccluded(cameraPos, p3, forWallRouting);
    }

    bool IsSegmentOccluded(Vector3 from, Vector3 to, bool forWallRouting)
    {
        var dir = to - from;
        var len = dir.magnitude;
        if (len <= 0.0001f)
            return false;

        dir /= len;
        var triggerMode = includeTriggerOccluders
            ? QueryTriggerInteraction.Collide
            : QueryTriggerInteraction.Ignore;
        var raycastMask = includeAllColliderLayers ? Physics.AllLayers : occluderMask.value;
        var hitCount = Physics.RaycastNonAlloc(
            from,
            dir,
            _raycastHits,
            len + raycastDistancePadding,
            raycastMask,
            triggerMode
        );

        // NonAlloc 不保證命中順序。緩衝區滿時擴大後重查，避免漏掉有效遮擋物。
        while (hitCount == _raycastHits.Length && _raycastHits.Length < MaximumRaycastBufferSize)
        {
            var newSize = Mathf.Min(_raycastHits.Length * 2, MaximumRaycastBufferSize);
            _raycastHits = new RaycastHit[newSize];
            hitCount = Physics.RaycastNonAlloc(
                from,
                dir,
                _raycastHits,
                len + raycastDistancePadding,
                raycastMask,
                triggerMode
            );
        }

        // 極端密集場景達上限仍塞滿時採保守判定，防止穿過未被回傳的遮擋物。
        if (hitCount == _raycastHits.Length)
            return true;

        for (var i = 0; i < hitCount; i++)
        {
            var hit = _raycastHits[i];
            if (hit.collider == null)
                continue;
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                continue;
            if (forWallRouting && IsBackgroundWallBodyCollider(hit.collider))
                continue;
            if (hit.distance > len + raycastDistancePadding * 0.5f)
                continue;

            return true;
        }

        return false;
    }

    static bool IsBackgroundWallBodyCollider(Collider col)
    {
        if (col == null)
            return false;
        return col.GetComponentInParent<FezBackgroundWall>() != null;
    }
}
