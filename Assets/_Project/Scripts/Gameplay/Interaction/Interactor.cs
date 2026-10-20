using Lichronicle.Gameplay.Inputs;
using UnityEngine;

namespace Lichronicle.Gameplay.Interaction
{
    /// <summary>
    /// 掛在玩家身上：用一個簡單的半徑偵測找最近的可互動物件。
    /// </summary>
    public sealed class Interactor : MonoBehaviour
    {
        [SerializeField] private float radius = 1.6f;
        [SerializeField] private LayerMask interactableMask = ~0;
        [SerializeField] private KeyCode interactKey = KeyCode.E;

        private IInteractable _current;

        public IInteractable Current => _current;

        private IPlayerInputSource GetInputSource()
        {
            return PlayerInputResolver.Resolve(gameObject);
        }

        private void Update()
        {
            var motor = GetComponent<PlayerController>();
            if (motor != null && motor.IsActionLocked) return;
            _current = FindBestInteractable();

            var inputSource = GetInputSource();

            var keyDown = inputSource != null
                ? inputSource.GetKeyDown(interactKey)
                : Input.GetKeyDown(interactKey);

            if (_current != null && keyDown)
            {
                if (_current.CanInteract(this))
                    _current.Interact(this);
            }
        }

        private IInteractable FindBestInteractable()
        {
            var hits = Physics.OverlapSphere(transform.position, radius, interactableMask, QueryTriggerInteraction.Collide);
            IInteractable best = null;
            var bestDist = float.PositiveInfinity;

            for (var i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                var interactable = col.GetComponentInParent<IInteractable>();
                if (interactable == null)
                    continue;
                if (!interactable.CanInteract(this))
                    continue;

                var d = (col.transform.position - transform.position).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = interactable;
                }
            }

            return best;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 1f, 0.7f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
    }
}

