using UnityEngine;
using UnityEngine.Rendering;

/// <summary>A static, one-metre tiled floor for spatial reference during free locomotion.</summary>
[DisallowMultipleComponent]
public sealed class QuestProFloor : MonoBehaviour
{
    private Mesh floorMesh;
    private Material floorMaterial;

    private void Awake()
    {
        const int tiles = 20;
        var vertices = new Vector3[tiles * tiles * 4];
        var normals = new Vector3[vertices.Length];
        var colors = new Color[vertices.Length];
        var triangles = new int[tiles * tiles * 6];
        Color light = new Color(0.32f, 0.36f, 0.40f);
        Color dark = new Color(0.28f, 0.32f, 0.36f);
        if (QualitySettings.activeColorSpace == ColorSpace.Linear)
        {
            light = light.linear;
            dark = dark.linear;
        }

        for (int z = 0; z < tiles; z++)
        for (int x = 0; x < tiles; x++)
        {
            int tile = z * tiles + x;
            int v = tile * 4;
            float left = x - tiles * 0.5f;
            float back = z - tiles * 0.5f;
            vertices[v] = new Vector3(left, 0f, back);
            vertices[v + 1] = new Vector3(left, 0f, back + 1f);
            vertices[v + 2] = new Vector3(left + 1f, 0f, back + 1f);
            vertices[v + 3] = new Vector3(left + 1f, 0f, back);
            for (int corner = 0; corner < 4; corner++)
            {
                normals[v + corner] = Vector3.up;
                colors[v + corner] = (x + z) % 2 == 0 ? light : dark;
            }
            int t = tile * 6;
            triangles[t] = v;
            triangles[t + 1] = v + 1;
            triangles[t + 2] = v + 2;
            triangles[t + 3] = v;
            triangles[t + 4] = v + 2;
            triangles[t + 5] = v + 3;
        }

        floorMesh = new Mesh
        {
            name = "20m tiled VR floor",
            vertices = vertices,
            normals = normals,
            colors = colors,
            triangles = triangles
        };
        floorMesh.RecalculateBounds();
        floorMesh.UploadMeshData(true);
        floorMaterial = new Material(Shader.Find("QuestPro/VoxelVertexColor"))
        {
            name = "VR floor vertex colors",
            enableInstancing = true
        };
        var floor = new GameObject("VR Ground (1m tiles)", typeof(MeshFilter), typeof(MeshRenderer));
        // The experience object has an offset in the scene. Keep the floor at world y = 0.
        floor.transform.SetParent(transform, true);
        floor.GetComponent<MeshFilter>().sharedMesh = floorMesh;
        var renderer = floor.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = floorMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private void OnDestroy()
    {
        if (floorMesh != null) Destroy(floorMesh);
        if (floorMaterial != null) Destroy(floorMaterial);
    }
}
