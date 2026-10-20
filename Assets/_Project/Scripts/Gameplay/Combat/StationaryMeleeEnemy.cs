using UnityEngine;
using UnityEngine.Events;

/// <summary>Gameplay root stays fixed; all replaceable art belongs under VisualRoot.</summary>
[RequireComponent(typeof(Health), typeof(CharacterController))]
public sealed class StationaryMeleeEnemy : MonoBehaviour
{
    public enum AttackState { Idle, Windup, Attack, Recovery, Dead }
    [Header("種類設定（選填，未指定則沿用原有參數）")]
    public EnemyDefinition definition;
    [Tooltip("選填：日後新增追擊、遠攻等行為，會取代內建巡邏及可選擇接管出手。")]
    public EnemyBehaviour customBehaviour;
    public EnemyNavigationArea navigationArea;
    [HideInInspector] public Transform[] patrolPoints;
    public bool CanScanForTarget => customBehaviour == null || customBehaviour.AllowsAttack;
    [Header("3D 視野：初始面向生成點藍色 Z 軸")]
    public Transform eyeAnchor;
    [Min(0f)] public float sightDistance = 8f;
    [Range(1f, 360f)] public float viewAngle = 110f;
    [Range(1f, 180f)] public float verticalViewAngle = 100f;
    [Min(0f)] public float sightMemory = 1.5f;
    public bool scanWhileIdle = true;
    [Range(0f, 180f)] public float scanHalfAngle = 50f;
    [Min(0.1f)] public float scanPeriod = 4f;
    [Min(0.01f)] public float turnSmoothTime = 0.2f;
    [Min(1f)] public float maxTurnSpeed = 180f;
    [Min(0.01f)] public float scanSmoothTime = 0.25f;
    private float desiredYaw, turnVelocity, scanVelocity;
    private float scanPhase, scanYaw;
    public Vector3 SightForward => Quaternion.AngleAxis(scanYaw, Vector3.up) * transform.forward;
    public bool HasVisualContact { get; private set; }
    public Vector3 LastSeenFeet { get; private set; }
    public bool HasRememberedTarget => Time.time < lastSeenUntil;
    public void RememberAttackOrigin(Vector3 position)
    {
        LastSeenFeet = position;
        lastSeenUntil = Time.time + sightMemory;
        if (customBehaviour is EnemyChaseBehaviour chase) chase.RememberAttackOrigin(position);
    }
    private float lastSeenUntil = float.NegativeInfinity;
    [Header("Combat (world units / seconds)")]
    [Min(0.1f)] public float reach = 1.4f;
    [Min(0.1f)] public float verticalReach = 0.8f;
    [Min(0.05f)] public float windup = 0.65f;
    [Min(0.05f)] public float recovery = 1f;
    [Min(0.01f)] public float attackDuration = 0.12f;
    [Min(0f)] public float attackInterval = 2f;
    [Min(0f)] public float maxAttackDepthGap = 3f;
    public float TargetDepthGap => targetBody != null && body != null
        ? Mathf.Abs(Vector3.Dot(targetBody.bounds.center - body.bounds.center, ViewAxis)) : 0f;
    public bool MustCloseDepthBeforeAttack { get; private set; }
    private bool depthRecoveryCanMove;
    public bool ContinueDepthPursuit => MustCloseDepthBeforeAttack || (depthRecoveryCanMove && State == AttackState.Recovery);
    public LayerMask wallMask = ~0;
    [Header("Replaceable presentation — optional")]
    public Transform visualRoot;
    public Transform warningAnchor;
    [Tooltip("Replace the placeholder with a sprite, mesh or UI object. Shown only during windup.")]
    public GameObject warningIcon;
    [Tooltip("Optional Animator parameters: CombatState (int), Facing (float).")]
    public Animator animator;
    public UnityEvent onWindup = new UnityEvent();
    public UnityEvent onAttack = new UnityEvent();
    public UnityEvent onRecovery = new UnityEvent();
    public UnityEvent onDeath = new UnityEvent();
    public AttackState State { get; private set; }
    public float Facing { get; private set; } = 1f;
    private Health health, targetHealth;
    private CharacterController body, targetBody;
    private PlayerController target;
    private FezRotationOcclusionGate gate;
    private Vector3 attackAxis;
    private float deadline, searchAt, fallSpeed;
    private bool generatedIcon;
    private EnemyHitResponse hitResponse;
    public static readonly System.Collections.Generic.List<StationaryMeleeEnemy> ActiveEnemies = new System.Collections.Generic.List<StationaryMeleeEnemy>();
    private EnemyTuning.Movement movement;
    private Vector3 patrolOrigin, patrolRight;
    private float patrolSpeed, patrolHalfDistance, patrolSign = 1f, nextAttackAt;
    private bool initialized;
    public Transform Target => target != null ? target.transform : null;
    public Vector3 CommittedAttackAxis => attackAxis;
    public EnemyTuning.Movement MovementMode => movement;

