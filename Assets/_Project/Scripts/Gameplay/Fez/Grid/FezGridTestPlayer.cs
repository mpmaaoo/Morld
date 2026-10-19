using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 格子投影測試用角色。不使用 DepthProxy。
/// 左右走、跳躍；Q／E 轉 90 度後，用新的深度軸重查腳下那一格。
/// </summary>
[RequireComponent(typeof(CharacterController))]
public sealed class FezGridTestPlayer : MonoBehaviour
{
    [Header("移動（沿用 PlayerOld 手感）")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float jumpHeight = 1.3f;
    [SerializeField] private float gravity = -25f;
    [SerializeField] private float maxFallSpeed = 10f;
    [SerializeField] private float coyoteTime = 0.1f;
    [SerializeField] private float jumpBufferTime = 0.1f;
    [SerializeField] private float acceleration = 35f;
    [SerializeField] private float deceleration = 35f;
    [SerializeField] private float stickDeadzone = 0.25f;
    [SerializeField] private float downInputThreshold = -0.5f;

    [Header("手把（對齊舊 Ability：L1／R1）")]
    [SerializeField] private KeyCode rotateLeftPad = KeyCode.JoystickButton4;
    [SerializeField] private KeyCode rotateRightPad = KeyCode.JoystickButton5;

    [Header("格子投影")]
    [SerializeField] private float rotateDuration = 0.35f;
    [SerializeField] private float floorReach = 0.55f;

    private CharacterController _cc;
    private FezGridMap _map;
    private FezGridShell _shell;

    private const float DepthGlideDuration = 0.1f;

    private Vector3 _velocity;
    private float _currentSpeed;
    private float _coyoteCounter;
    private float _jumpBufferCounter;
    private bool _holdingCover;
    private bool _bypassingWall;
    private float _bypassFeetY;
    private FezGridBlock _bypassSupport;
    private bool _gliding;
    private float _glideFrom;
    private float _glideTo;
    private float _glideElapsed;
    private FezGridBlock _glideFloor;
    private FezGridBlock _glideSupport;
    private float _glideFeetY;
    private bool _rotating;
    private float _rotateElapsed;
    private Quaternion _fromRot;
    private Quaternion _toRot;

    private FezGridBlock _dropThroughBlock;
    private float _dropThroughUntil;
    private float _dropThroughStartY;
    private float _dropThroughRouteDepth;
    private bool _dropThroughHasRoute;

    /// <summary>最後一次穩穩踩過的一般方塊；掉出地圖時拉回這裡。</summary>
    private FezGridBlock _lastStoodBlock;

    private float _slideSpeed;
    private bool _knockbackActive;
    private const float KnockbackSlideSpeed = 8f;
    private const float KnockbackFriction = 12f;
    private const float KnockbackHopHeight = 0.55f;

    private readonly Transform[] _probeMarkers = new Transform[4];
    private readonly Renderer[] _probeRenderers = new Renderer[4];
    private readonly Vector3[] _probeWorld = new Vector3[4];
    private readonly bool[] _probeOccluded = new bool[4];
    private Material _probeClearMat;
    private Material _probeBlockedMat;

    public void Bind(FezGridMap map, FezGridShell shell)
    {
        _map = map;
        _shell = shell;
        EnsureProbeMarkers();
        var axes = FezGridProjection.FromYaw(transform.eulerAngles.y);
        ResolveDepth(transform.position, axes, 1.2f, false, true, null, false, 0f);
        if (FezGridProjection.TryResolveFloor(
                _map, transform.position, axes, floorReach, 1.6f, false, out var floor, out _))
            RememberStoodBlock(floor);
    }

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        // 高度只由地板決定。轉視角後碰撞體重擺時，上台階會把人頂起來。
        _cc.stepOffset = 0f;
    }

    private void LateUpdate()
    {
        UpdateProbeMarkers();
    }

