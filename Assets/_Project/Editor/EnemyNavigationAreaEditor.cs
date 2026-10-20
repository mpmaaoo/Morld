using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EnemyNavigationArea))]
public sealed class EnemyNavigationAreaEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var area = (EnemyNavigationArea)target;
        EditorGUILayout.HelpBox($"{area.NodeCount} 個實體地板節點，實際採樣間距 {area.SamplingSpacing:0.##}。修改地形或區域設定後請重建。區域不隨物件旋轉與縮放，請直接調整區域大小。", MessageType.Info);
        if (GUILayout.Button("重建 3D 地板路網"))
        {
            Undo.RecordObject(area, "Rebuild enemy navigation");
            area.Rebuild();
            EditorUtility.SetDirty(area);
            PrefabUtility.RecordPrefabInstancePropertyModifications(area);
            SceneView.RepaintAll();
        }
    }
    [MenuItem("GameObject/Morld/3D Enemy Navigation Area", false, 11)]
    private static void CreateArea()
    {
        var root = new GameObject("EnemyNavigationArea");
        Undo.RegisterCreatedObjectUndo(root, "Create enemy navigation area");
        root.AddComponent<EnemyNavigationArea>();
        var selected = Selection.activeGameObject;
        if (selected != null) root.transform.position = selected.transform.position;
        else if (SceneView.lastActiveSceneView != null) root.transform.position = SceneView.lastActiveSceneView.pivot;
        Selection.activeGameObject = root;
    }
}

[CustomEditor(typeof(StationaryMeleeEnemy))]
public sealed class MorldEnemyRuntimeEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (Application.isPlaying)
        {
            var enemy = (StationaryMeleeEnemy)target;
            var chase = enemy.GetComponent<EnemyChaseBehaviour>();
            EditorGUILayout.HelpBox(chase != null ? chase.PathStatus
                : $"目前行為：{enemy.MovementMode}。若要 A* 追擊，生成點請選 ChaseGuard，並檢查本地覆寫是否設為 3D A* 追擊。", MessageType.Info);
        }
        DrawDefaultInspector();
    }
    public override bool RequiresConstantRepaint() => Application.isPlaying;
}
