using System;
using System.Collections.Generic;
using UnityEngine;
using Lichronicle.Gameplay.Inputs;

namespace Lichronicle.Gameplay.PhantomRecord
{
    public sealed class PhantomOperationRecorder : MonoBehaviour
    {
        [Header("按鍵設定")]
        [SerializeField] private KeyCode recordKey = KeyCode.G;
        [SerializeField] private KeyCode replayKey = KeyCode.H;

        [Header("錄製設定")]
        [SerializeField, Min(0.1f)] private float recordDuration = 10f;
        [SerializeField] private bool allowOverwriteRecord = true;

        [Header("複製體設定")]
        [SerializeField] private GameObject clonePrefab;
        [SerializeField, Min(1)] private int maxActiveClones = 1;
        [SerializeField] private bool destroyCloneWhenFinished = true;

        [Header("按鍵記錄範圍")]
        [SerializeField] private bool recordAllKeyboardKeyDowns = true;
        [SerializeField] private List<KeyCode> customRecordedKeys = new()
        {
            KeyCode.E,
            KeyCode.Q,
            KeyCode.R,
            KeyCode.F,
            KeyCode.LeftShift,
            KeyCode.RightShift
        };

        [Header("狀態，只讀觀察")]
        [SerializeField] private bool isRecording;
        [SerializeField] private bool hasRecord;
        [SerializeField] private float currentRecordTime;
        [SerializeField] private int recordedFrameCount;
        [SerializeField] private int activeCloneCount;

        private readonly List<RecordedPlayerFrame> _frames = new();
        private readonly List<KeyCode> _allKeyCodes = new();
        private readonly List<GameObject> _activeClones = new();

        private float _lastRecordTime;

        public bool IsRecording => isRecording;
        public bool HasRecord => hasRecord;
        public bool HasActiveClone => _activeClones.Count > 0;
        public float CurrentRecordTime => currentRecordTime;
        public float RecordDuration => recordDuration;
        public int RecordedFrameCount => recordedFrameCount;
        public int ActiveCloneCount => _activeClones.Count;

        private void Awake()
        {
            BuildKeyCodeCache();

            if (GetComponent<UnityPlayerInputSource>() == null)
                gameObject.AddComponent<UnityPlayerInputSource>();
        }

        private void Update()
        {
            CleanupMissingClones();
            activeCloneCount = _activeClones.Count;

            if (Input.GetKeyDown(recordKey))
                TryStartRecording();

            if (Input.GetKeyDown(replayKey))
                TrySpawnCloneAndReplay();

            if (isRecording)
            {
                currentRecordTime += Time.deltaTime;

                if (currentRecordTime >= recordDuration)
                    StopRecording();
            }
        }

        private void LateUpdate()
        {
            if (!isRecording)
                return;

            RecordFrame();
        }

        public void TryStartRecording()
        {
            if (isRecording)
                return;

            if (_activeClones.Count > 0)
            {
                Debug.Log("[PhantomOperationRecorder] 複製體存在時不能開始錄製。", this);
                return;
            }

            if (hasRecord && !allowOverwriteRecord)
            {
                Debug.Log("[PhantomOperationRecorder] 已經有紀錄，且不允許覆蓋。", this);
                return;
            }

            _frames.Clear();

            isRecording = true;
            hasRecord = false;
            currentRecordTime = 0f;
            recordedFrameCount = 0;
            _lastRecordTime = Time.time;

            Debug.Log("[PhantomOperationRecorder] 開始錄製。", this);
        }

        public void StopRecording()
        {
            if (!isRecording)
                return;

            isRecording = false;
            hasRecord = _frames.Count > 0;
            recordedFrameCount = _frames.Count;

            Debug.Log($"[PhantomOperationRecorder] 錄製完成，共 {_frames.Count} 幀。", this);
        }

