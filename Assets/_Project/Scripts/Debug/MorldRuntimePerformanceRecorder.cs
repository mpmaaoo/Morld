using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

/// <summary>Player-build capture. Frames are buffered in memory and written only after capture.</summary>
[DefaultExecutionOrder(32000)]
public sealed class MorldRuntimePerformanceRecorder : MonoBehaviour
{
    public static MorldRuntimePerformanceRecorder Instance { get; private set; }
    public static string OutputDirectory => Path.Combine(Application.persistentDataPath, "MorldPerformance");
    public bool IsRecording { get; private set; }
    public int SampleCount => frames.Count;
    public float CapturedSeconds => capturedSeconds;
    public bool CapturingUncapped { get; private set; }
    public string LastResultPath { get; private set; }
    private struct Frame
    {
        public int number, width, height, enemies;
        public bool focused;
        public float seconds, wallSeconds;
        public int gc0, gc1, gc2;
        public long movement, path, visibility, enemy, main, render, camera, scripts, physics;
        public long allocation, draws, batches, triangles, gui, hud, input, present;
    }
    private static readonly string[] MarkerNames = {
        "Morld.PlayerMovement", "Morld.EnemyPathfinding", "Morld.PlayerVisibility", "Morld.EnemyUpdate",
        "Main Thread", "Render Thread", "Camera.Render", "Update.ScriptRunBehaviourUpdate",
        "FixedUpdate.PhysicsFixedUpdate", "GC Allocated In Frame", "Draw Calls Count", "Batches Count",
        "Triangles Count", "GUI.Repaint", "Morld.InputHud", "Morld.InputTrace", "Gfx.WaitForPresentOnGfxThread"
    };
    private readonly List<Frame> frames = new List<Frame>(8192);
    private readonly ProfilerRecorder[] counters = new ProfilerRecorder[MarkerNames.Length];
    private double warmupUntil;
    private float capturedSeconds, hudSeconds;
    private int savedFrameRate, savedSync, lastFrame, hudFrames;
    private bool focusLostSinceSample;
    private long lastTimestamp;
    private int lastGc0, lastGc1, lastGc2;
    private string settings, hud = "FPS 暖機中", status = "F10 遊玩記錄／Shift+F10 極限／F11 資料夾";
    private GUIStyle hudStyle;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Application.isEditor || Instance != null || SceneManager.GetActiveScene().name != "SampleScene") return;
        var host = new GameObject("Morld Runtime FPS Recorder") { hideFlags = HideFlags.DontSave };
        DontDestroyOnLoad(host);
        host.AddComponent<MorldRuntimePerformanceRecorder>();
    }
    private void OnEnable()
    {
        if (Instance != null && Instance != this) { enabled = false; return; }
        Instance = this;
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F10))
        {
            if (IsRecording) StopCapture();
            else StartCapture(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        }
        if (Input.GetKeyDown(KeyCode.F11))
        {
            try { Directory.CreateDirectory(OutputDirectory); Application.OpenURL(new Uri(OutputDirectory).AbsoluteUri); }
            catch (Exception error) { status = "無法開啟資料夾：" + error.Message; }
        }
        hudSeconds += Time.unscaledDeltaTime;
        hudFrames++;
        if (hudSeconds >= 0.25f)
        {
            hud = $"FPS {hudFrames / hudSeconds:F1}　{hudSeconds * 1000 / hudFrames:F2} ms　目標 120";
            hudFrames = 0; hudSeconds = 0f;
            if (IsRecording) status = Time.realtimeSinceStartupAsDouble < warmupUntil
                ? "記錄暖機中…" : $"{(CapturingUncapped ? "極限" : "遊玩")}記錄 {capturedSeconds:F1} / 30 秒　F10 結束";
        }
    }
    public void StartCapture(bool measureMaxThroughput = false)
    {
        if (IsRecording || !isActiveAndEnabled) return;
        frames.Clear(); capturedSeconds = 0f; lastFrame = Time.frameCount;
        focusLostSinceSample = !Application.isFocused;
        lastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        lastGc0 = GC.CollectionCount(0); lastGc1 = GC.CollectionCount(1); lastGc2 = GC.CollectionCount(2);
        savedFrameRate = Application.targetFrameRate; savedSync = QualitySettings.vSyncCount;
        CapturingUncapped = measureMaxThroughput;
        settings = $"Scene: {SceneManager.GetActiveScene().name}\nRuntime: Unity {Application.unityVersion}; development build: {Debug.isDebugBuild}; editor: {Application.isEditor}\n" +
            $"GPU: {SystemInfo.graphicsDeviceName}; CPU: {SystemInfo.processorType}\n" +
            $"Resolution at start: {Screen.width}x{Screen.height}; fullscreen: {Screen.fullScreenMode}; quality: {QualitySettings.names[QualitySettings.GetQualityLevel()]}; monitor refresh: {Screen.currentResolution.refreshRateRatio.value:F2} Hz\n" +
            $"Prototype120: {MorldPrototypePerformance.Instance != null && MorldPrototypePerformance.Instance.enableProfile}; terrain instancing: {MorldTerrainRenderOptimizer.Instance != null && MorldTerrainRenderOptimizer.Instance.enableOptimization}\n" +
            $"Shadows: {QualitySettings.shadows}; resolution: {QualitySettings.shadowResolution}; cascades: {QualitySettings.shadowCascades}; distance: {QualitySettings.shadowDistance}\n" +
            $"Capture: {(measureMaxThroughput ? "maximum throughput (uncapped, VSync off)" : "normal gameplay (existing cap and VSync preserved)")}; previous cap: {savedFrameRate}; previous VSync: {savedSync}\n";
        try
        {
            OpenCounters();
            if (measureMaxThroughput) { Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0; }
            warmupUntil = Time.realtimeSinceStartupAsDouble + 2;
            IsRecording = true;
            var loop = PlayerLoop.GetCurrentPlayerLoop(); SetProbe(ref loop, true); PlayerLoop.SetPlayerLoop(loop);
            status = "記錄暖機中…";
        }
        catch (Exception error)
        {
            IsRecording = false; CleanupCapture();
            status = "無法開始記錄：" + error.Message;
            Debug.LogException(error, this);
        }
    }
    private void OpenCounters()
    {
        var handles = new List<ProfilerRecorderHandle>();
        // Timing must work even when a release platform strips profiler support.
        try { ProfilerRecorderHandle.GetAvailable(handles); }
        catch { return; }
        for (int i = 0; i < MarkerNames.Length; i++)
            foreach (var handle in handles)
                try
                {
                    var description = ProfilerRecorderHandle.GetDescription(handle);
                    if (description.Name != MarkerNames[i]) continue;
                    counters[i] = ProfilerRecorder.StartNew(description.Category, description.Name, 1);
                    break;
                }
                catch { /* Unavailable counters are recorded as -1. */ }
    }
    private long Read(int index) => counters[index].Valid ? counters[index].LastValue : -1;
    private static void SampleFrame() { if (Instance != null) Instance.Tick(); }
    private void Tick()
    {
        if (!IsRecording || lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        float wallSeconds = (float)((timestamp - lastTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency);
        lastTimestamp = timestamp;
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        int deltaGc0 = gc0 - lastGc0, deltaGc1 = gc1 - lastGc1, deltaGc2 = gc2 - lastGc2;
        lastGc0 = gc0; lastGc1 = gc1; lastGc2 = gc2;
        if (Time.realtimeSinceStartupAsDouble < warmupUntil || Time.unscaledDeltaTime <= 0f) return;
        frames.Add(new Frame {
            number = Time.frameCount, seconds = Time.unscaledDeltaTime,
            wallSeconds = wallSeconds, gc0 = deltaGc0, gc1 = deltaGc1, gc2 = deltaGc2,
            focused = Application.isFocused && !focusLostSinceSample,
            width = Screen.width, height = Screen.height, enemies = StationaryMeleeEnemy.ActiveEnemies.Count,
            movement = Read(0), path = Read(1), visibility = Read(2), enemy = Read(3), main = Read(4),
            render = Read(5), camera = Read(6), scripts = Read(7), physics = Read(8), allocation = Read(9),
            draws = Read(10), batches = Read(11), triangles = Read(12), gui = Read(13), hud = Read(14),
            input = Read(15), present = Read(16)
        });
        focusLostSinceSample = !Application.isFocused;
        capturedSeconds += Time.unscaledDeltaTime;
        if (capturedSeconds >= 30f) StopCapture();
    }
    private static void SetProbe(ref PlayerLoopSystem loop, bool install)
    {
        if (loop.subSystemList == null) return;
        var children = new List<PlayerLoopSystem>(loop.subSystemList);
        children.RemoveAll(child => child.type == typeof(MorldRuntimePerformanceRecorder));
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i]; SetProbe(ref child, install);
            if (install && child.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
            {
                var samples = new List<PlayerLoopSystem>(child.subSystemList ?? Array.Empty<PlayerLoopSystem>());
                samples.Add(new PlayerLoopSystem { type = typeof(MorldRuntimePerformanceRecorder), updateDelegate = SampleFrame });
                child.subSystemList = samples.ToArray();
            }
            children[i] = child;
        }
        loop.subSystemList = children.ToArray();
    }
    public void StopCapture()
    {
        if (!IsRecording) return;
        IsRecording = false;
        CleanupCapture();
        if (frames.Count == 0) { status = "暖機期間已結束，尚無遊玩樣本；F10 可重新記錄"; return; }
        try
        {
            var csv = new StringBuilder("frame,frame_ms,game_focused,width,height,enemies,movement_ns,pathfinding_ns,visibility_ns,enemy_update_ns,main_thread_ns,render_thread_ns,camera_render_ns,script_update_ns,physics_update_ns,gc_bytes,draw_calls,batches,triangles,gui_ns,input_hud_ns,input_trace_ns,present_wait_ns,wall_frame_ms,gc0_collections,gc1_collections,gc2_collections\n");
            var durations = new float[frames.Count]; int missed120 = 0, missed60 = 0, slow = 0, unfocused = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                var f = frames[i]; durations[i] = f.seconds;
                if (f.seconds > 1f / 120) missed120++;
                if (f.seconds > 1f / 60) missed60++;
                if (f.seconds > 1f / 30) slow++;
                if (!f.focused) unfocused++;
                csv.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1:F4},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22},{23:F4},{24},{25},{26}\n",
                    f.number, f.seconds * 1000, f.focused ? 1 : 0, f.width, f.height, f.enemies,
                    f.movement, f.path, f.visibility, f.enemy, f.main, f.render, f.camera, f.scripts, f.physics,
                    f.allocation, f.draws, f.batches, f.triangles, f.gui, f.hud, f.input, f.present,
                    f.wallSeconds * 1000, f.gc0, f.gc1, f.gc2);
            }
            Array.Sort(durations);
            float p95 = durations[Mathf.Clamp(Mathf.CeilToInt(durations.Length * 0.95f) - 1, 0, durations.Length - 1)];
            string summary = string.Format(CultureInfo.InvariantCulture,
                "Average FPS: {0:F2}\nLowest single-frame FPS: {1:F2}\nP95 frame time: {2:F3} ms\nBelow 120 FPS: {3}/{4} ({5:F2}%)\nBelow 60 FPS: {6}\nOver 33.3 ms: {7}\nUnfocused frames: {8}\nCaptured seconds: {9:F4}\n\n",
                frames.Count / capturedSeconds, 1f / durations[durations.Length - 1], p95 * 1000,
                missed120, frames.Count, 100.0 * missed120 / frames.Count, missed60, slow, unfocused, capturedSeconds);
            Directory.CreateDirectory(OutputDirectory);
            string basename = Path.Combine(OutputDirectory, "player-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            File.WriteAllText(basename + ".csv", csv.ToString(), new UTF8Encoding(false));
            File.WriteAllText(basename + ".txt", summary + settings +
                "\nUnavailable counters are -1 (often stripped in non-development builds). CPU scopes overlap; do not add them.\nFrame times/FPS remain available without profiler counters.\n", new UTF8Encoding(false));
            LastResultPath = basename + ".csv";
            status = $"已儲存：平均 {frames.Count / capturedSeconds:F1} FPS　F11 開啟資料夾";
            Debug.Log("[Morld Player Performance] " + LastResultPath + "\n" + summary);
        }
        catch (Exception error) { status = "儲存失敗：" + error.Message; Debug.LogException(error, this); }
    }
    private void CleanupCapture()
    {
        var loop = PlayerLoop.GetCurrentPlayerLoop(); SetProbe(ref loop, false); PlayerLoop.SetPlayerLoop(loop);
        for (int i = 0; i < counters.Length; i++) { counters[i].Dispose(); counters[i] = default; }
        Application.targetFrameRate = savedFrameRate; QualitySettings.vSyncCount = savedSync;
    }
    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (hudStyle == null) hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        float width = Mathf.Min(400, Screen.width - 24);
        GUI.Box(new Rect(Screen.width - width - 12, 12, width, 92), GUIContent.none);
        GUI.Label(new Rect(Screen.width - width, 20, width - 24, 28), hud, hudStyle);
        GUI.Label(new Rect(Screen.width - width, 48, width - 24, 52), status, hudStyle);
    }
    private void OnApplicationQuit() { StopCapture(); }
    private void OnApplicationFocus(bool focused) { if (!focused && IsRecording) focusLostSinceSample = true; }
    private void OnDisable() { StopCapture(); if (Instance == this) Instance = null; }
}
