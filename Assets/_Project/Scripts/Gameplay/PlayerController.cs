using UnityEngine;
using System.Collections.Generic;
using Lichronicle.Gameplay.Inputs;

/// <summary>Owns motion: projected footholds hold height until a clear path permits depth alignment.</summary>
[RequireComponent(typeof(CharacterController))]
[DefaultExecutionOrder(1000)]
public class PlayerController : MonoBehaviour
{
    public float moveSpeed = 8f, jumpHeight = 5f, gravity = -25f, maxFallSpeed = 38f;
    public float coyoteTime = 0.1f, jumpBufferTime = 0.1f;
    public float downInputThreshold = -0.5f, stickDeadzone = 0.25f;
    public float acceleration = 25f, deceleration = 35f;
    public Transform movementPlane;
    [SerializeField] private float dropThroughDownwardBoost = 3.5f;
    [SerializeField, Min(0f)] private float extraLandingEdgeForgiveness = 0.05f;
    [Tooltip("畫面頂面的額外落地容許範圍；深度被擋住時先保留投影支撐。")]
    [SerializeField, Min(0f)] private float projectedLandingEdgeForgiveness = 0.075f;
    private const float Step = 1f / 120f, MaxFrameTime = 0.1f, GroundTolerance = 0.025f;
    private static RaycastHit[] DepthSweepHits = new RaycastHit[32];
    private static Collider[] RouteOverlaps = new Collider[32];
    private CharacterController _controller;
    private FezRotationOcclusionGate _gate;
    private IPlayerInputSource _input;
    private float _verticalSpeed, _speed, _coyote, _jumpBuffer, _dropTime;
    private float _dropTop;
    private bool _jumpConsumed;
    private Vector3 _lastDepthAxis;
    private bool _wasRotating, _holdDepthAfterOccludedRotation;
    private FezOneWayPlatform _support;
    private bool _supportUsesProjection;
    private Vector3 _supportProjectionAxis;
    private Transform _supportTransform;
    private Vector3 _supportLocalPoint, _supportWorldPoint;
    private FezOneWayPlatform _lastSafePlatform;
    private Collider _lastSafeCollider;
    private Vector3 _lastSafeLocalPlayerPosition;
    private PlayerDebugTools _debugTools;
    private bool _reportedLandingDiagnostic;
    private string _lastRouteObstruction = "none";
    private float _nextRouteDiagnosticTime;
    private float _nextRouteDecisionTime;
    private string _routeDecision = "not evaluated";

