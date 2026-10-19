using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 掛在平台的「透明碰撞箱（DepthProxy）」上：
/// 只沿著深度軸移動，使自己的深度對齊玩家深度。
/// </summary>
[DisallowMultipleComponent]
public sealed class FezDepthProxyFollower : MonoBehaviour
{
    /// <summary>
    /// <see cref="Staggered"/>：Move 前對齊玩家深度 → 玩家 Move／Snap → Snap 後再對齊（推薦）。<br/>
    /// 其餘為舊版自行 Update／LateUpdate。
    /// </summary>
    public enum SyncMode
    {
        Staggered = 0,
        Update = 1,
        LateUpdate = 2
    }

    private static readonly List<FezDepthProxyFollower> StaggeredRegistry = new List<FezDepthProxyFollower>(64);
    private static int _lastBeforeMoveFrame = -1;
    private static int _lastAfterSnapFrame = -1;

    /// <summary>階段 1（A）：<c>Move</c> 前對齊平台本體深度（<see cref="FezDepthProxySurface"/>）。</summary>
    public static void SyncAllBeforeCharacterControllerMove(float deltaTime)
    {
        SyncStaggeredPhase(deltaTime, ref _lastBeforeMoveFrame, alignToPlatformBody: true);
    }

    /// <summary>階段 3（B）：深度 Snap 後對齊玩家深度。</summary>
    public static void SyncAllAfterPlayerDepthSnap(float deltaTime)
    {
        SyncStaggeredPhase(deltaTime, ref _lastAfterSnapFrame, alignToPlatformBody: false);
    }

    /// <summary>玩家剛生成時強制跑完 A／B 兩階段（略過同幀 gate）。</summary>
    public static void ForceSyncAllPhases(float deltaTime)
    {
        _lastBeforeMoveFrame = -1;
        _lastAfterSnapFrame = -1;
        SyncAllBeforeCharacterControllerMove(deltaTime);
        SyncAllAfterPlayerDepthSnap(deltaTime);
    }

    public void SetPlayerRoot(Transform root)
    {
        playerRoot = root;
        CachePlayerOcclusionGate();
    }

    private static void SyncStaggeredPhase(float deltaTime, ref int lastFrameGate, bool alignToPlatformBody)
    {
        if (PlayerController.IsDropThroughActiveStatic)
            return;

        var f = Time.frameCount;
        if (f == lastFrameGate)
            return;
        lastFrameGate = f;

        for (var i = 0; i < StaggeredRegistry.Count; i++)
        {
            var c = StaggeredRegistry[i];
            if (c != null && c.isActiveAndEnabled && c.syncMode == SyncMode.Staggered)
                c.Tick(deltaTime, alignToPlatformBody);
        }
    }

    [Header("目標")]
    [SerializeField] private Transform playerRoot;

    [Header("深度軸來源")]
    [Tooltip("WorldViewState：FezWorldViewState 的四向深度軸。MainCamera／ExplicitTransform：用鏡頭或指定 Transform 的 forward。")]
    [SerializeField] private FezDepthAxisSource depthAxisSource = FezDepthAxisSource.WorldViewState;
    [Tooltip("ExplicitTransform 時使用。")]
    [SerializeField] private Transform explicitAxisTransform;
    [Tooltip("MainCamera 時使用；可留空以使用 Camera.main。")]
    [SerializeField] private Camera explicitCamera;

    [Header("跟隨設定")]
    [Tooltip("0 = 瞬間對齊；越大越快。")]
    [SerializeField] private float followSpeed = 0f;
    [SerializeField] private SyncMode syncMode = SyncMode.Staggered;

    [Header("視野遮擋")]
    [Tooltip("留空則自動找同物件的 FezPlatformViewOcclusion。被遮擋時頂面只留在本體中心深度、不跟玩家。")]
    [SerializeField] private FezPlatformViewOcclusion viewOcclusion;

    private Transform _parent;
    private Vector3 _baseLocalPos;
    private FezRotationOcclusionGate _playerOcclusionGate;

    private void Awake()
    {
        _parent = transform.parent;
        _baseLocalPos = transform.localPosition;
        if (viewOcclusion == null)
            viewOcclusion = GetComponent<FezPlatformViewOcclusion>();
        CachePlayerOcclusionGate();
    }