    // Spawn points call this immediately after Instantiate, before the first Update.
    public void Configure(EnemyDefinition type, EnemyTuning localOverride = null)
    {
        definition = type;
        var settings = localOverride ?? (type != null ? type.settings : null);
        if (settings != null)
        {
            health.SetMax(Mathf.Max(1, settings.health), true);
            var response = GetComponent<EnemyHitResponse>();
            if (settings.overrideHitResponse && response != null)
            {
                response.pause = Mathf.Max(0f, settings.hitPause);
                response.pushSpeed = Mathf.Max(0f, settings.hitPushSpeed);
                response.launchSpeed = Mathf.Max(0f, settings.hitLaunchSpeed);
                response.bounceSpeed = Mathf.Max(0f, settings.hitBounceSpeed);
                response.friction = Mathf.Max(0.1f, settings.hitFriction);
                response.showHealthBar = settings.showHealthBar;
            }
            reach = Mathf.Max(0.1f, settings.reach);
            verticalReach = Mathf.Max(0.1f, settings.verticalReach);
            windup = Mathf.Max(0.05f, settings.windup);
            recovery = Mathf.Max(0.05f, settings.recovery);
            attackDuration = Mathf.Max(0.01f, settings.attackDuration);
            attackInterval = Mathf.Max(0f, settings.attackInterval);
            maxAttackDepthGap = Mathf.Max(0f, settings.maxAttackDepthGap);
            movement = settings.movement;
            patrolSpeed = Mathf.Max(0f, settings.patrolSpeed);
            patrolHalfDistance = Mathf.Max(0f, settings.patrolHalfDistance);
            sightDistance = Mathf.Max(0f, settings.detectionDistance);
            viewAngle = Mathf.Clamp(settings.viewAngle, 1f, 360f);
            verticalViewAngle = Mathf.Clamp(settings.verticalViewAngle, 1f, 180f);
            sightMemory = Mathf.Max(0f, settings.sightMemory);
            scanWhileIdle = settings.scanWhileIdle;
            scanHalfAngle = Mathf.Clamp(settings.scanHalfAngle, 0f, 180f);
            scanPeriod = Mathf.Max(0.1f, settings.scanPeriod);
            turnSmoothTime = Mathf.Max(0.01f, settings.turnSmoothTime);
            maxTurnSpeed = Mathf.Max(1f, settings.maxTurnSpeed);
            scanSmoothTime = Mathf.Max(0.01f, settings.scanSmoothTime);
        }
        patrolOrigin = transform.position;
        patrolRight = transform.right;
        initialized = true;
        if (movement == EnemyTuning.Movement.Chase && customBehaviour == null)
        {
            var chase = gameObject.AddComponent<EnemyChaseBehaviour>();
            customBehaviour = chase;
        }
        if (customBehaviour is EnemyChaseBehaviour chaseBehaviour) chaseBehaviour.SetTuning(settings ?? new EnemyTuning());
        if (customBehaviour != null) customBehaviour.Initialize(this);
    }

    private void Start() { if (!initialized) Configure(definition); }

