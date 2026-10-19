using Spine.Unity;
using UnityEngine;

/// <summary>
/// 依移動狀態切換 idle / walk / jump / fall，並依平面速度翻轉朝向。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SkeletonAnimation))]
public sealed class PlayerSpineAnimator : MonoBehaviour
{
    const int MainTrack = 0;

    [Header("動畫名稱")]
    [SerializeField] string _idleAnim = "idle";
    [SerializeField] string _walkAnim = "walk";
    [SerializeField] string _jumpAnim = "jump";
    [SerializeField] string _fallAnim = "fall";

    [Header("切換門檻")]
    [SerializeField] float _walkSpeedThreshold = 0.05f;
    [SerializeField] float _jumpVelocityThreshold = 0.5f;

    [Header("朝向")]
    [Tooltip("勾選後左右翻面與預設相反；若移動方向與臉朝向相反，試著切換此項。")]
    [SerializeField] bool _invertFacing;
    [SerializeField] float _facingInputThreshold = 0.01f;
    [SerializeField] float _facingVelocityThreshold = 0.05f;

    [Header("Spine 物理")]
    [Tooltip("關閉 Transform 位移/旋轉對 Spine 物理的影響，並停用骨架上的 Physics Constraint。")]
    [SerializeField] bool _disableSpinePhysics = true;

    [Header("行走動畫速度")]
    [SerializeField] bool _syncWalkAnimationSpeed = true;
    [Tooltip("角色達此移動速度時 walk 為 1 倍速；設 0 則使用 PlayerController.moveSpeed。")]
    [SerializeField] float _walkReferenceSpeed;
    [SerializeField] float _minWalkTimeScale = 0.25f;
    [SerializeField] float _maxWalkTimeScale = 1.5f;

    SkeletonAnimation _skeletonAnimation;
    SkeletonRenderer _skeletonRenderer;
    PlayerController _player;
    CharacterController _controller;
    Transform _movementPlane;
    string _currentAnim = string.Empty;
    float _facingScaleX = 1f;
    Quaternion _viewRotationOffset;

    void Awake()
    {
        _skeletonAnimation = GetComponent<SkeletonAnimation>();
        _skeletonRenderer = GetComponent<SkeletonRenderer>();
        _player = GetComponentInParent<PlayerController>();
        _controller = GetComponentInParent<CharacterController>();
        if (_player != null)
            _movementPlane = _player.movementPlane;
        if (_movementPlane != null)
            _viewRotationOffset = Quaternion.Inverse(_movementPlane.rotation) * transform.rotation;

        ApplySpinePhysicsSettings();
    }

    void Start()
    {
        DisableSkeletonPhysicsConstraints();
        PlayIfNeeded(_idleAnim, true);
    }

    void ApplySpinePhysicsSettings()
    {
        if (!_disableSpinePhysics || _skeletonRenderer == null)
            return;

        _skeletonRenderer.PhysicsPositionInheritanceFactor = Vector2.zero;
        _skeletonRenderer.PhysicsRotationInheritanceFactor = 0f;
    }

    void DisableSkeletonPhysicsConstraints()
    {
        if (!_disableSpinePhysics || _skeletonAnimation?.Skeleton == null)
            return;

        var constraints = _skeletonAnimation.Skeleton.PhysicsConstraints;
        for (int i = 0; i < constraints.Count; i++)
            constraints.Items[i].Active = false;
    }

    void LateUpdate()
    {
        if (_skeletonAnimation == null)
            return;

        SyncViewFacing();
        UpdateFacing();
        UpdateAnimation();
        UpdateWalkTimeScale();
    }

    // Rotate only the artwork. The capsule, foothold and camera pivot keep their own transforms.
    public void SyncViewFacing()
    {
        if (_movementPlane != null && transform != _movementPlane &&
            (_player == null || transform != _player.transform))
            transform.rotation = _movementPlane.rotation * _viewRotationOffset;
    }

    void UpdateAnimation()
    {
        bool grounded = _player != null ? _player.IsGrounded : _controller != null && _controller.isGrounded;
        float vy = GetVerticalSpeed();

        if (!grounded)
        {
            if (vy > _jumpVelocityThreshold)
                PlayIfNeeded(_jumpAnim, true);
            else
                PlayIfNeeded(_fallAnim, true);
            return;
        }

        float speed = GetHorizontalSpeed();
        if (speed > _walkSpeedThreshold)
            PlayIfNeeded(_walkAnim, true);
        else
            PlayIfNeeded(_idleAnim, true);
    }

    float GetHorizontalSpeed()
    {
        if (_player != null) return _player.WalkingSpeed;
        if (_controller == null)
            return 0f;

        Vector3 v = _player != null ? _player.Velocity : _controller.velocity;
        if (_movementPlane != null)
        {
            Vector3 right = _movementPlane.right;
            Vector3 forward = _movementPlane.forward;
            right.y = 0f;
            forward.y = 0f;
            right.Normalize();
            forward.Normalize();
            return new Vector3(Vector3.Dot(v, right), 0f, Vector3.Dot(v, forward)).magnitude;
        }

        return new Vector3(v.x, 0f, v.z).magnitude;
    }

    float GetVerticalSpeed()
    {
        return _player != null ? _player.Velocity.y : _controller != null ? _controller.velocity.y : 0f;
    }

    void UpdateFacing()
    {
        if (_movementPlane == null || _skeletonAnimation?.Skeleton == null)
            return;

        float planarSign = 0f;

        // 有按鍵時以輸入為準，避免加減速期間速度方向與意圖不一致
        float inputX = Input.GetAxisRaw("Horizontal");
        if (Mathf.Abs(inputX) > _facingInputThreshold)
        {
            planarSign = Mathf.Sign(inputX);
        }
        else if (_controller != null)
        {
            Vector3 right = _movementPlane.right;
            right.y = 0f;
            right.Normalize();
            float dot = Vector3.Dot(new Vector3(_controller.velocity.x, 0f, _controller.velocity.z), right);
            if (Mathf.Abs(dot) > _facingVelocityThreshold)
                planarSign = Mathf.Sign(dot);
        }

        if (planarSign == 0f)
            return;

        _facingScaleX = planarSign >= 0f ? 1f : -1f;
        if (_invertFacing)
            _facingScaleX = -_facingScaleX;

        _skeletonAnimation.Skeleton.ScaleX = _facingScaleX;
    }

    void UpdateWalkTimeScale()
    {
        if (!_syncWalkAnimationSpeed)
            return;

        var entry = _skeletonAnimation.AnimationState.GetTrack(MainTrack);
        if (entry == null)
            return;

        if (_currentAnim != _walkAnim)
        {
            entry.TimeScale = 1f;
            return;
        }

        float refSpeed = _walkReferenceSpeed > 0.01f
            ? _walkReferenceSpeed
            : (_player != null ? _player.moveSpeed : 8f);

        if (refSpeed <= 0.01f)
        {
            entry.TimeScale = 1f;
            return;
        }

        float scale = GetHorizontalSpeed() / refSpeed;
        entry.TimeScale = Mathf.Clamp(scale, _minWalkTimeScale, _maxWalkTimeScale);
    }

    void PlayIfNeeded(string animName, bool loop)
    {
        if (string.IsNullOrEmpty(animName) || _currentAnim == animName)
            return;

        _skeletonAnimation.AnimationState.SetAnimation(MainTrack, animName, loop);
        _currentAnim = animName;
    }
}
