using System.Collections;
using UnityEngine;

/// <summary>
/// Play 開始時先讓場景暖機，再啟用／生成玩家，避免第一幀 DepthProxy 尚未對齊而穿透地板。
/// </summary>
[DefaultExecutionOrder(-5000)]
public sealed class PlayerSpawner : MonoBehaviour
{
    public enum SpawnMode
    {
        /// <summary>場景內已有 Player（進 Play 時先關閉，暖機後再啟用）。保留關卡裡對 playerRoot 的引用。</summary>
        ActivateExisting = 0,
        /// <summary>從 Prefab 生成於 Spawn Point，並重設所有 <see cref="FezDepthProxyFollower"/> 的 playerRoot。</summary>
        InstantiatePrefab = 1
    }

    [Header("模式")]
    [SerializeField] private SpawnMode spawnMode = SpawnMode.ActivateExisting;
    [Tooltip("InstantiatePrefab 時使用。")]
    [SerializeField] private GameObject playerPrefab;
    [Tooltip("留空則用本物件 Transform。")]
    [SerializeField] private Transform spawnPoint;
    [Tooltip("ActivateExisting 時使用；留空會找 Tag=Player 的物件。")]
    [SerializeField] private GameObject existingPlayer;

    [Header("暖機")]
    [SerializeField] private int warmupFrames = 2;
    [SerializeField] private bool waitForFixedUpdate = true;
    [Tooltip("啟用／生成後將玩家放到 Spawn Point。")]
    [SerializeField] private bool snapToSpawnPoint = true;

    [Header("Follower 重綁")]
    [SerializeField] private bool rewireDepthProxyFollowers = true;

    /// <summary>本場景目前作用中的玩家（暖機完成後）。</summary>
    public static GameObject ActivePlayer { get; private set; }

    private void Awake()
    {
        if (spawnPoint == null)
            spawnPoint = transform;

        if (spawnMode != SpawnMode.ActivateExisting)
            return;

        if (existingPlayer == null)
        {
            var tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null)
                existingPlayer = tagged;
        }

        if (existingPlayer != null && existingPlayer.activeSelf)
            existingPlayer.SetActive(false);
    }

    private IEnumerator Start()
    {
        for (var i = 0; i < Mathf.Max(0, warmupFrames); i++)
            yield return null;

        if (waitForFixedUpdate)
            yield return new WaitForFixedUpdate();

        var player = ResolvePlayer();
        if (player == null)
        {
            Debug.LogError("[PlayerSpawner] 無法取得玩家物件。", this);
            yield break;
        }

        if (snapToSpawnPoint && spawnPoint != null)
            PlacePlayer(player, spawnPoint.position, spawnPoint.rotation);

        StabilizeAndSyncFez(player);

        if (rewireDepthProxyFollowers)
            RewireDepthProxyFollowers(player.transform);

        ActivePlayer = player;
    }

    private GameObject ResolvePlayer()
    {
        switch (spawnMode)
        {
            case SpawnMode.ActivateExisting:
                if (existingPlayer == null)
                    return null;
                existingPlayer.SetActive(true);
                return existingPlayer;

            case SpawnMode.InstantiatePrefab:
                if (playerPrefab == null)
                    return null;
                var pos = spawnPoint != null ? spawnPoint.position : Vector3.zero;
                var rot = spawnPoint != null ? spawnPoint.rotation : Quaternion.identity;
                return Instantiate(playerPrefab, pos, rot);

            default:
                return null;
        }
    }

    private static void PlacePlayer(GameObject player, Vector3 position, Quaternion rotation)
    {
        var cc = player.GetComponent<CharacterController>();
        if (cc != null)
            cc.enabled = false;

        player.transform.SetPositionAndRotation(position, rotation);
        Physics.SyncTransforms();

        if (cc != null)
        {
            cc.enabled = true;
            FezOneWayPlatform.InvalidatePlayerCollisionState(cc);
        }
    }

    private static void StabilizeAndSyncFez(GameObject player)
    {
        var cc = player.GetComponent<CharacterController>();
        if (cc != null)
        {
            cc.enabled = false;
            Physics.SyncTransforms();
            cc.enabled = true;
            FezOneWayPlatform.InvalidatePlayerCollisionState(cc);
        }

        var viewState = player.GetComponent<FezWorldViewState>();
        if (viewState != null)
            viewState.RefreshFromPivot();

        FezDepthProxyFollower.ForceSyncAllPhases(0f);
    }

    private static void RewireDepthProxyFollowers(Transform playerRoot)
    {
        var followers = Object.FindObjectsOfType<FezDepthProxyFollower>(includeInactive: true);
        for (var i = 0; i < followers.Length; i++)
        {
            if (followers[i] != null)
                followers[i].SetPlayerRoot(playerRoot);
        }
    }

}
