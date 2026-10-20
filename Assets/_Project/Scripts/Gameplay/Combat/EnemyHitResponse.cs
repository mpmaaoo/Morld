using UnityEngine;
using UnityEngine.Events;

/// <summary>Enemy motor delegates movement here during hit stun; never moves the player.</summary>
[RequireComponent(typeof(Health), typeof(CharacterController))]
public sealed class EnemyHitResponse : MonoBehaviour
{
    [Min(0f)] public float pause = 0.045f;
    [Min(0f)] public float pushSpeed = 6f;
    [Min(0f)] public float launchSpeed = 4f;
    [Min(0f)] public float bounceSpeed = 1.8f;
    [Min(0.1f)] public float friction = 10f;
    public bool showHealthBar = true;
    public UnityEvent onHit = new UnityEvent();
    public UnityEvent onBounce = new UnityEvent();
    public UnityEvent onRecovered = new UnityEvent();
    public bool IsActive { get; private set; }
    private bool hitByPlayer;
    public bool CanBePushedByPlayer => isActiveAndEnabled && IsActive && hitByPlayer && health != null && !health.IsDead;
    public void MarkPlayerAttackHit() { if (IsActive) hitByPlayer = true; }
    private Health health;
    private CharacterController body;
    private StationaryMeleeEnemy owner;
    private Vector3 direction;
    private float pauseLeft, elapsed, horizontalSpeed, verticalSpeed, flashUntil;
    private bool bouncePending;
    private Renderer[] visuals;
    private MaterialPropertyBlock[] original;
    private MaterialPropertyBlock flash;
    private void Awake()
    {
        flash = new MaterialPropertyBlock();
        health = GetComponent<Health>(); body = GetComponent<CharacterController>(); owner = GetComponent<StationaryMeleeEnemy>();
        visuals = owner != null && owner.visualRoot != null ? owner.visualRoot.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        original = new MaterialPropertyBlock[visuals.Length];
        for (int i = 0; i < visuals.Length; i++) { original[i] = new MaterialPropertyBlock(); visuals[i].GetPropertyBlock(original[i]); }
    }
    private void OnEnable() { GetComponent<Health>().Damaged += Hit; }
    private void OnDisable()
    {
        if (health != null) health.Damaged -= Hit;
        IsActive = false;
        RestoreVisuals();
    }
    private void Hit(int damage, Vector3 source)
    {
        hitByPlayer = false;
        Vector3 axis = FezWorldViewState.Active != null ? FezWorldViewState.Active.DepthAxisWorld : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, axis).normalized;
        float side = Vector3.Dot(body.bounds.center - source, right);
        direction = right * (Mathf.Abs(side) > 0.001f ? Mathf.Sign(side) : 1f);
        pauseLeft = pause; elapsed = 0f; horizontalSpeed = pushSpeed; verticalSpeed = launchSpeed;
        bouncePending = true; IsActive = true; flashUntil = Time.time + 0.12f;
        if (owner != null)
        {
            owner.RememberAttackOrigin(source);
            owner.CancelAttackForReturn(); owner.FaceDirection(-direction);
        }
        onHit.Invoke();
    }
    public void SimulateMotion(float frameTime)
    {
        float dt = Mathf.Min(frameTime, 0.1f);
        if (pauseLeft > 0f) { float taken = Mathf.Min(dt, pauseLeft); pauseLeft -= taken; dt -= taken; }
        while (dt > 0.000001f && IsActive)
        {
            float h = Mathf.Min(dt, 1f / 120f); dt -= h; elapsed += h;
            horizontalSpeed = Mathf.MoveTowards(horizontalSpeed, 0f, (body.isGrounded ? friction : 1.5f) * h);
            verticalSpeed = Mathf.Max(-20f, verticalSpeed - 25f * h);
            var flags = body.Move((direction * horizontalSpeed + Vector3.up * verticalSpeed) * h);
            if ((flags & CollisionFlags.Sides) != 0) horizontalSpeed = 0f;
            if ((flags & CollisionFlags.Above) != 0 && verticalSpeed > 0f) verticalSpeed = 0f;
            if ((flags & CollisionFlags.Below) != 0 && verticalSpeed <= 0f)
            {
                if (bouncePending) { bouncePending = false; verticalSpeed = bounceSpeed; horizontalSpeed *= 0.7f; onBounce.Invoke(); }
                else { verticalSpeed = 0f; if (horizontalSpeed < 0.2f && elapsed >= 0.35f) End(); }
            }
            if (elapsed >= 2f) End();
        }
    }
    private void End() { IsActive = false; if (!health.IsDead) onRecovered.Invoke(); }
    private void LateUpdate()
    {
        for (int i = 0; i < visuals.Length; i++)
        {
            if (visuals[i] == null) continue;
            if (Time.time >= flashUntil) { visuals[i].SetPropertyBlock(original[i]); continue; }
            visuals[i].GetPropertyBlock(flash); flash.SetColor("_Color", Color.red); flash.SetColor("_BaseColor", Color.red);
            visuals[i].SetPropertyBlock(flash);
        }
    }
    private void RestoreVisuals() { if (visuals == null) return; for (int i = 0; i < visuals.Length; i++) if (visuals[i] != null) visuals[i].SetPropertyBlock(original[i]); }
    private void OnGUI()
    {
        if (!showHealthBar || body == null || Camera.main == null) return;
        Vector3 screen = Camera.main.WorldToScreenPoint(body.bounds.max + Vector3.up * 0.2f);
        if (screen.z <= 0f) return;
        float x = screen.x - 45f, y = Screen.height - screen.y - 72f;
        Color previous = GUI.color;
        GUI.color = Color.black; GUI.DrawTexture(new Rect(x, y, 90f, 9f), Texture2D.whiteTexture);
        GUI.color = health.IsDead ? Color.gray : Color.red;
        GUI.DrawTexture(new Rect(x + 1f, y + 1f, 88f * health.Current / Mathf.Max(1, health.MaxHealth), 7f), Texture2D.whiteTexture);
        GUI.color = previous; GUI.Label(new Rect(x, y - 21f, 100f, 21f), $"HP {health.Current}/{health.MaxHealth}");
    }
}