    private void OnEnable()
    {
        if (syncMode == SyncMode.Staggered)
            RegisterStaggered(this);
    }

    private void OnDisable()
    {
        UnregisterStaggered(this);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!Application.isPlaying)
            return;
        UnregisterStaggered(this);
        if (syncMode == SyncMode.Staggered && isActiveAndEnabled)
            RegisterStaggered(this);
    }
#endif

    private static void RegisterStaggered(FezDepthProxyFollower c)
    {
        if (c == null || StaggeredRegistry.Contains(c))
            return;
        StaggeredRegistry.Add(c);
    }

    private static void UnregisterStaggered(FezDepthProxyFollower c)
    {
        if (c == null)
            return;
        StaggeredRegistry.Remove(c);
    }

    private void Update()
    {
        if (syncMode == SyncMode.Update)
            Tick(Time.deltaTime);
    }

    private void LateUpdate()
    {
        if (syncMode == SyncMode.LateUpdate)
            Tick(Time.deltaTime);
    }

    private void Tick(float dt, bool alignToPlatformBody = false)
    {
        // The new motor reads real platform geometry; moving an invisible collider is obsolete.
        if (playerRoot != null && playerRoot.TryGetComponent<PlayerController>(out var motor)
            && motor.isActiveAndEnabled) return;

        if (PlayerController.IsDropThroughActiveStatic)
            return;

        if (playerRoot == null)
            return;

        if (IsPlayerDepthMovementBlocked())
            return;

        if (viewOcclusion != null)
        {
            viewOcclusion.RefreshNow();
            if (viewOcclusion.IsViewOccluded)
                alignToPlatformBody = true;
        }

        var viewForward = FezDepthAxis.GetViewForward(depthAxisSource, explicitAxisTransform, explicitCamera);
        var targetDepth = FezDepthAxis.DepthOf(playerRoot.position, viewForward);
        if (alignToPlatformBody)
        {
            var surface = GetComponent<FezDepthProxySurface>();
            if (surface == null)
                surface = GetComponentInParent<FezDepthProxySurface>();
            if (surface != null)
                targetDepth = FezDepthAxis.DepthOf(surface.GetPlatformDepthWorldPos(), viewForward);
        }

        var playerDepth = targetDepth;

        // 若有父物件（平台），鎖住平面位置，只在 local 空間沿深度軸滑動，避免旋轉後左右脫離本體。
        if (_parent != null)
        {
            var baseWorld = _parent.TransformPoint(_baseLocalPos);
            var baseDepth = FezDepthAxis.DepthOf(baseWorld, viewForward);
            var delta = playerDepth - baseDepth;

            // 將世界的 viewForward 轉為父物件的 local 方向，用 localPosition 移動
            var localDepthDir = _parent.InverseTransformDirection(viewForward);
            localDepthDir.y = 0f;
            if (localDepthDir.sqrMagnitude < 0.0001f)
                localDepthDir = Vector3.forward;
            localDepthDir.Normalize();

            var desiredLocal = _baseLocalPos + localDepthDir * delta;
            if (followSpeed <= 0f)
            {
                transform.localPosition = desiredLocal;
            }
            else
            {
                var t = 1f - Mathf.Exp(-followSpeed * dt);
                transform.localPosition = Vector3.Lerp(transform.localPosition, desiredLocal, t);
            }
        }
        else
        {
            // 無父物件時退回世界座標跟隨
            var selfPos = transform.position;
            var selfDepth = FezDepthAxis.DepthOf(selfPos, viewForward);
            var delta = playerDepth - selfDepth;

            var targetPos = selfPos + viewForward * delta;
            if (followSpeed <= 0f)
            {
                transform.position = targetPos;
            }
            else
            {
                var t = 1f - Mathf.Exp(-followSpeed * dt);
                transform.position = Vector3.Lerp(selfPos, targetPos, t);
            }
        }
    }

    private void CachePlayerOcclusionGate()
    {
        _playerOcclusionGate = playerRoot != null
            ? playerRoot.GetComponent<FezRotationOcclusionGate>()
            : null;
    }

    private bool IsPlayerDepthMovementBlocked()
    {
        if (_playerOcclusionGate == null && playerRoot != null)
            CachePlayerOcclusionGate();
        return _playerOcclusionGate != null && _playerOcclusionGate.IsPlayerDepthMovementBlocked;
    }
}
