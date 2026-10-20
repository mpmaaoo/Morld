using System.Collections.Generic;
using System.IO;
using System.Text;
using Lichronicle.Gameplay.PhantomRecord;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Local play-test input HUD, bug marker, and safe-platform recovery shortcuts.</summary>
[DisallowMultipleComponent]
public sealed class PlayerDebugTools : MonoBehaviour
{
    [Header("測試快捷鍵")]
    [SerializeField] private KeyCode markBugKey = KeyCode.F6;
    [SerializeField] private KeyCode toggleOverlayKey = KeyCode.F7;
    [SerializeField] private KeyCode recoverKey = KeyCode.F8;
    [SerializeField, Range(4, 14)] private int visibleActionCount = 9;
    [SerializeField] private bool showOverlay = true;

    private const int MaxHistory = 120;
    private readonly Queue<InputTraceEvent> _history = new Queue<InputTraceEvent>(MaxHistory);
    private readonly HashSet<KeyCode> _knownKeys = new HashSet<KeyCode>();
    private readonly List<KeyCode> _keyboardKeys = new List<KeyCode>(160);
    private readonly List<string> _heldKeys = new List<string>(12);
    private PlayerController _player;
    private string _horizontalState = "";
    private string _verticalState = "";
    private string _status = "";
    private float _statusUntil;

    private struct InputTraceEvent
    {
        public float elapsed;
        public int frame;
        public string action;
        public Vector3 position;
        public bool grounded;
        public string support;
    }

    private string LogPath => Path.Combine(Application.persistentDataPath, "MorldInputTrace.txt");

    private void Awake()
    {
        _player = GetComponent<PlayerController>();
        foreach (KeyCode key in System.Enum.GetValues(typeof(KeyCode)))
        {
            if ((int)key < (int)KeyCode.Backspace || (int)key > (int)KeyCode.Menu ||
                !_knownKeys.Add(key)) continue;
            _keyboardKeys.Add(key);
        }
    }

    /// <summary>Called by PlayerController so the trace follows the same input source as movement.</summary>
    public void RecordInput(float horizontal, float vertical, bool jumpPressed)
    {
        if (GetComponent<PhantomPlaybackInputSource>() != null)
        {
            enabled = false;
            return;
        }

        bool hasModifier = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ||
            Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        if (!hasModifier && Input.GetKeyDown(toggleOverlayKey))
            showOverlay = !showOverlay;
        if (!hasModifier && Input.GetKeyDown(markBugKey))
            MarkBug();
        if (!hasModifier && Input.GetKeyDown(recoverKey))
        {
            string message = "找不到玩家控制器";
            if (_player != null)
            {
                bool returned = _player.TryReturnToLastSafePlatform(out message);
                AddEvent(returned
                    ? "返回平台：" + (_player.StandingPlatform != null ? _player.StandingPlatform.name : "未知")
                    : "返回失敗：" + message);
            }
            else AddEvent("返回失敗：" + message);
            ShowStatus(message);
        }

        RecordAxisChange(ref _horizontalState, horizontal, "左移", "右移");
        RecordAxisChange(ref _verticalState, vertical, "下壓／下落", "前進／深度移動");
        if (jumpPressed)
            AddEvent(vertical <= -0.5f ? "S／下方向 + 空白：嘗試下落" : "空白：跳躍");

        _heldKeys.Clear();
        for (int i = 0; i < _keyboardKeys.Count; i++)
        {
            KeyCode key = _keyboardKeys[i];
            if (key != markBugKey && key != toggleOverlayKey && key != recoverKey && Input.GetKey(key))
                _heldKeys.Add(KeyLabel(key));
            if (Input.GetKeyDown(key) && key != markBugKey && key != toggleOverlayKey && key != recoverKey)
                AddEvent("按鍵 " + KeyLabel(key));
        }
    }

