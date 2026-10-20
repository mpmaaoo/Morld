using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;

/// <summary>Explicit, bounded Play-mode capture; never adds or saves scene objects.</summary>
public sealed class MorldPerformanceRecorder : EditorWindow
{
    private struct Frame
    {
        public float seconds;
        public long movement, path, visibility, allocation;
        public long enemyUpdate, proxySync, mainThread, renderThread, presentWait, renderWait;
        public long cameraRender, scriptUpdate, physicsUpdate, gui, editorLoop, inputHud, inputTrace;
        public int drawCalls, batches, triangles, enemies;
        public int sharedRenderers, sharedMeshGroups;
        public bool focused;
    }
    private static readonly List<Frame> Frames = new List<Frame>(4096);
    private static ProfilerRecorder movement, path, visibility, allocations;
    private static ProfilerRecorder enemyUpdate, proxySync, mainThread, renderThread, presentWait, renderWait;
    private static ProfilerRecorder cameraRender, scriptUpdate, physicsUpdate, gui, editorLoop, inputHud, inputTrace;
    private static int savedCaptureFrameRate;
    private static string captureSettings;
    private static bool recording;
    private static int lastFrame;
    private static double warmupUntil;
    private static float capturedSeconds;
    private static string result = "進入 Play 後按開始，在最容易卡頓的區域測試。";
    private static string sceneName;
    private static MorldTerrainRenderOptimizer terrainOptimizer;