    private void Awake()
    {
        health = GetComponent<Health>();
        desiredYaw = transform.eulerAngles.y;
        body = GetComponent<CharacterController>();
        if (GetComponent<EnemyPlayerContact>() == null) gameObject.AddComponent<EnemyPlayerContact>();
        if (GetComponent<EnemyDepthPresentation>() == null) gameObject.AddComponent<EnemyDepthPresentation>();
        hitResponse = GetComponent<EnemyHitResponse>();
        if (hitResponse == null) hitResponse = gameObject.AddComponent<EnemyHitResponse>();
        // Projection shells exist only for the player. Enemy motion uses real world solids.
        foreach (var proxy in FindObjectsOfType<FezDepthProxyFollower>())
            foreach (var collider in proxy.GetComponentsInChildren<Collider>(true))
                if (collider != body) Physics.IgnoreCollision(body, collider, true);
        if (warningAnchor == null)
        {
            warningAnchor = new GameObject("WarningAnchor").transform;
            warningAnchor.SetParent(transform, false);
            warningAnchor.localPosition = Vector3.up * (body.center.y + body.height * 0.5f + 0.3f);
        }
        if (warningIcon == null)
        {
            warningIcon = new GameObject("Placeholder_Warning_ReplaceWithArt");
            warningIcon.transform.SetParent(warningAnchor, false);
            var text = warningIcon.AddComponent<TextMesh>();
            text.text = "!";
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 64;
            text.characterSize = 0.12f;
            text.color = Color.yellow;
            generatedIcon = true;
        }
        warningIcon.SetActive(false);
    }

    private void OnEnable() { if (!ActiveEnemies.Contains(this)) ActiveEnemies.Add(this); }
    private void OnDisable() { ActiveEnemies.Remove(this); if (warningIcon != null) warningIcon.SetActive(false); }

    private Vector3 ViewAxis => FezWorldViewState.Active != null ? FezWorldViewState.Active.DepthAxisWorld : Vector3.forward;

    private void Update()
    {
        using (EnemyUpdateProfile.Auto()) UpdateEnemy();
    }

