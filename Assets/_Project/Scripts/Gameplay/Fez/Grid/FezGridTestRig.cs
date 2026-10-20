using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 【已停用的舊試作】預設不執行；定義 MORLD_LEGACY_TESTS 才可啟用。
/// 進 Play 後跑格子投影。若這個場景的 FezMapRoot 裡有編輯器放的方塊，就用那些；
/// 否則生成內建測試關。SampleScene 與原本的 DepthProxy 不會被改到。
/// </summary>
[DisallowMultipleComponent]
public sealed class FezGridTestRig : MonoBehaviour
{
    private const string DefaultPlayerPrefabPath = "Assets/_Project/Legacy/Prefabs/GridTestPlayer.prefab";

    [SerializeField] private Transform authoredRoot;
    [SerializeField] private FezGridTestPlayer playerPrefab;
    [SerializeField] private Transform spawnPoint;

    private void Start()
    {
#if MORLD_LEGACY_TESTS
        EnsurePlayerPrefab();

        var mapObject = new GameObject("GridMap");
        var map = mapObject.AddComponent<FezGridMap>();
        var root = ResolveAuthoredRoot();
        var imported = map.ImportPlacedBlocks(root);
        if (imported <= 0)
            BuildDemo(map);

        ResolveSpawn(map, out var spawn, out var spawnRot);
        if (imported > 0)
            EnsureTestBoundaryWalls(map, spawn);

        var shellObject = new GameObject("GridShell");
        var shell = shellObject.AddComponent<FezGridShell>();

        var player = SpawnPlayer(spawn, spawnRot);
        player.Bind(map, shell);
        AttachCombatTest(player, spawn, spawnRot);

        var camera = Camera.main;
        if (camera == null)
        {
            var cameraObject = new GameObject("GridTestCamera");
            camera = cameraObject.AddComponent<Camera>();
            cameraObject.tag = "MainCamera";
            if (FindObjectOfType<AudioListener>() == null)
                cameraObject.AddComponent<AudioListener>();
        }

        camera.orthographic = true;
        camera.orthographicSize = 6.5f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 80f;
        camera.transform.SetParent(player.transform, false);
        camera.transform.localPosition = new Vector3(0f, 2.2f, -16f);
        camera.transform.localRotation = Quaternion.identity;

        var lightObject = new GameObject("Light");
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
#else
        // SampleScene + PlayerController is the active implementation.
        enabled = false;
#endif
    }

    private void EnsurePlayerPrefab()
    {
        if (playerPrefab != null)
            return;

#if UNITY_EDITOR
        playerPrefab = AssetDatabase.LoadAssetAtPath<FezGridTestPlayer>(DefaultPlayerPrefabPath);
#endif
        if (playerPrefab == null)
            Debug.LogWarning("[FezGridTestRig] Player Prefab 未指定，將用現場組裝（預製件數值不會生效）。請在 Inspector 指定 GridTestPlayer。");
    }

    private FezGridTestPlayer SpawnPlayer(Vector3 spawn, Quaternion rotation)
    {
        if (playerPrefab != null)
        {
            var instance = Instantiate(playerPrefab, spawn, rotation);
            instance.name = "GridTestPlayer";
            return instance;
        }

        // 後備：場景未指定預製件時才現場組裝。
        var playerObject = new GameObject("GridTestPlayer");
        playerObject.transform.SetPositionAndRotation(spawn, rotation);
        var controller = playerObject.AddComponent<CharacterController>();
        controller.height = 1.6f;
        controller.radius = 0.28f;
        controller.center = new Vector3(0f, 0.8f, 0f);
        controller.skinWidth = 0.05f;
        controller.stepOffset = 0f;
        var player = playerObject.AddComponent<FezGridTestPlayer>();

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(playerObject.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.8f, 0f);
        Destroy(body.GetComponent<Collider>());
        var renderer = body.GetComponent<Renderer>();
        var shader = Shader.Find("Unlit/Color");
        if (renderer != null && shader != null)
            renderer.sharedMaterial = new Material(shader) { color = new Color(0.95f, 0.95f, 0.9f) };

        return player;
    }

