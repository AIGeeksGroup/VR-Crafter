using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Small mutable draw meshes; individual voxel colliders and transforms stay independent.</summary>
[DefaultExecutionOrder(200)]
public sealed class QuestVoxelBatchRenderer : MonoBehaviour
{
    public const int VoxelsPerChunk = 128;
    private sealed class Chunk
    {
        public readonly List<Transform> voxels = new(VoxelsPerChunk);
        public readonly List<GeneratedVoxel> data = new(VoxelsPerChunk);
        public readonly List<Color> colors = new(VoxelsPerChunk);
        public readonly List<Vector3> vertices = new(VoxelsPerChunk * 24);
        public readonly List<Vector3> normals = new(VoxelsPerChunk * 24);
        public readonly List<Color> vertexColors = new(VoxelsPerChunk * 24);
        public readonly List<int> triangles = new(VoxelsPerChunk * 36);
        public Mesh mesh;
        public bool dirty;
        public bool colorsDirty;
    }
    private readonly List<Chunk> chunks = new();
    private static Vector3[] cubeVertices;
    private static Vector3[] cubeNormals;
    private static int[] cubeTriangles;
    private Material material;
    public int ChunkCount => chunks.Count;
    public int RebuildCount { get; private set; }
    public int ColorUpdateCount { get; private set; }

    public void Initialize()
    {
        if (cubeVertices == null)
        {
            var prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
            prototype.SetActive(false);
            var cube = prototype.GetComponent<MeshFilter>().sharedMesh;
            cubeVertices = cube.vertices;
            cubeNormals = cube.normals;
            cubeTriangles = cube.triangles;
            Dispose(prototype);
        }
        var shader = Shader.Find("QuestPro/VoxelVertexColor");
        if (shader == null) throw new System.InvalidOperationException("QuestPro voxel shader missing.");
        material = new Material(shader) { name = "Shared voxel vertex colors", enableInstancing = true };
    }

    public void Add(GeneratedVoxel voxel, Color color)
    {
        if (chunks.Count == 0 || chunks[chunks.Count - 1].voxels.Count == VoxelsPerChunk)
        {
            var obj = new GameObject("Voxel draw batch " + chunks.Count, typeof(MeshFilter), typeof(MeshRenderer));
            obj.transform.SetParent(transform, false);
            var chunk = new Chunk { mesh = new Mesh { name = obj.name } };
            chunk.mesh.MarkDynamic();
            obj.GetComponent<MeshFilter>().sharedMesh = chunk.mesh;
            var renderer = obj.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            chunks.Add(chunk);
        }
        int index = chunks.Count - 1;
        chunks[index].voxels.Add(voxel.transform);
        chunks[index].data.Add(voxel);
        // Mesh vertex colors are raw values; unlike material Color properties,
        // Unity does not convert HTML/sRGB colors for a linear render pipeline.
        chunks[index].colors.Add(QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color);
        chunks[index].dirty = true;
        voxel.SetRenderBatch(this, index);
    }

    public void MarkDirty(int index)
    {
        if (index >= 0 && index < chunks.Count) chunks[index].dirty = true;
    }

    private void LateUpdate() => FlushDirty();

    public void MarkColorsDirty(int index)
    {
        if (index >= 0 && index < chunks.Count) chunks[index].colorsDirty = true;
    }

    private static Color DisplayColor(Chunk chunk, int index)
    {
        if (chunk.data[index] == null || !chunk.data[index].IsRayHighlighted) return chunk.colors[index];
        return QualitySettings.activeColorSpace == ColorSpace.Linear ? QuestVoxelDraggable.HoverColor.linear : QuestVoxelDraggable.HoverColor;
    }

    // Called after each generation slice as well, so edit-mode validation uses the
    // same meshes without depending on Unity invoking LateUpdate.
    public void FlushDirty()
    {
        foreach (var chunk in chunks)
        {
            if (!chunk.dirty)
            {
                if (!chunk.colorsDirty) continue;
                int offset = 0;
                for (int i = 0; i < chunk.voxels.Count; i++)
                {
                    var voxel = chunk.voxels[i];
                    if (voxel == null || !voxel.gameObject.activeInHierarchy) continue;
                    Color color = DisplayColor(chunk, i);
                    for (int v = 0; v < cubeVertices.Length; v++) chunk.vertexColors[offset++] = color;
                }
                chunk.mesh.SetColors(chunk.vertexColors);
                chunk.colorsDirty = false;
                ColorUpdateCount++;
                continue;
            }
            chunk.dirty = false;
            chunk.colorsDirty = false;
            chunk.vertices.Clear();
            chunk.normals.Clear();
            chunk.vertexColors.Clear();
            chunk.triangles.Clear();
            for (int i = 0; i < chunk.voxels.Count; i++)
            {
                var voxel = chunk.voxels[i];
                if (voxel == null || !voxel.gameObject.activeInHierarchy) continue;
                Matrix4x4 matrix = Matrix4x4.TRS(voxel.localPosition, voxel.localRotation, voxel.localScale);
                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                int offset = chunk.vertices.Count;
                Color color = DisplayColor(chunk, i);
                for (int v = 0; v < cubeVertices.Length; v++)
                {
                    chunk.vertices.Add(matrix.MultiplyPoint3x4(cubeVertices[v]));
                    chunk.normals.Add(normalMatrix.MultiplyVector(cubeNormals[v]).normalized);
                    chunk.vertexColors.Add(color);
                }
                foreach (int triangle in cubeTriangles) chunk.triangles.Add(offset + triangle);
            }
            chunk.mesh.Clear();
            chunk.mesh.SetVertices(chunk.vertices);
            chunk.mesh.SetNormals(chunk.normals);
            chunk.mesh.SetColors(chunk.vertexColors);
            chunk.mesh.SetTriangles(chunk.triangles, 0, true);
            RebuildCount++;
        }
    }

    private void OnDestroy()
    {
        foreach (var chunk in chunks) Dispose(chunk.mesh);
        Dispose(material);
    }

    private static void Dispose(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
    }
}