    private void Update()
    {
        if (_rotating)
        {
            TickRotation();
            return;
        }

        if (!_knockbackActive)
        {
            if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(rotateLeftPad))
                BeginRotate(1f);
            else if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(rotateRightPad))
                BeginRotate(-1f);
        }

        if (_rotating)
            return;

        var axes = FezGridProjection.FromYaw(transform.eulerAngles.y);

        // 四角偵測：還遮著才繼續 hold。四角都亮＝離開遮擋，同一幀接繞行。
        var coveredNow = FezGridProjection.IsCovered(_map, transform.position, axes, 1.6f);
        var leavingCover = _holdingCover && !coveredNow;
        var hold = _holdingCover && coveredNow;

        FezGridBlock support = null;
        FezGridProjection.TryResolveFloor(
            _map, transform.position, axes, floorReach, 1.6f, hold, out support, out _);

        TickDropThrough(axes, support);

        var grounded = _cc.isGrounded;
        if (grounded)
        {
            _coyoteCounter = coyoteTime;
            if (_velocity.y < 0f && _dropThroughBlock == null)
                _velocity.y = -2f;
            RememberStoodBlock(support);
        }
        else
        {
            _coyoteCounter -= Time.deltaTime;
        }

        var input = ApplyAxisDeadzone(Input.GetAxisRaw("Horizontal"), stickDeadzone);
        var inputMagnitude = Mathf.Clamp01(Mathf.Abs(input));
        if (inputMagnitude > 0.01f)
            input = Mathf.Sign(input);
        else
            input = 0f;

        if (_knockbackActive)
        {
            input = 0f;
            inputMagnitude = 0f;
            _currentSpeed = 0f;
            _jumpBufferCounter = 0f;
            _slideSpeed = Mathf.MoveTowards(_slideSpeed, 0f, KnockbackFriction * Time.deltaTime);
            if (Mathf.Abs(_slideSpeed) <= 0.2f)
            {
                _slideSpeed = 0f;
                _knockbackActive = false;
            }
        }

        var targetSpeed = inputMagnitude * moveSpeed;
        var accel = inputMagnitude > 0.01f ? acceleration : deceleration;
        _currentSpeed = Mathf.MoveTowards(_currentSpeed, targetSpeed, accel * Time.deltaTime);
        var moveDir = inputMagnitude > 0.01f ? transform.right * input : Vector3.zero;
        var horizontal = _knockbackActive
            ? axes.lateral * _slideSpeed
            : moveDir * _currentSpeed;

        var jumpPressed = Input.GetButtonDown("Jump")
            || Input.GetKeyDown(KeyCode.Space)
            || Input.GetKeyDown(KeyCode.JoystickButton0)
            || Input.GetKeyDown(KeyCode.JoystickButton1);
        // 按住 S／下／搖桿下時禁止跳躍，只做下落穿過。
        var vertical = ApplyAxisDeadzone(Input.GetAxisRaw("Vertical"), stickDeadzone);
        var holdDrop = Input.GetKey(KeyCode.S)
            || Input.GetKey(KeyCode.DownArrow)
            || vertical <= downInputThreshold;
        if (_knockbackActive)
            jumpPressed = false;

        if (jumpPressed)
        {
            if (holdDrop && _coyoteCounter > 0f)
            {
                _jumpBufferCounter = 0f;
                TryBeginDropThrough(support, axes);
            }
            else if (_dropThroughBlock == null)
            {
                _jumpBufferCounter = jumpBufferTime;
            }
        }
        else
        {
            _jumpBufferCounter -= Time.deltaTime;
        }

        if (_jumpBufferCounter > 0f && _coyoteCounter > 0f && _dropThroughBlock == null && !holdDrop)
        {
            _jumpBufferCounter = 0f;
            _coyoteCounter = 0f;
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        _velocity.y += gravity * Time.deltaTime;
        if (maxFallSpeed > 0f)
            _velocity.y = Mathf.Max(_velocity.y, -maxFallSpeed);

        var motion = horizontal;
        motion.y = _velocity.y;

        // 下落穿過中：不要跑深度重算／Depenetrate，否則會被吸回原本深度或高度。
        if (_dropThroughBlock != null)
        {
            FezGridProjection.SetIgnoredFloor(_dropThroughBlock);
            if (_dropThroughHasRoute)
            {
                var now = FezGridProjection.DepthOf(transform.position, axes);
                if (Mathf.Abs(now - _dropThroughRouteDepth) > 0.02f)
                {
                    _cc.enabled = false;
                    transform.position = FezGridProjection.SnapToDepth(
                        transform.position, _dropThroughRouteDepth, axes);
                    _cc.enabled = true;
                }
            }

            if (_shell != null)
                _shell.RebuildForDepth(_map, axes, FezGridProjection.DepthOf(transform.position, axes), null);

            _cc.Move(motion * Time.deltaTime);
            TickDropThrough(axes, support);

            if (transform.position.y < -6f)
                RespawnOnLastStoodBlock(axes);

            return;
        }

        var predicted = transform.position + motion * Time.deltaTime;
        var query = predicted;
        if (!_holdingCover && Mathf.Abs(input) > 0.01f)
            query += transform.right * (input * 0.35f);

        var bypass = false;
        var routeDepth = 0f;
        var wasBypassing = _bypassingWall;
        if (!hold)
        {
            // 先看實際位置還在不在牆柱裡；不在就結束，避免預測點把人又吸回去。
            if (wasBypassing)
            {
                bypass = FezGridProjection.TryResolveWallBypass(
                    _map, transform.position, axes, 1.6f, true, out routeDepth);
            }

            if (!bypass)
            {
                // 前方探測或實際位置任一對到牆柱即可；人在牆後更深時也要能啟動，不必「撞到」牆。
                bypass = FezGridProjection.TryResolveWallBypass(
                    _map, query, axes, 1.6f, false, out routeDepth);
                if (!bypass)
                {
                    bypass = FezGridProjection.TryResolveWallBypass(
                        _map, transform.position, axes, 1.6f, false, out routeDepth);
                }
                else
                {
                    // 人已經在淺側、身體也不在牆柱上時，不要被前方探測點再吸回去。
                    var depthNow = FezGridProjection.DepthOf(transform.position, axes);
                    if (depthNow <= routeDepth + 0.12f
                        && !FezGridProjection.TryResolveWallBypass(
                            _map, transform.position, axes, 1.6f, false, out _))
                    {
                        bypass = false;
                    }
                }
            }

            // 四角已離開遮擋，但人還在更淺方塊後面：一般遮片繞行可能對不到高度，這裡補一刀。
            if (!bypass && leavingCover)
            {
                bypass = FezGridProjection.TryResolveLeaveCoverRoute(
                    _map, transform.position, axes, 1.6f, out routeDepth);
            }
        }

        if (bypass && !wasBypassing)
        {
            _bypassSupport = support;
            _bypassFeetY = support != null
                ? support.TopY + 0.08f
                : transform.position.y;
        }

        if (!bypass)
            _bypassSupport = null;

        _bypassingWall = bypass;

        if (!ResolveDepth(query, axes, floorReach, hold, false, support, bypass, routeDepth))
            ResolveDepth(transform.position, axes, floorReach, hold, false, support, false, 0f);

        var gliding = _gliding;
        ApplyDepthGlide(axes);
        // 繞牆鎖腳高只限「還在原本支撐格正上方」；一側向走出那格就該掉落。
        var overBypassSupport = _bypassSupport != null
            && IsStandingOverSupport(_bypassSupport, transform.position, axes);
        var ridingWall = _bypassingWall
            && !gliding
            && overBypassSupport
            && _velocity.y <= 0.01f
            && !_cc.isGrounded
            && transform.position.y <= _bypassFeetY + 0.25f;
        var lockFeet = ridingWall || (gliding && _velocity.y <= 0.01f);
        if (lockFeet)
        {
            _velocity.y = 0f;
            motion.y = 0f;
        }

        var yBefore = transform.position.y;
        var flags = _cc.Move(motion * Time.deltaTime);
        if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0f)
            _velocity.y = 0f;
        // 沒有在跳時，不讓碰撞把腳抬過目前站的地板。
        if (_velocity.y <= 0.01f && transform.position.y > yBefore + 0.001f)
        {
            var limit = yBefore;
            if (support != null && support.Solid)
                limit = Mathf.Max(yBefore, support.TopY);
            if (transform.position.y > limit + 0.001f)
            {
                var pinned = transform.position;
                pinned.y = limit;
                _cc.enabled = false;
                transform.position = pinned;
                _cc.enabled = true;
            }
        }

        if (lockFeet)
        {
            var feet = transform.position;
            feet.y = ridingWall ? _bypassFeetY : _glideFeetY;
            Teleport(feet, axes);
        }
        else if (_bypassingWall && _cc.isGrounded)
        {
            _bypassFeetY = transform.position.y;
            if (support != null)
                _bypassSupport = support;
        }

        // 站在頂面上時不往淺側推。轉視角後旁邊的方塊會被算進身體，這一推就是往淺處彈一下。
        if (!FezGridProjection.IsFeetPlantedOn(support, transform.position))
            Teleport(transform.position, axes);

        if (transform.position.y < -6f)
            RespawnOnLastStoodBlock(axes);
    }

    /// <summary>
    /// 受擊沿畫面左右滑開，並只在這次擊退開始時往上彈一次。深度軸不動。
    /// 滑行中不能操作；還貼在敵人身上時會維持側向速度，但不會再彈第二次。
    /// </summary>
    public void KnockbackFrom(Vector3 sourcePosition)
    {
        var axes = FezGridProjection.FromYaw(transform.eulerAngles.y);
        var away = Vector3.Dot(transform.position - sourcePosition, axes.lateral);
        var sign = Mathf.Abs(away) < 0.04f
            ? (_slideSpeed > 0.1f || _currentSpeed > 0.1f ? -1f : 1f)
            : Mathf.Sign(away);
        _slideSpeed = sign * KnockbackSlideSpeed;
        _currentSpeed = 0f;
        if (_knockbackActive)
            return;

        _knockbackActive = true;
        _jumpBufferCounter = 0f;
        _coyoteCounter = 0f;
        _velocity.y = Mathf.Sqrt(KnockbackHopHeight * -2f * gravity);
    }

    private void RememberStoodBlock(FezGridBlock support)
    {
        if (support == null || !support.Solid)
            return;
        if (_dropThroughBlock != null)
            return;
        _lastStoodBlock = support;
    }

    private void RespawnOnLastStoodBlock(FezGridProjection.Axes axes)
    {
        _holdingCover = false;
        _bypassingWall = false;
        _bypassSupport = null;
        _gliding = false;
        _velocity = Vector3.zero;
        _currentSpeed = 0f;
        EndDropThrough();

        Vector3 feet;
        if (_lastStoodBlock != null && _lastStoodBlock.Solid)
        {
            feet = new Vector3(
                _lastStoodBlock.Center.x,
                _lastStoodBlock.TopY + 0.08f,
                _lastStoodBlock.Center.z);
        }
        else
        {
            feet = new Vector3(0.5f, 1.08f, 0.5f);
        }

        var safe = FezGridProjection.DepenetrateShallow(_map, feet, axes, 1.6f);
        _cc.enabled = false;
        transform.position = safe;
        _cc.enabled = true;

        ResolveDepth(transform.position, axes, 1.2f, false, true, _lastStoodBlock, false, 0f);
    }

    private void TryBeginDropThrough(FezGridBlock support, FezGridProjection.Axes axes)
    {
        if (support == null || !support.Solid || _dropThroughBlock != null)
            return;

        _dropThroughBlock = support;
        _dropThroughStartY = transform.position.y;
        _dropThroughUntil = Time.time + 2f;
        FezGridProjection.SetIgnoredFloor(support);
        _holdingCover = false;
        _gliding = false;
        _bypassingWall = false;
        _bypassSupport = null;
        // 往下推開，避免還沒掉就被頂板接回去。
        _velocity.y = Mathf.Min(_velocity.y, -4f);

        // 繞過深度只在開始時算一次並鎖定，下落過程不再被 ResolveDepth 拉回。
        _dropThroughHasRoute = FezGridProjection.TryResolveDropThroughRoute(
            _map, transform.position, axes, 1.6f, support, out _dropThroughRouteDepth);
        if (_dropThroughHasRoute)
        {
            _cc.enabled = false;
            transform.position = FezGridProjection.SnapToDepth(
                transform.position, _dropThroughRouteDepth, axes);
            _cc.enabled = true;
        }

        if (_shell != null)
        {
            var depth = FezGridProjection.DepthOf(transform.position, axes);
            _shell.RebuildForDepth(_map, axes, depth, null);
        }
    }

    private void TickDropThrough(FezGridProjection.Axes axes, FezGridBlock support)
    {
        if (_dropThroughBlock == null)
            return;

        FezGridProjection.SetIgnoredFloor(_dropThroughBlock);

        // 至少下落 0.3 格（或腳底低於頂面 0.3）才重新開啟頂面碰撞。
        const float minDrop = 0.3f;
        var fallen = _dropThroughStartY - transform.position.y;
        var belowTop = transform.position.y <= _dropThroughBlock.TopY - minDrop;
        var clearedHeight = fallen >= minDrop || belowTop;

        if (!clearedHeight && Time.time < _dropThroughUntil)
            return;

        EndDropThrough();
        if (_shell != null)
        {
            var depth = FezGridProjection.DepthOf(transform.position, axes);
            _shell.RebuildForDepth(_map, axes, depth, support);
        }
    }

    private void EndDropThrough()
    {
        _dropThroughBlock = null;
        _dropThroughUntil = 0f;
        _dropThroughStartY = 0f;
        _dropThroughHasRoute = false;
        FezGridProjection.ClearIgnoredFloor();
    }

    private static bool IsStandingOverSupport(
        FezGridBlock support,
        Vector3 feet,
        FezGridProjection.Axes axes)
    {
        if (support == null)
            return false;

        // 略寬於半格：走出支撐格後不會立刻掉，但仍遠小於「整面牆都離開才掉」。
        var lateral = Mathf.Abs(
            FezGridProjection.LateralOf(feet, axes)
            - FezGridProjection.LateralOf(support.Center, axes));
        return lateral <= 0.78f;
    }

    private static float ApplyAxisDeadzone(float value, float deadzone)
    {
        var dz = Mathf.Clamp01(deadzone);
        if (Mathf.Abs(value) < dz)
            return 0f;
        return value;
    }

    private void BeginRotate(float direction)
    {
        _rotating = true;
        _rotateElapsed = 0f;
        _fromRot = transform.rotation;
        var targetY = Mathf.Round((_fromRot.eulerAngles.y + 90f * Mathf.Sign(direction)) / 90f) * 90f;
        _toRot = Quaternion.Euler(0f, targetY, 0f);
        _velocity = Vector3.zero;
        _currentSpeed = 0f;
        _jumpBufferCounter = 0f;
    }

    private void TickRotation()
    {
        _rotateElapsed += Time.deltaTime;
        var t = Mathf.Clamp01(_rotateElapsed / Mathf.Max(0.05f, rotateDuration));
        transform.rotation = Quaternion.Slerp(_fromRot, _toRot, t);
        if (t < 1f)
            return;

        _rotating = false;
        transform.rotation = _toRot;

        var axes = FezGridProjection.FromYaw(transform.eulerAngles.y);
        _holdingCover = FezGridProjection.IsCovered(_map, transform.position, axes, 1.6f);
        _bypassingWall = false;
        _bypassSupport = null;
        EndDropThrough();
        ResolveDepth(transform.position, axes, 1.2f, _holdingCover, true, null, false, 0f);
    }

    private bool ResolveDepth(
        Vector3 feet,
        FezGridProjection.Axes axes,
        float reach,
        bool holdDepth,
        bool instant,
        FezGridBlock support,
        bool wallBypass,
        float wallRoute)
    {
        var resolved = FezGridProjection.TryResolveFloor(
            _map, feet, axes, reach, 1.6f, holdDepth, out var floor, out var covered);
        if (wallBypass && floor != null)
        {
            var floorDepth = FezGridProjection.DepthOf(floor.Center, axes);
            // 腳下已有更淺（或等深）的地板時，不必再吊在牆的繞行深度上。
            if (floorDepth <= wallRoute + 0.05f)
            {
                wallBypass = false;
                _bypassingWall = false;
            }
        }

        if (!resolved && !wallBypass)
            return false;

        // 被遮擋只留在該深度，不隱藏玩家。
        _holdingCover = covered && !wallBypass;

        var targetDepth = wallBypass
            ? wallRoute
            : FezGridProjection.DepthOf(floor.Center, axes);
        // 轉視角時人已在格子中心或更深的半格，就留在原地。
        // 從下往上跳、只踩到上面那格的淺側半格時，收到這一格中心。
        var pullOntoBlock = false;
        if (!wallBypass && FezGridProjection.IsFeetPlantedOn(floor, transform.position))
        {
            var floorDepth = FezGridProjection.DepthOf(floor.Center, axes);
            var playerDepth = FezGridProjection.DepthOf(transform.position, axes);
            if (playerDepth < floorDepth - 0.08f)
            {
                targetDepth = floorDepth;
                pullOntoBlock = true;
            }
            else
            {
                targetDepth = playerDepth;
            }
        }

        // 目標深度會讓身體嵌進實體時：繞行取消；一般換深度則留在原處。
        var probe = FezGridProjection.SnapToDepth(transform.position, targetDepth, axes);
        if (FezGridProjection.BodyBlockedAtDepth(_map, probe, axes, targetDepth, 1.6f))
        {
            if (wallBypass)
            {
                wallBypass = false;
                _bypassingWall = false;
                if (!resolved)
                    return false;
                targetDepth = FezGridProjection.DepthOf(floor.Center, axes);
                probe = FezGridProjection.SnapToDepth(transform.position, targetDepth, axes);
                if (FezGridProjection.BodyBlockedAtDepth(_map, probe, axes, targetDepth, 1.6f))
                    return false;
            }
            else
            {
                return false;
            }
        }

        var currentDepth = FezGridProjection.DepthOf(transform.position, axes);
        var depthDiff = Mathf.Abs(targetDepth - currentDepth);
        var aboveFloor = floor != null && transform.position.y > floor.TopY + 0.15f;
        var falling = _velocity.y < -3.5f;
        if (instant || holdDepth || aboveFloor || falling || pullOntoBlock || depthDiff < 0.08f)
        {
            _gliding = false;
            RebuildShell(axes, targetDepth, support);
            if (depthDiff >= 0.02f)
                Teleport(probe, axes);
            else if (!FezGridProjection.IsFeetPlantedOn(floor, transform.position))
                Teleport(transform.position, axes);
            return true;
        }

        if (!_gliding || Mathf.Abs(_glideTo - targetDepth) > 0.05f)
        {
            _gliding = true;
            _glideElapsed = 0f;
            _glideFrom = currentDepth;
            _glideTo = targetDepth;
            _glideFloor = wallBypass ? support : floor;
            _glideSupport = support;
            _glideFeetY = transform.position.y;
        }

        RebuildShell(axes, targetDepth, _glideSupport);
        return true;
    }

    private void RebuildShell(
        FezGridProjection.Axes axes,
        float depth,
        FezGridBlock support)
    {
        if (_shell == null)
            return;

        // 平台與邊界牆保留。繞行中只拿掉遮片牆，避免整層清空後嵌進淺側地形。
        _shell.RebuildForDepth(_map, axes, depth, support, _bypassingWall);
    }

    private void ApplyDepthGlide(FezGridProjection.Axes axes)
    {
        if (!_gliding)
            return;

        _glideElapsed += Time.deltaTime;
        var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_glideElapsed / DepthGlideDuration));
        var depth = Mathf.Lerp(_glideFrom, _glideTo, t);
        var current = FezGridProjection.DepthOf(transform.position, axes);
        var next = transform.position + axes.depth * (depth - current);
        // 滑移途中若會嵌進實體，停在上一個安全深度並結束滑移。
        if (FezGridProjection.BodyBlockedAtDepth(_map, next, axes, depth, 1.6f))
        {
            _gliding = false;
            Teleport(transform.position, axes);
            return;
        }

        Teleport(next, axes);
        if (t >= 1f)
            _gliding = false;
    }

    private void Teleport(Vector3 feet, FezGridProjection.Axes axes)
    {
        var safe = FezGridProjection.DepenetrateShallow(_map, feet, axes, 1.6f);
        if ((safe - transform.position).sqrMagnitude < 0.000001f)
            return;

        _cc.enabled = false;
        transform.position = safe;
        _cc.enabled = true;
    }

    private void OnGUI()
    {
        var axes = FezGridProjection.FromYaw(transform.eulerAngles.y);
        var depthName = axes.depth == Vector3.forward ? "+Z"
            : axes.depth == Vector3.right ? "+X"
            : axes.depth == Vector3.back ? "-Z"
            : "-X";

        ScreenHud.Draw(new Rect(16f, 16f, 760f, 164f),
            "格子投影測試（不影響 SampleScene）\n" +
            "A／D／左搖桿移動，空白／A／B 跳躍，Q／E／L1／R1 轉 90 度\n" +
            "S／下／搖桿下＋跳躍＝下落穿過\n" +
            $"目前深度軸 {depthName}\n" +
            "轉視角後依四角偵測點判定遮擋（綠＝未遮、紅＝被遮）。\n" +
            "一般方塊頂面可踩、側面可繞。遮片牆平常硬擋，繞行中暫時不碰撞。\n" +
            "粉紅邊界牆＝看得見才硬擋、看不見可穿。");
    }

    private void EnsureProbeMarkers()
    {
        if (_probeMarkers[0] != null)
            return;

        var shader = Shader.Find("Unlit/Color");
        _probeClearMat = shader != null
            ? new Material(shader) { color = new Color(0.2f, 1f, 0.35f) }
            : null;
        _probeBlockedMat = shader != null
            ? new Material(shader) { color = new Color(1f, 0.2f, 0.2f) }
            : null;

        var names = new[] { "CoverProbe_BL", "CoverProbe_BR", "CoverProbe_TL", "CoverProbe_TR" };
        for (var i = 0; i < 4; i++)
        {
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = names[i];
            ball.transform.SetParent(transform, false);
            ball.transform.localScale = Vector3.one * 0.16f;
            var col = ball.GetComponent<Collider>();
            if (col != null)
                Destroy(col);

            _probeMarkers[i] = ball.transform;
            _probeRenderers[i] = ball.GetComponent<Renderer>();
            if (_probeRenderers[i] != null && _probeClearMat != null)
                _probeRenderers[i].sharedMaterial = _probeClearMat;
        }
    }

    private void UpdateProbeMarkers()
    {
        if (_map == null)
            return;

        EnsureProbeMarkers();
        var axes = FezGridProjection.FromYaw(transform.eulerAngles.y);
        FezGridProjection.GetCoverProbeWorldPoints(
            _map, transform.position, axes, 1.6f, _probeWorld, _probeOccluded);

        for (var i = 0; i < 4; i++)
        {
            if (_probeMarkers[i] == null)
                continue;

            _probeMarkers[i].position = _probeWorld[i];
            if (_probeRenderers[i] == null)
                continue;

            var mat = _probeOccluded[i] ? _probeBlockedMat : _probeClearMat;
            if (mat != null)
                _probeRenderers[i].sharedMaterial = mat;
        }
    }
}
