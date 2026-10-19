using System.Collections.Generic;
using UnityEngine;

/// <summary>Reports collider GameObject lifecycle changes without a per-frame callback.</summary>
[DisallowMultipleComponent]
public sealed class FezPlatformCollisionLifecycle : MonoBehaviour
{
    private readonly HashSet<FezOneWayPlatform> _owners = new HashSet<FezOneWayPlatform>();

    public void Register(FezOneWayPlatform platform) { _owners.Add(platform); }
    public void Unregister(FezOneWayPlatform platform) { _owners.Remove(platform); }

    private void OnEnable() { NotifyChanged(); }
    private void OnDisable() { NotifyChanged(); }

    /// <summary>Call after directly toggling a collider or its Rigidbody on this GameObject.</summary>
    public void NotifyChanged()
    {
        foreach (var owner in _owners)
            if (owner != null) owner.NotifyCollisionStateChanged();
    }
}
