using UnityEngine;
using UnityEngine.Events;

/// <summary>Presentation-only depth signal; never changes collision or attack eligibility.</summary>
public sealed class EnemyDepthPresentation : MonoBehaviour
{
    public enum DepthState { SameDepth, InFrontOfPlayer, BehindPlayer }
    [Min(0f)] public float sameDepthTolerance = 0.25f;
    public Transform playerOverride;
    public GameObject sameDepthEffect, frontEffect, behindEffect;
    public UnityEvent<DepthState> onStateChanged = new UnityEvent<DepthState>();
    public UnityEvent<float> onDepthDifferenceChanged = new UnityEvent<float>();
    public DepthState State { get; private set; }
    public float SignedDepthDifference { get; private set; }
    private StationaryMeleeEnemy enemy;
    private bool initialized;
    private float previousDepth = float.PositiveInfinity;
    private void Awake() { enemy = GetComponent<StationaryMeleeEnemy>(); }
    private void LateUpdate()
    {
        Transform player = playerOverride != null ? playerOverride : enemy != null ? enemy.Target : null;
        if (player == null) { initialized = false; SetEffects(false); return; }
        Vector3 axis = FezWorldViewState.Active != null ? FezWorldViewState.Active.DepthAxisWorld : Vector3.forward;
        var ownBody = GetComponent<CharacterController>();
        var playerBody = player.GetComponent<CharacterController>();
        Vector3 ownCenter = ownBody != null ? ownBody.bounds.center : transform.position;
        Vector3 playerCenter = playerBody != null ? playerBody.bounds.center : player.position;
        SignedDepthDifference = Vector3.Dot(ownCenter - playerCenter, axis);
        DepthState next = Mathf.Abs(SignedDepthDifference) <= sameDepthTolerance ? DepthState.SameDepth
            : SignedDepthDifference < 0f ? DepthState.InFrontOfPlayer : DepthState.BehindPlayer;
        if (!initialized || next != State)
        {
            State = next; initialized = true; SetEffects(true); onStateChanged.Invoke(State);
        }
        if (Mathf.Abs(previousDepth - SignedDepthDifference) >= 0.01f)
        { previousDepth = SignedDepthDifference; onDepthDifferenceChanged.Invoke(SignedDepthDifference); }
    }
    private void SetEffects(bool enabled)
    {
        if (sameDepthEffect != null && sameDepthEffect != gameObject) sameDepthEffect.SetActive(enabled && State == DepthState.SameDepth);
        if (frontEffect != null && frontEffect != gameObject) frontEffect.SetActive(enabled && State == DepthState.InFrontOfPlayer);
        if (behindEffect != null && behindEffect != gameObject) behindEffect.SetActive(enabled && State == DepthState.BehindPlayer);
    }
    private void OnDisable() { SetEffects(false); initialized = false; previousDepth = float.PositiveInfinity; }
}
