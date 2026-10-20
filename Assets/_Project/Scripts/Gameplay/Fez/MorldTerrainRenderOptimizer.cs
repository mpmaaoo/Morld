using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>Shares identical render data at runtime. Authored meshes and physics remain independent.</summary>
[DefaultExecutionOrder(10000)]
public sealed class MorldTerrainRenderOptimizer : MonoBehaviour
{
    public static MorldTerrainRenderOptimizer Instance { get; private set; }
    [InspectorName("啟用地形網格共用與 GPU Instancing")]
    public bool enableOptimization = true;
    public int SharedRendererCount { get; private set; }
    public int SharedMeshGroupCount { get; private set; }
    private sealed class Group
    {
        public byte[] signature;
        public readonly List<MeshFilter> filters = new List<MeshFilter>();
    }
    private struct Binding
    {
        public MeshFilter filter;
        public Mesh originalMesh, sharedMesh;
        public MeshRenderer renderer;
        public Material[] originalMaterials, sharedMaterials;
    }
    private readonly List<Binding> bindings = new List<Binding>();
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();
    private int pendingFrames = 1;
    private bool lastEnabled;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        // FindObjectOfType excludes DontSave objects, including this runtime-only host.
        if (Instance == null)
            foreach (var existing in Resources.FindObjectsOfTypeAll<MorldTerrainRenderOptimizer>())
                if (existing.gameObject.scene.IsValid() && existing.isActiveAndEnabled) { Instance = existing; break; }
        if (Instance != null) return;
        var host = new GameObject("Morld Terrain Render Optimizer");
        host.hideFlags = HideFlags.DontSave;
        DontDestroyOnLoad(host);
        host.AddComponent<MorldTerrainRenderOptimizer>();
    }

    private void OnEnable()
    {
        if (Instance != null && Instance != this) { enabled = false; return; }
        Instance = this;
        SceneManager.sceneLoaded += SceneLoaded; pendingFrames = 1;
    }
    private void SceneLoaded(Scene scene, LoadSceneMode mode) { pendingFrames = 1; }
    private void LateUpdate()
    {
        if (pendingFrames > 0) { pendingFrames--; return; }
        if (pendingFrames == 0 || lastEnabled != enableOptimization)
        {
            ApplyNow(); pendingFrames = -1; lastEnabled = enableOptimization;
        }
    }
    private void OnDisable() { SceneManager.sceneLoaded -= SceneLoaded; Restore(); }
    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void ApplyNow()
    {
        Restore();
        if (!enableOptimization || !SystemInfo.supportsInstancing) return;
        var groups = new Dictionary<ulong, List<Group>>();
        var signatures = new Dictionary<Mesh, byte[]>();
        foreach (var filter in FindObjectsOfType<MeshFilter>())
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            var mesh = filter.sharedMesh;
            if (renderer == null || !renderer.enabled || mesh == null ||
                mesh.blendShapeCount > 0 || mesh.HasVertexAttribute(VertexAttribute.BlendWeight) ||
                mesh.HasVertexAttribute(VertexAttribute.BlendIndices) || !IsTerrain(filter)) continue;
            var sourceMaterials = renderer.sharedMaterials;
            bool opaque = sourceMaterials.Length > 0;
            foreach (var material in sourceMaterials)
                if (material == null || material.renderQueue > 2500) opaque = false;
            if (!opaque) continue;
            if (!signatures.TryGetValue(mesh, out var signature))
            {
                signature = Signature(mesh);
                signatures.Add(mesh, signature);
            }
            ulong hash = Hash(signature);
            if (!groups.TryGetValue(hash, out var candidates))
            { candidates = new List<Group>(); groups.Add(hash, candidates); }
            Group match = null;
            foreach (var candidate in candidates)
                if (Equal(candidate.signature, signature)) { match = candidate; break; }
            if (match == null) { match = new Group { signature = signature }; candidates.Add(match); }
            match.filters.Add(filter);
        }
        foreach (var candidates in groups.Values)
        foreach (var group in candidates)
        {
            if (group.filters.Count < 2) continue;
            var shared = Instantiate(group.filters[0].sharedMesh);
            shared.name = "Morld shared terrain mesh";
            shared.hideFlags = HideFlags.DontSave;
            meshes.Add(shared);
            SharedMeshGroupCount++;
            foreach (var filter in group.filters)
            {
                var renderer = filter.GetComponent<MeshRenderer>();
                var original = renderer.sharedMaterials;
                var instanced = new Material[original.Length];
                for (int i = 0; i < original.Length; i++)
                {
                    if (!materials.TryGetValue(original[i], out var material))
                    {
                        material = new Material(original[i]) { enableInstancing = true,
                            name = original[i].name + " (Morld runtime)", hideFlags = HideFlags.DontSave };
                        materials.Add(original[i], material);
                    }
                    instanced[i] = material;
                }
                bindings.Add(new Binding { filter = filter, originalMesh = filter.sharedMesh, sharedMesh = shared,
                    renderer = renderer, originalMaterials = original, sharedMaterials = instanced });
                filter.sharedMesh = shared;
                renderer.sharedMaterials = instanced;
                SharedRendererCount++;
            }
        }
        Debug.Log($"[Morld Rendering] {SharedRendererCount} terrain renderers share {SharedMeshGroupCount} meshes; colliders unchanged.", this);
    }

    private static bool IsTerrain(MeshFilter filter)
    {
        if (filter.GetComponentInParent<FezDepthProxyFollower>() != null ||
            filter.GetComponentInParent<PlayerController>() != null ||
            filter.GetComponentInParent<StationaryMeleeEnemy>() != null ||
            filter.GetComponentInParent<Animator>() != null) return false;
        return filter.GetComponentInParent<FezGridPlatform>() != null ||
            filter.GetComponentInParent<FezBackgroundWall>() != null ||
            filter.GetComponentInParent<FezRampBlock>() != null ||
            filter.GetComponentInParent<FezGridBoundaryWall>() != null ||
            filter.GetComponentInParent<FezGridBlock>() != null ||
            filter.GetComponentInParent<FezOneWayPlatform>() != null;
    }

    private static byte[] Signature(Mesh mesh)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(mesh.isReadable);
            // Imported models often keep their vertices GPU-only. Reusing the same asset
            // is already sufficient proof of identity; no Read/Write import change is needed.
            if (!mesh.isReadable) { writer.Write(mesh.GetInstanceID()); return stream.ToArray(); }
            using (var dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            {
            var data = dataArray[0];
            writer.Write(mesh.vertexCount); writer.Write((int)mesh.indexFormat);
            WriteBounds(writer, mesh.bounds);
            var attributes = mesh.GetVertexAttributes();
            writer.Write(attributes.Length);
            foreach (var attribute in attributes)
            { writer.Write((int)attribute.attribute); writer.Write((int)attribute.format); writer.Write(attribute.dimension); writer.Write(attribute.stream); }
            writer.Write(data.vertexBufferCount);
            for (int i = 0; i < data.vertexBufferCount; i++)
            { var bytes = data.GetVertexData<byte>(i).ToArray(); writer.Write(bytes.Length); writer.Write(bytes); }
            var indices = data.GetIndexData<byte>().ToArray(); writer.Write(indices.Length); writer.Write(indices);
            writer.Write(mesh.subMeshCount);
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var submesh = data.GetSubMesh(i);
                writer.Write((int)submesh.topology); writer.Write(submesh.baseVertex);
                writer.Write(submesh.indexStart); writer.Write(submesh.indexCount);
                writer.Write(submesh.firstVertex); writer.Write(submesh.vertexCount); WriteBounds(writer, submesh.bounds);
            }
            return stream.ToArray();
            }
        }
    }
    private static void WriteBounds(BinaryWriter writer, Bounds bounds)
    {
        writer.Write(bounds.center.x); writer.Write(bounds.center.y); writer.Write(bounds.center.z);
        writer.Write(bounds.extents.x); writer.Write(bounds.extents.y); writer.Write(bounds.extents.z);
    }
    private static ulong Hash(byte[] bytes)
    { unchecked { ulong hash = 14695981039346656037UL; foreach (byte value in bytes) hash = (hash ^ value) * 1099511628211UL; return hash; } }
    private static bool Equal(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

    public void Restore()
    {
        foreach (var binding in bindings)
        {
            if (binding.filter != null && binding.filter.sharedMesh == binding.sharedMesh)
                binding.filter.sharedMesh = binding.originalMesh;
            if (binding.renderer == null) continue;
            var current = binding.renderer.sharedMaterials;
            for (int i = 0; i < current.Length && i < binding.sharedMaterials.Length; i++)
                if (current[i] == binding.sharedMaterials[i]) current[i] = binding.originalMaterials[i];
            binding.renderer.sharedMaterials = current;
        }
        bindings.Clear();
        foreach (var mesh in meshes) if (mesh != null) Destroy(mesh);
        foreach (var material in materials.Values) if (material != null) Destroy(material);
        meshes.Clear(); materials.Clear();
        SharedRendererCount = SharedMeshGroupCount = 0;
    }
}