    private static void AttachCombatTest(FezGridTestPlayer player, Vector3 spawn, Quaternion rotation)
    {
        var host = player.gameObject;
        var health = host.GetComponent<Health>();
        if (health == null)
            health = host.AddComponent<Health>();
        health.SetMax(100, true);
        if (host.GetComponent<HitFeedback>() == null)
            host.AddComponent<HitFeedback>();
        if (host.GetComponent<HealthDebugInput>() == null)
            host.AddComponent<HealthDebugInput>();

        var root = new GameObject("TestMonster");
        root.transform.SetPositionAndRotation(spawn + rotation * Vector3.right * 3f, rotation);

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0f, 0.8f, 0f);
        var hitbox = body.GetComponent<CapsuleCollider>();
        if (hitbox != null)
            hitbox.isTrigger = true;
        var renderer = body.GetComponent<Renderer>();
        var shader = Shader.Find("Unlit/Color");
        if (renderer != null && shader != null)
            renderer.sharedMaterial = new Material(shader) { color = new Color(0.85f, 0.25f, 0.3f) };

        var monsterHealth = root.AddComponent<Health>();
        monsterHealth.SetMax(60, true);
        root.AddComponent<HitFeedback>();
        root.AddComponent<Monster>();
    }

    private void ResolveSpawn(FezGridMap map, out Vector3 position, out Quaternion rotation)
    {
        if (spawnPoint != null)
        {
            position = spawnPoint.position;
            rotation = spawnPoint.rotation;
            return;
        }

        var found = GameObject.Find("PlayerSpawn");
        if (found != null && found.scene == gameObject.scene)
        {
            position = found.transform.position;
            rotation = found.transform.rotation;
            return;
        }

        position = SpawnOnLowestBlock(map);
        rotation = Quaternion.identity;
    }

    private Transform ResolveAuthoredRoot()
    {
        if (authoredRoot != null)
            return authoredRoot;

        var found = GameObject.Find("FezMapRoot");
        if (found != null && found.scene == gameObject.scene)
            return found.transform;

        return null;
    }

    private void OnDrawGizmos()
    {
        if (spawnPoint == null)
            return;

        Gizmos.color = new Color(0.2f, 0.85f, 1f, 0.9f);
        Gizmos.DrawWireSphere(spawnPoint.position + Vector3.up * 0.8f, 0.28f);
        Gizmos.DrawLine(spawnPoint.position, spawnPoint.position + Vector3.up * 1.6f);
        Gizmos.DrawRay(spawnPoint.position + Vector3.up * 0.9f, spawnPoint.forward * 0.6f);
    }

    private static Vector3 SpawnOnLowestBlock(FezGridMap map)
    {
        FezGridBlock lowest = null;
        foreach (var block in map.Blocks)
        {
            if (block == null)
                continue;
            if (lowest == null
                || block.Cell.y < lowest.Cell.y
                || (block.Cell.y == lowest.Cell.y && block.Cell.x < lowest.Cell.x)
                || (block.Cell.y == lowest.Cell.y && block.Cell.x == lowest.Cell.x && block.Cell.z < lowest.Cell.z))
                lowest = block;
        }

        if (lowest == null)
            return new Vector3(0.5f, 1.08f, 0.5f);

        return new Vector3(lowest.Center.x, lowest.TopY + 0.08f, lowest.Center.z);
    }

    private static void BuildDemo(FezGridMap map)
    {
        var gray = new Color(0.75f, 0.78f, 0.82f);
        var orange = new Color(0.95f, 0.62f, 0.18f);
        var blue = new Color(0.25f, 0.45f, 0.95f);
        var green = new Color(0.35f, 0.75f, 0.4f);
        var purple = new Color(0.62f, 0.38f, 0.82f);
        var red = new Color(0.85f, 0.28f, 0.28f);
        var cyan = new Color(0.25f, 0.75f, 0.82f);
        var gold = new Color(0.85f, 0.72f, 0.28f);
        var white = new Color(0.92f, 0.92f, 0.9f);
        var mask = new Color(0.12f, 0.13f, 0.16f);

        Add(map, 0, 0, 0, gray);
        Add(map, 1, 0, 0, gray);
        Add(map, 2, 0, 5, orange);
        Add(map, 3, 0, 5, orange);

        Add(map, -1, 0, 0, white);
        Add(map, -2, 0, 0, white);
        Add(map, -3, 0, 0, white);
        Add(map, -4, 0, 3, white);
        Add(map, -5, 0, 3, white);
        Add(map, -6, 1, 3, mask);
        Add(map, -6, 2, 3, mask);
        Add(map, -6, 0, 2, white);
        Add(map, -6, 0, 4, white);

        Add(map, 4, 1, 5, red);
        Add(map, 5, 0, 5, green);
        Add(map, 6, 0, 5, green);

        Add(map, 7, 1, 5, purple);
        Add(map, 8, 1, 5, purple);
        Add(map, 9, 2, 5, purple);
        Add(map, 10, 2, 5, purple);
        Add(map, 11, 1, 5, purple);
        Add(map, 12, 0, 5, gray);

        Add(map, 13, -1, 5, gold);
        Add(map, 14, -1, 5, gold);
        Add(map, 15, 0, 5, gold);
        Add(map, 16, 0, 5, gold);

        for (var z = 1; z <= 4; z++)
            Add(map, 0, 0, z, blue);
        for (var z = 6; z <= 8; z++)
            Add(map, 0, 0, z, blue);

        for (var z = 6; z <= 10; z++)
            Add(map, 7, 1, z, cyan);

        // 邊界牆測試：預設視角（-Z）下 (2,y,-1) 為最淺 → 硬擋；轉 90° 後可穿。
        var boundary = new Color(0.9f, 0.25f, 0.45f);
        AddBoundary(map, 2, 0, -1, boundary);
        AddBoundary(map, 2, 1, -1, boundary);
        AddBoundary(map, 3, 0, -1, boundary);
    }

    /// <summary>
    /// 場景地圖若尚未有邊界牆，在 spawn 附近補一排供 Play 驗證。
    /// </summary>
    private static void EnsureTestBoundaryWalls(FezGridMap map, Vector3 spawn)
    {
        if (map == null)
            return;

        foreach (var block in map.Blocks)
        {
            if (block != null && block.IsBoundaryWall)
                return;
        }

        var ox = Mathf.RoundToInt(spawn.x);
        var oy = Mathf.Max(0, Mathf.FloorToInt(spawn.y - 0.05f));
        var oz = Mathf.RoundToInt(spawn.z);
        var boundary = new Color(0.9f, 0.25f, 0.45f);

        // 預設視角深度 -Z：放在 spawn 前方（較小 z）一排，面對時硬擋。
        TryAddBoundary(map, ox + 1, oy, oz - 2, boundary);
        TryAddBoundary(map, ox + 1, oy + 1, oz - 2, boundary);
        TryAddBoundary(map, ox + 2, oy, oz - 2, boundary);
    }

    private static void TryAddBoundary(FezGridMap map, int x, int y, int z, Color color)
    {
        var cell = new Vector3Int(x, y, z);
        if (map.HasSolid(cell))
            return;

        // HasSolid 只查平台；空格或非平台才 Add（AddBlock 遇已佔格會直接回傳舊塊）。
        foreach (var block in map.Blocks)
        {
            if (block != null && block.Cell == cell)
                return;
        }

        AddBoundary(map, x, y, z, color);
    }

    private static void Add(FezGridMap map, int x, int y, int z, Color color, bool solid = true)
    {
        map.AddBlock(new Vector3Int(x, y, z), color, solid);
    }

    private static void AddBoundary(FezGridMap map, int x, int y, int z, Color color)
    {
        map.AddBlock(new Vector3Int(x, y, z), color, FezGridBlockKind.BoundaryWall);
    }
}
