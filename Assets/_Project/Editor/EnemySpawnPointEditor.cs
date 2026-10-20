using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

[CustomEditor(typeof(EnemySpawnPoint))]
[CanEditMultipleObjects]
public sealed class EnemySpawnPointEditor : Editor
{
    private ReorderableList patrolList;
    private bool placingPatrol;
    private int selectedPatrol = -1;
    private bool multipleSpawnPoints;

    private void OnEnable()
    {
        multipleSpawnPoints = targets.Length > 1;
        patrolList = new ReorderableList(serializedObject, serializedObject.FindProperty("patrolPoints"), true, true, true, true);
        patrolList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "巡邏路線（拖曳排序）");
        patrolList.drawElementCallback = (rect, index, active, focused) =>
        {
            rect.height = EditorGUIUtility.singleLineHeight;
            EditorGUI.PropertyField(rect, patrolList.serializedProperty.GetArrayElementAtIndex(index), new GUIContent((index + 1).ToString()));
        };
        patrolList.onSelectCallback = list => { selectedPatrol = list.index; SceneView.RepaintAll(); };
        patrolList.onAddCallback = list =>
        {
            serializedObject.ApplyModifiedProperties();
            var point = (EnemySpawnPoint)target;
            var points = point.patrolPoints;
            Vector3 position = points != null && points.Length > 0 && points[points.Length - 1] != null
                ? points[points.Length - 1].position + point.transform.right : point.transform.position;
            AddPatrolPoint(point, position);
            serializedObject.Update();
        };
        patrolList.onRemoveCallback = list =>
        {
            // Remove only the reference: existing points may be shared by other enemies.
            var element = list.serializedProperty.GetArrayElementAtIndex(list.index);
            element.objectReferenceValue = null;
            list.serializedProperty.DeleteArrayElementAtIndex(list.index);
            selectedPatrol = -1;
        };
    }

    private void OnDisable() { placingPatrol = false; SceneView.RepaintAll(); }

    private void AddPatrolPoint(EnemySpawnPoint point, Vector3 position)
    {
        if (Application.isPlaying || !point.gameObject.scene.IsValid()) return;
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("新增巡邏點");
        Undo.RecordObject(point, "新增巡邏點");
        int count = point.patrolPoints != null ? point.patrolPoints.Length : 0;
        var marker = new GameObject("PatrolPoint_" + (count + 1));
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(marker, point.gameObject.scene);
        Undo.RegisterCreatedObjectUndo(marker, "新增巡邏點");
        Undo.SetTransformParent(marker.transform, point.transform, "新增巡邏點");
        marker.transform.position = position;
        var points = new Transform[count + 1];
        if (count > 0) System.Array.Copy(point.patrolPoints, points, count);
        points[count] = marker.transform;
        point.patrolPoints = points;
        selectedPatrol = count;
        EditorUtility.SetDirty(point);
        PrefabUtility.RecordPrefabInstancePropertyModifications(point);
        Undo.CollapseUndoOperations(group);
        SceneView.RepaintAll();
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.HelpBox("完整巡邏／追擊／搜尋行為請使用 3D A* 追擊種類。巡邏點依列表順序行走；黃色是攻擊範圍，橘色是視野。", MessageType.Info);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enemyType"), new GUIContent("敵人種類"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("enemyPrefab"), new GUIContent("備用敵人 Prefab"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("navigationArea"), new GUIContent("3D 尋路區域"));
        if (targets.Length == 1)
        {
            using (new EditorGUI.DisabledScope(Application.isPlaying || !((EnemySpawnPoint)target).gameObject.scene.IsValid()))
            {
                patrolList.DoLayoutList();
                bool next = GUILayout.Toggle(placingPatrol, placingPatrol ? "結束放置巡邏點（Esc）" : "在場景點地面放巡邏點", "Button");
                if (next != placingPatrol) { placingPatrol = next; SceneView.RepaintAll(); }
            }
            EditorGUILayout.HelpBox("放置模式：左鍵點實體地面，連續新增腳底位置；Esc 結束，Alt 保留視角操作。點場景中的編號可移動該點。清單 − 只解除指派，不刪除物件。", MessageType.None);
        }
        else EditorGUILayout.PropertyField(serializedObject.FindProperty("patrolPoints"), new GUIContent("巡邏點（依序）"), true);
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(serializedObject.FindProperty("overrideSettings"), new GUIContent("此生成點獨立設定"));
        if (EditorGUI.EndChangeCheck() && serializedObject.FindProperty("overrideSettings").boolValue)
        {
            serializedObject.ApplyModifiedProperties();
            CopySharedSettings();
            serializedObject.Update();
        }
        if (serializedObject.FindProperty("overrideSettings").boolValue)
        {
            EditorGUILayout.HelpBox("以下參數只影響這個生成點，下次生成／重生時套用。取消獨立設定會回到共用種類；重新啟用會重新複製共用值。", MessageType.None);
            var tuning = serializedObject.FindProperty("settings");
            tuning.isExpanded = true;
            EditorGUILayout.PropertyField(tuning, new GUIContent("血量、移動、視野、巡邏、攻擊與受擊"), true);
        }
        else if (targets.Length == 1)
        {
            var point = (EnemySpawnPoint)target;
            var type = point.enemyType != null ? point.enemyType : point.enemyPrefab != null ? point.enemyPrefab.definition : null;
            EditorGUILayout.HelpBox("目前沿用共用種類／Prefab。勾選「此生成點獨立設定」即可在這裡修改血量、前搖、攻擊間隔等，不影響其他生成點。", MessageType.None);
            if (type != null)
            {
                using (var shared = new SerializedObject(type))
                using (new EditorGUI.DisabledScope(true))
                {
                    var tuning = shared.FindProperty("settings");
                    EditorGUILayout.PropertyField(tuning, new GUIContent("目前共用參數（預覽）"), true);
                }
            }
        }
        EditorGUILayout.PropertyField(serializedObject.FindProperty("spawnOnStart"), new GUIContent("進場生成"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("respawn"), new GUIContent("死亡後重生"));
        if (serializedObject.FindProperty("respawn").boolValue)
            EditorGUILayout.PropertyField(serializedObject.FindProperty("respawnDelay"), new GUIContent("重生延遲"));
        serializedObject.ApplyModifiedProperties();
        if (GUILayout.Button("複製種類參數到本地覆寫"))
            CopySharedSettings();
        if (targets.Length == 1)
        {
            var point = (EnemySpawnPoint)target;
            var type = point.enemyType != null ? point.enemyType : point.enemyPrefab != null ? point.enemyPrefab.definition : null;
            if (type != null)
            {
                if (GUILayout.Button("選取共用種類設定")) Selection.activeObject = type;
                if (!string.IsNullOrEmpty(type.designerNotes)) EditorGUILayout.HelpBox(type.designerNotes, MessageType.None);
            }
            if (Application.isPlaying)
            {
                if (GUILayout.Button("生成")) point.Spawn();
                if (GUILayout.Button("清除")) point.Despawn();
            }
        }
    }
    private void CopySharedSettings()
    {
        foreach (EnemySpawnPoint point in targets)
        {
            var type = point.enemyType != null ? point.enemyType : point.enemyPrefab != null ? point.enemyPrefab.definition : null;
            Undo.RecordObject(point, "Copy enemy tuning");
            if (type != null && type.settings != null)
                point.settings = JsonUtility.FromJson<EnemyTuning>(JsonUtility.ToJson(type.settings));
            else if (point.settings == null) point.settings = new EnemyTuning();
            point.overrideSettings = true;
            EditorUtility.SetDirty(point);
            PrefabUtility.RecordPrefabInstancePropertyModifications(point);
        }
    }

    private void OnSceneGUI()
    {
        var point = (EnemySpawnPoint)target;
        if (multipleSpawnPoints || Application.isPlaying) return;
        var evt = Event.current;
        if (placingPatrol)
        {
            if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
            { placingPatrol = false; evt.Use(); Repaint(); SceneView.RepaintAll(); }
            if (!evt.alt)
            {
                if (evt.type == EventType.Layout) HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                var hits = Physics.RaycastAll(ray, 10000f, ~0, QueryTriggerInteraction.Ignore);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var hit in hits)
                {
                    if (hit.normal.y < 0.25f || hit.collider.transform.IsChildOf(point.transform)) continue;
                    Handles.color = Color.cyan;
                    Handles.DrawWireDisc(hit.point, hit.normal, 0.2f);
                    Handles.Label(hit.point + Vector3.up * 0.25f, "左鍵新增巡邏點");
                    if (evt.type == EventType.MouseDown && evt.button == 0)
                    { AddPatrolPoint(point, hit.point); evt.Use(); Repaint(); }
                    break;
                }
                if (evt.type == EventType.MouseMove) SceneView.RepaintAll();
            }
        }
        if (point.patrolPoints == null) return;
        Handles.color = Color.cyan;
        Transform previous = null;
        for (int i = 0; i < point.patrolPoints.Length; i++)
        {
            var marker = point.patrolPoints[i];
            if (marker == null) continue;
            Handles.Label(marker.position + Vector3.up * 0.2f, "巡邏 " + (i + 1));
            if (previous != null)
            {
                Handles.DrawDottedLine(previous.position, marker.position, 5f);
                Vector3 direction = marker.position - previous.position;
                if (direction.sqrMagnitude > 0.001f)
                    Handles.ArrowHandleCap(0, Vector3.Lerp(previous.position, marker.position, 0.5f), Quaternion.LookRotation(direction), HandleUtility.GetHandleSize(marker.position) * 0.35f, EventType.Repaint);
            }
            if (!placingPatrol && Handles.Button(marker.position, Quaternion.identity, HandleUtility.GetHandleSize(marker.position) * 0.08f, HandleUtility.GetHandleSize(marker.position) * 0.12f, Handles.SphereHandleCap))
            { selectedPatrol = i; patrolList.index = i; Repaint(); }
            if (!placingPatrol && selectedPatrol == i)
            {
            EditorGUI.BeginChangeCheck();
            Vector3 position = Handles.PositionHandle(marker.position, marker.rotation);
            if (EditorGUI.EndChangeCheck()) { Undo.RecordObject(marker, "Move patrol point"); marker.position = position; PrefabUtility.RecordPrefabInstancePropertyModifications(marker); }
            }
            previous = marker;
        }
    }

    [MenuItem("Assets/Create/Morld/Create Example Enemy Types")]
    private static void CreateExamples()
    {
        const string folder = "Assets/_Project/Prefabs/Enemies";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/_Project/Prefabs", "Enemies");
        CreateExample(folder, "QuickGuard", 0.35f, 0.6f, 1.2f, false);
        CreateExample(folder, "HeavyGuard", 1.1f, 1.4f, 3f, false);
        CreateExample(folder, "PatrolGuard", 0.65f, 1f, 2f, true);
        var chase = CreateInstance<EnemyDefinition>();
        chase.displayName = "3D 追擊守衛";
        chase.prefab = AssetDatabase.LoadAssetAtPath<StationaryMeleeEnemy>(folder + "/StationaryMeleeEnemy.prefab");
        chase.settings.movement = EnemyTuning.Movement.Chase;
        AssetDatabase.CreateAsset(chase, AssetDatabase.GenerateUniqueAssetPath(folder + "/ChaseGuard.asset"));
        AssetDatabase.SaveAssets();
    }

    private static void CreateExample(string folder, string name, float windup, float recovery, float interval, bool patrol)
    {
        var type = CreateInstance<EnemyDefinition>();
        type.displayName = name;
        type.prefab = AssetDatabase.LoadAssetAtPath<StationaryMeleeEnemy>(folder + "/StationaryMeleeEnemy.prefab");
        type.settings.windup = windup;
        type.settings.recovery = recovery;
        type.settings.attackInterval = interval;
        type.settings.movement = patrol ? EnemyTuning.Movement.Patrol : EnemyTuning.Movement.Stationary;
        type.designerNotes = patrol ? "沿生成點本地 X 軸巡邏；遇到實體牆或平台邊緣折返。" : "定點近戰。修改本資產會影響所有共用此種類的生成點。";
        AssetDatabase.CreateAsset(type, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".asset"));
    }
}
