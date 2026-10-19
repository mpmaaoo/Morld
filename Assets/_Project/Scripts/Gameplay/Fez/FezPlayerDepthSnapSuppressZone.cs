using UnityEngine;

/// <summary>
/// 相容舊版 Router 的玩家判定區元件。
/// 若場景已改為平台判定區流程，可不使用此元件。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public sealed class FezPlayerDepthSnapSuppressZone : MonoBehaviour
{
    private BoxCollider _box;

    private void Awake()
    {
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
}