    private static readonly Unity.Profiling.ProfilerMarker EnemyUpdateProfile = new Unity.Profiling.ProfilerMarker("Morld.EnemyUpdate");
    private void UpdateEnemy()
    {
        bool reacting = hitResponse != null && hitResponse.IsActive;
        if (body.enabled && reacting) hitResponse.SimulateMotion(Time.deltaTime);
        else if (body.enabled)
        {
            fallSpeed = body.isGrounded ? -2f : Mathf.Max(fallSpeed - 25f * Time.deltaTime, -20f);
            body.Move(Vector3.up * (fallSpeed * Time.deltaTime));
        }
        if (health.IsDead)
        {
            if (State != AttackState.Dead) { ChangeState(AttackState.Dead); onDeath.Invoke(); }
            return;
        }
        if (reacting) return;
        if (target == null && Time.time >= searchAt)
        {
            searchAt = Time.time + 0.5f;
            target = FindObjectOfType<PlayerController>();
            if (target != null)
            {
                targetBody = target.GetComponent<CharacterController>();
                targetHealth = target.GetComponent<Health>();
                gate = target.GetComponent<FezRotationOcclusionGate>();
            }
        }
        bool valid = target != null && targetBody != null && targetBody.enabled && targetHealth != null && !targetHealth.IsDead;
        bool rotating = gate != null && gate.IsRotationInProgress;
        if (valid && !rotating)
        {
            // Enter at 3, leave slightly inside the boundary to prevent attack/chase chatter.
            float release = Mathf.Max(0f, maxAttackDepthGap - 0.25f);
            MustCloseDepthBeforeAttack = MustCloseDepthBeforeAttack ? TargetDepthGap > release
                : EnemyPursuitRules.ShouldPrioritizeDepth(TargetDepthGap, maxAttackDepthGap);
        }
        else if (!valid) MustCloseDepthBeforeAttack = false;
        bool ownsLook = customBehaviour != null && customBehaviour.OwnsLookDirection;
        bool scanning = scanWhileIdle && !ownsLook && State == AttackState.Idle && !HasVisualContact && !HasRememberedTarget && !rotating;
        float desiredScan = 0f;
        if (scanning)
        {
            scanPhase += Time.deltaTime * Mathf.PI * 2f / Mathf.Max(0.1f, scanPeriod);
            desiredScan = Mathf.Sin(scanPhase) * scanHalfAngle;
        }
        else scanPhase = 0f;
        // Ease back to centre instead of snapping when attention changes.
        scanYaw = Mathf.SmoothDampAngle(scanYaw, desiredScan, ref scanVelocity,
            Mathf.Max(0.01f, scanSmoothTime), maxTurnSpeed, Time.deltaTime);
        HasVisualContact = valid && !rotating && CanSeeTarget();
        if (HasVisualContact)
        {
            LastSeenFeet = targetBody.bounds.center;
            LastSeenFeet = new Vector3(LastSeenFeet.x, targetBody.bounds.min.y, LastSeenFeet.z);
            lastSeenUntil = Time.time + sightMemory;
            if (State == AttackState.Idle && CanScanForTarget && !ownsLook)
            {
                Vector3 direction = Vector3.ProjectOnPlane(targetBody.bounds.center - body.bounds.center, Vector3.up);
                FaceDirection(direction);
                scanPhase = 0f;
            }
        }
        // Patrol must continue before a player spawns; custom strategies accept a null Target.
        if (valid && !rotating && MustCloseDepthBeforeAttack)
        {
            CancelAttackForReturn();
            if (State == AttackState.Recovery) depthRecoveryCanMove = true;
        }
        if (!rotating) TickBehaviour();
        if (!valid || rotating || (State == AttackState.Windup && Vector3.Dot(ViewAxis, attackAxis) < 0.99f))
        {
            if (State == AttackState.Windup || State == AttackState.Attack) BeginRecovery();
            return;
        }
        bool canLocateTarget = HasVisualContact || (customBehaviour != null && customBehaviour.CanAttackWithoutSight);
        bool proximity = IsWithinProximityAttackRange();
        bool mayStart = (proximity && (customBehaviour == null || customBehaviour.AllowsProximityAttack))
            || (CanScanForTarget && canLocateTarget && InRange(ViewAxis, false) && ClearLine());
        if (State == AttackState.Idle && !MustCloseDepthBeforeAttack && mayStart && Time.time >= nextAttackAt)
        {
            attackAxis = ViewAxis;
            Facing = Mathf.Sign(Vector3.Dot(targetBody.bounds.center - body.bounds.center, Vector3.Cross(Vector3.up, attackAxis)));
            ChangeState(AttackState.Windup);
            deadline = Time.time + windup;
            nextAttackAt = Time.time + attackInterval;
            onWindup.Invoke();
        }
        else if (State == AttackState.Windup && Time.time >= deadline)
        {
            ChangeState(AttackState.Attack);
            deadline = Time.time + attackDuration;
            // One committed hit. Art/animation events never apply additional damage.
            bool handled = customBehaviour != null && customBehaviour.PerformAttack(this, targetHealth);
            if (!handled && (IsWithinProximityAttackRange() || (InRange(attackAxis, true) && ClearLine())))
                targetHealth.Damage(1, body.bounds.center);
            onAttack.Invoke();
        }
        else if (State == AttackState.Attack && Time.time >= deadline) BeginRecovery();
        else if (State == AttackState.Recovery && Time.time >= deadline) ChangeState(AttackState.Idle);
    }

