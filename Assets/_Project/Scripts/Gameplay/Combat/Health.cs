using System;
using UnityEngine;

/// <summary>
/// 血量。玩家與怪物共用。死亡後仍可用治療把血加回來，方便測試。
/// </summary>
[DisallowMultipleComponent]
public sealed class Health : MonoBehaviour
{
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private float invulnerableSeconds = 0.25f;
    [Tooltip("玩家每次有效受擊固定扣 1，避免舊攻擊設定造成大量傷害。")]
    [SerializeField] private bool oneDamagePerHit;
    public bool DebugInvincible { get; set; }
    public bool DodgeInvulnerable { get; set; }

    private float _invulnerableUntil;

    public int MaxHealth => maxHealth;
    public int Current { get; private set; }
    public bool IsDead => Current <= 0;

    public event Action<int, Vector3> Damaged;
    public event Action<int> Healed;
    public event Action Died;

    private void Awake()
    {
        maxHealth = Mathf.Max(1, maxHealth);
        Current = maxHealth;
    }

    public void SetMax(int value, bool fill)
    {
        maxHealth = Mathf.Max(1, value);
        if (fill || Current > maxHealth)
            Current = maxHealth;
    }

    public void Damage(int amount, Vector3 sourcePosition, bool ignoreInvulnerability = false)
    {
        if (amount <= 0 || IsDead)
            return;
        if ((DebugInvincible || DodgeInvulnerable) && !ignoreInvulnerability) return;
        if (!ignoreInvulnerability && Time.time < _invulnerableUntil)
            return;

        if (oneDamagePerHit) amount = 1;

        Current = Mathf.Max(0, Current - amount);
        if (invulnerableSeconds > 0f && !ignoreInvulnerability)
            _invulnerableUntil = Time.time + invulnerableSeconds;

        Damaged?.Invoke(amount, sourcePosition);
        if (Current <= 0)
            Died?.Invoke();
    }

    public void Heal(int amount)
    {
        if (amount <= 0 || Current >= maxHealth)
            return;

        var before = Current;
        Current = Mathf.Min(maxHealth, Current + amount);
        var gained = Current - before;
        if (gained <= 0)
            return;

        Healed?.Invoke(gained);
    }
    /// <summary>Execution is explicitly separate from the normal one-damage-per-hit rule.</summary>
    public bool TryExecute(Vector3 sourcePosition)
    {
        if (IsDead || DebugInvincible || DodgeInvulnerable || Time.time < _invulnerableUntil) return false;
        int remaining = Current;
        Current = 0;
        _invulnerableUntil = Time.time + Mathf.Max(0f, invulnerableSeconds);
        Damaged?.Invoke(remaining, sourcePosition);
        Died?.Invoke();
        return true;
    }
    public bool CanReceiveHit => !IsDead && !DebugInvincible && !DodgeInvulnerable && Time.time >= _invulnerableUntil;
}
