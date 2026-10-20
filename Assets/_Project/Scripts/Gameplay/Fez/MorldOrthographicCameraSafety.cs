using Cinemachine;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>After Cinemachine blends, retreat along the optical axis to protect the orthographic near plane.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class MorldOrthographicCameraSafety : MonoBehaviour
{
    [Tooltip("近裁切面與地圖碰撞器的安全距離；不改變正交大小。")]
    [SerializeField, Min(10f)] private float safetyDistance = 10f;
    [SerializeField, Min(0f)] private float viewportPadding = 0.03f;
    [Tooltip("障礙離開畫面後，每秒恢復原鏡頭距離的速度。避障往外退立即生效。")]
    [SerializeField, Min(0.01f)] private float returnSpeed = 12f;
    [SerializeField] private LayerMask terrainLayers = Physics.DefaultRaycastLayers;
    private Camera output;
    private Transform ignoredPlayerRoot;
    private Collider[] overlaps = new Collider[128];
    private float retreat;
    private bool hasSafePose;
    private Vector3 previousSafePosition;
    private bool applied;
    private Vector3 basePosition, appliedPosition;
    private float baseFar, appliedFar;
    public float RetreatDistance => retreat;
    private static readonly Unity.Profiling.ProfilerMarker SafetyProfile = new Unity.Profiling.ProfilerMarker("Morld.CameraSafety");

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (SceneManager.GetActiveScene().name != "SampleScene") return;
        var camera = Camera.main;
        if (camera != null && camera.GetComponent<MorldOrthographicCameraSafety>() == null)
            camera.gameObject.AddComponent<MorldOrthographicCameraSafety>();
    }
    private void OnEnable()
    {
        output = GetComponent<Camera>();
        CinemachineCore.CameraUpdatedEvent.RemoveListener(AfterCameraUpdated);
        CinemachineCore.CameraUpdatedEvent.AddListener(AfterCameraUpdated);
    }
    private void AfterCameraUpdated(CinemachineBrain brain)
    {
        if (!isActiveAndEnabled || output == null || !output.orthographic || brain.OutputCamera != output) return;
        var target = brain.ActiveVirtualCamera != null ? brain.ActiveVirtualCamera.Follow : null;
        if (target != null)
        {
            var motor = target.GetComponentInParent<PlayerController>();
            ignoredPlayerRoot = motor != null ? motor.transform : target;
        }
        else ignoredPlayerRoot = PlayerSpawner.ActivePlayer != null ? PlayerSpawner.ActivePlayer.transform : null;
        basePosition = output.transform.position;
        baseFar = output.farClipPlane;
        float focusDistance = ignoredPlayerRoot != null
            ? Mathf.Max(output.nearClipPlane, Vector3.Dot(ignoredPlayerRoot.position - basePosition, output.transform.forward))
            : 10f;
        Physics.SyncTransforms();
        appliedPosition = ResolvePosition(basePosition, output.transform.rotation, focusDistance, Time.deltaTime);
        output.transform.position = appliedPosition;
        // Retreat must not clip geometry that was inside the original far plane.
        appliedFar = baseFar + retreat;
        output.farClipPlane = appliedFar;
        applied = true;
    }

    /// <summary>Depth-only correction. Call with a fresh uncorrected camera pose, never the previous output.</summary>
    public Vector3 ResolvePosition(Vector3 desired, Quaternion rotation, float focusDistance, float deltaTime)
    {
        using (SafetyProfile.Auto())
        {
            if (output == null) output = GetComponent<Camera>();
            if (!output.orthographic) { retreat = 0f; hasSafePose = false; return desired; }
            Vector3 forward = rotation * Vector3.forward;
            float margin = Mathf.Max(10f, safetyDistance);
            // Keep the previous safe depth in the query. A fast depth move must not
            // forget a thin wall just because the new desired pose already passed it.
            float start = output.nearClipPlane - retreat;
            if (hasSafePose)
                start = Mathf.Min(start, Vector3.Dot(previousSafePosition - desired, forward) + output.nearClipPlane);
            start -= margin;
            float end = Mathf.Max(output.nearClipPlane, focusDistance);
            Vector3 center = desired + forward * ((start + end) * 0.5f);
            Vector3 half = new Vector3(output.orthographicSize * output.aspect + viewportPadding,
                output.orthographicSize + viewportPadding, Mathf.Max(0.005f, (end - start) * 0.5f));
            int count;
            while (true)
            {
                count = Physics.OverlapBoxNonAlloc(center, half, overlaps, rotation,
                    terrainLayers, QueryTriggerInteraction.Ignore);
                if (count < overlaps.Length) break;
                System.Array.Resize(ref overlaps, overlaps.Length * 2);
            }
            float desiredNearDepth = Vector3.Dot(desired, forward) + output.nearClipPlane;
            float required = 0f;
            for (int i = 0; i < count; i++)
            {
                var body = overlaps[i];
                if (!IsTerrain(body)) continue;
                Bounds bounds = body.bounds;
                float front = Vector3.Dot(bounds.center, forward) -
                    Mathf.Abs(forward.x) * bounds.extents.x - Mathf.Abs(forward.y) * bounds.extents.y -
                    Mathf.Abs(forward.z) * bounds.extents.z;
                required = Mathf.Max(required, desiredNearDepth - front + margin);
            }
            retreat = required >= retreat ? required
                : Mathf.MoveTowards(retreat, required, Mathf.Max(0.01f, returnSpeed) * Mathf.Max(0f, deltaTime));
            previousSafePosition = desired - forward * retreat;
            hasSafePose = true;
            return previousSafePosition;
        }
    }
    private bool IsTerrain(Collider body)
    {
        if (body == null || body.isTrigger || body is CharacterController) return false;
        var owner = body.transform;
        if (ignoredPlayerRoot != null && (owner == ignoredPlayerRoot || owner.IsChildOf(ignoredPlayerRoot))) return false;
        return body.GetComponentInParent<PlayerController>() == null &&
            body.GetComponentInParent<StationaryMeleeEnemy>() == null &&
            body.GetComponentInParent<FezDepthProxyFollower>() == null;
    }
    private void OnDisable()
    {
        CinemachineCore.CameraUpdatedEvent.RemoveListener(AfterCameraUpdated);
        if (applied && output != null)
        {
            if ((output.transform.position - appliedPosition).sqrMagnitude < 0.000001f)
                output.transform.position = basePosition;
            if (Mathf.Approximately(output.farClipPlane, appliedFar)) output.farClipPlane = baseFar;
        }
        retreat = 0f; applied = false; hasSafePose = false;
    }
}