    [MenuItem("Morld/Debug/關卡效能量測（30 秒）")]
    public static void Open() { GetWindow<MorldPerformanceRecorder>("關卡效能量測"); }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("量測包含 Unity 編輯器開銷。請保持 Game 視窗在前景；開始後先暖機 2 秒，再記錄 30 秒。", MessageType.Info);
        if (EditorApplication.isPlaying)
        {
            var prototype = MorldPrototypePerformance.Instance;
            if (prototype != null)
                using (new EditorGUI.DisabledScope(recording))
                {
                    bool useProfile = EditorGUILayout.Toggle("原型 120 FPS 模式", prototype.enableProfile);
                    if (useProfile != prototype.enableProfile) prototype.SetProfile(useProfile);
                }
            EditorGUILayout.LabelField($"陰影：{QualitySettings.shadowDistance:F0} 單位／{QualitySettings.shadowCascades} 段／{QualitySettings.shadowResolution}");
            if (terrainOptimizer == null) terrainOptimizer = MorldTerrainRenderOptimizer.Instance;
            if (terrainOptimizer != null)
            {
                using (new EditorGUI.DisabledScope(recording))
                    terrainOptimizer.enableOptimization = EditorGUILayout.Toggle("地形網格共用／Instancing", terrainOptimizer.enableOptimization);
                EditorGUILayout.LabelField($"共用 {terrainOptimizer.SharedRendererCount} 個渲染器／{terrainOptimizer.SharedMeshGroupCount} 組網格");
            }
        }
        using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || EditorApplication.isPaused || recording))
            if (GUILayout.Button("開始記錄 30 秒")) StartCapture();
        if (recording && GUILayout.Button("結束並儲存目前結果")) StopCapture();
        EditorGUILayout.LabelField(recording ? $"記錄中：{capturedSeconds:F1} / 30 秒" : result, EditorStyles.wordWrappedLabel);
    }

    private static ProfilerRecorder Watch(ProfilerCategory category, string name)
        => ProfilerRecorder.StartNew(category, name, 1);

    private static ProfilerRecorder WatchAvailable(string name)
    {
        var handles = new List<ProfilerRecorderHandle>();
        ProfilerRecorderHandle.GetAvailable(handles);
        foreach (var handle in handles)
        {
            var description = ProfilerRecorderHandle.GetDescription(handle);
            if (description.Name == name) return Watch(description.Category, name);
        }
        return default;
    }

    private static void StartCapture()
    {
        Frames.Clear();
        capturedSeconds = 0;
        lastFrame = Time.frameCount;
        warmupUntil = EditorApplication.timeSinceStartup + 2;
        sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        savedCaptureFrameRate = Application.targetFrameRate;
        captureSettings = $"Prototype120: {MorldPrototypePerformance.Instance != null && MorldPrototypePerformance.Instance.enableProfile}\n" +
            $"Resolution: {Screen.width}x{Screen.height}; Quality: {QualitySettings.names[QualitySettings.GetQualityLevel()]}\n" +
            $"GPU: {SystemInfo.graphicsDeviceName}; VSync: {QualitySettings.vSyncCount}; Capture frame cap: unlimited (previous {savedCaptureFrameRate})\n" +
            $"Shadows: {QualitySettings.shadows}; resolution: {QualitySettings.shadowResolution}; cascades: {QualitySettings.shadowCascades}; distance: {QualitySettings.shadowDistance}\n";
        Application.targetFrameRate = -1;
        terrainOptimizer = MorldTerrainRenderOptimizer.Instance;
        movement = Watch(ProfilerCategory.Scripts, "Morld.PlayerMovement");
        path = Watch(ProfilerCategory.Scripts, "Morld.EnemyPathfinding");
        visibility = Watch(ProfilerCategory.Scripts, "Morld.PlayerVisibility");
        allocations = Watch(ProfilerCategory.Memory, "GC Allocated In Frame");
        enemyUpdate = WatchAvailable("Morld.EnemyUpdate");
        proxySync = WatchAvailable("Morld.DepthProxySync");
        mainThread = WatchAvailable("Main Thread");
        renderThread = WatchAvailable("Render Thread");
        presentWait = WatchAvailable("Gfx.WaitForPresentOnGfxThread");
        renderWait = WatchAvailable("Gfx.WaitForRenderThread");
        cameraRender = WatchAvailable("Camera.Render");
        scriptUpdate = WatchAvailable("Update.ScriptRunBehaviourUpdate");
        physicsUpdate = WatchAvailable("FixedUpdate.PhysicsFixedUpdate");
        gui = WatchAvailable("GUI.Repaint");
        editorLoop = WatchAvailable("EditorLoop");
        inputHud = WatchAvailable("Morld.InputHud");
        inputTrace = WatchAvailable("Morld.InputTrace");
        recording = true;
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        SetProbe(ref loop, true);
        PlayerLoop.SetPlayerLoop(loop);
        EditorApplication.playModeStateChanged += PlayChanged;
        AssemblyReloadEvents.beforeAssemblyReload += StopCapture;
    }

    private static long Read(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue : -1;

    private static void Tick()
    {
        if (!recording || !EditorApplication.isPlaying || EditorApplication.isPaused || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        if (EditorApplication.timeSinceStartup < warmupUntil) return;
        float seconds = Time.unscaledDeltaTime;
        if (seconds <= 0) return;
        Frames.Add(new Frame { seconds = seconds, movement = Read(movement), path = Read(path),
            visibility = Read(visibility), allocation = Read(allocations), focused = Application.isFocused,
            drawCalls = UnityEditor.UnityStats.drawCalls, batches = UnityEditor.UnityStats.batches,
            triangles = UnityEditor.UnityStats.triangles, enemies = StationaryMeleeEnemy.ActiveEnemies.Count,
            sharedRenderers = terrainOptimizer != null ? terrainOptimizer.SharedRendererCount : 0,
            sharedMeshGroups = terrainOptimizer != null ? terrainOptimizer.SharedMeshGroupCount : 0,
            enemyUpdate = Read(enemyUpdate), proxySync = Read(proxySync), mainThread = Read(mainThread),
            renderThread = Read(renderThread), presentWait = Read(presentWait), renderWait = Read(renderWait),
            cameraRender = Read(cameraRender), scriptUpdate = Read(scriptUpdate), physicsUpdate = Read(physicsUpdate),
            gui = Read(gui), editorLoop = Read(editorLoop), inputHud = Read(inputHud), inputTrace = Read(inputTrace) });
        capturedSeconds += seconds;
        if (capturedSeconds >= 30) StopCapture();
    }

    private void OnInspectorUpdate() { if (recording) Repaint(); }

    // Sample once per game frame, rather than sampling the independently ticking editor UI.
    private static void SetProbe(ref PlayerLoopSystem loop, bool install)
    {
        if (loop.subSystemList == null) return;
        var children = new List<PlayerLoopSystem>(loop.subSystemList);
        children.RemoveAll(child => child.type == typeof(MorldPerformanceRecorder));
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate) && install)
            {
                var samples = new List<PlayerLoopSystem>(child.subSystemList ?? new PlayerLoopSystem[0]);
                samples.RemoveAll(sample => sample.type == typeof(MorldPerformanceRecorder));
                samples.Add(new PlayerLoopSystem { type = typeof(MorldPerformanceRecorder), updateDelegate = Tick });
                child.subSystemList = samples.ToArray();
            }
            else SetProbe(ref child, install);
            children[i] = child;
        }
        loop.subSystemList = children.ToArray();
    }

    private static void PlayChanged(PlayModeStateChange state)
    { if (state == PlayModeStateChange.ExitingPlayMode) StopCapture(); }

    private static void StopCapture()
    {
        if (!recording) return;
        recording = false;
        Application.targetFrameRate = savedCaptureFrameRate;
        var loop = PlayerLoop.GetCurrentPlayerLoop();
        SetProbe(ref loop, false);
        PlayerLoop.SetPlayerLoop(loop);
        EditorApplication.playModeStateChanged -= PlayChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= StopCapture;
        movement.Dispose(); path.Dispose(); visibility.Dispose(); allocations.Dispose();
        enemyUpdate.Dispose(); proxySync.Dispose(); mainThread.Dispose(); renderThread.Dispose(); presentWait.Dispose(); renderWait.Dispose();
        cameraRender.Dispose(); scriptUpdate.Dispose(); physicsUpdate.Dispose(); gui.Dispose(); editorLoop.Dispose(); inputHud.Dispose(); inputTrace.Dispose();
        if (Frames.Count == 0) { result = "尚未取得暖機後的遊玩影格，請再試一次。"; return; }
        try
        {
            var durations = new float[Frames.Count];
            float worst = 0;
            int slow = 0, unfocused = 0;
            var csv = new StringBuilder("frame,frame_ms,movement_ns,pathfinding_ns,visibility_ns,gc_bytes,game_focused,draw_calls,batches,triangles,enemies,shared_terrain_renderers,shared_terrain_mesh_groups,enemy_update_ns,proxy_sync_ns,main_thread_ns,render_thread_ns,present_wait_ns,render_wait_ns,camera_render_ns,script_update_ns,physics_update_ns,gui_ns,editor_loop_ns,input_hud_ns,input_trace_ns\n");
            for (int i = 0; i < Frames.Count; i++)
            {
                var f = Frames[i]; durations[i] = f.seconds;
                worst = Mathf.Max(worst, f.seconds);
                if (f.seconds > 1f / 30) slow++;
                if (!f.focused) unfocused++;
                csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1:F3},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23},{24},{25}\n", i,
                    f.seconds * 1000, f.movement, f.path, f.visibility, f.allocation, f.focused ? 1 : 0,
                    f.drawCalls, f.batches, f.triangles, f.enemies, f.sharedRenderers, f.sharedMeshGroups,
                    f.enemyUpdate, f.proxySync, f.mainThread, f.renderThread, f.presentWait, f.renderWait,
                    f.cameraRender, f.scriptUpdate, f.physicsUpdate, f.gui, f.editorLoop, f.inputHud, f.inputTrace);
            }
            Array.Sort(durations);
            float p95 = durations[Mathf.Clamp(Mathf.CeilToInt(durations.Length * 0.95f) - 1, 0, durations.Length - 1)];
            int missed120 = 0;
            foreach (var f in Frames) if (f.seconds > 1f / 120) missed120++;
            result = $"場景：{sceneName}\n平均 FPS：{Frames.Count / capturedSeconds:F1}\n最低單幀 FPS：{1f / worst:F1}\n95% 影格耗時：{p95 * 1000:F2} ms\n超過 33.3 ms：{slow} 幀\n未達 120 FPS：{missed120} / {Frames.Count} 幀\nGame 未聚焦：{unfocused} 幀\n";
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Temp/MorldPerformance"));
            Directory.CreateDirectory(directory);
            string filename = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.WriteAllText(Path.Combine(directory, filename + ".csv"), csv.ToString());
            File.WriteAllText(Path.Combine(directory, filename + ".txt"), result + "\n" + captureSettings + "\n-1 means a profiler counter was unavailable. CPU samples may overlap; do not add them together.\n");
            result += "結果已存至專案 Temp/MorldPerformance。";
            Debug.Log("[Morld Performance] " + result);
        }
        catch (Exception error) { result = "儲存量測失敗：" + error.Message; Debug.LogException(error); }
    }
}
