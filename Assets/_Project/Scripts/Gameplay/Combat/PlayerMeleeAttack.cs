using System.Collections.Generic;
using Lichronicle.Gameplay.Inputs;
using UnityEngine;
using UnityEngine.Events;

[DefaultExecutionOrder(1100)]
[RequireComponent(typeof(PlayerController), typeof(Health))]
public sealed class PlayerMeleeAttack : MonoBehaviour
{
    public enum DistanceRule { Projected2D, World3D }
    public KeyCode attackKey = KeyCode.Mouse0;
    public bool useNorthButton;
    public DistanceRule distanceRule = DistanceRule.Projected2D;
    [Min(0f)] public float reach = 0.9f;
    [Min(0f)] public float verticalReach = 0.6f;
    [Min(0f)] public float maxDepthGap = 3f;
    [Min(0f)] public float windup = 0.1f;
    [Min(0f)] public float recovery = 0.25f;
    [Min(0f)] public float missPenalty = 0.2f;
    [Min(0.05f)] public float strikeVisualDuration = 0.24f;
    public bool showAttackHint = true;
    [Header("空中爆擊")]
    [Tooltip("橫軸為本次滯空最高下落速度，縱軸為普攻傷害倍率。無最低速度門檻；最終傷害四捨五入為整數。")]
    public AnimationCurve airborneCriticalMultiplier = new AnimationCurve(
        new Keyframe(0f, 2f, 0.2f, 0.2f),
        new Keyframe(5f, 3f, 0.2f, 0.4f),
        new Keyframe(10f, 5f, 0.4f, 0.4f));
    public bool enableFallingExecution = true;
    [Min(0f)] public float executionHalfWidth = 0.6f;
    [Range(0f, 0.5f)] public float executionLateralExpansion = 0.05f;
    [Min(0f)] public float executionDownReach = 1.2f;
    [Min(0f)] public float executionDepthGap = 3f;
    public bool showExecutionZone = true;
    public UnityEvent onExecution = new UnityEvent();
    private readonly List<StationaryMeleeEnemy> executionHints = new List<StationaryMeleeEnemy>();
    private readonly List<StationaryMeleeEnemy> executionAttemptTargets = new List<StationaryMeleeEnemy>();
    private bool trackingAirborneAttack;
    private int executionLandingSequence;
    private string executionResult;
    private float executionResultUntil;
    private bool executed;
    public UnityEvent onMiss = new UnityEvent();
    public UnityEvent onWindup = new UnityEvent();
    public UnityEvent onStrike = new UnityEvent();
    public UnityEvent onHit = new UnityEvent();
    public UnityEvent onFinished = new UnityEvent();
    public bool IsAttacking { get; private set; }
    public float AttackFacing { get; private set; }
    private PlayerController motor;
    private CharacterController body;
    private Health health;
    private FezRotationOcclusionGate gate;
    private Vector3 axis;
    private float deadline;
    private bool struck;
    private float showStrikeUntil;
    private bool hitSomething;
    private GUIStyle hintStyle;
    private void Awake()
    {
        motor = GetComponent<PlayerController>(); body = GetComponent<CharacterController>();
        health = GetComponent<Health>(); gate = GetComponent<FezRotationOcclusionGate>();
    }
    private void OnDisable()
    {
        Finish();
        executionAttemptTargets.Clear();
        trackingAirborneAttack = false;
    }
    private void Update()
    {
        if (health.IsDead || !body.enabled || (gate != null && gate.IsRotationInProgress))
        {
            executionHints.Clear();
            executionAttemptTargets.Clear();
            trackingAirborneAttack = false;
            Finish();
            return;
        }
        UpdateFallingExecutionAttempt();
        bool attackPressed = motor.ReadActionKeyDown(attackKey) || motor.ReadActionKeyDown(GamepadMapping.AttackKey(motor.gamepadLayout, useNorthButton));
        // Execution intent is independent of normal attack windup/recovery.
        if (enableFallingExecution && !motor.IsGrounded && attackPressed && !motor.IsHitStunned && !motor.IsDodging)
            RegisterFallingExecutionAttempt();
        UpdateExecutionHints();
        if (motor.IsHitStunned || motor.IsDodging) { Finish(); return; }
        if (IsAttacking)
        {
            if (Vector3.Dot(axis, motor.DepthAxis) < 0.999f) { Finish(); return; }
            if (Time.time < deadline) return;
            if (!struck) { struck = true; Strike(); deadline = Time.time + Mathf.Max(0f, recovery) + (hitSomething ? 0f : missPenalty); }
            else Finish();
            return;
        }
        if (motor.IsActionLocked || !attackPressed) return;
        axis = motor.DepthAxis;
        AttackFacing = motor.FacingDirection;
        hitSomething = false;
        executed = false;
        IsAttacking = true; struck = false; deadline = Time.time + Mathf.Max(0f, windup);
        var trace = GetComponent<PlayerDebugTools>();
        if (trace != null) trace.RecordAttack();
        onWindup.Invoke();
    }
    private void Strike()
    {
        showStrikeUntil = Time.time + strikeVisualDuration;
        onStrike.Invoke();
        Vector3 origin = body.bounds.center;
        Vector3 right = Vector3.Cross(Vector3.up, axis).normalized;
        foreach (var enemy in new List<StationaryMeleeEnemy>(StationaryMeleeEnemy.ActiveEnemies))
        {
            if (enemy == null || !enemy.isActiveAndEnabled) continue;
            var targetHealth = enemy.GetComponent<Health>();
            var targetBody = enemy.GetComponent<CharacterController>();
            if (targetHealth == null || targetHealth.IsDead || targetBody == null || !targetBody.enabled) continue;
            bool pendingExecution = executionAttemptTargets.Contains(enemy) && IsInsideExecutionRange(targetBody, origin, axis);
            bool normalTarget = IsInsideNormalRange(targetBody.bounds, body.bounds, axis, AttackFacing);
            if (pendingExecution) { hitSomething = true; continue; }
            if (!normalTarget) continue;
            if (!IsVisibleToGameCamera(enemy) && !ClearLine(origin, targetBody.bounds.center)) continue;
            hitSomething = true;
            int before = targetHealth.Current;
            Vector3 attackSource = origin;
            attackSource.y = body.bounds.min.y;
            targetHealth.Damage(1, attackSource);
            if (targetHealth.Current < before)
            {
                var response = enemy.GetComponent<EnemyHitResponse>();
                if (response != null) response.MarkPlayerAttackHit();
                onHit.Invoke();
            }
        }
        if (!hitSomething) { motor.ApplyMissRecovery(missPenalty); onMiss.Invoke(); }
    }
    private bool CanExecuteTarget(CharacterController target, Vector3 origin, Vector3 viewAxis)
    {
        return enableFallingExecution && !motor.IsGrounded
            && IsInsideExecutionRange(target, origin, viewAxis);
    }

