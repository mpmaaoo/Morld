using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 這一關的三維格子表。四個視角都讀同一張表，不另存四張二維地圖。
/// </summary>
[DisallowMultipleComponent]
public sealed class FezGridMap : MonoBehaviour
{
    private const string PlatformPrefabPath =
        "Assets/_Project/Prefabs/Building blocks/TestBlocks/Block1x1.prefab";
    private const string OccluderPrefabPath =
        "Assets/_Project/Prefabs/Building blocks/TestBlocks/Wall 1x1.prefab";
    private const string BoundaryPrefabPath =
        "Assets/_Project/Prefabs/Building blocks/TestBlocks/BoundaryWall 1x1.prefab";

    private readonly Dictionary<Vector3Int, FezGridBlock> _cells = new Dictionary<Vector3Int, FezGridBlock>();

    public IEnumerable<FezGridBlock> Blocks => _cells.Values;

    public bool HasSolid(Vector3Int cell)
    {
        return _cells.TryGetValue(cell, out var block) && block != null && block.Solid;
    }

    public FezGridBlock AddBlock(Vector3Int cell, Color color, bool solid = true)
    {
        return AddBlock(cell, color, solid ? FezGridBlockKind.Platform : FezGridBlockKind.Occluder);
    }

    public FezGridBlock AddBlock(Vector3Int cell, Color color, FezGridBlockKind kind)
    {
        if (_cells.TryGetValue(cell, out var existing))
            return existing;

        var visual = CreateVisual(cell, color, kind);
        return Adopt(visual, cell, kind);
    }

    /// <summary>
    /// 把地圖編輯器放好的方塊收進同一張格子表。不另做一顆方塊。
    /// </summary>
    public int ImportPlacedBlocks(Transform root)
    {
        if (root == null)
            return 0;

        var count = 0;
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child == null || child.GetComponentInChildren<Renderer>(true) == null)
                continue;

            var cell = TryReadStoredCell(child, out var stored)
                ? stored
                : CellFromMinCorner(child.position);
            if (_cells.ContainsKey(cell))
            {
                // 重複格未進表也要剝碰撞，避免遮片牆 MeshCollider 殘留。
                SilenceLegacyBlock(child.gameObject);
                continue;
            }

            Adopt(child.gameObject, cell, ResolveKind(child.gameObject));
            count++;
        }

        return count;
    }

    /// <summary>方塊上已寫入格子時以該整數為準，否則用最小角世界座標。</summary>
    public static bool TryReadStoredCell(Transform blockRoot, out Vector3Int cell)
    {
        cell = default;
        if (blockRoot == null)
            return false;

        var block = blockRoot.GetComponent<FezGridBlock>();
        if (block == null || !block.HasCell)
            return false;

        cell = block.Cell;
        return true;
    }

    public static Vector3Int CellFromMinCorner(Vector3 worldMinCorner)
    {
        const float epsilon = 0.001f;
        return new Vector3Int(
            Mathf.FloorToInt(worldMinCorner.x + epsilon),
            Mathf.FloorToInt(worldMinCorner.y + epsilon),
            Mathf.FloorToInt(worldMinCorner.z + epsilon));
    }

    private GameObject CreateVisual(Vector3Int cell, Color color, FezGridBlockKind kind)
    {
        var prefab = LoadKindPrefab(kind);
        if (prefab != null)
        {
            var instance = Instantiate(prefab, transform, false);
            instance.name = $"{prefab.name}_{cell.x}_{cell.y}_{cell.z}";
            // 與地圖編輯器一致：根節點在格子最小角，BlockCenter／WallCenter 在 +0.5。
            instance.transform.SetPositionAndRotation((Vector3)cell, Quaternion.identity);
            instance.transform.localScale = Vector3.one;
            return instance;
        }

        // 後備：無預製件時才用簡易方塊（正式流程應走 TestBlocks Prefab）。
        var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = kind == FezGridBlockKind.BoundaryWall
            ? $"BoundaryWall {cell.x},{cell.y},{cell.z}"
            : $"Cell {cell.x},{cell.y},{cell.z}";
        visual.transform.SetParent(transform, false);
        visual.transform.position = (Vector3)cell + Vector3.one * 0.5f;
        visual.transform.localScale = Vector3.one * 0.96f;

        var collider = visual.GetComponent<Collider>();
        if (collider != null)
            Destroy(collider);

        var renderer = visual.GetComponent<Renderer>();
        if (renderer != null)
        {
            var shader = Shader.Find("Unlit/Color");
            if (shader != null)
                renderer.sharedMaterial = new Material(shader) { color = color };
        }

        if (kind == FezGridBlockKind.BoundaryWall)
            visual.AddComponent<FezGridBoundaryWall>();
        else if (kind == FezGridBlockKind.Platform)
            visual.AddComponent<FezGridPlatform>();

        return visual;
    }

    private static GameObject LoadKindPrefab(FezGridBlockKind kind)
    {
#if UNITY_EDITOR
        var path = kind == FezGridBlockKind.BoundaryWall
            ? BoundaryPrefabPath
            : kind == FezGridBlockKind.Occluder
                ? OccluderPrefabPath
                : PlatformPrefabPath;
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
#else
        return null;
#endif
    }

    private static FezGridBlockKind ResolveKind(GameObject visual)
    {
        if (visual == null)
            return FezGridBlockKind.Platform;

        if (visual.GetComponentInChildren<FezGridBoundaryWall>(true) != null)
            return FezGridBlockKind.BoundaryWall;

        var name = visual.name;
        if (!string.IsNullOrEmpty(name)
            && name.IndexOf("BoundaryWall", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return FezGridBlockKind.BoundaryWall;

        if (IsOccluder(visual))
            return FezGridBlockKind.Occluder;

        // FezGridPlatform 或名稱 Block → 平台；其餘預設平台。
        return FezGridBlockKind.Platform;
    }

    private static bool IsOccluder(GameObject visual)
    {
        if (visual == null)
            return false;

        if (visual.GetComponentInChildren<FezGridBoundaryWall>(true) != null)
            return false;

        if (visual.GetComponentInChildren<FezBackgroundWall>(true) != null)
            return true;

        var name = visual.name;
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.IndexOf("BoundaryWall", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        return name.IndexOf("Wall", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private FezGridBlock Adopt(GameObject visual, Vector3Int cell, FezGridBlockKind kind)
    {
        SilenceLegacyBlock(visual);

        var block = visual.GetComponent<FezGridBlock>();
        if (block == null)
            block = visual.AddComponent<FezGridBlock>();
        block.Initialize(cell, kind);
        _cells.Add(cell, block);
        return block;
    }

    private static void SilenceLegacyBlock(GameObject visual)
    {
        if (visual == null)
            return;

        var behaviours = visual.GetComponentsInChildren<MonoBehaviour>(true);
        for (var i = 0; i < behaviours.Length; i++)
        {
            var behaviour = behaviours[i];
            if (behaviour == null
                || behaviour is FezGridBlock
                || behaviour is FezGridBoundaryWall
                || behaviour is FezGridPlatform)
                continue;
            behaviour.enabled = false;
        }

        // 預製件自帶碰撞全刪：平台／邊界牆改由 Shell 重建；遮片牆不留硬碰。
        var colliders = visual.GetComponentsInChildren<Collider>(true);
        for (var i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                Object.DestroyImmediate(colliders[i]);
        }
    }
}
