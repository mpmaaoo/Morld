using UnityEngine;
using UnityEngine.Serialization;

namespace Lichronicle.Gameplay.Abilities
{
    /// <summary>
    /// FEZ 式四向視角切換。每次觸發時，讓指定樞紐平滑旋轉一個固定角度。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class CameraRotationAbility : MonoBehaviour, IAbilityHandler
    {
        [Tooltip("掛在 Player 根物件底下、只控制相機和移動平面的樞紐；不可指定 Player 根物件。")]
        [FormerlySerializedAs("targetPivot")]
        [SerializeField] private Transform cameraPivot;
        [SerializeField] private float stepDegrees = 90f;
        [SerializeField] private float rotateDuration = 0.35f;
        private bool _isRotating;
        private float _elapsed;
        private Quaternion _fromRot;
        private Quaternion _toRot;
        private FezWorldViewState _worldViewState;
        private FezRotationOcclusionGate _occlusionGate;

        private void Awake()
        {
            CacheRelatedComponents();
        }

        private void OnEnable()
        {
            CacheRelatedComponents();
        }

        private void OnDisable()
        {
            // 元件若在動畫途中停用，也要解除深度移動鎖，避免玩家永久卡住。
            if (!_isRotating)
                return;

            FinishRotation(true);
        }

        private void Update()
        {
            AdvanceRotation(Time.deltaTime);
        }

        public void AdvanceRotation(float deltaTime)
        {
            if (!_isRotating)
                return;

            if (cameraPivot == null)
            {
                FinishRotation(false);
                return;
            }

            _elapsed += Mathf.Clamp(deltaTime, 0f, 0.1f);
            var duration = Mathf.Max(0f, rotateDuration);
            var t = duration <= 0.0001f ? 1f : Mathf.Clamp01(_elapsed / duration);
            cameraPivot.localRotation = Quaternion.Slerp(_fromRot, _toRot, t);

            if (t >= 1f)
                FinishRotation(true);
        }

        public void OnAbilityTriggered(AbilityDefinition definition, bool active)
        {
            if (!isActiveAndEnabled || !active || definition == null || definition.kind != AbilityKind.CameraRotate)
                return;
            if (_isRotating || cameraPivot == null)
                return;

            CacheRelatedComponents();

            var direction = Mathf.Approximately(definition.floatParam, 0f)
                ? 1f
                : Mathf.Sign(definition.floatParam);
            var angleStep = Mathf.Abs(stepDegrees);
            if (angleStep <= 0.0001f)
                return;

            _isRotating = true;
            _elapsed = 0f;
            _fromRot = cameraPivot.localRotation;

            var currentYaw = cameraPivot.localEulerAngles.y;
            var snappedYaw = Mathf.Round(currentYaw / 90f) * 90f;
            var targetYaw = snappedYaw + angleStep * direction;
            _toRot = Quaternion.Euler(0f, targetYaw, 0f);

            if (_occlusionGate != null)
                _occlusionGate.SetRotationInProgress(true);

            if (rotateDuration <= 0.0001f)
                FinishRotation(true);
        }

        private void FinishRotation(bool applyTargetRotation)
        {
            if (applyTargetRotation && cameraPivot != null)
                cameraPivot.localRotation = _toRot;

            _isRotating = false;

            if (_worldViewState != null)
                _worldViewState.RefreshFromPivot();

            if (_occlusionGate != null)
                _occlusionGate.ScheduleViewRotationCompletion();
        }

        private void CacheRelatedComponents()
        {
            if (cameraPivot == null)
                return;

            _worldViewState = cameraPivot.GetComponent<FezWorldViewState>();
            if (_worldViewState == null)
                _worldViewState = cameraPivot.GetComponentInParent<FezWorldViewState>();
            if (_worldViewState == null)
                _worldViewState = cameraPivot.GetComponentInChildren<FezWorldViewState>();

            _occlusionGate = cameraPivot.GetComponent<FezRotationOcclusionGate>();
            if (_occlusionGate == null)
                _occlusionGate = cameraPivot.GetComponentInParent<FezRotationOcclusionGate>();
            if (_occlusionGate == null)
                _occlusionGate = cameraPivot.GetComponentInChildren<FezRotationOcclusionGate>();
        }
    }
}