    private bool IsInsideExecutionRange(CharacterController target, Vector3 origin, Vector3 viewAxis)
    {
        return target != null && IsInsideExecutionRange(target.bounds, body.bounds, viewAxis);
    }

    private bool IsInsideExecutionRange(Bounds target, Bounds player, Vector3 viewAxis)
    {
        // Union, not a larger enclosing box: preserve both ranges' individual depth rules.
        if (IsInsideNormalRange(target, player, viewAxis, motor.FacingDirection)) return true;
        Vector3 delta = target.center - player.center;
        Vector3 right = Vector3.Cross(Vector3.up, viewAxis).normalized;
        float targetHalfWidth = Mathf.Abs(right.x) * target.extents.x + Mathf.Abs(right.z) * target.extents.z;
        float playerHalfWidth = Mathf.Abs(right.x) * player.extents.x + Mathf.Abs(right.z) * player.extents.z;
        float halfWidth = (executionHalfWidth + playerHalfWidth) * (1f + executionLateralExpansion) + targetHalfWidth;
        return Mathf.Abs(Vector3.Dot(delta, right)) <= halfWidth
            && Mathf.Abs(Vector3.Dot(delta, viewAxis)) <= executionDepthGap
            && target.max.y >= player.min.y - executionDownReach && target.min.y <= player.center.y;
    }

