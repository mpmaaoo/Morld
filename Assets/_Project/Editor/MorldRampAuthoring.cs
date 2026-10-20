using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;

public static class MorldRampAuthoring
{
    [MenuItem("GameObject/Morld/Ramp Block (ProBuilder)", false, 15)]
    private static void CreateRamp()
    {
        var root = CreateRampObject();
        Vector3 position = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
        if (Selection.activeGameObject != null)
        {
            var renderer = Selection.activeGameObject.GetComponentInChildren<Renderer>();
            if (renderer != null) position = new Vector3(renderer.bounds.max.x, renderer.bounds.min.y, renderer.bounds.min.z);
        }
        root.transform.position = new Vector3(Mathf.Round(position.x), Mathf.Round(position.y), Mathf.Round(position.z));
        var map = GameObject.Find("FezMapRoot");
        if (map != null) root.transform.SetParent(map.transform, true);
        Undo.RegisterCreatedObjectUndo(root, "Create ProBuilder ramp block");
        Selection.activeGameObject = root;
    }

    public static GameObject CreateRampObject()
    {
        // Closed 1x1x1 wedge, rising along local +X; origin is the grid cell minimum.
        Vector3[] vertices = {
            new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0),
            new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1)
        };
        Face[] faces = {
            new Face(new[] {0,2,1}), new Face(new[] {3,4,5}),
            new Face(new[] {0,1,3, 1,4,3}),
            new Face(new[] {1,2,4, 2,5,4}),
            new Face(new[] {0,3,2, 2,3,5})
        };
        var mesh = ProBuilderMesh.Create(vertices, faces);
        mesh.ToMesh(); mesh.Refresh();
        var root = mesh.gameObject;
        root.name = "RampBlock1x1";
        root.layer = LayerMask.NameToLayer("Ground") >= 0 ? LayerMask.NameToLayer("Ground") : 9;
        var collider = root.GetComponent<MeshCollider>();
        if (collider == null) collider = root.AddComponent<MeshCollider>();
        collider.sharedMesh = root.GetComponent<MeshFilter>().sharedMesh;
        collider.convex = false;
        root.AddComponent<FezGridPlatform>();
        root.AddComponent<FezRampBlock>();
        root.GetComponent<FezOneWayPlatform>().ConfigureStandaloneBody(collider);
        var reference = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Building blocks/TestBlocks/Block1x1.prefab");
        if (reference != null)
        {
            var renderer = reference.GetComponentInChildren<Renderer>();
            if (renderer != null) root.GetComponent<MeshRenderer>().sharedMaterial = renderer.sharedMaterial;
        }
        return root;
    }

    [MenuItem("GameObject/Morld/Ramp Block (ProBuilder)", true)]
    private static bool CanCreateRamp() => !Application.isPlaying;
}
