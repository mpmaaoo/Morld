using System;
using UnityEngine;

[Serializable]
public sealed class EnemyTuning
{
    public enum Movement { [InspectorName("定點")] Stationary, [InspectorName("巡邏")] Patrol, [InspectorName("3D A* 追擊")] Chase }
    [Header("基本資料")]
    [InspectorName("最大血量"), Min(1)] public int health = 5;
    [InspectorName("移動方式")] public Movement movement;
    [InspectorName("巡邏速度"), Min(0f)] public float patrolSpeed = 1f;
    [InspectorName("單側巡邏距離"), Min(0f)] public float patrolHalfDistance = 2f;
    [Header("3D A* 追擊")]
    [InspectorName("追擊速度"), Min(0f)] public float chaseSpeed = 2f;
    [InspectorName("最遠視野距離"), Min(0f)] public float detectionDistance = 8f;
    [InspectorName("3D 停靠距離"), Min(0.1f)] public float chaseStopDistance = 0.8f;
    [InspectorName("重新尋路間隔"), Min(0.1f)] public float repathInterval = 0.5f;
    [Header("3D 視野（所有行為共用）")]
    [InspectorName("水平視角"), Range(1f, 360f)] public float viewAngle = 110f;
    [InspectorName("垂直視角"), Range(1f, 180f)] public float verticalViewAngle = 100f;
    [InspectorName("失去視野記憶秒數"), Min(0f)] public float sightMemory = 1.5f;
    [InspectorName("警戒時左右掃視")] public bool scanWhileIdle = true;
    [InspectorName("掃視單側角度"), Range(0f, 180f)] public float scanHalfAngle = 50f;
    [InspectorName("掃視來回秒數"), Min(0.1f)] public float scanPeriod = 4f;
    [InspectorName("轉向柔和時間"), Min(0.01f)] public float turnSmoothTime = 0.2f;
    [InspectorName("最大轉向速度（度／秒）"), Min(1f)] public float maxTurnSpeed = 180f;
    [InspectorName("掃視銜接柔和時間"), Min(0.01f)] public float scanSmoothTime = 0.25f;
    [Header("追擊、搜尋與巡邏")]
    [Tooltip("發現玩家後進入此 3D 半徑即鎖定追擊；範圍內不因視野、追擊時間或出生點距離而放棄。0 為關閉。")]
    [InspectorName("追擊鎖定半徑（以敵人為中心）"), Min(0f)] public float pursuitLockRadius = 2f;
    [InspectorName("追擊上限半徑（以出生點為中心）"), Min(1f)] public float leashRadius = 12f;
    [InspectorName("最長追擊秒數"), Min(0.1f)] public float maxChaseTime = 15f;
    [InspectorName("最後位置搜尋秒數"), Min(0f)] public float searchDuration = 3f;
    [InspectorName("路徑失敗放棄秒數"), Min(0.1f)] public float unreachableTimeout = 3f;
    [InspectorName("巡邏點停留秒數"), Min(0f)] public float patrolWait = 1.5f;
    [InspectorName("巡邏／返回速度"), Min(0f)] public float patrolMoveSpeed = 1f;
    [InspectorName("巡邏點往返（否則循環）")] public bool patrolPingPong = true;
    [Header("攻擊：距離與時間（秒）")]
    [InspectorName("水平攻擊距離"), Min(0.1f)] public float reach = 1.4f;
    [InspectorName("垂直攻擊距離"), Min(0.1f)] public float verticalReach = 0.8f;
    [InspectorName("前搖時間"), Min(0.05f)] public float windup = 0.65f;
    [InspectorName("出手展示時間"), Min(0.01f)] public float attackDuration = 0.12f;
    [InspectorName("收招時間"), Min(0.05f)] public float recovery = 1f;
    [Tooltip("兩次開始前搖的最小間隔，仍須等待本次出手及收招結束。")]
    [InspectorName("最短攻擊間隔"), Min(0f)] public float attackInterval = 2f;
    [InspectorName("可攻擊的最大視角深度差"), Min(0f)] public float maxAttackDepthGap = 3f;
    [Header("受擊反饋（未啟用時沿用 Prefab）")]
    [InspectorName("覆寫受擊反饋")] public bool overrideHitResponse;
    [InspectorName("受擊停頓秒數"), Min(0f)] public float hitPause = 0.045f;
    [InspectorName("擊退速度"), Min(0f)] public float hitPushSpeed = 6f;
    [InspectorName("打飛向上速度"), Min(0f)] public float hitLaunchSpeed = 4f;
    [InspectorName("落地反彈速度"), Min(0f)] public float hitBounceSpeed = 1.8f;
    [InspectorName("落地摩擦力"), Min(0.1f)] public float hitFriction = 10f;
    [InspectorName("顯示血條")] public bool showHealthBar = true;
}

/// <summary>Shared species data; runtime copies values instead of modifying this asset.</summary>
[CreateAssetMenu(menuName = "Morld/Enemy Type", fileName = "NewEnemyType")]
public sealed class EnemyDefinition : ScriptableObject
{
    [InspectorName("種類名稱")] public string displayName = "新敵人";
    [InspectorName("關卡設計備註"), TextArea] public string designerNotes;
    [InspectorName("敵人 Prefab")] public StationaryMeleeEnemy prefab;
    [InspectorName("共用參數")] public EnemyTuning settings = new EnemyTuning();
    [InspectorName("生成點顏色")] public Color gizmoColor = new Color(1f, 0.45f, 0.1f);
}