    private bool IsInsideNormalRange(Bounds target, Bounds player, Vector3 viewAxis, float facing)
    {
        Vector3 delta = target.center - player.center;
        Vector3 right = Vector3.Cross(Vector3.up, viewAxis).normalized;
        float x = Vector3.Dot(delta, right);
        float radius = Mathf.Max(target.extents.x, target.extents.z);
        return x * facing >= -0.05f && Mathf.Abs(x) <= reach + radius
            && Mathf.Abs(delta.y) <= verticalReach + target.extents.y
            && (distanceRule == DistanceRule.World3D ? delta.magnitude <= reach + radius
                : Mathf.Abs(Vector3.Dot(delta, viewAxis)) <= maxDepthGap);
    }

    private void RegisterFallingExecutionAttempt()
    {
        trackingAirborneAttack = true;
        Vector3 origin = body.bounds.center;
        Vector3 viewAxis = motor.DepthAxis;
        foreach (var enemy in StationaryMeleeEnemy.ActiveEnemies)
        {
            if (enemy == null || !enemy.isActiveAndEnabled || executionAttemptTargets.Contains(enemy)) continue;
            var targetHealth = enemy.GetComponent<Health>();
            var targetBody = enemy.GetComponent<CharacterController>();
            if (targetHealth == null || targetHealth.IsDead || targetBody == null || !targetBody.enabled
                || !IsInsideExecutionRange(targetBody, origin, viewAxis)) continue;
            executionAttemptTargets.Add(enemy);
        }
        if (executionAttemptTargets.Count == 0)
            SetExecutionResult("未登記：按下攻擊時沒有範圍內目標");
    }

    private void UpdateFallingExecutionAttempt()
    {
        if (!enableFallingExecution)
        {
            executionAttemptTargets.Clear();
            trackingAirborneAttack = false;
            return;
        }
        if (!motor.IsGrounded && !trackingAirborneAttack)
        {
            trackingAirborneAttack = true;
            executionLandingSequence = motor.LandingSequence;
            executionResult = null;
        }
        if (!trackingAirborneAttack) return;
        bool landed = motor.LandingSequence != executionLandingSequence;
        if (!landed && !motor.IsGrounded)
        {
            Vector3 origin = body.bounds.center;
            Vector3 viewAxis = motor.DepthAxis;
            if (executionAttemptTargets.RemoveAll(enemy => !IsExecutionTargetStillInRange(enemy, origin, viewAxis)) > 0)
                SetExecutionResult("爆擊取消：目標離開範圍");
            return;
        }

        if (landed)
        {
            Bounds landingBounds = motor.LastLandingBounds;
            Vector3 origin = landingBounds.center;
            Vector3 attackSource = origin;
            attackSource.y = landingBounds.min.y;
            Vector3 viewAxis = motor.LastLandingDepthAxis;
            foreach (var enemy in new List<StationaryMeleeEnemy>(executionAttemptTargets))
            {
                if (enemy == null || !enemy.isActiveAndEnabled) continue;
                var targetBody = enemy.GetComponent<CharacterController>();
                var targetHealth = enemy.GetComponent<Health>();
                if (targetBody == null || !targetBody.enabled || targetHealth == null || targetHealth.IsDead) continue;
                if (!IsInsideExecutionRange(targetBody.bounds, landingBounds, viewAxis))
                { SetExecutionResult("爆擊取消：落地前目標離開範圍"); continue; }
                if (!targetHealth.CanReceiveHit)
                { SetExecutionResult("爆擊取消：目標無敵中"); continue; }
                if (!IsVisibleToGameCamera(enemy) && !ClearLine(origin, targetBody.bounds.center))
                { SetExecutionResult("爆擊取消：攻擊受到遮擋"); continue; }
                int before = targetHealth.Current;
                float multiplier = GetAirborneCriticalMultiplier(motor.LastLandingPeakFallingSpeed);
                targetHealth.Damage(Mathf.Max(1, Mathf.RoundToInt(multiplier)), attackSource);
                if (targetHealth.Current < before)
                {
                    var response = enemy.GetComponent<EnemyHitResponse>();
                    if (response != null) response.MarkPlayerAttackHit();
                    executed = true;
                    hitSomething = true;
                    showStrikeUntil = Time.time + strikeVisualDuration;
                    onExecution.Invoke();
                    onHit.Invoke();
                    SetExecutionResult($"空中爆擊 ×{multiplier:F1}！傷害 {before - targetHealth.Current}");
                }
            }
        }
        executionAttemptTargets.Clear();
        trackingAirborneAttack = false;
    }

