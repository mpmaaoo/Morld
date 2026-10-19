using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 測試怪。不自己走路，只套用與格子測試玩家相同的重力。
/// 攻擊看 3D 碰撞箱有沒有相交，不用正交畫面上的重疊。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Health))]
public sealed class Monster : MonoBehaviour
{
    private static readonly List<Monster> Alive = new List<Monster>();

    [SerializeField] private int contactDamage = 10;
    [SerializeField] private float gravity = -25f;
    [SerializeField] private float maxFallSpeed = 10f;

    private Health _health;
    private CapsuleCollider _hitbox;
    private FezGridMap _map;
    private float _verticalVelocity;

    public Health Health => _health;

    public static Monster FindNearest(Vector3 position)
    {
        Monster best = null;
        var bestDistance = float.PositiveInfinity;
        for (var i = 0; i < Alive.Count; i++)
        {
            var monster = Alive[i];
            if (monster == null)
                continue;
            var distance = (monster.transform.position - position).sqrMagnitude;
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = monster;
        }

        return best;
    }

    private void Awake()
    {
        _health = GetComponent<Health>();
        _hitbox = GetComponentInChildren<CapsuleCollider>();
    }

    private void OnEnable()
    {
        Alive.Add(this);
    }

    private void OnDisable()
    {
        Alive.Remove(this);
    }

    private void Update()
    {
        ApplyGravity();
        if (_health.IsDead)
            return;

        TryContactDamage();
    }

    private void ApplyGravity()
    {
        if (_map == null)
            _map = FindObjectOfType<FezGridMap>();

        var feet = transform.position;
        var hasFloor = TryFindSupport(_map, feet, out var floor);
        if (hasFloor)
        {
            var gap = feet.y - floor.TopY;
            if (gap <= 0.2f)
            {
                _verticalVelocity = -2f;
                if (gap > 0.001f || gap < -0.001f)
                    transform.position = new Vector3(feet.x, floor.TopY, feet.z);
                return;
            }
        }

        _verticalVelocity += gravity * Time.deltaTime;
        if (maxFallSpeed > 0f)
            _verticalVelocity = Mathf.Max(_verticalVelocity, -maxFallSpeed);

        var nextY = feet.y + _verticalVelocity * Time.deltaTime;
        if (hasFloor && nextY <= floor.TopY)
        {
            nextY = floor.TopY;
            _verticalVelocity = -2f;
        }

        transform.position = new Vector3(feet.x, nextY, feet.z);
    }

    /// <summary>
    /// 用世界格子找腳下最高的平台。不看目前視角的深度殼，轉視角不會把地板抽掉。
    /// </summary>
    private static bool TryFindSupport(FezGridMap map, Vector3 feet, out FezGridBlock floor)
    {
        floor = null;
        if (map == null)
            return false;

        var bestTop = float.NegativeInfinity;
        foreach (var block in map.Blocks)
        {
            if (block == null || !block.Solid || map.HasSolid(block.Cell + Vector3Int.up))
                continue;

            var cell = block.Cell;
            if (feet.x < cell.x - 0.02f || feet.x > cell.x + 1.02f)
                continue;
            if (feet.z < cell.z - 0.02f || feet.z > cell.z + 1.02f)
                continue;
            if (block.TopY > feet.y + 0.2f || block.TopY <= bestTop)
                continue;

            bestTop = block.TopY;
            floor = block;
        }

        return floor != null;
    }

    private void TryContactDamage()
    {
        if (_hitbox == null)
            _hitbox = GetComponentInChildren<CapsuleCollider>();
        if (_hitbox == null)
            return;

        var hosts = FindObjectsOfType<Health>();
        for (var i = 0; i < hosts.Length; i++)
        {
            var other = hosts[i];
            if (other == null || other == _health || other.IsDead || other.GetComponent<Monster>() != null)
                continue;

            var otherBody = other.GetComponent<CharacterController>();
            if (otherBody == null || !CapsulesOverlap(_hitbox, otherBody))
                continue;

            var center = _hitbox.transform.TransformPoint(_hitbox.center);
            other.Damage(contactDamage, center);
            // 無敵時不會扣血，但碰撞箱還疊在一起就要繼續被擊退。
            var gridPlayer = other.GetComponent<FezGridTestPlayer>();
            if (gridPlayer != null)
                gridPlayer.KnockbackFrom(center);
            break;
        }
    }

    private static bool CapsulesOverlap(CapsuleCollider attacker, CharacterController defender)
    {
        GetCapsuleSegment(attacker, out var a0, out var a1, out var aRadius);
        GetCapsuleSegment(defender, out var b0, out var b1, out var bRadius);
        var reach = aRadius + bRadius;
        return SqrDistanceSegmentSegment(a0, a1, b0, b1) <= reach * reach;
    }

    private static void GetCapsuleSegment(CapsuleCollider capsule, out Vector3 p0, out Vector3 p1, out float radius)
    {
        var scale = capsule.transform.lossyScale;
        Vector3 axis;
        float heightScale;
        float radiusScale;
        switch (capsule.direction)
        {
            case 0:
                axis = capsule.transform.right;
                heightScale = Mathf.Abs(scale.x);
                radiusScale = Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                break;
            case 2:
                axis = capsule.transform.forward;
                heightScale = Mathf.Abs(scale.z);
                radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
                break;
            default:
                axis = capsule.transform.up;
                heightScale = Mathf.Abs(scale.y);
                radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                break;
        }

        radius = capsule.radius * radiusScale;
        var height = Mathf.Max(capsule.height * heightScale, radius * 2f);
        var center = capsule.transform.TransformPoint(capsule.center);
        var half = height * 0.5f - radius;
        p0 = center - axis * half;
        p1 = center + axis * half;
    }

    private static void GetCapsuleSegment(CharacterController controller, out Vector3 p0, out Vector3 p1, out float radius)
    {
        var scale = controller.transform.lossyScale;
        var scaleY = Mathf.Abs(scale.y);
        var scaleX = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
        radius = controller.radius * scaleX;
        var height = Mathf.Max(controller.height * scaleY, radius * 2f);
        var center = controller.transform.TransformPoint(controller.center);
        var half = height * 0.5f - radius;
        var up = controller.transform.up;
        p0 = center - up * half;
        p1 = center + up * half;
    }

    private static float SqrDistanceSegmentSegment(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);
        const float epsilon = 0.0001f;
        float s;
        float t;
        if (a <= epsilon && e <= epsilon)
            return r.sqrMagnitude;

        if (a <= epsilon)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= epsilon)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > epsilon ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }

        var c1 = p1 + d1 * s;
        var c2 = p2 + d2 * t;
        return (c1 - c2).sqrMagnitude;
    }

}
