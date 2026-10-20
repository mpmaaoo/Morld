using UnityEngine;

/// <summary>One explicit encounter position; no random or global enemy generation.</summary>
public sealed class EnemySpawnPoint : MonoBehaviour
{
    public StationaryMeleeEnemy enemyPrefab;
    [Tooltip("優先使用種類的 Prefab；未指定時沿用 enemyPrefab。")]
    public EnemyDefinition enemyType;
    public bool overrideSettings;
    public EnemyTuning settings = new EnemyTuning();
    [Tooltip("追擊敵人使用的實體 3D 尋路區域；留空時依出生位置尋找。")]
    public EnemyNavigationArea navigationArea;
    [Tooltip("依序拖入場景中的巡邏點；空白時返回出生點。點的座標代表腳底。")]
    public Transform[] patrolPoints;
    public bool spawnOnStart = true;
    public bool respawn;
    [Min(0.1f)] public float respawnDelay = 5f;
    private StationaryMeleeEnemy instance;
    private float spawnAt = -1f;
    private void Start() { if (spawnOnStart) Spawn(); }
    public void Spawn()
    {
        var prefab = enemyType != null && enemyType.prefab != null ? enemyType.prefab : enemyPrefab;
        if (instance != null || prefab == null) return;
        instance = Instantiate(prefab, transform.position, transform.rotation);
        instance.patrolPoints = patrolPoints;
        if (navigationArea != null) instance.navigationArea = navigationArea;
        instance.Configure(enemyType != null ? enemyType : prefab.definition, overrideSettings ? settings : null);
        instance.GetComponent<Health>().Died += OnDeath;
        spawnAt = -1f;
    }
    private void OnDeath() { if (respawn) spawnAt = Time.time + respawnDelay; }
    private void Update()
    {
        if (spawnAt < 0f || Time.time < spawnAt) return;
        Despawn();
        Spawn();
    }
    public void Despawn()
    {
        spawnAt = -1f;
        if (instance == null) return;
        instance.GetComponent<Health>().Died -= OnDeath;
        Destroy(instance.gameObject);
        instance = null;
    }
    private void OnDestroy() { Despawn(); }
    private void OnDrawGizmos()
    {
        Gizmos.color = enemyType != null ? enemyType.gizmoColor : new Color(1f, 0.45f, 0.1f);
        Gizmos.DrawWireCube(transform.position + Vector3.up * 0.5f, new Vector3(0.55f, 1f, 0.55f));
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.75f);
    }

    private void OnDrawGizmosSelected()
    {
        var type = enemyType != null ? enemyType : enemyPrefab != null ? enemyPrefab.definition : null;
        var tuning = overrideSettings ? settings : type != null ? type.settings : null;
        StationaryMeleeEnemy.DrawSightGizmo(transform.position + Vector3.up * 0.7f, transform.forward,
            tuning != null ? tuning.detectionDistance : 8f, tuning != null ? tuning.viewAngle : 110f);
        if (tuning == null || tuning.scanWhileIdle)
        {
            float scan = tuning != null ? tuning.scanHalfAngle : 50f;
            float angle = tuning != null ? tuning.viewAngle : 110f;
            float sightRange = tuning != null ? tuning.detectionDistance : 8f;
            Gizmos.color = Color.gray;
            Vector3 eye = transform.position + Vector3.up * 0.7f;
            Gizmos.DrawRay(eye, Quaternion.AngleAxis(-scan - angle * 0.5f, Vector3.up) * transform.forward * sightRange);
            Gizmos.DrawRay(eye, Quaternion.AngleAxis(scan + angle * 0.5f, Vector3.up) * transform.forward * sightRange);
        }
        float range = tuning != null ? tuning.reach : enemyPrefab != null ? enemyPrefab.reach : 1.4f;
        float height = tuning != null ? tuning.verticalReach : enemyPrefab != null ? enemyPrefab.verticalReach : 0.8f;
        Vector3 axis = FezWorldViewState.Active != null ? FezWorldViewState.Active.DepthAxisWorld : Vector3.forward;
        Vector3 right = Vector3.Cross(Vector3.up, axis);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(transform.position + Vector3.up * 0.5f,
            new Vector3(Mathf.Abs(right.x) * range * 2f + 0.05f, height * 2f, Mathf.Abs(right.z) * range * 2f + 0.05f));
        if (tuning == null || tuning.movement != EnemyTuning.Movement.Patrol) return;
        Gizmos.color = Color.cyan;
        Vector3 offset = transform.right * tuning.patrolHalfDistance;
        Gizmos.DrawLine(transform.position - offset, transform.position + offset);
        Gizmos.DrawWireSphere(transform.position - offset, 0.1f);
        Gizmos.DrawWireSphere(transform.position + offset, 0.1f);
    }
}
