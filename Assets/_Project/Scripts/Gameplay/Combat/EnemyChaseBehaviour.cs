using System.Collections.Generic;
using UnityEngine;
using Result = EnemyDecisionTree.Result;

/// <summary>Reactive tree priority: return > visible pursuit > investigation > patrol.</summary>
public sealed class EnemyChaseBehaviour : EnemyBehaviour
{
    public enum Mode { Patrol, Chase, Investigate, Observe, Return }
    public Mode CurrentMode { get; private set; }
    public override bool AllowsAttack => !returning && Time.time >= reacquireAt && host != null && tuning != null
        && (pursuitLocked || Vector3.Distance(host.LastSeenFeet, home) <= tuning.leashRadius);
    public override bool CanAttackWithoutSight => pursuitLocked && !returning;
    public override bool AllowsProximityAttack => !returning && Time.time >= reacquireAt;
    public bool IsPursuitLocked => pursuitLocked;
    private bool pursuitLocked;
    private Vector3 liveTargetFeet;
    public override bool OwnsLookDirection => CurrentMode == Mode.Observe;
    public string PathStatus { get; private set; } = "等待初始化";
    [InspectorName("顯示追擊狀態")] public bool showStatus = true;
    private readonly List<Vector3> path = new List<Vector3>();
    private readonly List<Vector3> replacementPath = new List<Vector3>();
    private readonly List<Transform> patrol = new List<Transform>();
    private EnemyDecisionTree.INode tree;
    private EnemyNavigationArea area;
    private CharacterController body;
    private StationaryMeleeEnemy host;
    private Vector3 home, goal, feet, lastKnown, committedPathGoal;
    private Quaternion homeRotation, observationFacing;
    private EnemyTuning tuning;
    private float nextRepath, chaseStarted, observeUntil, waitUntil = -1f, blockedSince = -1f, reacquireAt, observationPhase;
    private bool engaged, returning;
    private bool partialPath;
    private int waypoint, patrolIndex, patrolSign = 1;
    private string loggedStatus;