    private void LateUpdate()
    {
        // Gaze has priority over route direction, patrol facing and observation steering.
        // Attack direction remains committed separately in attackAxis/Facing.
        if (HasVisualContact && targetBody != null && CanScanForTarget
            && (gate == null || !gate.IsRotationInProgress))
            FaceDirection(targetBody.bounds.center - body.bounds.center);
        if (health != null && !health.IsDead)
        {
            float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, desiredYaw, ref turnVelocity,
                Mathf.Max(0.01f, turnSmoothTime), Mathf.Max(1f, maxTurnSpeed), Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
        if (generatedIcon && warningIcon != null && warningIcon.activeSelf && Camera.main != null)
            warningIcon.transform.rotation = Camera.main.transform.rotation;
    }

    private void TickBehaviour()
    {
        if (customBehaviour != null) customBehaviour.Tick(this, Time.deltaTime);
        else if (State == AttackState.Idle && movement == EnemyTuning.Movement.Patrol && patrolHalfDistance > 0f && patrolSpeed > 0f)
        {
            float remaining = patrolHalfDistance - Vector3.Dot(transform.position - patrolOrigin, patrolRight) * patrolSign;
            float distance = Mathf.Min(patrolSpeed * Mathf.Min(Time.deltaTime, 0.05f), Mathf.Max(0f, remaining));
            if (remaining <= 0.001f || !TryMoveOnGround(patrolRight * (patrolSign * distance))) patrolSign = -patrolSign;
        }
    }

    /// <summary>Ground movement stays physical, stops at walls and ledges; no FEZ depth teleport.</summary>
    public bool TryMoveOnGround(Vector3 displacement)
    {
        displacement.y = 0f;
        float distance = displacement.magnitude;
        if (!body.enabled || !body.isGrounded || distance < 0.00001f) return false;
        Bounds bounds = body.bounds;
        Vector3 direction = displacement / distance;
        float radius = Mathf.Min(bounds.extents.x, bounds.extents.z);
        Vector3 foot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        if (BlockedGroundCast(foot + Vector3.up * (radius + 0.05f), direction, radius + distance + 0.03f)) return false;
        Vector3 probe = foot + direction * (radius + distance + 0.04f) + Vector3.up * 0.12f;
        bool supported = false;
        foreach (var hit in Physics.RaycastAll(probe, Vector3.down, 0.3f, wallMask, QueryTriggerInteraction.Ignore))
        {
            if (IsOwnOrProxy(hit.collider) || hit.normal.y < 0.5f) continue;
            supported = true;
            break;
        }
        if (!supported) return false;
        Vector3 before = transform.position;
        body.Move(displacement);
        bool moved = Vector3.Dot(transform.position - before, direction) >= distance * 0.5f;
        if (moved) FaceDirection(direction);
        return moved;
    }

    public void FaceDirection(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.00001f) desiredYaw = Quaternion.LookRotation(direction, Vector3.up).eulerAngles.y;
    }
    public void FaceRotation(Quaternion rotation) { desiredYaw = rotation.eulerAngles.y; }

    private bool CanSeeTarget()
    {
        Vector3 eye = eyeAnchor != null ? eyeAnchor.position : body.bounds.center + Vector3.up * body.bounds.extents.y * 0.4f;
        Vector3 delta = targetBody.bounds.center - eye;
        if (delta.sqrMagnitude > sightDistance * sightDistance) return false;
        Vector3 planar = Vector3.ProjectOnPlane(delta, Vector3.up);
        if (planar.sqrMagnitude > 0.00001f && Vector3.Angle(SightForward, planar) > viewAngle * 0.5f) return false;
        if (Mathf.Abs(Mathf.Atan2(delta.y, planar.magnitude) * Mathf.Rad2Deg) > verticalViewAngle * 0.5f) return false;
        // Sample centre and upper torso, never a hidden target's live position through a wall.
        return SightRayClear(eye, targetBody.bounds.center)
            || SightRayClear(eye, targetBody.bounds.center + Vector3.up * targetBody.bounds.extents.y * 0.6f);
    }
    private bool SightRayClear(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        if (delta.sqrMagnitude < 0.00001f) return true;
        foreach (var hit in Physics.RaycastAll(from, delta.normalized, delta.magnitude, wallMask, QueryTriggerInteraction.Ignore))
        {
            if (IsOwnOrProxy(hit.collider) || hit.collider.transform.IsChildOf(target.transform)) continue;
            return false;
        }
        return true;
    }

    private bool IsOwnOrProxy(Collider other) => other.transform.IsChildOf(transform)
        || other.GetComponentInParent<FezDepthProxyFollower>() != null;

    private bool BlockedGroundCast(Vector3 origin, Vector3 direction, float distance)
    {
        foreach (var hit in Physics.RaycastAll(origin, direction, distance, wallMask, QueryTriggerInteraction.Ignore))
            if (!IsOwnOrProxy(hit.collider)) return true;
        return false;
    }

    private bool InRange(Vector3 axis, bool committed)
    {
        Vector3 delta = targetBody.bounds.center - body.bounds.center;
        Vector3 right = Vector3.Cross(Vector3.up, axis);
        float x = Vector3.Dot(delta, right);
        float radius = Mathf.Abs(right.x) * targetBody.bounds.extents.x + Mathf.Abs(right.z) * targetBody.bounds.extents.z;
        return Mathf.Abs(x) <= reach + radius && Mathf.Abs(delta.y) <= verticalReach + targetBody.bounds.extents.y
            && (!committed || x * Facing >= -0.02f);
    }