        public void TrySpawnCloneAndReplay()
        {
            if (isRecording)
            {
                Debug.Log("[PhantomOperationRecorder] 錄製中不能生成複製體。", this);
                return;
            }

            if (!hasRecord || _frames.Count == 0)
            {
                Debug.Log("[PhantomOperationRecorder] 沒有可重播的紀錄。", this);
                return;
            }

            CleanupMissingClones();

            if (_activeClones.Count >= maxActiveClones)
            {
                Debug.Log("[PhantomOperationRecorder] 已達複製體數量上限。", this);
                return;
            }

            if (clonePrefab == null)
            {
                Debug.LogError("[PhantomOperationRecorder] Clone Prefab 尚未設定。", this);
                return;
            }

            var first = _frames[0];

            var clone = Instantiate(clonePrefab, first.worldPosition, first.worldRotation);
            clone.name = clonePrefab.name + "_PlaybackClone";

            PrepareClone(clone);

            var playbackInput = clone.GetComponent<PhantomPlaybackInputSource>();

            if (playbackInput == null)
                playbackInput = clone.AddComponent<PhantomPlaybackInputSource>();

            _activeClones.Add(clone);
            activeCloneCount = _activeClones.Count;

            playbackInput.BeginPlayback(CloneFrames(_frames), OnClonePlaybackFinished);
        }

        private void RecordFrame()
        {
            var motor = GetComponent<PlayerController>();
            var layout = motor != null ? motor.gamepadLayout : GamepadLayout.Auto;
            var now = Time.time;
            var delta = Mathf.Max(0.0001f, now - _lastRecordTime);
            _lastRecordTime = now;

            var frame = new RecordedPlayerFrame
            {
                deltaTime = delta,
                worldPosition = transform.position,
                worldRotation = transform.rotation,
                horizontal = Input.GetAxisRaw("Horizontal"),
                vertical = Input.GetAxisRaw("Vertical"),
                jumpDown = Input.GetButtonDown("Jump") || Input.GetKeyDown(GamepadMapping.JumpKey(layout)),
                keyDowns = ReadKeyDownsThisFrame()
            };

            _frames.Add(frame);
            recordedFrameCount = _frames.Count;
        }

        private List<KeyCode> ReadKeyDownsThisFrame()
        {
            var result = new List<KeyCode>();
            var source = recordAllKeyboardKeyDowns ? _allKeyCodes : customRecordedKeys;

            for (var i = 0; i < source.Count; i++)
            {
                var key = source[i];

                if (key == KeyCode.None)
                    continue;

                if (key == recordKey || key == replayKey)
                    continue;

                if (Input.GetKeyDown(key))
                    result.Add(key);
            }

            return result;
        }

        private void PrepareClone(GameObject clone)
        {
            var liveInputs = clone.GetComponentsInChildren<UnityPlayerInputSource>(true);

            for (var i = 0; i < liveInputs.Length; i++)
                liveInputs[i].enabled = false;

            var recorders = clone.GetComponentsInChildren<PhantomOperationRecorder>(true);

            for (var i = 0; i < recorders.Length; i++)
                recorders[i].enabled = false;
        }

        private List<RecordedPlayerFrame> CloneFrames(List<RecordedPlayerFrame> source)
        {
            var result = new List<RecordedPlayerFrame>(source.Count);

            for (var i = 0; i < source.Count; i++)
            {
                var frame = source[i];

                result.Add(new RecordedPlayerFrame
                {
                    deltaTime = frame.deltaTime,
                    worldPosition = frame.worldPosition,
                    worldRotation = frame.worldRotation,
                    horizontal = frame.horizontal,
                    vertical = frame.vertical,
                    jumpDown = frame.jumpDown,
                    keyDowns = frame.keyDowns == null
                        ? new List<KeyCode>()
                        : new List<KeyCode>(frame.keyDowns)
                });
            }

            return result;
        }

        private void OnClonePlaybackFinished(PhantomPlaybackInputSource playback)
        {
            if (playback == null)
                return;

            var clone = playback.gameObject;

            _activeClones.Remove(clone);
            activeCloneCount = _activeClones.Count;

            if (destroyCloneWhenFinished && clone != null)
                Destroy(clone);
        }

        private void CleanupMissingClones()
        {
            for (var i = _activeClones.Count - 1; i >= 0; i--)
            {
                if (_activeClones[i] == null)
                    _activeClones.RemoveAt(i);
            }
        }

        private void BuildKeyCodeCache()
        {
            _allKeyCodes.Clear();

            var values = Enum.GetValues(typeof(KeyCode));

            for (var i = 0; i < values.Length; i++)
            {
                var key = (KeyCode)values.GetValue(i);

                if (key == KeyCode.None)
                    continue;

                _allKeyCodes.Add(key);
            }
        }
    }
}