    public bool IsGrounded { get; private set; }
    public Vector3 Velocity { get; private set; }
    public float WalkingSpeed { get; private set; }
    public FezOneWayPlatform StandingPlatform => _support;
    public static bool IsDropThroughActiveStatic { get; private set; }
    public bool IsDropping => _dropTime > 0f;
    public Vector3 DepthAxis => FezWorldViewState.Active != null
        ? FezWorldViewState.Active.DepthAxisWorld : Vector3.forward;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _gate = GetComponent<FezRotationOcclusionGate>();
        _lastDepthAxis = DepthAxis;
        if (movementPlane == null) movementPlane = transform;
        // Never let Rigidbody gravity integrate the CharacterController transform too.
        var body = GetComponent<Rigidbody>();
        if (body != null) { body.isKinematic = true; body.useGravity = false; }
        int backgroundLayer = LayerMask.NameToLayer("BackGroundWall");
        if (backgroundLayer >= 0) Physics.IgnoreLayerCollision(gameObject.layer, backgroundLayer, false);
        int platformLayer = LayerMask.NameToLayer("FezPlatform");
        if (platformLayer >= 0) Physics.IgnoreLayerCollision(gameObject.layer, platformLayer, false);
        _controller.minMoveDistance = 0f;
        _controller.stepOffset = 0f;
        _controller.skinWidth = Mathf.Max(0.02f, _controller.skinWidth);
        _controller.enableOverlapRecovery = false;
        var snap = GetComponent<FezPlayerDepthSnapper>();
        if (snap != null) snap.enabled = false;
        foreach (var probe in GetComponentsInChildren<PlayerDepthProxySideProbe>(true))
            probe.enabled = false;
        _debugTools = GetComponent<PlayerDebugTools>();
        if (_debugTools == null) _debugTools = gameObject.AddComponent<PlayerDebugTools>();
    }

    private void Start() { _input = PlayerInputResolver.Resolve(gameObject); }

    private void Update()
    {
        if (!_controller.enabled) return;
        float x = _input != null ? _input.GetAxisRaw("Horizontal") : Input.GetAxisRaw("Horizontal");
        float down = _input != null ? _input.GetAxisRaw("Vertical") : Input.GetAxisRaw("Vertical");
        bool jump = _input != null
            ? _input.GetButtonDown("Jump") || _input.GetKeyDown(KeyCode.Space)
            : Input.GetButtonDown("Jump") || Input.GetKeyDown(KeyCode.Space);
        if (_debugTools != null) _debugTools.RecordInput(x, down, jump);
        SimulateFrame(Time.deltaTime, x, down, jump);
    }

    // Runtime and regression harness use exactly the same simulation.
    public void SimulateFrame(float frameTime, float horizontal, float vertical, bool jumpPressed)
    {
        if (_controller == null || !_controller.enabled || frameTime <= 0f) return;
        float dt = Mathf.Min(frameTime, MaxFrameTime);
        bool rotating = _gate != null && _gate.IsRotationInProgress;
        float input = Mathf.Abs(horizontal) < stickDeadzone ? 0f : Mathf.Clamp(horizontal, -1f, 1f);
        if (rotating) { input = 0f; _speed = 0f; }
        // Preserve the original controls: acceleration changes magnitude, not direction.
        // Reversing input reverses immediately, including in the air; releasing stops displacement.
        _speed = Mathf.MoveTowards(_speed, Mathf.Abs(input) * moveSpeed,
            (Mathf.Abs(input) > 0f ? acceleration : deceleration) * dt);
        if (jumpPressed)
        {
            if (vertical <= downInputThreshold && IsGrounded && _support != null)
            {
                if (!TryBeginDropThrough(_support)) return;
                _dropTime = 0.18f;
                _dropTop = _controller.bounds.min.y;
                _verticalSpeed = -Mathf.Max(1f, dropThroughDownwardBoost);
                _jumpBuffer = _coyote = 0f;
                _jumpConsumed = true;
                IsGrounded = false;
                ClearSupport();
            }
            else _jumpBuffer = jumpBufferTime;
        }
        Physics.SyncTransforms();
        bool viewChanged = Vector3.Dot(_lastDepthAxis, DepthAxis) < 0.999f;
        // Becoming hidden because the view changed is not a request to reach the foreground.
        // Keep both wall routing and projected landing disabled until the silhouette is clear.
        if (!rotating && (viewChanged || _wasRotating || _holdDepthAfterOccludedRotation))
            _holdDepthAfterOccludedRotation = !IsFullyClearAfterRotation();
        _lastDepthAxis = DepthAxis;
        _wasRotating = rotating;
        if (_supportUsesProjection && Vector3.Dot(_supportProjectionAxis, DepthAxis) < 0.999f)
            _supportUsesProjection = false;
        bool canMoveDepth = !rotating && !_holdDepthAfterOccludedRotation;
        bool canAppearOnForeground = canMoveDepth && FezPlatformViewOcclusion.IsCharacterMostlyVisible(
            _controller, DepthAxis, 0.5f);
        FezOneWayPlatform.PreparePlayer(_controller);
        Vector3 carry = GetSupportMotion();
        Vector3 start = transform.position;
        int count = Mathf.CeilToInt(dt / Step);
        float h = dt / count;
        for (int i = 0; i < count; i++)
            SimulateStep(h, input, i == 0 ? carry : Vector3.zero,
                canAppearOnForeground, canMoveDepth);
        Velocity = (transform.position - start) / dt;
        WalkingSpeed = input == 0f ? 0f : Mathf.Abs(Vector3.Dot(
            transform.position - start - carry, Vector3.Cross(Vector3.up, DepthAxis).normalized)) / dt;
        IsDropThroughActiveStatic = IsDropping;
    }

    private void SimulateStep(float dt, float input, Vector3 carry, bool canAppearOnForeground,
        bool canMoveForeground)
    {
        _dropTime = Mathf.Max(0f, _dropTime - dt);
        if (IsGrounded && !_jumpConsumed) _coyote = coyoteTime;
        else _coyote = Mathf.Max(0f, _coyote - dt);
        if (_jumpBuffer > 0f && _coyote > 0f && !_jumpConsumed && !IsDropping)
        {
            _verticalSpeed = Mathf.Sqrt(2f * Mathf.Abs(gravity) * Mathf.Max(0f, jumpHeight));
            _jumpBuffer = _coyote = 0f;
            _jumpConsumed = true;
            IsGrounded = false;
            _reportedLandingDiagnostic = false;
            ClearSupport();
        }
        _jumpBuffer = Mathf.Max(0f, _jumpBuffer - dt);
        Vector3 right = Vector3.Cross(Vector3.up, DepthAxis).normalized;
        float oldY = _verticalSpeed;
        _verticalSpeed -= Mathf.Abs(gravity) * dt;
        if (maxFallSpeed > 0f) _verticalSpeed = Mathf.Max(_verticalSpeed, -maxFallSpeed);
        Vector3 delta = right * ((input == 0f ? 0f : Mathf.Sign(input)) * _speed * dt) + carry;
        delta.y += (oldY + _verticalSpeed) * 0.5f * dt;
        _routeDecision = canMoveForeground ? "collecting" : "depth movement held";
        if (canMoveForeground) RouteAroundWalls(ref delta);
        TraceRouteDecision(input, delta);
        Vector3 feet = _controller.bounds.center;
        feet.y = _controller.bounds.min.y;
        bool descending = _verticalSpeed <= 0f;
        bool hasPlatformFloor = TryResolvePlatformSupport(feet + carry, feet + delta, descending,
            canMoveForeground && canAppearOnForeground,
            out var floor, out bool projectedSupport, out bool alignDepth);
        if (hasPlatformFloor)
        {
            Vector3 landingDelta = delta;
            landingDelta.y = floor.point.y - feet.y;
            if (alignDepth)
                landingDelta += DepthAxis * Vector3.Dot(floor.point - (feet + landingDelta), DepthAxis);
            // Establish the visible foothold first. Depth alignment is a separate move
            // and must wait when the capsule or its clearance corridor is obstructed.
            if (!alignDepth || IsRouteClear(Vector3.zero, landingDelta)) delta = landingDelta;
            else
            {
                // A moving obstacle may invalidate the route after selection. Keep
                // the same projected top instead of forcing the capsule into the wall.
                projectedSupport = true;
                delta.y = floor.point.y - feet.y;
            }
        }
        if (descending && !IsGrounded && !hasPlatformFloor)
            TraceMissedLanding(feet, feet + delta, canMoveForeground && canAppearOnForeground);
        bool keepProjectedSupport = hasPlatformFloor && projectedSupport;
        CollisionFlags flags = _controller.Move(delta);
        bool physicalFloor = descending && (flags & CollisionFlags.Below) != 0;
        bool landedOnPlatform = hasPlatformFloor &&
            Mathf.Abs(_controller.bounds.min.y - floor.point.y) <= GroundTolerance &&
            ValidateSupport(ref floor, keepProjectedSupport);
        IsGrounded = physicalFloor || landedOnPlatform;
        if (IsGrounded)
        {
            _verticalSpeed = 0f;
            _jumpConsumed = false;
            if (landedOnPlatform) SetSupport(floor, keepProjectedSupport);
            else ClearSupport();
        }
        else ClearSupport();
        if ((flags & CollisionFlags.Above) != 0 && _verticalSpeed > 0f) _verticalSpeed = 0f;
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void TraceMissedLanding(Vector3 startFeet, Vector3 endFeet, bool allowVisibleLanding)
    {
        if (_reportedLandingDiagnostic) return;
        Vector3 right = Vector3.Cross(Vector3.up, DepthAxis).normalized;
        var report = new System.Text.StringBuilder();
        foreach (var platform in FezOneWayPlatform.ActivePlatforms)
        {
            if (platform == null || !platform.isActiveAndEnabled) continue;
            foreach (var body in platform.GetSolidBodyColliders())
            {
                if (!body.enabled || !body.gameObject.activeInHierarchy) continue;
                Bounds bounds = body.bounds;
                float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                if (Mathf.Abs(Vector3.Dot(endFeet - bounds.center, right)) > half + ProjectedLandingMargin ||
                    bounds.max.y > startFeet.y + GroundTolerance ||
                    bounds.max.y < endFeet.y - GroundTolerance) continue;
                bool sampled = platform.TryGetCenteredLanding(startFeet, endFeet, GroundTolerance,
                    out var hit, DepthAxis, ProjectedLandingMargin);
                bool visible = sampled && FezPlatformViewOcclusion.IsFootVisible(transform,
                    hit.point, DepthAxis, platform.GetAllPlatformColliders());
                bool pathClear = false, supportClear = false;
                if (sampled)
                {
                    Vector3 landing = endFeet - startFeet;
                    landing.y = hit.point.y - startFeet.y;
                    landing += DepthAxis * Vector3.Dot(hit.point - (startFeet + landing), DepthAxis);
                    pathClear = IsRouteClear(Vector3.zero, landing);
                    supportClear = CanEstablishProjectedSupport(startFeet, endFeet, hit);
                }
                report.AppendLine($"body={body.name} bounds={bounds} sampled={sampled} visible={visible} pathClear={pathClear} supportClear={supportClear}");
            }
        }
        if (report.Length == 0) return;
        _reportedLandingDiagnostic = true;
        Debug.Log($"[Morld Landing] feet={startFeet:R}->{endFeet:R} axis={DepthAxis} allowLanding={allowVisibleLanding} rotationHold={_holdDepthAfterOccludedRotation}\n{report}", this);
    }

    private bool CanEstablishProjectedSupport(Vector3 startFeet, Vector3 endFeet,
        FezOneWayPlatform.SupportHit candidate)
    {
        // Forgiveness is for a new descent onto a top, not walking beyond it.
        // Once the feet are already at that height, require the actual capsule
        // footprint so a released ledge cannot be reacquired every simulation step.
        if ((IsGrounded || startFeet.y <= candidate.point.y + GroundTolerance + 0.001f) &&
            !candidate.platform.TryGetProjectedSupport(startFeet, endFeet, GroundTolerance,
                out _, DepthAxis, SupportLateralMargin)) return false;
        Vector3 landing = endFeet - startFeet;
        landing.y = candidate.point.y - startFeet.y;
        // Only acquire height here. Solid geometry at the current depth still blocks
        // this movement, but a wall on the eventual depth path can safely wait.
        return IsRouteClear(Vector3.zero, landing);
    }

    private bool ValidateSupport(ref FezOneWayPlatform.SupportHit floor, bool projected)
    {
        Vector3 feet = _controller.bounds.center;
        feet.y = _controller.bounds.min.y;
        float margin = IsGrounded && _support == floor.platform
            ? SupportLateralMargin : ProjectedLandingMargin;
        return projected
            ? floor.platform.TryGetProjectedSupport(feet, feet, GroundTolerance, out floor, DepthAxis, margin)
            : floor.platform.TryGetAnchoredFloor(feet, feet, GroundTolerance, out floor, DepthAxis, SupportLateralMargin);
    }

    private bool TryResolvePlatformSupport(Vector3 startFeet, Vector3 endFeet, bool descending,
        bool allowVisibleLanding, out FezOneWayPlatform.SupportHit floor, out bool projectedSupport,
        out bool alignDepth)
    {
        floor = default;
        projectedSupport = false;
        alignDepth = false;
        if (!descending) return false;

        // Remaining on a valid foothold is not a new landing. Preserve its actual world point
        // even when the camera changes or another platform becomes visible in front of it.
        if (!IsDropping && IsGrounded && _support != null)
        {
            // Routing changes only depth. Preserve the same 2D foothold until its lateral edge
            // is left; reacquiring its physical depth here would undo the wall route every step.
            bool retained = _supportUsesProjection
                ? _support.TryGetProjectedSupport(startFeet, endFeet, GroundTolerance, out floor,
                    DepthAxis, SupportLateralMargin)
                : _support.TryGetAnchoredFloor(startFeet, endFeet, GroundTolerance, out floor,
                    DepthAxis, SupportLateralMargin);
            if (retained)
            {
                projectedSupport = _supportUsesProjection;
                // The routed foothold is temporary. Once the return corridor is clear,
                // reacquire this same block's center without requiring a jump or a new landing.
                if (projectedSupport && allowVisibleLanding &&
                    _support.TryGetCenteredLanding(startFeet, endFeet, GroundTolerance,
                        out var centeredSupport, DepthAxis, SupportLateralMargin) &&
                    CanAlignLandingDepth(startFeet, endFeet, centeredSupport))
                {
                    floor = centeredSupport;
                    projectedSupport = false;
                    alignDepth = true;
                }
                return true;
            }
        }

        float ignoredTop = IsDropping ? _dropTop : float.PositiveInfinity;
        // New visible contacts acquire height first; depth follows only with clearance.
        bool hasPhysicalFloor = FezOneWayPlatform.FindFloorAtCurrentDepth(startFeet, endFeet,
            GroundTolerance, out var physicalFloor, DepthAxis, SupportLateralMargin, ignoredTop);
        if (allowVisibleLanding &&
            FezOneWayPlatform.FindFloor(_controller, startFeet, endFeet, DepthAxis,
                GroundTolerance, out floor, ignoredTop, true, ProjectedLandingMargin, preserveDepth: false,
                reachable: candidate => CanEstablishProjectedSupport(startFeet, endFeet, candidate)))
        {
            // This is a new contact, unlike retaining a foothold while walking around a wall.
            // Align only as the feet cross the top; ascent and flight never acquire a target.
            bool needsDepth = Mathf.Abs(Vector3.Dot(floor.point - endFeet, DepthAxis)) > GroundTolerance;
            bool canAlign = CanAlignLandingDepth(startFeet, endFeet, floor);
            alignDepth = needsDepth && canAlign;
            projectedSupport = !canAlign;
            return true;
        }

        // Keep real support if no visible centered landing can actually be reached.
        if (hasPhysicalFloor)
        {
            floor = physicalFloor;
            return true;
        }

        return false;
    }

    private bool CanAlignLandingDepth(Vector3 startFeet, Vector3 endFeet,
        FezOneWayPlatform.SupportHit floor)
    {
        Vector3 alignedFeet = endFeet + DepthAxis * Vector3.Dot(floor.point - endFeet, DepthAxis);
        alignedFeet.y = floor.point.y;
        // The wider projected edge remains stable while waiting. Only convert back
        // to physical support once the ordinary footprint can really hold the player.
        return floor.platform.TryGetAnchoredFloor(alignedFeet, alignedFeet, GroundTolerance,
                out _, DepthAxis, SupportLateralMargin) &&
            CanRestoreRoutedSupport(startFeet, floor.point);
    }

    private bool CanRestoreRoutedSupport(Vector3 feet, Vector3 target)
    {
        Vector3 shift = DepthAxis * Vector3.Dot(target - feet, DepthAxis);
        // Leave more room than the wall approach padding, so routing and restoration
        // cannot alternate on successive simulation steps near the same wall edge.
        Bounds corridor = _controller.bounds;
        corridor.Encapsulate(corridor.min + shift);
        corridor.Encapsulate(_controller.bounds.max + shift);
        Vector3 right = Vector3.Cross(Vector3.up, DepthAxis);
        corridor.Expand(new Vector3(Mathf.Abs(right.x), 0f, Mathf.Abs(right.z)) *
            (2f * (_controller.skinWidth + GroundTolerance + 0.05f)));
        foreach (var wall in FezBackgroundWall.ActiveWalls)
        {
            if (wall == null || !wall.isActiveAndEnabled) continue;
            foreach (var body in wall.GetBodyColliders())
            {
                if (body == null || !body.enabled || body.isTrigger || !body.gameObject.activeInHierarchy) continue;
                Bounds wallClearance = body.bounds;
                // A route over a level red top must not immediately restore the white
                // platform's depth and put the capsule back onto that same red top.
                wallClearance.SetMinMax(wallClearance.min, wallClearance.max +
                    Vector3.up * (_controller.skinWidth + GroundTolerance));
                if (corridor.Intersects(wallClearance)) return false;
            }
        }
        foreach (var platform in FezOneWayPlatform.ActivePlatforms)
        {
            if (platform == null || !platform.isActiveAndEnabled) continue;
            foreach (var body in platform.GetSolidBodyColliders())
                if (body.enabled && body.gameObject.activeInHierarchy &&
                    _controller.bounds.min.y < body.bounds.max.y - GroundTolerance &&
                    _controller.bounds.max.y > body.bounds.min.y + GroundTolerance &&
                    corridor.Intersects(body.bounds)) return false;
        }
        return IsRouteClear(Vector3.zero, shift);
    }

    private Vector3 GetSupportMotion()
    {
        if (!IsGrounded || _support == null || _supportTransform == null) return Vector3.zero;
        Vector3 now = _supportTransform.TransformPoint(_supportLocalPoint);
        Vector3 delta = now - _supportWorldPoint;
        _supportWorldPoint = now;
        return delta;
    }

    private void SetSupport(FezOneWayPlatform.SupportHit hit, bool projected = false)
    {
        if (!projected && hit.platform != null && hit.collider != null &&
            (_lastSafePlatform != hit.platform || _lastSafeCollider != hit.collider))
        {
            _lastSafePlatform = hit.platform;
            _lastSafeCollider = hit.collider;
            _lastSafeLocalPlayerPosition = hit.collider.transform.InverseTransformPoint(transform.position);
        }
        _support = hit.platform;
        _supportUsesProjection = projected;
        _supportProjectionAxis = DepthAxis;
        _supportTransform = hit.collider.transform;
        _supportWorldPoint = hit.point;
        _supportLocalPoint = _supportTransform.InverseTransformPoint(hit.point);
    }

    private void ClearSupport() { _support = null; _supportTransform = null; _supportUsesProjection = false; }

    public bool TryReturnToLastSafePlatform(out string message)
    {
        message = "";
        if (_controller == null || !_controller.enabled)
        {
            message = "玩家碰撞器尚未就緒";
            return false;
        }
        if (_lastSafePlatform == null || !_lastSafePlatform.isActiveAndEnabled ||
            _lastSafeCollider == null || !_lastSafeCollider.enabled ||
            !_lastSafeCollider.gameObject.activeInHierarchy)
        {
            message = "還沒有記錄到可返回的平台";
            return false;
        }

        Vector3 previousPosition = transform.position;
        Quaternion previousRotation = transform.rotation;
        Vector3 targetPosition = _lastSafeCollider.transform.TransformPoint(_lastSafeLocalPlayerPosition);
        _controller.enabled = false;
        transform.SetPositionAndRotation(targetPosition, previousRotation);
        Physics.SyncTransforms();
        _controller.enabled = true;
        FezOneWayPlatform.InvalidatePlayerCollisionState(_controller);
        Physics.SyncTransforms();

        if (!IsRecoveryPositionClear(_lastSafePlatform, _lastSafeCollider))
        {
            RestorePosition(previousPosition, previousRotation);
            message = "上一個落點目前被障礙物擋住";
            return false;
        }

        Vector3 feet = _controller.bounds.center;
        feet.y = _controller.bounds.min.y;
        if (!_lastSafePlatform.TryGetAnchoredFloor(feet + Vector3.up * 0.1f,
                feet - Vector3.up * 0.1f, 0.2f, out var floor, DepthAxis, LandingLateralMargin))
        {
            RestorePosition(previousPosition, previousRotation);
            message = "上一個平台已無法支撐玩家";
            return false;
        }

        float verticalCorrection = floor.point.y - feet.y;
        if (Mathf.Abs(verticalCorrection) > 0.001f)
        {
            _controller.enabled = false;
            transform.position += Vector3.up * verticalCorrection;
            Physics.SyncTransforms();
            _controller.enabled = true;
            FezOneWayPlatform.InvalidatePlayerCollisionState(_controller);
            Physics.SyncTransforms();
            feet = _controller.bounds.center;
            feet.y = _controller.bounds.min.y;
            if (!_lastSafePlatform.TryGetAnchoredFloor(feet + Vector3.up * 0.1f,
                    feet - Vector3.up * 0.1f, 0.2f, out floor, DepthAxis, LandingLateralMargin))
            {
                RestorePosition(previousPosition, previousRotation);
                message = "返回位置未能重新確認平台支撐";
                return false;
            }
        }

        _verticalSpeed = _speed = _coyote = _jumpBuffer = _dropTime = 0f;
        _jumpConsumed = false;
        IsGrounded = true;
        _wasRotating = false;
        _holdDepthAfterOccludedRotation = false;
        _lastDepthAxis = DepthAxis;
        SetSupport(floor);
        Velocity = Vector3.zero;
        WalkingSpeed = 0f;
        message = $"已返回平台：{_lastSafePlatform.name}";
        return true;
    }

    private bool IsRecoveryPositionClear(FezOneWayPlatform allowedPlatform, Collider allowedSupport)
    {
        GetQueryCapsule(Vector3.zero, out var top, out var bottom, out float radius);
        int count;
        while (true)
        {
            count = Physics.OverlapCapsuleNonAlloc(top, bottom, radius, RouteOverlaps,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (count < RouteOverlaps.Length) break;
            if (RouteOverlaps.Length >= 4096) return false;
            System.Array.Resize(ref RouteOverlaps, RouteOverlaps.Length * 2);
        }
        for (int i = 0; i < count; i++)
        {
            Collider other = RouteOverlaps[i];
            if (other == allowedSupport) continue;
            if (FezOneWayPlatform.TryResolveByCollider(other, out var owner) && owner == allowedPlatform)
                continue;
            if (IsRouteObstacle(other)) return false;
        }
        return true;
    }

    private void RestorePosition(Vector3 position, Quaternion rotation)
    {
        _controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        Physics.SyncTransforms();
        _controller.enabled = true;
        FezOneWayPlatform.InvalidatePlayerCollisionState(_controller);
        Physics.SyncTransforms();
    }

    private bool TryBeginDropThrough(FezOneWayPlatform platform)
    {
        Vector3 axis = DepthAxis;
        Bounds player = _controller.bounds;
        float radius = Mathf.Abs(axis.x) * player.extents.x + Mathf.Abs(axis.z) * player.extents.z;
        float currentDepth = Vector3.Dot(player.center, axis);
        float targetDepth = currentDepth;
        foreach (var body in platform.GetSolidBodyColliders())
        {
            if (!body.enabled || !body.gameObject.activeInHierarchy ||
                !FezBackgroundWall.OverlapsViewColumn(player, body.bounds, axis)) continue;
            targetDepth = Mathf.Min(targetDepth,
                FezBackgroundWall.ProjectBoundsShallowDepth(body.bounds, axis) - radius -
                _controller.skinWidth - GroundTolerance);
        }
        // Adjacent top tiles form one continuous ledge. Extend to its front edge
        // while the capsule would otherwise still be supported by a neighbouring tile.
        bool extended;
        do
        {
            extended = false;
            foreach (var other in FezOneWayPlatform.ActivePlatforms)
            {
                if (other == null || !other.isActiveAndEnabled) continue;
                foreach (var body in other.GetSolidBodyColliders())
                {
                    if (!body.enabled || !body.gameObject.activeInHierarchy) continue;
                    Bounds bounds = body.bounds;
                    if (bounds.max.y < player.min.y - GroundTolerance ||
                        bounds.min.y > player.min.y + GroundTolerance ||
                        !FezBackgroundWall.OverlapsViewColumn(player, bounds, axis) ||
                        FezBackgroundWall.ProjectBoundsDeepDepth(bounds, axis) < targetDepth - radius ||
                        FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis) > currentDepth + radius) continue;
                    float required = FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis) -
                        radius - _controller.skinWidth - GroundTolerance;
                    if (required >= targetDepth - 0.001f) continue;
                    targetDepth = required;
                    extended = true;
                }
            }
        } while (extended);
        Vector3 displacement = axis * (targetDepth - currentDepth);
        // Drop follows a checked path around the front edge, never an ignored collision pair.
        if (!IsRouteClear(Vector3.zero, displacement)) return false;
        Vector3 before = transform.position;
        if (MoveRouteLeg(displacement)) return true;
        MoveRouteLeg(before - transform.position);
        return false;
    }

    // Route depth first, while still outside the wall silhouette, then continue sideways.
    // Never disable wall collision or move through the wall to reach the foreground.
    private void RouteAroundWalls(ref Vector3 delta)
    {
        Vector3 axis = DepthAxis;
        Vector3 right = Vector3.Cross(Vector3.up, axis);
        float lateralMove = Vector3.Dot(delta, right);
        if (delta.sqrMagnitude < 0.000000001f) return;
        Bounds current = _controller.bounds;
        Bounds next = current;
        next.center += delta;
        // Start the depth leg before native skin contact can stop the lateral leg.
        float approachPadding = _controller.skinWidth + GroundTolerance;
        next.Expand(new Vector3(Mathf.Abs(right.x), 0f, Mathf.Abs(right.z)) * (2f * approachPadding));
        float radius = Mathf.Abs(axis.x) * current.extents.x + Mathf.Abs(axis.z) * current.extents.z;
        float targetDepth = Vector3.Dot(current.center, axis);
        float routeMinSide = float.PositiveInfinity, routeMaxSide = float.NegativeInfinity;
        bool needsRoute = false;
        bool routeBlocksLateralEntry = false;
        int routePriority = -1;
        foreach (var wall in FezBackgroundWall.ActiveWalls)
        {
            if (wall == null || !wall.isActiveAndEnabled) continue;
            foreach (var body in wall.GetBodyColliders())
            {
                if (body == null || !body.enabled || body.isTrigger || !body.gameObject.activeInHierarchy) continue;
                Bounds bounds = body.bounds;
                // Keep an existing top underfoot, but route a new red wall approached
                // sideways even when its top is level with the current white platform.
                // Keep retrying while the center is still outside the new top; capsule
                // overlap alone must not turn a failed first attempt into a foothold.
                float wallHalfSide = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                float wallSideOffset = Vector3.Dot(current.center - bounds.center, right);
                bool enteringTopFromSide = Mathf.Abs(lateralMove) > 0.000001f &&
                    wallSideOffset * lateralMove <= 0f &&
                    Mathf.Abs(wallSideOffset) >= wallHalfSide - 0.001f &&
                    FezBackgroundWall.OverlapsViewColumn(next, bounds, axis);
                if (IsGrounded && current.min.y >= bounds.max.y - GroundTolerance &&
                    next.min.y >= bounds.max.y - GroundTolerance && !enteringTopFromSide) continue;
                // Keep the approach margin above background walls too: route before
                // the capsule touches their top, while the depth path is still clear.
                // Ordinary platform tops are handled separately below as floors.
                if (next.min.y >= bounds.max.y + approachPadding ||
                    next.max.y <= bounds.min.y - approachPadding ||
                    !FezBackgroundWall.OverlapsViewColumn(next, bounds, axis)) continue;
                bool approachingSide = Mathf.Abs(lateralMove) > 0.000001f &&
                    wallSideOffset * lateralMove <= 0f &&
                    Mathf.Abs(wallSideOffset) >= wallHalfSide - 0.001f;
                bool approachingTop = !IsGrounded && delta.y < 0f &&
                    current.min.y >= bounds.max.y - GroundTolerance;
                bool approachingBottom = delta.y > 0f &&
                    current.max.y <= bounds.min.y + GroundTolerance;
                if (!approachingSide && !approachingTop && !approachingBottom) continue;
                if (!IsInCurrentRoutingRoom(body, axis)) continue;
                float shallow = FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis);
                float required = shallow - radius - _controller.skinWidth - GroundTolerance;
                // A wall well behind the capsule is no longer an approaching obstacle.
                // It must not outrank a new level-top wall at the player's real depth.
                // Keep only the narrow cleared boundary band used by a recent route.
                if (Vector3.Dot(current.center, axis) < required - GroundTolerance) continue;
                // Body-height side obstacles define the route before lower top rims.
                // A lower wall may extend to the room boundary without preventing
                // a checked horizontal route around a shallower upper block.
                bool overlapsBodyHeight = current.min.y < bounds.max.y - GroundTolerance &&
                    current.max.y > bounds.min.y + GroundTolerance;
                int priority = approachingSide && overlapsBodyHeight ? 2 :
                    (approachingTop || approachingBottom ? 1 : 0);
                if (priority < routePriority) continue;
                if (priority > routePriority)
                {
                    routePriority = priority;
                    targetDepth = Vector3.Dot(current.center, axis);
                    routeMinSide = float.PositiveInfinity;
                    routeMaxSide = float.NegativeInfinity;
                    routeBlocksLateralEntry = false;
                    needsRoute = false;
                }
                // A body-height wall already cleared in depth still outranks the
                // lower top rim. Do not replace it with a needless route past the
                // entire supporting ledge all the way to the room boundary.
                if (Vector3.Dot(current.center, axis) <= required + 0.001f) continue;
                routeBlocksLateralEntry |= approachingSide && (overlapsBodyHeight || enteringTopFromSide);
                targetDepth = Mathf.Min(targetDepth, required);
                float halfSide = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                float centerSide = Vector3.Dot(bounds.center, right);
                routeMinSide = Mathf.Min(routeMinSide, centerSide - halfSide);
                routeMaxSide = Mathf.Max(routeMaxSide, centerSide + halfSide);
                needsRoute = true;
            }
        }
        // Blocks route only on side entry. Their top is a floor and their underside is solid.
        foreach (var platform in FezOneWayPlatform.ActivePlatforms)
        {
            if (platform == null || !platform.isActiveAndEnabled) continue;
            foreach (var body in platform.GetSolidBodyColliders())
            {
                if (!body.enabled || !body.gameObject.activeInHierarchy) continue;
                Bounds bounds = body.bounds;
                if (!IsInCurrentRoutingRoom(body, axis)) continue;
                if (current.min.y >= bounds.max.y - GroundTolerance ||
                    current.max.y <= bounds.min.y + GroundTolerance ||
                    next.min.y >= bounds.max.y - GroundTolerance ||
                    next.max.y <= bounds.min.y + GroundTolerance ||
                    !FezBackgroundWall.OverlapsViewColumn(next, bounds, axis)) continue;
                if (Mathf.Abs(lateralMove) <= 0.000001f ||
                    Vector3.Dot(current.center - bounds.center, right) * lateralMove > 0f) continue;
                float required = FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis) -
                    radius - _controller.skinWidth - GroundTolerance;
                if (Vector3.Dot(current.center, axis) < required - GroundTolerance) continue;
                // Solid platform sides are body-height obstacles too.
                if (routePriority < 2)
                {
                    routePriority = 2;
                    targetDepth = Vector3.Dot(current.center, axis);
                    routeMinSide = float.PositiveInfinity;
                    routeMaxSide = float.NegativeInfinity;
                    routeBlocksLateralEntry = false;
                    needsRoute = false;
                }
                if (Vector3.Dot(current.center, axis) <= required + 0.001f) continue;
                routeBlocksLateralEntry = true;
                targetDepth = Mathf.Min(targetDepth, required);
                float halfSide = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                float centerSide = Vector3.Dot(bounds.center, right);
                routeMinSide = Mathf.Min(routeMinSide, centerSide - halfSide);
                routeMaxSide = Mathf.Max(routeMaxSide, centerSide + halfSide);
                needsRoute = true;
            }
        }
        if (!needsRoute)
        {
            _routeDecision = "no target; priority=" + routePriority;
            return;
        }
        Vector3 depthMove = axis * (targetDepth - Vector3.Dot(current.center, axis));
        Vector3 lateral = right * lateralMove;
        if (!TryExecuteWallRoute(depthMove, lateral, right, routeMinSide, routeMaxSide, radius))
        {
            _routeDecision = "route failed; priority=" + routePriority;
            TraceBlockedRoute(targetDepth, lateralMove, routePriority);
            // A blocked foreground route is a solid side in the 2D view. Prevent
            // entry behind it, while preserving gravity, jumping and retreat.
            // A failed top/bottom route alone must not freeze sideways movement.
            if (routeBlocksLateralEntry) delta -= lateral;
            return;
        }
        _routeDecision = "routed; targetDepth=" + targetDepth;
        if (IsGrounded && _support != null)
        {
            _supportUsesProjection = true;
            _supportProjectionAxis = axis;
        }
        else ClearSupport();
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void TraceRouteDecision(float input, Vector3 movement)
    {
        if (Mathf.Abs(input) < stickDeadzone || Time.unscaledTime < _nextRouteDecisionTime) return;
        _nextRouteDecisionTime = Time.unscaledTime + 0.2f;
        Bounds player = _controller.bounds;
        Vector3 axis = DepthAxis;
        Vector3 right = Vector3.Cross(Vector3.up, axis);
        float playerHalf = Mathf.Abs(right.x) * player.extents.x + Mathf.Abs(right.z) * player.extents.z;
        var nearby = new System.Text.StringBuilder();
        foreach (var wall in FezBackgroundWall.ActiveWalls)
        {
            if (wall == null || !wall.isActiveAndEnabled) continue;
            foreach (var body in wall.GetBodyColliders())
            {
                if (body == null || !body.enabled || body.isTrigger || !body.gameObject.activeInHierarchy) continue;
                Bounds bounds = body.bounds;
                float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                float side = Vector3.Dot(player.center - bounds.center, right);
                if (Mathf.Abs(side) > half + playerHalf + 0.25f ||
                    player.min.y > bounds.max.y + 0.2f || player.max.y < bounds.min.y - 0.2f) continue;
                nearby.AppendLine($"wall={wall.name} bounds={bounds} side={side:R} half={half:R} " +
                    $"feetAboveTop={player.min.y - bounds.max.y:R} room={IsInCurrentRoutingRoom(body, axis)} " +
                    $"requiredDepth={FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis) - _controller.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z)) - _controller.skinWidth - GroundTolerance:R}");
            }
        }
        Debug.Log($"[Morld Route Decision] pos={transform.position:R} input={input:R} move={movement:R} " +
            $"axis={axis} grounded={IsGrounded} rotationHold={_holdDepthAfterOccludedRotation} " +
            $"support={(_support != null ? _support.name : "none")} projected={_supportUsesProjection} " +
            $"decision={_routeDecision}\n{nearby}", this);
    }

    private bool IsInCurrentRoutingRoom(Collider candidate, Vector3 axis)
    {
        Vector3 origin = _controller.bounds.center;
        float depth = Vector3.Dot(candidate.bounds.center - origin, axis);
        float distance = Mathf.Abs(depth);
        if (distance < 0.001f) return true;
        int count;
        while (true)
        {
            count = Physics.RaycastNonAlloc(origin, axis * Mathf.Sign(depth), DepthSweepHits,
                distance, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (count < DepthSweepHits.Length) break;
            if (DepthSweepHits.Length >= 4096) return false;
            System.Array.Resize(ref DepthSweepHits, DepthSweepHits.Length * 2);
        }
        foreach (var hit in new System.ArraySegment<RaycastHit>(DepthSweepHits, 0, count))
        {
            Collider other = hit.collider;
            if (other == candidate || !IsRouteObstacle(other)) continue;
            // Room planes divide projection space. Ordinary blocks remain route
            // obstacles rather than separators, so paths around them are still checked.
            Bounds bounds = other.bounds;
            float thickness = Mathf.Abs(axis.x) * bounds.size.x + Mathf.Abs(axis.z) * bounds.size.z;
            Vector3 right = Vector3.Cross(Vector3.up, axis);
            float width = Mathf.Abs(right.x) * bounds.size.x + Mathf.Abs(right.z) * bounds.size.z;
            bool roomPlane = other is MeshCollider && thickness < 0.01f &&
                bounds.size.y > _controller.bounds.size.y * 2f &&
                width > _controller.bounds.size.x * 2f;
            if (roomPlane || other.GetComponentInParent<FezGridBoundaryWall>() != null) return false;
        }
        return true;
    }

    private bool TryExecuteWallRoute(Vector3 depthMove, Vector3 lateral, Vector3 right,
        float minSide, float maxSide, float radius)
    {
        _lastRouteObstruction = "none";
        Vector3 sideLeg = Vector3.zero;
        if (!IsRouteClear(Vector3.zero, depthMove) || !IsRouteClear(depthMove, lateral))
        {
            string directFailure = _lastRouteObstruction;
            // A view change can leave the player behind the middle of a solid wall.
            // Check an actual three-leg path around either edge, including the return leg.
            ExpandConnectedWallEdges(ref minSide, ref maxSide, depthMove, right);
            float side = Vector3.Dot(_controller.bounds.center, right);
            float margin = radius + _controller.skinWidth + GroundTolerance;
            float first = minSide - margin - side, second = maxSide + margin - side;
            if (Mathf.Abs(second) < Mathf.Abs(first)) { float swap = first; first = second; second = swap; }
            if (CanUseSideRoute(right * first, depthMove, lateral)) sideLeg = right * first;
            else if (CanUseSideRoute(right * second, depthMove, lateral)) sideLeg = right * second;
            else
            {
                _lastRouteObstruction = "direct=" + directFailure + "; alternate=" + _lastRouteObstruction;
                return false;
            }
        }
        Vector3 before = transform.position;
        Vector3 afterSide = before, afterDepth = before;
        if (!MoveRouteLeg(sideLeg)) { MoveRouteLeg(before - transform.position); return false; }
        afterSide = transform.position;
        if (!MoveRouteLeg(depthMove))
        {
            MoveRouteLeg(afterSide - transform.position);
            MoveRouteLeg(before - transform.position);
            return false;
        }
        afterDepth = transform.position;
        if (!MoveRouteLeg(-sideLeg))
        {
            MoveRouteLeg(afterDepth - transform.position);
            MoveRouteLeg(afterSide - transform.position);
            MoveRouteLeg(before - transform.position);
            return false;
        }
        return true;
    }

    private bool MoveRouteLeg(Vector3 displacement)
    {
        if (displacement.sqrMagnitude < 0.000000001f) return true;
        Vector3 target = transform.position + displacement;
        _controller.Move(displacement);
        Physics.SyncTransforms();
        bool reached = (transform.position - target).sqrMagnitude <= GroundTolerance * GroundTolerance;
        if (!reached) _lastRouteObstruction = $"native Move: wanted={target:R} actual={transform.position:R} leg={displacement:R}";
        return reached;
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void TraceBlockedRoute(float targetDepth, float lateralMove, int priority)
    {
        if (Time.unscaledTime < _nextRouteDiagnosticTime) return;
        _nextRouteDiagnosticTime = Time.unscaledTime + 1f;
        Debug.Log($"[Morld Route] pos={transform.position:R} bounds={_controller.bounds} axis={DepthAxis} " +
            $"targetDepth={targetDepth:R} lateral={lateralMove:R} priority={priority} grounded={IsGrounded} " +
            $"obstruction={_lastRouteObstruction}", this);
    }

    private void DescribeRouteObstruction(Collider body, Vector3 offset, Vector3 displacement, string phase)
    {
#if UNITY_EDITOR
        string hierarchy = body.name;
        for (Transform parent = body.transform.parent; parent != null; parent = parent.parent)
            hierarchy = parent.name + "/" + hierarchy;
        _lastRouteObstruction = $"{phase}: {hierarchy} ({body.GetType().Name}) bounds={body.bounds} " +
            $"offset={offset:R} move={displacement:R}";
#endif
    }

    private bool CanUseSideRoute(Vector3 sideLeg, Vector3 depthMove, Vector3 lateral)
    {
        return IsRouteClear(Vector3.zero, sideLeg) &&
            IsRouteClear(sideLeg, depthMove) &&
            IsRouteClear(sideLeg + depthMove, -sideLeg + lateral);
    }

    private static IEnumerable<Collider> GetRoutingBodies()
    {
        foreach (var wall in FezBackgroundWall.ActiveWalls)
            if (wall != null && wall.isActiveAndEnabled)
                foreach (var body in wall.GetBodyColliders())
                    if (body != null && body.enabled && !body.isTrigger && body.gameObject.activeInHierarchy)
                        yield return body;
        foreach (var platform in FezOneWayPlatform.ActivePlatforms)
            if (platform != null && platform.isActiveAndEnabled)
                foreach (var body in platform.GetSolidBodyColliders())
                    if (body.enabled && body.gameObject.activeInHierarchy) yield return body;
    }

    private void ExpandConnectedWallEdges(ref float minSide, ref float maxSide,
        Vector3 depthMove, Vector3 right)
    {
        Bounds player = _controller.bounds;
        Vector3 axis = DepthAxis;
        float startDepth = Vector3.Dot(player.center, axis);
        float radius = Mathf.Abs(axis.x) * player.extents.x + Mathf.Abs(axis.z) * player.extents.z;
        float endDepth = startDepth + Vector3.Dot(depthMove, axis);
        bool changed;
        do
        {
            changed = false;
            foreach (var body in GetRoutingBodies())
                {
                    if (!IsInCurrentRoutingRoom(body, axis)) continue;
                    Bounds bounds = body.bounds;
                    if (player.min.y >= bounds.max.y - GroundTolerance || player.max.y <= bounds.min.y + GroundTolerance ||
                        FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis) > startDepth + radius ||
                        FezBackgroundWall.ProjectBoundsDeepDepth(bounds, axis) < endDepth - radius) continue;
                    float center = Vector3.Dot(bounds.center, right);
                    float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                    float left = center - half, rightEdge = center + half;
                    if (left > maxSide + GroundTolerance || rightEdge < minSide - GroundTolerance) continue;
                    if (left < minSide - 0.001f) { minSide = left; changed = true; }
                    if (rightEdge > maxSide + 0.001f) { maxSide = rightEdge; changed = true; }
                }
        } while (changed);
    }

    private bool IsFullyClearAfterRotation()
    {
        if (!FezPlatformViewOcclusion.IsCharacterMostlyVisible(_controller, DepthAxis, 1f)) return false;
        const float clearance = 0.05f;
        Bounds player = _controller.bounds;
        Vector3 axis = DepthAxis;
        Vector3 right = Vector3.Cross(Vector3.up, axis);
        float playerHalf = Mathf.Abs(right.x) * player.extents.x + Mathf.Abs(right.z) * player.extents.z;
        float deep = Vector3.Dot(player.center, axis) +
            Mathf.Abs(axis.x) * player.extents.x + Mathf.Abs(axis.z) * player.extents.z;
        foreach (var body in GetRoutingBodies())
            {
                Bounds bounds = body.bounds;
                // A surface below the feet supports the player; it does not hide the silhouette.
                // Applying the lateral clearance vertically latched the hold on tiled floors.
                if (player.min.y >= bounds.max.y - GroundTolerance || player.max.y <= bounds.min.y + GroundTolerance ||
                    FezBackgroundWall.ProjectBoundsShallowDepth(bounds, axis) >= deep - clearance) continue;
                float half = Mathf.Abs(right.x) * bounds.extents.x + Mathf.Abs(right.z) * bounds.extents.z;
                if (Mathf.Abs(Vector3.Dot(player.center - bounds.center, right)) < half + playerHalf + clearance)
                    return false;
            }
        return true;
    }

    private void GetQueryCapsule(Vector3 offset, out Vector3 top, out Vector3 bottom, out float radius)
    {
        Vector3 scale = transform.lossyScale;
        float fullRadius = _controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float height = Mathf.Max(_controller.height * Mathf.Abs(scale.y), fullRadius * 2f);
        Vector3 center = transform.TransformPoint(_controller.center) + offset;
        float halfSegment = Mathf.Max(0f, height * 0.5f - fullRadius);
        // A small inset permits touching a floor/side without treating it as penetration.
        radius = Mathf.Max(0.001f, fullRadius - 0.005f);
        top = center + Vector3.up * halfSegment;
        bottom = center - Vector3.up * halfSegment;
    }

    private bool IsRouteObstacle(Collider other)
    {
        if (other == null || other == _controller || other.transform.IsChildOf(transform)) return false;
        // Depth proxies are query geometry. Real platform bodies still occupy route space.
        if (other.GetComponentInParent<FezDepthProxyFollower>() != null) return false;
        if (other.GetComponentInParent<FezBackgroundWall>() != null) return true;
        if (FezOneWayPlatform.TryResolveByCollider(other, out _))
        {
            // A standalone one-way platform has no parent proxy; test its real body as solid.
            return true;
        }
        return !Physics.GetIgnoreLayerCollision(gameObject.layer, other.gameObject.layer) &&
            !Physics.GetIgnoreCollision(_controller, other);
    }

    private bool IsRouteClear(Vector3 startOffset, Vector3 displacement)
    {
        GetQueryCapsule(startOffset, out var top, out var bottom, out float radius);
        float distance = displacement.magnitude;
        if (distance > 0.00001f)
        {
            int count;
            while (true)
            {
                count = Physics.CapsuleCastNonAlloc(top, bottom, radius, displacement / distance,
                    DepthSweepHits, distance, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                if (count < DepthSweepHits.Length) break;
                if (DepthSweepHits.Length >= 4096) return false;
                System.Array.Resize(ref DepthSweepHits, DepthSweepHits.Length * 2);
            }
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = DepthSweepHits[i];
                if (!IsRouteObstacle(hit.collider)) continue;
                // A horizontal route above a top cannot enter its solid body. Casts
                // near a touching floor can still return initial-overlap contacts.
                float lowestFoot = _controller.bounds.min.y +
                    Mathf.Min(startOffset.y, startOffset.y + displacement.y);
                if (Mathf.Abs(displacement.y) <= 0.0001f &&
                    lowestFoot >= hit.collider.bounds.max.y - 0.001f) continue;
                // Native skin contact can also appear as a zero-distance cast hit.
                // Moving along or away from that face is not entering an obstacle.
                // Endpoint penetration checks below still reject a blocked destination.
                if (hit.distance <= 0.0001f)
                {
                    Vector3 normal = hit.normal;
                    // Initial-overlap cast normals may simply oppose the cast direction.
                    // Ask the actual shapes for the surface's separation direction instead.
                    if (Physics.ComputePenetration(_controller, transform.position + startOffset,
                            transform.rotation, hit.collider, hit.collider.transform.position,
                            hit.collider.transform.rotation, out var separation, out _)) normal = separation;
                    if (normal.sqrMagnitude > 0.5f &&
                        Vector3.Dot(normal, displacement / distance) >= -0.0001f) continue;
                }
                DescribeRouteObstruction(hit.collider, startOffset, displacement, "sweep");
                return false;
            }
        }
        // Sweeps do not reliably report an initial overlap; validate both endpoints too.
        return IsRoutePositionClear(startOffset) && IsRoutePositionClear(startOffset + displacement);
    }

    private bool IsRoutePositionClear(Vector3 offset)
    {
        GetQueryCapsule(offset, out var top, out var bottom, out float radius);
        int count;
        while (true)
        {
            count = Physics.OverlapCapsuleNonAlloc(top, bottom, radius, RouteOverlaps,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (count < RouteOverlaps.Length) break;
            if (RouteOverlaps.Length >= 4096) return false;
            System.Array.Resize(ref RouteOverlaps, RouteOverlaps.Length * 2);
        }
        for (int i = 0; i < count; i++)
        {
            Collider other = RouteOverlaps[i];
            if (!IsRouteObstacle(other)) continue;
            // Overlap queries alone cannot distinguish controller skin contact from
            // penetration. Use the actual capsule at the proposed position so standing
            // on a floor or touching a wall does not require a jump before routing.
            Vector3 scale = transform.lossyScale;
            float contactTolerance = _controller.skinWidth *
                Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) + 0.005f;
            if (Physics.ComputePenetration(_controller, transform.position + offset, transform.rotation,
                    other, other.transform.position, other.transform.rotation,
                    out _, out float penetration) && penetration > contactTolerance)
            {
                DescribeRouteObstruction(other, offset, Vector3.zero, "overlap " + penetration);
                return false;
            }
        }
        return true;
    }

    private float ProjectedLandingMargin => LandingLateralMargin + projectedLandingEdgeForgiveness;

    private float SupportLateralMargin
    {
        get
        {
            if (_controller == null) return 0f;
            Vector3 scale = transform.lossyScale;
            return Mathf.Max(0f, _controller.radius *
                Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)) - 0.001f);
        }
    }

    private float LandingLateralMargin
    {
        get
        {
            if (_controller == null) return Mathf.Max(0f, extraLandingEdgeForgiveness);
            Vector3 scale = transform.lossyScale;
            float radius = _controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            return radius + Mathf.Max(0f, extraLandingEdgeForgiveness);
        }
    }

    private void OnDisable()
    {
        FezOneWayPlatform.ReleasePlayer(_controller);
        ClearSupport();
        IsGrounded = false;
        _verticalSpeed = _speed = _coyote = _jumpBuffer = _dropTime = 0f;
        _wasRotating = _holdDepthAfterOccludedRotation = false;
        _lastDepthAxis = DepthAxis;
        WalkingSpeed = 0f;
        IsDropThroughActiveStatic = false;
    }
}
