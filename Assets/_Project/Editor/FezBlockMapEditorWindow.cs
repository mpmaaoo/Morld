#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 簡易 1x1 方塊地圖編輯器：在 Scene 視圖以網格放置／清除方塊。
/// 筆刷種類來自 <see cref="FezBlockMapBrushCatalog"/>；新方塊加目錄即可。
/// </summary>
public sealed class FezBlockMapEditorWindow : EditorWindow
{
    private const string PrefRootKey = "Lichronicle.FezBlockMap.RootId";
    private const string PrefDragKey = "Lichronicle.FezBlockMap.DragPaint";
    private const string PrefBrushKey = "Lichronicle.FezBlockMap.BrushId";
    private const float GridSize = 1f;
    private const float PaintHoldIntervalSeconds = 0.2f;

    private static readonly Color ErasePreviewColor = new Color(1f, 0.25f, 0.2f, 0.35f);

    private readonly GameObject[] _brushPrefabs = new GameObject[FezBlockMapBrushCatalog.PlaceBrushes.Length];
    private readonly string[] _brushPopupNames;

    private Transform _mapRoot;
    private int _brushIndex; // PlaceBrushes index；Erase 時為 -1
    private bool _eraseMode;
    private bool _paintEnabled;
    private bool _replaceExisting = true;
    private bool _dragPaint = true;
    private Vector3Int? _hoverCell;
    private Vector3Int? _lastPaintCell;
    private double _lastPaintTime;
    private int _lastErasedInstanceId;
    private Vector2 _scroll;
    private Vector3? _surfacePoint;
    [SerializeField] private int _rampTurns, _enemyTurns;
    [SerializeField] private EnemyDefinition _enemyType;
    [SerializeField] private EnemyNavigationArea _navigationArea;
    private Transform _spawnRoot;
    private static string ProjectPref(string key) => key + "." + Hash128.Compute(Application.dataPath);

    public FezBlockMapEditorWindow()
    {
        var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
        _brushPopupNames = new string[brushes.Length + 1];
        for (var i = 0; i < brushes.Length; i++)
            _brushPopupNames[i] = brushes[i].DisplayName;
        _brushPopupNames[brushes.Length] = "清除";
    }

    [MenuItem("Lichronicle/Map/Block Map Editor")]
    [MenuItem("Morld/Map/Block Map Editor")]
    public static void Open()
    {
        var win = GetWindow<FezBlockMapEditorWindow>("Block Map");
        win.minSize = new Vector2(320, 360);
        win.Show();
    }