    public void SetTuning(EnemyTuning value) { tuning = JsonUtility.FromJson<EnemyTuning>(JsonUtility.ToJson(value)); }
    public void RememberAttackOrigin(Vector3 position)
    {
        lastKnown = position;
        path.Clear(); nextRepath = 0f; blockedSince = -1f; waitUntil = -1f;
        if (!engaged) chaseStarted = Time.time;
        engaged = true;
        // Keep forced-return priority, but preserve the new clue for the next decision.
        if (!returning) SetMode(Mode.Investigate);
    }
    public override void Initialize(StationaryMeleeEnemy enemy)
    {
        host = enemy; body = enemy.GetComponent<CharacterController>();
        home = body.bounds.center; home.y = body.bounds.min.y;
        homeRotation = enemy.transform.rotation;
        patrol.Clear();
        if (enemy.patrolPoints != null) foreach (var point in enemy.patrolPoints) if (point != null) patrol.Add(point);
        tuning = tuning ?? new EnemyTuning(); area = enemy.navigationArea;
        if (area == null) foreach (var candidate in FindObjectsOfType<EnemyNavigationArea>())
        {
            if (!new Bounds(candidate.transform.position, candidate.size).Contains(body.bounds.center)) continue;
            area = candidate; break;
        }
        tree = new EnemyDecisionTree.Selector(
            new EnemyDecisionTree.Sequence(new EnemyDecisionTree.Condition(() => returning), new EnemyDecisionTree.Action(TickReturn)),
            new EnemyDecisionTree.Sequence(new EnemyDecisionTree.Condition(CanAcquire), new EnemyDecisionTree.Action(TickChase)),
            new EnemyDecisionTree.Sequence(new EnemyDecisionTree.Condition(() => engaged), new EnemyDecisionTree.Action(TickInvestigation)),
            new EnemyDecisionTree.Action(TickPatrol));
    }
    public override void Tick(StationaryMeleeEnemy enemy, float deltaTime)
    {
        var targetBody = host.Target != null ? host.Target.GetComponent<CharacterController>() : null;
        var targetHealth = host.Target != null ? host.Target.GetComponent<Health>() : null;
        bool alive = targetBody != null && targetBody.enabled && targetHealth != null && !targetHealth.IsDead;
        if (alive) { liveTargetFeet = targetBody.bounds.center; liveTargetFeet.y = targetBody.bounds.min.y; }
        bool wasLocked = pursuitLocked;
        pursuitLocked = !returning && (pursuitLocked || Time.time >= reacquireAt)
            && EnemyPursuitRules.ShouldLock(pursuitLocked, engaged || host.HasVisualContact,
            alive, alive ? Vector3.Distance(body.bounds.center, targetBody.bounds.center) : float.PositiveInfinity, tuning.pursuitLockRadius);
        if (wasLocked && !pursuitLocked) chaseStarted = Time.time;
        if (area == null || !area.IsBuilt) { PathStatus = "缺少已重建的尋路區域"; return; }
        Vector3 scale = body.transform.lossyScale;
        float radius = body.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        float height = Mathf.Max(body.height * Mathf.Abs(scale.y), radius * 2f);
        if (radius > area.agentRadius + 0.01f || height > area.agentHeight + 0.01f)
        { PathStatus = "尋路區域尺寸小於敵人碰撞箱"; return; }
        feet = body.bounds.center; feet.y = body.bounds.min.y;
        if (!area.TryGroundPosition(feet, out feet)) { PathStatus = "敵人尚未落在實體地板"; return; }
        if (engaged && !pursuitLocked && (Vector3.Distance(feet, home) > tuning.leashRadius
            || (CurrentMode != Mode.Observe && Time.time - chaseStarted >= tuning.maxChaseTime)
            || (host.HasVisualContact && Vector3.Distance(host.LastSeenFeet, home) > tuning.leashRadius))) BeginReturn();
        tree.Tick();
    }
    private bool CanAcquire() => pursuitLocked || (host.HasVisualContact && Time.time >= reacquireAt
        && Vector3.Distance(host.LastSeenFeet, home) <= tuning.leashRadius);
    private void SetMode(Mode mode)
    {
        if (CurrentMode == mode) return;
        bool continuousPursuit = (CurrentMode == Mode.Chase || CurrentMode == Mode.Investigate)
            && (mode == Mode.Chase || mode == Mode.Investigate);
        CurrentMode = mode;
        if (!continuousPursuit) { path.Clear(); nextRepath = 0f; blockedSince = -1f; }
    }
    private Result TickChase()
    {
        if (!engaged) { engaged = true; chaseStarted = Time.time; waitUntil = -1f; }
        SetMode(Mode.Chase); lastKnown = pursuitLocked ? liveTargetFeet : host.LastSeenFeet;
        bool depthPriority = host.ContinueDepthPursuit;
        if (!depthPriority && host.State != StationaryMeleeEnemy.AttackState.Idle) { PathStatus = "攻擊中，暫停追擊"; return Result.Running; }
        if (pursuitLocked)
        {
            Vector3 toward = Vector3.ProjectOnPlane(lastKnown - feet, Vector3.up);
            host.FaceDirection(toward);
        }
        float stopping = depthPriority ? Mathf.Min(tuning.chaseStopDistance, 0.5f) : tuning.chaseStopDistance;
        MoveTo(lastKnown, tuning.chaseSpeed, stopping, depthPriority ? "深度差過大，優先追近" : pursuitLocked ? "範圍內鎖定追擊" : "目視追擊中", true);
        if (!pursuitLocked && BlockedTooLong()) BeginReturn();
        return Result.Running;
    }
    private Result TickInvestigation()
    {
        if (host.Target == null || host.Target.GetComponent<Health>() == null || host.Target.GetComponent<Health>().IsDead)
        { BeginReturn(); return Result.Running; }
        // Losing sight should not insert an attack/recovery wait before investigation.
        // Cancel an uncompleted strike; recovery can finish while walking to the last sighting.
        host.CancelAttackForReturn();
        if (CurrentMode != Mode.Observe)
        {
            SetMode(host.HasRememberedTarget ? Mode.Chase : Mode.Investigate);
            if (!MoveTo(lastKnown, tuning.chaseSpeed, 0.12f, host.HasRememberedTarget ? "短暫追蹤最後位置" : "前往最後目擊位置", true))
            { if (BlockedTooLong()) BeginReturn(); return Result.Running; }
            SetMode(Mode.Observe);
            observeUntil = Time.time + Mathf.Max(0f, tuning.searchDuration);
            observationFacing = host.transform.rotation; observationPhase = 0f;
        }
        observationPhase += Time.deltaTime * Mathf.PI * 2f / Mathf.Max(0.1f, host.scanPeriod);
        host.FaceRotation(observationFacing * Quaternion.Euler(0f, Mathf.Sin(observationPhase) * host.scanHalfAngle, 0f));
        PathStatus = $"原地觀察 {Mathf.Max(0f, observeUntil - Time.time):0.0}s";
        if (Time.time >= observeUntil) BeginReturn();
        return Result.Running;
    }
    private void BeginReturn()
    {
        if (returning) return;
        returning = true; engaged = false; pursuitLocked = false; host.CancelAttackForReturn(); SetMode(Mode.Return);
        if (patrol.Count == 0) return;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < patrol.Count; i++)
        {
            if (patrol[i] == null) continue;
            float distance = (patrol[i].position - feet).sqrMagnitude;
            if (distance < nearest) { nearest = distance; patrolIndex = i; }
        }
    }
    private Result TickReturn()
    {
        if (host.State != StationaryMeleeEnemy.AttackState.Idle) { PathStatus = "停止攻擊，準備返回"; return Result.Running; }
        if (!MoveTo(PatrolDestination(), tuning.patrolMoveSpeed, 0.12f, "返回巡邏點"))
        { if (BlockedTooLong()) PathStatus = "返回路線不通，停留並定期重試"; return Result.Running; }
        returning = false; reacquireAt = Time.time + 1f; SetMode(Mode.Patrol);
        waitUntil = Time.time + Mathf.Max(0f, tuning.patrolWait); RestorePatrolFacing();
        return Result.Running;
    }
    private Result TickPatrol()
    {
        SetMode(Mode.Patrol);
        if (host.State != StationaryMeleeEnemy.AttackState.Idle) return Result.Running;
        if (waitUntil >= 0f)
        {
            PathStatus = "巡邏點警戒";
            if (Time.time < waitUntil) return Result.Running;
            waitUntil = -1f; AdvancePatrol();
        }
        if (MoveTo(PatrolDestination(), tuning.patrolMoveSpeed, 0.12f, "沿巡邏路線移動"))
        { waitUntil = Time.time + Mathf.Max(0f, tuning.patrolWait); RestorePatrolFacing(); }
        return Result.Running;
    }
    private Vector3 PatrolDestination() => patrol.Count > 0 && patrol[patrolIndex] != null ? patrol[patrolIndex].position : home;
    private void RestorePatrolFacing() { host.FaceRotation(patrol.Count > 0 && patrol[patrolIndex] != null ? patrol[patrolIndex].rotation : homeRotation); }
    private void AdvancePatrol()
    {
        if (patrol.Count <= 1) return;
        if (!tuning.patrolPingPong) { patrolIndex = (patrolIndex + 1) % patrol.Count; return; }
        if (patrolIndex + patrolSign >= patrol.Count || patrolIndex + patrolSign < 0) patrolSign = -patrolSign;
        patrolIndex += patrolSign;
    }
    private bool BlockedTooLong() => blockedSince >= 0f && Time.time - blockedSince >= Mathf.Max(0.1f, tuning.unreachableTimeout);
    private bool MoveTo(Vector3 destination, float speed, float stopping, string status, bool allowClosest = false)
    {
        if (area.TryGroundPosition(destination, out var grounded)) destination = grounded;
        if (Vector3.Distance(feet, destination) <= Mathf.Max(0.05f, stopping))
        { path.Clear(); nextRepath = 0f; blockedSince = -1f; PathStatus = "已抵達"; return true; }
        if ((destination - goal).sqrMagnitude > 0.1f) nextRepath = 0f;
        goal = destination;
        if (Time.time >= nextRepath)
        {
            nextRepath = Time.time + Mathf.Max(0.1f, tuning.repathInterval);
            if (area.FindPath(feet, destination, replacementPath, allowClosest, out bool replacementPartial))
            {
                if (!replacementPartial && (replacementPath.Count == 0 || (replacementPath[replacementPath.Count - 1] - destination).sqrMagnitude > 0.0001f))
                    replacementPath.Add(destination);
                path.Clear(); path.AddRange(replacementPath); partialPath = replacementPartial; committedPathGoal = destination;
                // The first graph node is an attachment point, not a reason to step backwards.
                waypoint = path.Count > 1 && area.CanTraverse(feet, path[1]) ? 1 : 0;
            }
            else
            {
                MarkBlocked(area.LastPathStatus);
                if (path.Count == 0) return false;
                // Keep a safe previous route until a replacement is available.
            }
        }
        if (path.Count == 0) return false;
        while (waypoint < path.Count && Vector3.Distance(feet, path[waypoint]) < 0.09f) waypoint++;
        if (waypoint >= path.Count)
        {
            if ((committedPathGoal - destination).sqrMagnitude > 0.01f) { nextRepath = 0f; return false; }
            if (partialPath) { blockedSince = -1f; PathStatus = "抵達最近可達地板，等待目標移動"; return true; }
            nextRepath = 0f; return false;
        }
        Vector3 delta = path[waypoint] - feet; delta.y = 0f;
        Vector3 move = delta.normalized * Mathf.Min(delta.magnitude, Mathf.Max(0f, speed) * Mathf.Min(Time.deltaTime, 0.05f));
        if (!area.CanTraverse(feet, path[waypoint]) || !host.TryMoveOnGround(move))
        { path.Clear(); MarkBlocked("路線被阻擋，等待重新規劃"); return false; }
        blockedSince = -1f; PathStatus = partialPath ? "前往最近可達地板" : status; return false;
    }
    private void MarkBlocked(string reason) { if (blockedSince < 0f) blockedSince = Time.time; PathStatus = reason; }
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        for (int i = 1; i < path.Count; i++) Gizmos.DrawLine(path[i - 1] + Vector3.up * 0.1f, path[i] + Vector3.up * 0.1f);
        if (Application.isPlaying && tuning != null) { Gizmos.color = Color.red; Gizmos.DrawWireSphere(home, tuning.leashRadius); }
    }
    private void LateUpdate()
    {
        string key = CurrentMode + ":" + (CurrentMode == Mode.Observe ? "原地觀察" : PathStatus);
        if (!showStatus || key == loggedStatus) return;
        loggedStatus = key; Debug.Log($"[Morld A*] {name}: {key}; depthGap={host.TargetDepthGap:0.00}; depthPriority={host.MustCloseDepthBeforeAttack}", this);
    }
    private void OnGUI()
    {
        if (!showStatus || body == null || Camera.main == null) return;
        Vector3 screen = Camera.main.WorldToScreenPoint(body.bounds.max + Vector3.up * 0.2f);
        if (screen.z > 0f) GUI.Box(new Rect(screen.x - 150f, Screen.height - screen.y - 44f, 300f, 44f),
            PathStatus + $"\n深度差 {host.TargetDepthGap:0.00} 格");
    }
}
