using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Reversible prototype graphics budget; leaves geometry and gameplay unchanged.</summary>
public sealed class MorldPrototypePerformance : MonoBehaviour
{
    public static MorldPrototypePerformance Instance { get; private set; }
    public bool enableProfile = true;
    private bool applied;
    private int savedSync, savedFrameRate, savedCascades;
    private float savedDistance;
    private ShadowResolution savedResolution;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        // This prototype preset belongs to Morld, not unrelated scenes in the project.
        if (SceneManager.GetActiveScene().name != "SampleScene" || Instance != null) return;
        var host = new GameObject("Morld Prototype Performance") { hideFlags = HideFlags.DontSave };
        DontDestroyOnLoad(host);
        host.AddComponent<MorldPrototypePerformance>();
    }

    private void OnEnable()
    {
        if (Instance != null && Instance != this) { enabled = false; return; }
        Instance = this;
        Apply();
    }
    private void Update() { if (enableProfile != applied) { if (enableProfile) Apply(); else Restore(); } }
    public void SetProfile(bool value) { enableProfile = value; if (value) Apply(); else Restore(); }
    private void Apply()
    {
        if (applied || !enableProfile) return;
        savedSync = QualitySettings.vSyncCount;
        savedFrameRate = Application.targetFrameRate;
        savedCascades = QualitySettings.shadowCascades;
        savedDistance = QualitySettings.shadowDistance;
        savedResolution = QualitySettings.shadowResolution;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 120;
        QualitySettings.shadowDistance = Mathf.Min(savedDistance, 35f);
        QualitySettings.shadowResolution = (ShadowResolution)Mathf.Min((int)savedResolution, (int)ShadowResolution.Medium);
        if (savedCascades == 4) QualitySettings.shadowCascades = 2;
        applied = true;
    }
    private void Restore()
    {
        if (!applied) return;
        QualitySettings.vSyncCount = savedSync;
        Application.targetFrameRate = savedFrameRate;
        QualitySettings.shadowCascades = savedCascades;
        QualitySettings.shadowDistance = savedDistance;
        QualitySettings.shadowResolution = savedResolution;
        applied = false;
    }
    private void OnDisable() { Restore(); if (Instance == this) Instance = null; }
}
