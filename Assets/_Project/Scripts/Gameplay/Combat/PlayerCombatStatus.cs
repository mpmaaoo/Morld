using UnityEngine;

/// <summary>Temporary combat HUD. Presentation can be replaced independently of Health.</summary>
[RequireComponent(typeof(Health))]
public sealed class PlayerCombatStatus : MonoBehaviour
{
    [Tooltip("美術可接受擊動畫、閃白、音效；位移由 PlayerController 處理。")]
    public UnityEngine.Events.UnityEvent onHit = new UnityEngine.Events.UnityEvent();
    private Health health;
    private PlayerController motor;
    private float flashUntil;
    private bool showControls;
    private bool hitFromLeft = true;
    private void Awake() { health = GetComponent<Health>(); motor = GetComponent<PlayerController>(); }
    private void OnEnable() { GetComponent<Health>().Damaged += OnDamaged; }
    private void OnDisable() { if (health != null) health.Damaged -= OnDamaged; }
    private void OnDamaged(int amount, Vector3 source)
    {
        flashUntil = Time.time + 0.25f;
        if (motor != null) motor.ApplyHitResponse(source);
        onHit.Invoke();
    }
    private void OnGUI()
    {
        var previous = GUI.color;
        GUI.color = Time.time < flashUntil ? Color.red : Color.white;
        GUI.Box(new Rect(16, Screen.height - 56, 240, 38), health.IsDead ? "HP 0 — Test: F9 to recover" : $"HP {health.Current} / {health.MaxHealth}");
        GUI.color = previous;
        if (GUI.Button(new Rect(264, Screen.height - 56, 130, 38), "HP tools [F10]")) showControls = !showControls;
        if (!showControls) return;
        float panelX = Mathf.Max(8f, Screen.width - 316f);
        float panelY = Mathf.Max(8f, Screen.height - 272f);
        GUILayout.BeginArea(new Rect(panelX, panelY, 300, 204), GUI.skin.box);
        GUILayout.Label($"TEST HP  {health.Current} / {health.MaxHealth}");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("HP -1 / hit")) TestHit();
        if (GUILayout.Button("HP +1")) health.Heal(1);
        if (GUILayout.Button("Full [F9]")) health.Heal(health.MaxHealth);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Max HP (1-9)");
        if (GUILayout.Button("-")) health.SetMax(Mathf.Max(1, health.MaxHealth - 1), false);
        if (GUILayout.Button("+")) health.SetMax(Mathf.Min(9, health.MaxHealth + 1), false);
        GUILayout.EndHorizontal();
        health.DebugInvincible = GUILayout.Toggle(health.DebugInvincible, "Invincible (enemy damage off)");
        hitFromLeft = GUILayout.Toggle(hitFromLeft, "Test hit from LEFT (off = right)");
        GUILayout.Label("Test hit bypasses invincibility. Runtime only.");
        GUILayout.EndArea();
    }
    private void TestHit()
    {
        Vector3 right = motor != null ? Vector3.Cross(Vector3.up, motor.DepthAxis).normalized : Vector3.right;
        health.Damage(1, transform.position + right * (hitFromLeft ? -1f : 1f), true);
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F9)) health.Heal(health.MaxHealth);
        if (Input.GetKeyDown(KeyCode.F10)) showControls = !showControls;
    }
}
