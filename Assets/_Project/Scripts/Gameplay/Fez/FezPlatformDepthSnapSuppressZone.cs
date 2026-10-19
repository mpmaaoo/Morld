using UnityEngine;

/// <summary>
/// 掛在平台（Block）上：此平台的判定區與「正在繞行的背景牆」重疊時，
/// 只暫停這塊平台的深度傳送，直到判定區脫離該牆。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class FezPlatformDepthSnapSuppressZone : MonoBehaviour
{
    private static readonly System.Collections.Generic.HashSet<FezPlatformDepthSnapSuppressZone> ActiveZones =
        new System.Collections.Generic.HashSet<FezPlatformDepthSnapSuppressZone>();

    private BoxCollider _box;

    public static System.Collections.Generic.IEnumerable<FezPlatformDepthSnapSuppressZone> EnumerateActiveZones() => ActiveZones;

    private void OnEnable()
    {
        ActiveZones.Add(this);
        DisablePhysicsCollider();
    }

    private void OnDisable()
    {
        ActiveZones.Remove(this);
    }

    private void Awake()
    {
        _box = GetComponent<BoxCollider>();
        DisablePhysicsCollider();
    }

    private void DisablePhysicsCollider()
    {
        if (_box == null)
            _box = GetComponent<BoxCollider>();
        if (_box != null)
            _box.enabled = false;
    }

    public Bounds GetWorldBounds()
    {
        if (_box == null)
            return new Bounds(transform.position, Vector3.one);

        var center = transform.TransformPoint(_box.center);
        var size = Vector3.Scale(_box.size, Abs(transform.lossyScale));
        return new Bounds(center, size);
    }

    public bool OverlapsBypassObject(FezBackgroundWall wall)
    {
        if (wall == null)
            return false;

        var zone = GetWorldBounds();
        if (zone.Intersects(wall.GetBodyWorldBounds()))
            return true;

        return zone.Intersects(wall.GetRoutingColumnBounds());
    }

    private static Vector3 Abs(Vector3 v) =>
        new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        var box = _box != null ? _box : GetComponent<BoxCollider>();
        if (box == null)
            return;

        var center = transform.TransformPoint(box.center);
        var ext = Vector3.Scale(box.size * 0.5f, Abs(transform.lossyScale));
        Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.22f);
        Gizmos.DrawCube(center, ext * 2f);
        Gizmos.color = new Color(1f, 0.75f, 0.2f, 0.85f);
        Gizmos.DrawWireCube(center, ext * 2f);
    }
#endif
}