    private void OnEnable()
    {
        LoadPrefs();
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SavePrefs();
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
        EditorGUILayout.LabelField("Morld 地圖編輯器", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "開啟「繪製模式」後，在 Scene 視圖：\n" +
            "• 左鍵：依格點放置一格（對準可見模型表面）\n" +
            "• 右鍵：刪除游標下可見模型對應的方塊\n" +
            "• 中鍵：吸取游標下可見模型的方塊種類\n" +
            "• Shift＋左鍵：清除游標格（格點模式）\n" +
            "• Alt＋滑鼠：照常旋轉視角\n" +
            "• 左鍵按住拖曳：每 0.2 秒放置一格\n" +
            "生成點放在表面上；玩家生成點再次放置會移動原出生點。\n" +
            "方塊／敵人朝向：使用下方方向選單或轉向按鈕。生成點與地形分開管理。",
            MessageType.Info);

        _paintEnabled = EditorGUILayout.ToggleLeft("繪製模式（Scene 可繪）", _paintEnabled);

        var popupIndex = _eraseMode ? _brushPopupNames.Length - 1 : Mathf.Clamp(_brushIndex, 0, FezBlockMapBrushCatalog.PlaceBrushes.Length - 1);
        var nextPopup = EditorGUILayout.Popup("筆刷", popupIndex, _brushPopupNames);
        if (nextPopup != popupIndex)
        {
            if (nextPopup >= FezBlockMapBrushCatalog.PlaceBrushes.Length)
            {
                _eraseMode = true;
            }
            else
            {
                _eraseMode = false;
                _brushIndex = nextPopup;
            }
        }

        if (!_eraseMode)
        {
            string id = FezBlockMapBrushCatalog.PlaceBrushes[Mathf.Clamp(_brushIndex, 0, FezBlockMapBrushCatalog.PlaceBrushes.Length - 1)].Id;
            if (!FezBlockMapBrushCatalog.PlaceBrushes[_brushIndex].IsSpawn)
                _rampTurns = EditorGUILayout.Popup(id == "ramp" ? "上坡方向" : "方塊朝向", _rampTurns,
                    id == "ramp" ? new[] { "+X", "−Z", "−X", "+Z" } : new[] { "0°", "90°", "180°", "270°" });
            if (id == "enemySpawn")
            {
                _enemyTurns = EditorGUILayout.Popup("敵人朝向", _enemyTurns, new[] { "+Z", "+X", "−Z", "−X" });
                _enemyType = (EnemyDefinition)EditorGUILayout.ObjectField("敵人種類（空白沿用 Prefab）", _enemyType, typeof(EnemyDefinition), false);
                _navigationArea = (EnemyNavigationArea)EditorGUILayout.ObjectField("尋路區域（可留空）", _navigationArea, typeof(EnemyNavigationArea), true);
                EditorGUILayout.HelpBox("放置後選取生成點，可在 Inspector 調整巡邏點與個別參數。修改地形後請重建導航。", MessageType.Info);
            }
            if (!FezBlockMapBrushCatalog.PlaceBrushes[_brushIndex].IsSpawn || id == "enemySpawn")
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    int turn = 0;
                    if (GUILayout.Button("左轉 90°")) turn = 3;
                    if (GUILayout.Button("右轉 90°")) turn = 1;
                    if (turn != 0)
                    {
                        if (id == "enemySpawn") _enemyTurns = (_enemyTurns + turn) % 4;
                        else _rampTurns = (_rampTurns + turn) % 4;
                        _lastPaintCell = null; SceneView.RepaintAll();
                    }
                }
            }
        }

        _replaceExisting = EditorGUILayout.ToggleLeft("同格已有方塊則取代", _replaceExisting);
        _dragPaint = EditorGUILayout.ToggleLeft("右鍵拖曳連續刪除", _dragPaint);

        EditorGUILayout.Space(6);
        _mapRoot = (Transform)EditorGUILayout.ObjectField("地圖根節點", _mapRoot, typeof(Transform), true);
        if (_mapRoot == null && GUILayout.Button("建立 / 尋找 MapRoot"))
            EnsureMapRoot();

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("筆刷 Prefab", EditorStyles.boldLabel);
        var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
        for (var i = 0; i < brushes.Length; i++)
        {
            _brushPrefabs[i] = (GameObject)EditorGUILayout.ObjectField(
                brushes[i].FieldLabel,
                _brushPrefabs[i],
                typeof(GameObject),
                false);
        }

        if (GUILayout.Button("載入預設筆刷"))
            LoadDefaultPrefabs();

        EditorGUILayout.Space(8);
        using (new EditorGUI.DisabledScope(!_paintEnabled))
        {
            EditorGUILayout.LabelField(_hoverCell.HasValue
                ? $"游標格子：{_hoverCell.Value}"
                : "游標格子：（移到 Scene 場景面）");
        }

        EditorGUILayout.Space(8);
        if (GUILayout.Button("將地圖根下的格子座標寫入方塊"))
            SnapMapRootChildren();

        if (GUI.changed)
            SavePrefs();
        }
        EditorGUILayout.EndScrollView();
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!_paintEnabled || EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        var e = Event.current;

        // Alt：視角操作放行（旋轉常用 Alt＋左鍵）
        if (e.alt)
        {
            UpdateHoverCell(e);
            if (_hoverCell.HasValue && e.type == EventType.Repaint)
                DrawCellPreview(_hoverCell.Value);
            return;
        }

        UpdateHoverCell(e);

        // 中鍵：吸取筆刷
        if (e.type == EventType.MouseDown && e.button == 2)
        {
            if (TryPickBrushFromRaycast(e.mousePosition))
            {
                e.Use();
                sceneView.Repaint();
                Repaint();
            }

            return;
        }

        // 右鍵：刪除可見方塊
        if ((e.type == EventType.MouseDown || (_dragPaint && e.type == EventType.MouseDrag)) && e.button == 1)
        {
            if (TryEraseByRaycast(e.mousePosition, e.type == EventType.MouseDown))
            {
                e.Use();
                sceneView.Repaint();
            }

            return;
        }

        if (e.type == EventType.MouseUp)
        {
            _lastPaintCell = null;
            _lastErasedInstanceId = 0;
        }

        if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0)
        {
            if (!_hoverCell.HasValue)
                return;

            var erase = e.shift || _eraseMode;
            if (erase)
            {
                if (TryPaint(_hoverCell.Value, erase: true, force: e.type == EventType.MouseDown))
                {
                    e.Use();
                    sceneView.Repaint();
                }
            }
            else if (TryPaint(_hoverCell.Value, erase: false, force: e.type == EventType.MouseDown))
            {
                e.Use();
                sceneView.Repaint();
            }
        }

        if (_hoverCell.HasValue && e.type == EventType.Repaint)
            DrawCellPreview(_hoverCell.Value);

        if (e.type == EventType.MouseMove)
            sceneView.Repaint();
    }

    private bool TryPaint(Vector3Int cell, bool erase, bool force)
    {
        if (!force)
        {
            if (_lastPaintCell.HasValue && _lastPaintCell.Value == cell)
                return false;

            if (EditorApplication.timeSinceStartup - _lastPaintTime < PaintHoldIntervalSeconds)
                return false;
        }

        PaintAt(cell, erase);
        _lastPaintCell = cell;
        _lastPaintTime = EditorApplication.timeSinceStartup;
        return true;
    }

    private bool TryEraseByRaycast(Vector2 guiPoint, bool force)
    {
        EnsureMapRoot();
        if (_mapRoot == null)
            return false;

        if (!TryPickVisibleBlock(guiPoint, out var block))
            return false;

        if (!force)
        {
            if (block.GetInstanceID() == _lastErasedInstanceId)
                return false;

            if (EditorApplication.timeSinceStartup - _lastPaintTime < PaintHoldIntervalSeconds)
                return false;
        }

        _lastErasedInstanceId = block.GetInstanceID();
        _lastPaintTime = EditorApplication.timeSinceStartup;
        Undo.DestroyObjectImmediate(block);
        EditorUtility.SetDirty(_mapRoot);
        return true;
    }

    private bool TryPickVisibleBlock(Vector2 guiPoint, out GameObject block)
    {
        block = null;
        // Spawn markers have no gameplay collider or renderer. Their Scene gizmos
        // remain paint targets without adding physical obstacles to the level.
        float nearest = 16f;
        foreach (var marker in SceneSpawnPoints())
        {
            var player = marker.GetComponent<PlayerSpawner>();
            Vector3 position = marker.position + Vector3.up * (player != null ? 0.35f - PlayerFootOffset(player) : 0.5f);
            float distance = Vector2.Distance(HandleUtility.WorldToGUIPoint(position), guiPoint);
            if (distance >= nearest) continue;
            var ray = HandleUtility.GUIPointToWorldRay(guiPoint);
            float depth = Vector3.Dot(position - ray.origin, ray.direction);
            if (depth <= 0f || Physics.Raycast(ray, out var hit, Mathf.Max(0f, depth - 0.1f),
                    Physics.AllLayers, QueryTriggerInteraction.Ignore)) continue;
            block = marker.gameObject; nearest = distance;
        }
        if (block != null) return true;
        var picked = HandleUtility.PickGameObject(guiPoint, false);
        if (picked == null)
            return false;

        block = ResolveMapBlockRoot(picked);
        if (block != null)
            return true;

        var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(picked);
        if (prefabRoot != null && IsKnownBlockPrefab(prefabRoot))
        {
            block = prefabRoot;
            return true;
        }

        return false;
    }

    private GameObject ResolveMapBlockRoot(GameObject go)
    {
        if (go == null || _mapRoot == null)
            return null;

        var player = go.GetComponentInParent<PlayerSpawner>();
        if (player != null) return player.gameObject;
        var enemy = go.GetComponentInParent<EnemySpawnPoint>();
        if (enemy != null) return enemy.gameObject;

        var t = go.transform;
        while (t != null)
        {
            if (t.parent == _mapRoot)
                return t.gameObject;
            if (t == _mapRoot)
                return null;
            t = t.parent;
        }

        var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
        if (prefabRoot != null && IsKnownBlockPrefab(prefabRoot))
            return prefabRoot;

        return null;
    }

    private bool IsKnownBlockPrefab(GameObject go)
    {
        if (go == null)
            return false;

        var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
        if (source == null)
            return false;

        for (var i = 0; i < _brushPrefabs.Length; i++)
        {
            if (_brushPrefabs[i] != null && source == _brushPrefabs[i])
                return true;
        }

        return source.name.IndexOf("Block", System.StringComparison.OrdinalIgnoreCase) >= 0
            || source.name.IndexOf("Wall", System.StringComparison.OrdinalIgnoreCase) >= 0
            || source.name.IndexOf("Boundary", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool TryPickBrushFromRaycast(Vector2 guiPoint)
    {
        if (!TryPickVisibleBlock(guiPoint, out var block))
            return false;

        var source = PrefabUtility.GetCorrespondingObjectFromSource(block) as GameObject;
        if (source == null && block.GetComponent<FezGridBlock>() != null && block.transform.childCount == 1)
            source = PrefabUtility.GetCorrespondingObjectFromSource(block.transform.GetChild(0).gameObject);
        if (source == null)
            source = block;

        var index = FezBlockMapBrushCatalog.FindMatchingBrushIndex(source, block);
        _eraseMode = false;
        _brushIndex = index;
        if (index >= 0 && index < _brushPrefabs.Length && source != null && EditorUtility.IsPersistent(source))
            _brushPrefabs[index] = source;
        if (block.GetComponentInChildren<EnemySpawnPoint>() is EnemySpawnPoint spawn)
        {
            _enemyType = spawn.enemyType; _navigationArea = spawn.navigationArea;
            _enemyTurns = Mathf.RoundToInt(spawn.transform.eulerAngles.y / 90f) % 4;
        }
        var ramp = block.GetComponentInChildren<FezRampBlock>();
        if (ramp != null) _rampTurns = Mathf.RoundToInt(ramp.transform.eulerAngles.y / 90f) % 4;
        else if (!FezBlockMapBrushCatalog.PlaceBrushes[index].IsSpawn)
        {
            var visual = block.GetComponent<FezGridBlock>() != null && block.transform.childCount == 1
                ? block.transform.GetChild(0) : block.transform;
            _rampTurns = Mathf.RoundToInt(visual.eulerAngles.y / 90f) % 4;
        }

        Debug.Log($"[FezBlockMap] 筆刷切換為 {FezBlockMapBrushCatalog.PlaceBrushes[index].DisplayName}：{source.name}");
        SavePrefs();
        Repaint();
        return true;
    }

    private void UpdateHoverCell(Event e)
    {
        if (e.type != EventType.MouseMove
            && e.type != EventType.MouseDown
            && e.type != EventType.MouseDrag
            && e.type != EventType.Repaint
            && e.type != EventType.Layout)
            return;

        if (HandleUtility.PlaceObject(e.mousePosition, out var point, out var normal))
        {
            _surfacePoint = point;
            _hoverCell = WorldToCell(point + normal * 0.01f);
            return;
        }

        var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        var plane = new Plane(Vector3.up, Vector3.zero);
        if (plane.Raycast(ray, out var enter))
        {
            _surfacePoint = ray.GetPoint(enter);
            _hoverCell = WorldToCell(ray.GetPoint(enter));
            return;
        }

        _hoverCell = null;
        _surfacePoint = null;
    }

    private static Vector3Int WorldToCell(Vector3 world)
    {
        return new Vector3Int(
            Mathf.FloorToInt(world.x / GridSize),
            Mathf.FloorToInt(world.y / GridSize),
            Mathf.FloorToInt(world.z / GridSize));
    }

    private static Vector3 CellToWorld(Vector3Int cell)
    {
        return new Vector3(cell.x * GridSize, cell.y * GridSize, cell.z * GridSize);
    }

    private void DrawCellPreview(Vector3Int cell)
    {
        var center = CellToWorld(cell) + Vector3.one * (GridSize * 0.5f);
        var size = Vector3.one * GridSize;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;

        Color preview;
        if (_eraseMode)
            preview = ErasePreviewColor;
        else
        {
            var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
            var i = Mathf.Clamp(_brushIndex, 0, brushes.Length - 1);
            preview = brushes[i].PreviewColor;
        }

        Handles.color = preview;
        Handles.DrawWireCube(center, size);
        Handles.color = new Color(preview.r, preview.g, preview.b, 0.12f);
        Handles.CubeHandleCap(0, center, Quaternion.identity, GridSize, EventType.Repaint);
        if (!_eraseMode)
        {
            var brush = FezBlockMapBrushCatalog.PlaceBrushes[_brushIndex];
            if (brush.Id == "ramp")
            {
                Quaternion rotation = Quaternion.Euler(0, _rampTurns * 90f, 0);
                Vector3 origin = CellToWorld(cell) + Vector3.one * 0.5f;
                Vector3 a = origin + rotation * new Vector3(-0.5f, -0.5f, 0);
                Vector3 b = origin + rotation * new Vector3(0.5f, -0.5f, 0);
                Vector3 c = origin + rotation * new Vector3(0.5f, 0.5f, 0);
                Handles.color = Color.cyan; Handles.DrawAAPolyLine(4f, a, b, c, a);
            }
            else if (!brush.IsSpawn)
            {
                Handles.color = new Color(brush.PreviewColor.r, brush.PreviewColor.g, brush.PreviewColor.b, 1f);
                Handles.ArrowHandleCap(0, center, Quaternion.Euler(0, _rampTurns * 90f, 0), 0.8f, EventType.Repaint);
            }
            if (brush.IsSpawn)
            {
                Vector3 feet = SpawnFeet(cell);
                Handles.color = brush.PreviewColor;
                Handles.DrawWireCube(feet + Vector3.up * 0.5f, new Vector3(0.5f, 1f, 0.5f));
                Handles.Label(feet + Vector3.up, brush.DisplayName);
                if (brush.Id == "enemySpawn")
                    Handles.ArrowHandleCap(0, feet + Vector3.up * 0.3f,
                        Quaternion.Euler(0, _enemyTurns * 90f, 0), 0.8f, EventType.Repaint);
            }
        }
    }

    private void PaintAt(Vector3Int cell, bool erase)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        EnsureMapRoot();
        if (_mapRoot == null)
            return;

        var existing = erase ? FindSpawnAtCell(cell, null) ?? FindBlockAtCell(cell) : FindBlockAtCell(cell);
        if (erase)
        {
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
                EditorUtility.SetDirty(_mapRoot);
            }

            return;
        }

        var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
        var index = Mathf.Clamp(_brushIndex, 0, brushes.Length - 1);
        var prefab = _brushPrefabs[index];
        if (brushes[index].IsSpawn)
        {
            PaintSpawn(cell, brushes[index], prefab);
            return;
        }
        if (prefab == null && brushes[index].Factory == null)
        {
            Debug.LogWarning($"[FezBlockMap] 尚未指定「{brushes[index].DisplayName}」Prefab。");
            return;
        }

        if (existing != null)
        {
            if (!_replaceExisting)
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        GameObject instance;
        if (!brushes[index].IsSpawn)
        {
            instance = new GameObject(prefab != null ? prefab.name : "RampBlock1x1");
            instance.transform.SetParent(_mapRoot, true);
            var shape = prefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(prefab) : brushes[index].Factory();
            shape.transform.SetParent(instance.transform, false);
            Quaternion rotation = Quaternion.Euler(0, _rampTurns * 90f, 0);
            shape.transform.localPosition = Vector3.one * 0.5f - rotation * (Vector3.one * 0.5f);
            shape.transform.localRotation = rotation;
        }
        else instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _mapRoot);

        Undo.RegisterCreatedObjectUndo(instance, "Place Fez Block");
        instance.transform.position = CellToWorld(cell);
        instance.transform.rotation = Quaternion.identity;
        instance.name = $"{(prefab != null ? prefab.name : "RampBlock1x1")}_{cell.x}_{cell.y}_{cell.z}";
        FezGridCellAuthoring.WriteCell(instance, cell);
        EditorUtility.SetDirty(_mapRoot);
    }

    private IEnumerable<Transform> SceneSpawnPoints()
    {
        var scene = _mapRoot != null ? _mapRoot.gameObject.scene : UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        foreach (var player in Object.FindObjectsOfType<PlayerSpawner>(true))
            if (player.gameObject.scene == scene) yield return player.transform;
        foreach (var enemy in Object.FindObjectsOfType<EnemySpawnPoint>(true))
            if (enemy.gameObject.scene == scene) yield return enemy.transform;
    }

    private Vector3 SpawnFeet(Vector3Int cell)
    {
        var feet = CellToWorld(cell) + new Vector3(0.5f, 0.03f, 0.5f);
        if (_surfacePoint.HasValue)
        {
            float surfaceY = _surfacePoint.Value.y;
            float nearest = float.PositiveInfinity;
            foreach (var hit in Physics.RaycastAll(new Vector3(feet.x, surfaceY + 1.1f, feet.z),
                         Vector3.down, 2.2f, Physics.AllLayers, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < 0.7f || hit.collider.GetComponentInParent<PlayerController>() != null ||
                    hit.collider.GetComponentInParent<StationaryMeleeEnemy>() != null) continue;
                float distance = Mathf.Abs(hit.point.y - _surfacePoint.Value.y);
                if (distance >= nearest) continue;
                nearest = distance; surfaceY = hit.point.y;
            }
            feet.y = surfaceY + 0.03f;
        }
        return feet;
    }

    private static float PlayerFootOffset(PlayerSpawner point)
    {
        var data = new SerializedObject(point);
        var player = data.FindProperty("playerPrefab").objectReferenceValue as GameObject;
        if (player == null) player = data.FindProperty("existingPlayer").objectReferenceValue as GameObject;
        var controller = player != null ? player.GetComponent<CharacterController>() : null;
        return controller != null
            ? -(controller.center.y - controller.height * 0.5f) * Mathf.Abs(player.transform.lossyScale.y) + controller.skinWidth
            : 0.55f;
    }

    private GameObject FindSpawnAtCell(Vector3Int cell, string id)
    {
        foreach (var marker in SceneSpawnPoints())
        {
            var player = marker.GetComponent<PlayerSpawner>();
            if (id == "playerSpawn" && player == null || id == "enemySpawn" && player != null) continue;
            Vector3 feet = marker.position;
            if (player != null) feet.y -= PlayerFootOffset(player);
            if (WorldToCell(feet) == cell) return marker.gameObject;
        }
        return null;
    }

    private void PaintSpawn(Vector3Int cell, FezBlockMapBrushDef brush, GameObject prefab)
    {
        GameObject instance = null;
        if (brush.Id == "playerSpawn")
        {
            foreach (var marker in SceneSpawnPoints())
                if (marker.GetComponent<PlayerSpawner>() != null) { instance = marker.gameObject; break; }
        }
        else instance = FindSpawnAtCell(cell, brush.Id);
        if (instance != null && brush.Id != "playerSpawn" && !_replaceExisting) return;
        if (instance == null)
        {
            if (prefab == null || !brush.Matches(prefab, null))
            {
                Debug.LogWarning($"[Morld Map] 請指定有效的{brush.DisplayName} Prefab。"); return;
            }
            if (_spawnRoot == null)
            {
                var scene = _mapRoot.gameObject.scene;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == "MorldSpawns") { _spawnRoot = root.transform; break; }
                if (_spawnRoot == null)
                {
                    var root = new GameObject("MorldSpawns");
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
                    Undo.RegisterCreatedObjectUndo(root, "Create spawn group"); _spawnRoot = root.transform;
                }
            }
            instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _spawnRoot);
            Undo.RegisterCreatedObjectUndo(instance, "Place spawn point");
        }
        Undo.RecordObject(instance.transform, "Move spawn point");
        Vector3 position = SpawnFeet(cell);
        var player = instance.GetComponent<PlayerSpawner>();
        if (player != null)
        {
            position.y += PlayerFootOffset(player);
            var data = new SerializedObject(player);
            data.FindProperty("spawnPoint").objectReferenceValue = player.transform;
            data.FindProperty("snapToSpawnPoint").boolValue = true;
            data.ApplyModifiedProperties();
        }
        else
        {
            instance.transform.rotation = Quaternion.Euler(0, _enemyTurns * 90f, 0);
            var enemy = instance.GetComponent<EnemySpawnPoint>();
            Undo.RecordObject(enemy, "Configure encounter");
            if (_enemyType != null) enemy.enemyType = _enemyType;
            if (_navigationArea != null) enemy.navigationArea = _navigationArea;
            PrefabUtility.RecordPrefabInstancePropertyModifications(enemy);
        }
        instance.transform.position = position;
        PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
        EditorUtility.SetDirty(instance);
        Selection.activeGameObject = instance;
    }

    [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
    private static void DrawPlayerSpawn(PlayerSpawner point, GizmoType type)
    {
        Vector3 feet = point.transform.position - Vector3.up * PlayerFootOffset(point);
        Gizmos.color = new Color(0.2f, 1f, 0.8f);
        Gizmos.DrawWireCube(feet + Vector3.up * 0.35f, new Vector3(0.45f, 0.7f, 0.45f));
        Handles.Label(feet + Vector3.up * 0.8f, "玩家生成點");
    }

    private GameObject FindBlockAtCell(Vector3Int cell)
    {
        if (_mapRoot == null)
            return null;

        var target = CellToWorld(cell);
        for (var i = 0; i < _mapRoot.childCount; i++)
        {
            var child = _mapRoot.GetChild(i);
            if (child == null)
                continue;
            if (child.GetComponent<PlayerSpawner>() != null || child.GetComponent<EnemySpawnPoint>() != null) continue;
            if (FezGridMap.TryReadStoredCell(child, out var stored) && stored == cell)
                return child.gameObject;
            if ((child.position - target).sqrMagnitude < 0.01f)
                return child.gameObject;
        }

        return null;
    }

    private void EnsureMapRoot()
    {
        if (_mapRoot != null)
            return;

        var existing = GameObject.Find("FezMapRoot");
        if (existing != null)
        {
            _mapRoot = existing.transform;
            return;
        }

        var go = new GameObject("FezMapRoot");
        Undo.RegisterCreatedObjectUndo(go, "Create FezMapRoot");
        _mapRoot = go.transform;
    }

    private void SnapMapRootChildren()
    {
        EnsureMapRoot();
        if (_mapRoot == null)
            return;

        var count = 0;
        var transforms = new List<Transform>(_mapRoot.childCount);
        for (var i = 0; i < _mapRoot.childCount; i++)
            transforms.Add(_mapRoot.GetChild(i));

        count = FezGridCellAuthoring.WriteMapRoot(transforms, "Snap Map Root To Grid");
        Debug.Log($"[FezBlockMap] 已把地圖根下 {count} 個方塊的格子寫入方塊。");
    }

    private void LoadDefaultPrefabs()
    {
        var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
        var missing = false;
        for (var i = 0; i < brushes.Length; i++)
        {
            _brushPrefabs[i] = LoadPrefabByGuid(brushes[i].DefaultPrefabGuid);
            if (_brushPrefabs[i] == null && brushes[i].Factory == null)
                missing = true;
        }

        if (missing)
            Debug.LogWarning("[FezBlockMap] 部分預設筆刷 Prefab 找不到，請手動指定。");
    }

    private static GameObject LoadPrefabByGuid(string guid)
    {
        var path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path))
            return null;
        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    private void LoadPrefs()
    {
        var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
        for (var i = 0; i < brushes.Length; i++)
        {
            var guid = EditorPrefs.GetString(ProjectPref(brushes[i].PrefKey),
                EditorPrefs.GetString(brushes[i].PrefKey, brushes[i].DefaultPrefabGuid));
            _brushPrefabs[i] = LoadPrefabByGuid(guid);
            if (_brushPrefabs[i] == null)
                _brushPrefabs[i] = LoadPrefabByGuid(brushes[i].DefaultPrefabGuid);
        }

        _dragPaint = EditorPrefs.GetBool(ProjectPref(PrefDragKey), true);
        var brushId = EditorPrefs.GetString(ProjectPref(PrefBrushKey), "platform");
        if (brushId == FezBlockMapBrushCatalog.EraseId)
        {
            _eraseMode = true;
            _brushIndex = 0;
        }
        else
        {
            _eraseMode = false;
            _brushIndex = FezBlockMapBrushCatalog.IndexOfId(brushId);
        }

        if (GlobalObjectId.TryParse(EditorPrefs.GetString(ProjectPref(PrefRootKey), ""), out var rootId))
        {
            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(rootId) as Transform;
            if (obj != null && obj.gameObject.scene.IsValid())
                _mapRoot = obj;
        }
    }

    private void SavePrefs()
    {
        var brushes = FezBlockMapBrushCatalog.PlaceBrushes;
        for (var i = 0; i < brushes.Length; i++)
        {
            if (_brushPrefabs[i] == null)
                continue;
            var path = AssetDatabase.GetAssetPath(_brushPrefabs[i]);
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid))
                EditorPrefs.SetString(ProjectPref(brushes[i].PrefKey), guid);
        }

        EditorPrefs.SetBool(ProjectPref(PrefDragKey), _dragPaint);
        EditorPrefs.SetString(
            ProjectPref(PrefBrushKey),
            _eraseMode
                ? FezBlockMapBrushCatalog.EraseId
                : brushes[Mathf.Clamp(_brushIndex, 0, brushes.Length - 1)].Id);
        EditorPrefs.SetString(ProjectPref(PrefRootKey), _mapRoot != null
            ? GlobalObjectId.GetGlobalObjectIdSlow(_mapRoot).ToString() : "");
    }
}
#endif