    private void RecordAxisChange(ref string previous, float value, string negative, string positive)
    {
        string current = value < -0.25f ? negative : value > 0.25f ? positive : "";
        if (current == previous) return;
        if (!string.IsNullOrEmpty(previous)) AddEvent("停止" + previous);
        if (!string.IsNullOrEmpty(current)) AddEvent("開始" + current);
        previous = current;
    }

    private void AddEvent(string action)
    {
        if (_history.Count >= MaxHistory) _history.Dequeue();
        _history.Enqueue(new InputTraceEvent
        {
            elapsed = Time.unscaledTime,
            frame = Time.frameCount,
            action = action,
            position = transform.position,
            grounded = _player != null && _player.IsGrounded,
            support = _player != null && _player.StandingPlatform != null
                ? _player.StandingPlatform.name : "無"
        });
    }

    private void MarkBug()
    {
        AddEvent("BUG 標記");
        var report = new StringBuilder(4096);
        report.AppendLine();
        report.AppendLine($"===== BUG MARK {System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====");
        report.AppendLine($"Scene: {SceneManager.GetActiveScene().name}");
        report.AppendLine($"Time: {Time.unscaledTime:F2}s  Frame: {Time.frameCount}");
        report.AppendLine($"Position: {transform.position:F3}  Grounded: {_player != null && _player.IsGrounded}");
        report.AppendLine($"Support: {(_player != null && _player.StandingPlatform != null ? _player.StandingPlatform.name : "無")}");
        report.AppendLine("Recent input/actions:");
        foreach (InputTraceEvent entry in _history)
            report.AppendLine($"  {entry.elapsed:F2}s f{entry.frame}: {entry.action} | pos={entry.position:F2} grounded={entry.grounded} support={entry.support}");
        try
        {
            File.AppendAllText(LogPath, report.ToString(), new UTF8Encoding(false));
            Debug.Log($"[Morld Debug] Bug 操作記錄已寫入：{LogPath}", this);
            ShowStatus("已標記 Bug；近期操作已存檔");
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"[Morld Debug] 無法儲存操作記錄：{exception.Message}", this);
            ShowStatus("標記完成，但記錄檔無法寫入");
        }
    }

    private void ShowStatus(string message)
    {
        _status = message;
        _statusUntil = Time.unscaledTime + 3f;
    }

    private static string KeyLabel(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.LeftArrow: return "←";
            case KeyCode.RightArrow: return "→";
            case KeyCode.UpArrow: return "↑";
            case KeyCode.DownArrow: return "↓";
            case KeyCode.Space: return "SPACE";
            case KeyCode.LeftShift: return "L-SHIFT";
            case KeyCode.RightShift: return "R-SHIFT";
            default: return key.ToString().ToUpperInvariant();
        }
    }

    private void OnGUI()
    {
        if (!showOverlay || !Application.isPlaying || _player == null ||
            GetComponent<PhantomPlaybackInputSource>() != null) return;

        int visible = Mathf.Clamp(visibleActionCount, 4, 14);
        float height = 108f + visible * 19f +
            (Time.unscaledTime < _statusUntil ? 24f : 0f);
        GUILayout.BeginArea(new Rect(12f, 12f, 355f, height), GUI.skin.box);
        GUILayout.BeginVertical();
        GUILayout.Label("MORLD  |  INPUT TRACE");
        GUILayout.Label("按住：" + (_heldKeys.Count == 0 ? "（無）" : string.Join(" + ", _heldKeys.ToArray())));
        GUILayout.Label($"F6 標記 Bug  ·  F7 顯示/隱藏  ·  F8 返回上一個平台");

        InputTraceEvent[] entries = _history.ToArray();
        int first = Mathf.Max(0, entries.Length - visible);
        for (int i = first; i < entries.Length; i++)
            GUILayout.Label($"{entries[i].elapsed,6:F2}s  {entries[i].action}");

        if (Time.unscaledTime < _statusUntil)
            GUILayout.Label(_status);
        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
