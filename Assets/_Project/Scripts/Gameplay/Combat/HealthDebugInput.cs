using UnityEngine;

/// <summary>
/// 測試加減血。-／= 改玩家，[／] 改最近的怪物。死亡後加血會把血加回來。
/// </summary>
[DisallowMultipleComponent]
public sealed class HealthDebugInput : MonoBehaviour
{
    [SerializeField] private int step = 10;

    private Health _health;

    private void Awake()
    {
        _health = GetComponent<Health>();
    }

    private void Update()
    {
        if (_health == null)
            return;

        if (Pressed(KeyCode.Minus, KeyCode.KeypadMinus))
            _health.Damage(step, transform.position, true);
        else if (Pressed(KeyCode.Equals, KeyCode.KeypadPlus))
            _health.Heal(step);

        var monster = Monster.FindNearest(transform.position);
        if (monster == null || monster.Health == null)
            return;

        if (Pressed(KeyCode.LeftBracket))
            monster.Health.Damage(step, transform.position, true);
        else if (Pressed(KeyCode.RightBracket))
            monster.Health.Heal(step);
    }

    private void OnGUI()
    {
        if (_health == null)
            return;

        var monster = Monster.FindNearest(transform.position);
        var monsterLine = monster == null || monster.Health == null
            ? "附近沒有怪物"
            : $"怪物 {monster.Health.Current}/{monster.Health.MaxHealth}" + (monster.Health.IsDead ? "（倒地）" : "");

        ScreenHud.Draw(new Rect(16f, 188f, 520f, 78f),
            $"玩家 {_health.Current}/{_health.MaxHealth}" + (_health.IsDead ? "（倒地）" : "") + "\n" +
            monsterLine + "\n" +
            "-／= 玩家減／加血，[／] 最近怪物減／加血");
    }

    private static bool Pressed(params KeyCode[] keys)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (Input.GetKeyDown(keys[i]))
                return true;
        }

        return false;
    }
}