    private void SetExecutionResult(string message)
    {
        executionResult = message;
        executionResultUntil = Time.time + 2f;
    }

    public float GetAirborneCriticalMultiplier(float peakFallingSpeed)
    {
        float speed = Mathf.Max(0f, peakFallingSpeed);
        if (airborneCriticalMultiplier == null || airborneCriticalMultiplier.length == 0) return 2f;
        // Clamp to endpoints even if a designer enables curve looping.
        var keys = airborneCriticalMultiplier.keys;
        speed = Mathf.Clamp(speed, keys[0].time, keys[keys.Length - 1].time);
        return Mathf.Max(1f, airborneCriticalMultiplier.Evaluate(speed));
    }

    private bool IsExecutionTargetStillInRange(StationaryMeleeEnemy enemy, Vector3 origin, Vector3 viewAxis)
    {
        if (enemy == null || !enemy.isActiveAndEnabled) return false;
        var targetHealth = enemy.GetComponent<Health>();
        var targetBody = enemy.GetComponent<CharacterController>();
        return targetHealth != null && !targetHealth.IsDead && targetBody != null && targetBody.enabled
            && IsInsideExecutionRange(targetBody, origin, viewAxis);
    }

    private void UpdateExecutionHints()
    {
        executionHints.Clear();
        if (!showAttackHint || !enableFallingExecution || health.IsDead || motor.IsGrounded
            || (gate != null && gate.IsRotationInProgress)) return;
        Vector3 viewAxis = motor.DepthAxis;
        Vector3 origin = body.bounds.center;
        Vector3 right = Vector3.Cross(Vector3.up, viewAxis).normalized;
        foreach (var enemy in StationaryMeleeEnemy.ActiveEnemies)
        {
            if (enemy == null || !enemy.isActiveAndEnabled) continue;
            var targetHealth = enemy.GetComponent<Health>();
            var targetBody = enemy.GetComponent<CharacterController>();
            if (targetHealth == null || !targetHealth.CanReceiveHit || targetBody == null || !targetBody.enabled || !CanExecuteTarget(targetBody, origin, viewAxis)) continue;
            if (!IsVisibleToGameCamera(enemy) && !ClearLine(origin, targetBody.bounds.center)) continue;
            executionHints.Add(enemy);
        }
    }
    private bool IsVisibleToGameCamera(StationaryMeleeEnemy enemy)
    {
        Camera camera = Camera.main;
        if (camera == null || enemy.visualRoot == null) return false;
        foreach (var visual in enemy.visualRoot.GetComponentsInChildren<Renderer>())
        {
            if (!visual.enabled || visual.forceRenderingOff || (camera.cullingMask & (1 << visual.gameObject.layer)) == 0) continue;
            Bounds bounds = visual.bounds;
            // Sample actual art bounds rather than relying on Renderer.isVisible (which includes Scene view).
            Vector3 cameraRight = camera.transform.right;
            float halfWidth = Mathf.Min(bounds.extents.x / Mathf.Max(0.0001f, Mathf.Abs(cameraRight.x)),
                Mathf.Min(bounds.extents.y / Mathf.Max(0.0001f, Mathf.Abs(cameraRight.y)), bounds.extents.z / Mathf.Max(0.0001f, Mathf.Abs(cameraRight.z))));
            Vector3 right = cameraRight * halfWidth * 0.8f;
            Vector3 up = Vector3.up * bounds.extents.y * 0.8f;
            foreach (Vector3 sample in new[] { bounds.center, bounds.center + up, bounds.center - up,
                bounds.center + right, bounds.center - right })
            {
                Vector3 viewport = camera.WorldToViewportPoint(sample);
                if (viewport.z < camera.nearClipPlane || viewport.z > camera.farClipPlane || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f) continue;
                Ray ray = camera.ViewportPointToRay(viewport);
                float distance = Vector3.Dot(sample - ray.origin, ray.direction);
                bool blocked = false;
                foreach (var hit in Physics.RaycastAll(ray, distance, camera.cullingMask, QueryTriggerInteraction.Ignore))
                {
                    if (hit.distance >= distance - 0.01f || hit.collider.transform.IsChildOf(enemy.transform)
                        || hit.collider.GetComponentInParent<PlayerController>() != null
                        || hit.collider.GetComponentInParent<FezDepthProxyFollower>() != null) continue;
                    var renderer = hit.collider.GetComponentInParent<Renderer>();
                    if (renderer == null || !renderer.enabled || renderer.forceRenderingOff) continue;
                    blocked = true; break;
                }
                if (!blocked) return true;
            }
        }
        return false;
    }
    private bool ClearLine(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        if (delta.sqrMagnitude < 0.00001f) return true;
        foreach (var hit in Physics.RaycastAll(from, delta.normalized, delta.magnitude, Physics.AllLayers, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.transform.IsChildOf(transform) || hit.collider.GetComponentInParent<StationaryMeleeEnemy>() != null
                || hit.collider.GetComponentInParent<FezDepthProxyFollower>() != null) continue;
            return false;
        }
        return true;
    }
    private void Finish() { if (!IsAttacking) return; IsAttacking = false; onFinished.Invoke(); }
    private void OnGUI()
    {
        if (!showAttackHint || Camera.main == null || body == null) return;
        if (showExecutionZone && enableFallingExecution && !motor.IsGrounded && !health.IsDead)
            DrawExecutionZone();
        if (hintStyle == null) hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        Color beforeHints = GUI.color;
        foreach (var target in executionHints)
        {
            if (target == null) continue;
            Vector3 hint = Camera.main.WorldToScreenPoint(target.GetComponent<CharacterController>().bounds.center);
            if (hint.z <= 0f) continue;
            GUI.color = Color.Lerp(Color.yellow, new Color(1f, 0.2f, 0.1f), (Mathf.Sin(Time.unscaledTime * 18f) + 1f) * 0.5f);
            GUI.Label(new Rect(hint.x - 110f, Screen.height - hint.y - 30f, 220f, 35f),
                executionAttemptTargets.Contains(target) ? "↓ 已登記，等待落地" : "↓ 按攻擊登記爆擊", hintStyle);
        }
        GUI.color = beforeHints;
        string status = !motor.IsGrounded && executionAttemptTargets.Count > 0
            ? $"爆擊已登記 {executionAttemptTargets.Count}｜速度 {motor.AirbornePeakFallingSpeed:F1}｜×{GetAirborneCriticalMultiplier(motor.AirbornePeakFallingSpeed):F1}"
            : Time.time < executionResultUntil ? executionResult : null;
        if (!string.IsNullOrEmpty(status))
        {
            Vector3 labelPosition = Camera.main.WorldToScreenPoint(body.bounds.center);
            if (labelPosition.z > 0f)
                GUI.Label(new Rect(labelPosition.x - 210f, Screen.height - labelPosition.y + 40f, 420f, 30f), status, hintStyle);
        }
        bool winding = IsAttacking && !struck;
        if (!winding && Time.time >= showStrikeUntil && !motor.IsMissRecoveryLocked) return;
        Vector3 screen = Camera.main.WorldToScreenPoint(body.bounds.center);
        if (screen.z <= 0f) return;
        Vector2 centre = new Vector2(screen.x, Screen.height - screen.y);
        Vector3 endpoint = Camera.main.WorldToScreenPoint(body.bounds.center + Vector3.Cross(Vector3.up, axis) * reach);
        float radius = Mathf.Clamp(Vector2.Distance(new Vector2(endpoint.x, Screen.height - endpoint.y), centre), 25f, 160f);
        Color previous = GUI.color;
        if (hintStyle == null) hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        Color effect = winding ? Color.yellow : hitSomething ? new Color(0.3f, 1f, 0.8f) : new Color(1f, 0.55f, 0.15f);
        float progress = winding ? 0f : Mathf.Clamp01(1f - (showStrikeUntil - Time.time) / Mathf.Max(0.05f, strikeVisualDuration));
        GUI.color = effect;
        GUI.Label(new Rect(centre.x - 90f, centre.y - 64f, 180f, 28f), winding ? "▶ 準備攻擊" : executed ? "下落爆擊！" : hitSomething ? "命中！" : motor.IsMissRecoveryLocked ? "揮空 · 硬直" : "揮空", hintStyle);
        if (winding || Time.time < showStrikeUntil)
        {
            float start = -75f + progress * 30f;
            for (int i = 0; i < 15; i++)
            {
                float a = (start + i * 10f) * Mathf.Deg2Rad;
                float b = (start + (i + 1) * 10f) * Mathf.Deg2Rad;
                Vector2 from = centre + new Vector2(Mathf.Cos(a) * AttackFacing, Mathf.Sin(a)) * radius;
                Vector2 to = centre + new Vector2(Mathf.Cos(b) * AttackFacing, Mathf.Sin(b)) * radius;
                DrawLine(from, to, winding ? 2f : 7f * (1f - progress * 0.6f));
            }
        }
        GUI.color = previous;
    }
    private static void DrawLine(Vector2 from, Vector2 to, float width)
    {
        Matrix4x4 previous = GUI.matrix;
        Vector2 difference = to - from;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(difference.y, difference.x) * Mathf.Rad2Deg, from);
        GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, difference.magnitude + 1f, width), Texture2D.whiteTexture);
        GUI.matrix = previous;
    }
    private void DrawExecutionZone()
    {
        Vector3 viewRight = Vector3.Cross(Vector3.up, motor.DepthAxis).normalized;
        float playerHalfWidth = Mathf.Abs(viewRight.x) * body.bounds.extents.x + Mathf.Abs(viewRight.z) * body.bounds.extents.z;
        Vector3 right = viewRight * ((executionHalfWidth + playerHalfWidth) * (1f + executionLateralExpansion));
        Vector3 feet = body.bounds.center; feet.y = body.bounds.min.y;
        Vector3 top = body.bounds.center;
        Vector3 bottom = feet - Vector3.up * executionDownReach;
        Vector3[] corners = { top - right, top + right, bottom + right, bottom - right };
        Color previous = GUI.color; GUI.color = new Color(1f, 0.4f, 0.1f, 0.8f);
        DrawZoneOutline(corners);
        // Show the normal-attack half of the union as a separate outline.
        Vector3 centre = body.bounds.center;
        Vector3 near = centre - viewRight * motor.FacingDirection * 0.05f;
        Vector3 far = centre + viewRight * motor.FacingDirection * reach;
        Vector3 up = Vector3.up * verticalReach;
        GUI.color = new Color(1f, 0.85f, 0.15f, 0.8f);
        DrawZoneOutline(new[] { near + up, far + up, far - up, near - up });
        GUI.color = previous;
    }

    private static void DrawZoneOutline(Vector3[] corners)
    {
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = Camera.main.WorldToScreenPoint(corners[i]), b = Camera.main.WorldToScreenPoint(corners[(i + 1) % 4]);
            if (a.z > 0f && b.z > 0f) DrawLine(new Vector2(a.x, Screen.height - a.y), new Vector2(b.x, Screen.height - b.y), 2f);
        }
    }
    private void OnDrawGizmosSelected()
    {
        var capsule = GetComponent<CharacterController>();
        if (capsule == null) return;
        Vector3 feet = capsule.bounds.center; feet.y = capsule.bounds.min.y;
        Vector3 depth = FezWorldViewState.Active != null ? FezWorldViewState.Active.DepthAxisWorld : Vector3.forward;
        Matrix4x4 previous = Gizmos.matrix;
        Vector3 right = Vector3.Cross(Vector3.up, depth).normalized;
        float playerHalfWidth = Mathf.Abs(right.x) * capsule.bounds.extents.x + Mathf.Abs(right.z) * capsule.bounds.extents.z;
        float expandedHalfWidth = (executionHalfWidth + playerHalfWidth) * (1f + executionLateralExpansion);
        float upReach = capsule.bounds.extents.y;
        Gizmos.matrix = Matrix4x4.TRS(feet + Vector3.up * (upReach - executionDownReach) * 0.5f, Quaternion.LookRotation(depth, Vector3.up), Vector3.one);
        Gizmos.color = new Color(1f, 0.4f, 0.1f);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(expandedHalfWidth * 2f, executionDownReach + upReach, executionDepthGap * 2f));
        Gizmos.matrix = previous;
    }
}
