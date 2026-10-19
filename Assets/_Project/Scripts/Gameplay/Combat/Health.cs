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
        if (!ignoreInvulnerability && Time.time < _invulnerableUntil)
            return;

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
}
