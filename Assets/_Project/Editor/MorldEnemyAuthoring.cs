using UnityEditor;
using UnityEngine;

/// <summary>Creates editable prefab assets once, then places linked spawn points.</summary>
public static class MorldEnemyAuthoring
{
    private const string Folder = "Assets/_Project/Prefabs/Enemies";
    [MenuItem("GameObject/Morld/3D A Star Chase Test", false, 12)]
    private static void CreateChaseTest()
    {
        CreateSpawnPoint();
        var point = Selection.activeGameObject.GetComponent<EnemySpawnPoint>();
        Undo.RecordObject(point, "Configure A star encounter");
        point.enemyType = AssetDatabase.LoadAssetAtPath<EnemyDefinition>(Folder + "/ChaseGuard.asset");
        var root = new GameObject("EnemyNavigationArea");
        Undo.RegisterCreatedObjectUndo(root, "Create A star area");
        root.transform.position = point.transform.position + Vector3.up;
        var area = root.AddComponent<EnemyNavigationArea>();
        area.Rebuild();
        point.navigationArea = area;
        EditorUtility.SetDirty(area);
        PrefabUtility.RecordPrefabInstancePropertyModifications(point);
        Selection.activeGameObject = root;
    }
    [MenuItem("GameObject/Morld/Enemy Spawn Point", false, 10)]
    private static void CreateSpawnPoint()
    {
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Enemies");
        string enemyPath = Folder + "/StationaryMeleeEnemy.prefab";
        var enemy = AssetDatabase.LoadAssetAtPath<StationaryMeleeEnemy>(enemyPath);
        if (enemy == null)
        {
            var root = new GameObject("StationaryMeleeEnemy");
            var controller = root.AddComponent<CharacterController>();
            controller.height = 1f;
            controller.radius = 0.28f;
            controller.center = Vector3.up * 0.5f;
            controller.stepOffset = 0f;
            root.AddComponent<Health>();
            enemy = root.AddComponent<StationaryMeleeEnemy>();
            var visual = new GameObject("VisualRoot").transform;
            visual.SetParent(root.transform, false);
            enemy.visualRoot = visual;
            var placeholder = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            placeholder.name = "Placeholder_ReplaceWithArt";
            Object.DestroyImmediate(placeholder.GetComponent<Collider>());
            placeholder.transform.SetParent(visual, false);
            placeholder.transform.localPosition = Vector3.up * 0.5f;
            placeholder.transform.localScale = new Vector3(0.55f, 0.5f, 0.55f);
            var anchor = new GameObject("WarningAnchor").transform;
            anchor.SetParent(root.transform, false);
            anchor.localPosition = Vector3.up * 1.3f;
            enemy.warningAnchor = anchor;
            var icon = new GameObject("Placeholder_Warning_ReplaceWithArt");
            icon.transform.SetParent(anchor, false);
            var text = icon.AddComponent<TextMesh>();
            text.text = "!";
            text.fontSize = 64;
            text.characterSize = 0.12f;
            text.anchor = TextAnchor.MiddleCenter;
            text.color = Color.yellow;
            enemy.warningIcon = icon;
            icon.AddComponent<EnemyWarningBillboard>();
            icon.SetActive(false);
            PrefabUtility.SaveAsPrefabAsset(root, enemyPath);
            Object.DestroyImmediate(root);
            enemy = AssetDatabase.LoadAssetAtPath<StationaryMeleeEnemy>(enemyPath);
        }
        string spawnPath = Folder + "/EnemySpawnPoint.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spawnPath);
        if (prefab == null)
        {
            var root = new GameObject("EnemySpawnPoint");
            root.AddComponent<EnemySpawnPoint>().enemyPrefab = enemy;
            prefab = PrefabUtility.SaveAsPrefabAsset(root, spawnPath);
            Object.DestroyImmediate(root);
        }
        var point = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(point, "Place enemy spawn point");
        // Selecting a platform places the enemy on its physical upper face.
        var selected = Selection.activeGameObject;
        var floor = selected != null ? selected.GetComponent<Collider>() : null;
        if (floor != null) point.transform.position = new Vector3(floor.bounds.center.x, floor.bounds.max.y + 0.03f, floor.bounds.center.z);
        else if (SceneView.lastActiveSceneView != null) point.transform.position = SceneView.lastActiveSceneView.pivot;
        Selection.activeGameObject = point;
    }
}
