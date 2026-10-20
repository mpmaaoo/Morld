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
    [Header("手把顯示")]
    [SerializeField] private bool useXboxButtonLabels = true;
    [SerializeField, Range(0.05f, 0.5f)] private float gamepadAxisDeadzone = 0.2f;

    private const int MaxHistory = 120;
    private readonly Queue<InputTraceEvent> _history = new Queue<InputTraceEvent>(MaxHistory);
    private readonly HashSet<KeyCode> _knownKeys = new HashSet<KeyCode>();
    private readonly List<KeyCode> _keyboardKeys = new List<KeyCode>(160);
    private readonly List<string> _heldKeys = new List<string>(12);
    private readonly List<string> _heldPadButtons = new List<string>(8);
    private readonly StringBuilder _padAxes = new StringBuilder(160);
    private readonly int[] _padAxisStates = new int[10];
    private static readonly string[] PadButtonNames =
        { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "LS", "RS" };
    private string _gamepadNames = "未連接";
    private float _nextGamepadScan;
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
        {
            string source = Input.GetKeyDown(KeyCode.Space) ? "SPACE" :
                Input.GetKeyDown(KeyCode.JoystickButton0) ? PadButtonLabel(0) :
                Input.GetKeyDown(KeyCode.JoystickButton1) ? PadButtonLabel(1) : "Jump";
            AddEvent(vertical <= -0.5f ? "下方向 + " + source + "：嘗試下落" : source + "：跳躍");
        }

        _heldKeys.Clear();
        for (int i = 0; i < _keyboardKeys.Count; i++)
        {
            KeyCode key = _keyboardKeys[i];
            if (key != markBugKey && key != toggleOverlayKey && key != recoverKey && Input.GetKey(key))
                _heldKeys.Add(KeyLabel(key));
            if (Input.GetKeyDown(key) && key != markBugKey && key != toggleOverlayKey && key != recoverKey)
                AddEvent("按鍵 " + KeyLabel(key));
        }
        RecordGamepad();
        // Include this frame's keyboard and gamepad state in the bug snapshot.
        if (!hasModifier && Input.GetKeyDown(markBugKey)) MarkBug();
    }

    private string PadButtonLabel(int button)
    {
        return useXboxButtonLabels && button < PadButtonNames.Length
            ? PadButtonNames[button] + " [B" + button + "]" : "B" + button;
    }

    private void RecordGamepad()
    {
        if (Time.unscaledTime >= _nextGamepadScan)
        {
            _nextGamepadScan = Time.unscaledTime + 0.5f;
            var connected = new List<string>();
            foreach (string pad in Input.GetJoystickNames())
                if (!string.IsNullOrEmpty(pad)) connected.Add(pad);
            string names = connected.Count == 0 ? "未連接" : string.Join(" / ", connected);
            if (names != _gamepadNames)
            {
                _gamepadNames = names;
                AddEvent("手把：" + names);
            }
        }

        _heldPadButtons.Clear();
        for (int button = 0; button < 20; button++)
        {
            var key = (KeyCode)((int)KeyCode.JoystickButton0 + button);
            string label = PadButtonLabel(button);
            if (Input.GetKey(key)) _heldPadButtons.Add(label);
            if (Input.GetKeyDown(key)) AddEvent("手把按下 " + label);
            if (Input.GetKeyUp(key)) AddEvent("手把放開 " + label);
        }

        _padAxes.Clear();
        for (int axis = 0; axis < _padAxisStates.Length; axis++)
        {
            float raw = Input.GetAxisRaw("MorldDebugPadAxis" + (axis + 1));
            float value = Mathf.Abs(raw) < gamepadAxisDeadzone ? 0f : raw;
            int state = value == 0f ? 0 : Mathf.RoundToInt(value * 4f);
            if (state != _padAxisStates[axis])
            {
                _padAxisStates[axis] = state;
                AddEvent(value == 0f ? "手把軸" + (axis + 1) + " 回中" :
                    "手把軸" + (axis + 1) + " " + value.ToString("+0.00;-0.00;0.00"));
            }
            if (value == 0f) continue;
            if (_padAxes.Length > 0) _padAxes.Append("  ");
            _padAxes.Append("軸").Append(axis + 1).Append(' ')
                .Append(value.ToString("+0.00;-0.00;0.00"));
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
        report.AppendLine("Gamepad: " + _gamepadNames);
        report.AppendLine("Held keyboard: " + string.Join(" + ", _heldKeys));
        report.AppendLine("Held gamepad: " + string.Join(" + ", _heldPadButtons));
        report.AppendLine("Gamepad axes: " + (_padAxes.Length == 0 ? "neutral" : _padAxes.ToString()));
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
        var wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
        string keyboardText = "鍵盤：" + (_heldKeys.Count == 0 ? "（無）" : string.Join(" + ", _heldKeys));
        string padText = "手把：" + _gamepadNames;
        string buttonsText = "按住：" + (_heldPadButtons.Count == 0 ? "（無）" : string.Join(" + ", _heldPadButtons));
        string axesText = "搖桿／方向／扳機：" + (_padAxes.Length == 0 ? "（回中）" : _padAxes.ToString());
        float height = 76f + visible * 19f + wrap.CalcHeight(new GUIContent(keyboardText), 416f) +
            wrap.CalcHeight(new GUIContent(padText), 416f) +
            wrap.CalcHeight(new GUIContent(buttonsText), 416f) +
            wrap.CalcHeight(new GUIContent(axesText), 416f) +
            (Time.unscaledTime < _statusUntil ? 24f : 0f);
        GUILayout.BeginArea(new Rect(12f, 12f, 440f, height), GUI.skin.box);
        GUILayout.BeginVertical();
        GUILayout.Label("MORLD  |  INPUT TRACE");
        GUILayout.Label(keyboardText, wrap);
        GUILayout.Label(padText, wrap);
        GUILayout.Label(buttonsText, wrap);
        GUILayout.Label(axesText, wrap);
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