    private bool IsWithinProximityAttackRange()
    {
        if (targetBody == null) return false;
        GetPhysicalCapsule(body, out var a, out float aRadius, out float aHalf);
        GetPhysicalCapsule(targetBody, out var b, out float bRadius, out float bHalf);
        // Upright capsules: shortest distance between their vertical centre segments,
        // then include both radii. This handles a player standing on a nearby block.
        Vector3 difference = a - b;
        float verticalGap = Mathf.Max(0f, Mathf.Abs(difference.y) - aHalf - bHalf);
        float reach = aRadius + bRadius + 0.5f;
        return difference.x * difference.x + difference.z * difference.z + verticalGap * verticalGap <= reach * reach;
    }

    private static void GetPhysicalCapsule(CharacterController controller, out Vector3 center, out float radius, out float segmentHalf)
    {
        Vector3 scale = controller.transform.lossyScale;
        center = controller.transform.TransformPoint(controller.center);
        radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float height = Mathf.Max(controller.height * Mathf.Abs(scale.y), radius * 2f);
        segmentHalf = Mathf.Max(0f, height * 0.5f - radius);
    }

    private bool ClearLine()
    {
        Vector3 origin = body.bounds.center;
        Vector3 delta = targetBody.bounds.center - origin;
        if (delta.sqrMagnitude < 0.0001f) return true;
        foreach (var hit in Physics.RaycastAll(origin, delta.normalized, delta.magnitude, wallMask, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.transform.IsChildOf(transform) || hit.collider.transform.IsChildOf(target.transform)) continue;
            if (hit.collider.GetComponentInParent<FezDepthProxyFollower>() != null) continue;
            return false;
        }
        return true;
    }

    private void BeginRecovery() { ChangeState(AttackState.Recovery); deadline = Time.time + recovery; onRecovery.Invoke(); }
    public void CancelAttackForReturn()
    {
        if (State == AttackState.Windup || State == AttackState.Attack) BeginRecovery();
    }
    private void ChangeState(AttackState next)
    {
        if (next == AttackState.Idle || next == AttackState.Windup || next == AttackState.Dead) depthRecoveryCanMove = false;
        State = next;
        if (warningIcon != null) warningIcon.SetActive(next == AttackState.Windup);
        if (animator != null)
        {
            foreach (var parameter in animator.parameters)
            {
                if (parameter.name == "CombatState" && parameter.type == AnimatorControllerParameterType.Int) animator.SetInteger("CombatState", (int)next);
                if (parameter.name == "Facing" && parameter.type == AnimatorControllerParameterType.Float) animator.SetFloat("Facing", Facing);
            }
        }
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 eye = eyeAnchor != null ? eyeAnchor.position : transform.position + Vector3.up * 0.7f;
        DrawSightGizmo(eye, SightForward, sightDistance, viewAngle);
        Gizmos.color = Color.yellow;
        Vector3 right = Vector3.Cross(Vector3.up, ViewAxis);
        Gizmos.DrawWireCube(transform.position + Vector3.up * 0.5f, new Vector3(Mathf.Abs(right.x) * reach * 2f + 0.05f, verticalReach * 2f, Mathf.Abs(right.z) * reach * 2f + 0.05f));
    }

    public static void DrawSightGizmo(Vector3 eye, Vector3 forward, float distance, float angle)
    {
        Gizmos.color = new Color(1f, 0.65f, 0.1f);
        Gizmos.DrawRay(eye, forward * distance);
        Vector3 previous = eye + Quaternion.AngleAxis(-angle * 0.5f, Vector3.up) * forward * distance;
        Gizmos.DrawLine(eye, previous);
        for (int i = 1; i <= 24; i++)
        {
            Vector3 next = eye + Quaternion.AngleAxis(-angle * 0.5f + angle * i / 24f, Vector3.up) * forward * distance;
            Gizmos.DrawLine(previous, next);
            previous = next;
        }
        Gizmos.DrawLine(eye, previous);
    }
}
