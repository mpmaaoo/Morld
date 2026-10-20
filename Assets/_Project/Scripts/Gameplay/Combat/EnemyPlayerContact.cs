using System.Collections.Generic;
using UnityEngine;

/// <summary>One-way yielding: only the enemy moves; player motion is never modified.</summary>
[DefaultExecutionOrder(2000)]
[RequireComponent(typeof(CharacterController))]
public sealed class EnemyPlayerContact : MonoBehaviour
{
    private static readonly List<EnemyPlayerContact> Active = new List<EnemyPlayerContact>();
    private readonly List<PlayerController> players = new List<PlayerController>();
    private CharacterController body;
    private EnemyHitResponse hitResponse;
    public bool ignoreDepthWhileHit = true;
    [Min(0f)] public float maxPushSpeed = 16f;
    private void Awake() { body = GetComponent<CharacterController>(); }
    private void OnEnable()
    {
        Active.Add(this);
        foreach (var player in FindObjectsOfType<PlayerController>()) Connect(player);
    }
    private void OnDisable() { Active.Remove(this); }
    public static void RegisterPlayer(PlayerController player)
    {
        foreach (var enemy in Active) if (enemy != null) enemy.Connect(player);
    }
    public static void PreparePlayer(CharacterController player)
    {
        // CharacterController enable/disable (e.g. F8 recovery) can reset pair state.
        foreach (var enemy in Active)
            if (enemy != null && enemy.body != null) Physics.IgnoreCollision(enemy.body, player, true);
    }
    private void Connect(PlayerController player)
    {
        if (player == null || body == null) return;
        if (!players.Contains(player)) players.Add(player);
        foreach (var enemyCollider in GetComponentsInChildren<Collider>(true))
            foreach (var playerCollider in player.GetComponentsInChildren<Collider>(true))
                Physics.IgnoreCollision(enemyCollider, playerCollider, true);
    }
    private void LateUpdate()
    {
        if (body == null || !body.enabled) return;
        if (hitResponse == null) hitResponse = GetComponent<EnemyHitResponse>();
        // Collision pairs stay ignored at all times; yielding is only a player-hit reaction.
        if (hitResponse == null || !hitResponse.CanBePushedByPlayer) return;
        for (int i = players.Count - 1; i >= 0; i--)
        {
            var player = players[i];
            if (player == null) { players.RemoveAt(i); continue; }
            var other = player.GetComponent<CharacterController>();
            if (other == null || !other.enabled) continue;
            if (ignoreDepthWhileHit)
            {
                PushProjected(player, other);
                continue;
            }
            if (!body.bounds.Intersects(other.bounds)) continue;
            if (!Physics.ComputePenetration(body, transform.position, transform.rotation,
                    other, other.transform.position, other.transform.rotation, out var direction, out float distance)) continue;
            Vector3 horizontal = Vector3.ProjectOnPlane(direction, Vector3.up);
            float planar = horizontal.magnitude;
            if (planar < 0.01f)
            {
                horizontal = Vector3.ProjectOnPlane(body.bounds.center - other.bounds.center, Vector3.up);
                if (horizontal.sqrMagnitude < 0.00001f) horizontal = Vector3.ProjectOnPlane(player.Velocity, Vector3.up);
                if (horizontal.sqrMagnitude < 0.00001f) horizontal = Vector3.Cross(Vector3.up, player.DepthAxis);
            }
            float correction = distance / Mathf.Max(0.2f, planar) + 0.005f;
            body.Move(horizontal.normalized * Mathf.Min(correction, maxPushSpeed * Mathf.Min(Time.deltaTime, 0.05f)));
        }
    }
    private void PushProjected(PlayerController player, CharacterController other)
    {
        Vector3 right = Vector3.Cross(Vector3.up, player.DepthAxis).normalized;
        GetCapsule(body, out var enemyCenter, out float enemyRadius, out float enemyHalf);
        GetCapsule(other, out var playerCenter, out float playerRadius, out float playerHalf);
        float verticalGap = Mathf.Max(0f, Mathf.Abs(enemyCenter.y - playerCenter.y) - enemyHalf - playerHalf);
        float radii = enemyRadius + playerRadius;
        if (verticalGap >= radii) return;
        float lateralReach = Mathf.Sqrt(Mathf.Max(0f, radii * radii - verticalGap * verticalGap));
        float side = Vector3.Dot(enemyCenter - playerCenter, right);
        float penetration = lateralReach - Mathf.Abs(side);
        if (penetration <= 0f) return;
        float sign = Mathf.Abs(side) > 0.001f ? Mathf.Sign(side) : player.FacingDirection;
        float distance = Mathf.Min(penetration + 0.005f, maxPushSpeed * Mathf.Min(Time.deltaTime, 0.05f));
        // Correct only the enemy along screen-horizontal; depth and player motion stay untouched.
        body.Move(right * (sign * distance));
    }
    private static void GetCapsule(CharacterController controller, out Vector3 center, out float radius, out float segmentHalf)
    {
        Vector3 scale = controller.transform.lossyScale;
        center = controller.transform.TransformPoint(controller.center);
        radius = controller.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        segmentHalf = Mathf.Max(0f, Mathf.Max(controller.height * Mathf.Abs(scale.y), radius * 2f) * 0.5f - radius);
    }
}
