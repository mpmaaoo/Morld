using System.Collections.Generic;
using Lichronicle.Gameplay.Inputs;
using UnityEngine;

namespace Lichronicle.Gameplay.Abilities
{
    /// <summary>
    /// 掛在玩家身上：依據 AbilityDefinition 監聽按鍵，呼叫對應的能力行為。
    /// 具體效果由其他 Component 實作（CameraRotationAbility / PhantomRecorderAbility / WorldShiftAbility）。
    /// </summary>
    public sealed class AbilityController : MonoBehaviour
    {
        [SerializeField] private List<AbilityDefinition> abilities = new();

        private readonly Dictionary<AbilityKind, float> _cooldownUntil = new();
        private readonly HashSet<AbilityKind> _toggledOn = new();

        public bool IsToggledOn(AbilityKind kind) => _toggledOn.Contains(kind);

        private IPlayerInputSource GetInputSource()
        {
            return PlayerInputResolver.Resolve(gameObject);
        }

        private void Update()
        {
            var motor = GetComponent<PlayerController>();
            if (motor != null && motor.IsActionLocked) return;
            var inputSource = GetInputSource();
            var now = Time.time;

            for (var i = 0; i < abilities.Count; i++)
            {
                var def = abilities[i];

                if (def == null || def.kind == AbilityKind.None)
                    continue;

                if (def.key == KeyCode.None && def.alternateKey == KeyCode.None)
                    continue;

                var keyDown = IsAbilityKeyDown(inputSource, def.key) || IsAbilityKeyDown(inputSource, def.alternateKey);

                if (!keyDown)
                    continue;

                _cooldownUntil.TryGetValue(def.kind, out var until);

                if (now < until)
                    continue;

                if (def.toggle)
                {
                    if (_toggledOn.Contains(def.kind))
                    {
                        _toggledOn.Remove(def.kind);
                        BroadcastAbility(def, false);
                    }
                    else
                    {
                        _toggledOn.Add(def.kind);
                        BroadcastAbility(def, true);
                    }
                }
                else
                {
                    BroadcastAbility(def, true);

                    if (def.cooldownSeconds > 0f)
                        _cooldownUntil[def.kind] = now + def.cooldownSeconds;
                }
            }
        }

        static bool IsAbilityKeyDown(IPlayerInputSource inputSource, KeyCode key)
        {
            if (key == KeyCode.None)
                return false;

            return inputSource != null
                ? inputSource.GetKeyDown(key)
                : Input.GetKeyDown(key);
        }

        private void BroadcastAbility(AbilityDefinition def, bool active)
        {
            // 將事件往同一個 GameObject 上的能力元件廣播
            var handlers = GetComponents<IAbilityHandler>();
            for (var i = 0; i < handlers.Length; i++)
            {
                handlers[i].OnAbilityTriggered(def, active);
            }
        }
    }
}

