using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 受擊時閃白。格子玩家改由自己沿畫面左右擊退；沒有角色控制器的物件才直接被推開。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Health))]
public sealed class HitFeedback : MonoBehaviour
{
    [SerializeField] private float flashSeconds = 0.12f;
    [SerializeField] private float knockbackDistance = 0.35f;
    [SerializeField] private Color flashColor = Color.white;

    private Health _health;
    private Renderer[] _renderers;
    private readonly List<Color> _baseColors = new List<Color>();
    private float _flashUntil;
    private Vector3 _knockFrom;
    private float _knockRemaining;

    private void Awake()
    {
        _health = GetComponent<Health>();
        _renderers = GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in _renderers)
        {
            var color = Color.white;
            if (renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty("_Color"))
                color = renderer.sharedMaterial.color;
            _baseColors.Add(color);
        }
    }

    private void OnEnable()
    {
        if (_health != null)
            _health.Damaged += OnDamaged;
    }

    private void OnDisable()
    {
        if (_health != null)
            _health.Damaged -= OnDamaged;
    }

    private void OnDamaged(int amount, Vector3 sourcePosition)
    {
        _flashUntil = Time.time + flashSeconds;
        var player = GetComponent<FezGridTestPlayer>();
        if (player != null)
        {
            player.KnockbackFrom(sourcePosition);
            return;
        }

        if (GetComponent<CharacterController>() != null || GetComponent<Monster>() != null || knockbackDistance <= 0f)
            return;

        var away = transform.position - sourcePosition;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
            away = transform.right;
        _knockFrom = away.normalized * knockbackDistance;
        _knockRemaining = 1f;
    }

    private void Update()
    {
        ApplyFlash();
        ApplyKnockback();
    }

    private void ApplyFlash()
    {
        var flashing = Time.time < _flashUntil;
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (renderer == null)
                continue;

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            var color = flashing ? flashColor : _baseColors[i];
            if (_health != null && _health.IsDead && !flashing)
                color *= 0.35f;
            block.SetColor("_Color", color);
            renderer.SetPropertyBlock(block);
        }
    }

    private void ApplyKnockback()
    {
        if (_knockRemaining <= 0f)
            return;

        var step = Mathf.Min(1f, Time.deltaTime / 0.08f);
        _knockRemaining = Mathf.Max(0f, _knockRemaining - step);
        transform.position += _knockFrom * step;
    }
}
