using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;

/// <summary>
/// Unity test client for a local Python generation server.
///
/// Workflow:
/// 1. Upload a Texture2D image to the Python server.
/// 2. Receive a GLB model URL, voxel data, or both.
/// 3. Display the result in the Unity scene.
///
/// Voxel mode:
/// - The server returns voxel coordinates in a 64x64x64 grid.
/// - Unity creates one cube or prefab per voxel.
/// - Change Voxel Size during Play Mode to resize and reposition existing voxels in real time.
/// </summary>
[DisallowMultipleComponent]
public class ModelGenerationServerTest_Voxel : MonoBehaviour
{
    public enum OutputMode
    {
        Model,
        Voxels,
        Both
    }

    [Header("Server")]
    [SerializeField] private string generateUrl = "http://127.0.0.1:8000/generate";

    [Header("Input")]
    [SerializeField] private Texture2D imageToUpload;
    [SerializeField] private OutputMode outputMode = OutputMode.Voxels;
    [SerializeField, Min(1)] private int maxVoxels = 12000;
    [SerializeField] private bool autoRunOnPlay = false;
    [SerializeField, Min(1)] private int requestTimeoutSeconds = 180;

    [Header("GLB Model Output")]
    [SerializeField] private Transform modelParent;
    [SerializeField] private Vector3 modelPosition = new Vector3(0f, 0f, 2f);
    [SerializeField] private Vector3 modelScale = Vector3.one;

    [Header("Voxel Output")]
    [SerializeField] private Transform voxelParent;
    [SerializeField] private Vector3 voxelRootPosition = Vector3.zero;
    [SerializeField] private Vector3 voxelRootScale = Vector3.one;
    [SerializeField] private PrimitiveType voxelShape = PrimitiveType.Cube;

    [Tooltip("World-space size of one voxel cube. Change this during Play Mode to resize the generated voxel object in real time.")]
    [SerializeField, Min(0.001f)] private float voxelSize = 0.05f;

    [Tooltip("Optional prefab for each voxel. Leave empty to use Unity's default cube. For Quest interaction, use a prefab with Collider and grab components.")]
    [SerializeField] private GameObject voxelPrefab;

    [Header("Quest / XR Interaction Test")]
    [SerializeField] private bool makeVoxelsGrabbableForXR = false;

    private GameObject modelRoot;
    private GameObject voxelRoot;
    private bool isRunning;
    private UnityWebRequest activeRequest;
    public bool IsRunning => isRunning;
    public int GeneratedVoxelCount { get; private set; }
    public int VoxelDrawBatchCount => voxelBatch != null ? voxelBatch.ChunkCount : 0;
    public string StatusMessage { get; private set; } = "Ready. Select Get 3D Model.";
    private int currentGridSize = 64;
    private float appliedVoxelSize = -1f;
    private QuestVoxelBatchRenderer voxelBatch;

    private readonly Dictionary<string, Material> materialCache = new Dictionary<string, Material>();

    public void SetOutputParent(Transform parent)
    {
        modelParent = voxelParent = parent;
        modelPosition = voxelRootPosition = Vector3.zero;
    }

    private void OnDisable()
    {
        if (activeRequest != null) activeRequest.Abort();
        StopAllCoroutines();
        if (activeRequest != null) activeRequest.Dispose();
        activeRequest = null;
        if (isRunning) StatusMessage = "Generation cancelled. Select Get 3D Model to retry.";
        isRunning = false;
    }

    private void OnDestroy()
    {
        ClearGeneratedObjects();
        foreach (Material material in materialCache.Values) DestroyGeneratedObject(material);
        materialCache.Clear();
    }

    [System.Serializable]
    private class ServerResponse
    {
        public string status;
        public string error;
        public string response_mode;
        public string model_url;
        public string model_error;
        public int grid_size;
        public int voxel_count;
        public VoxelData[] voxels;
    }

    [System.Serializable]
    private class VoxelData
    {
        public int x;
        public int y;
        public int z;
        public string type;
        public string color;
    }

    private void Start()
    {
        Debug.Log("[ModelGenerationServerTest] Started.");

        if (autoRunOnPlay)
        {
            UploadImageAndGenerate();
        }
    }

    private void Update()
    {
        // Runtime Inspector update: resize existing voxels when Voxel Size changes.
        if (voxelRoot != null && !Mathf.Approximately(voxelSize, appliedVoxelSize))
        {
            ApplyVoxelSizeToExistingVoxels();
        }
    }

    private void OnValidate()
    {
        maxVoxels = Mathf.Max(1, maxVoxels);
        voxelSize = Mathf.Max(0.001f, voxelSize);
    }

    [ContextMenu("Upload Image And Generate")]
    public void UploadImageAndGenerate()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[ModelGenerationServerTest] Enter Play Mode first.");
            return;
        }

        if (isRunning)
        {
            Debug.LogWarning("[ModelGenerationServerTest] A request is already running.");
            return;
        }

        if (imageToUpload == null)
        {
            StatusMessage = "No input image. Assign Image To Upload on Server.";
            Debug.LogError("[ModelGenerationServerTest] Assign an image in the Inspector.");
            return;
        }

        StartCoroutine(RunGenerationSafely());
    }

    // Traverse nested enumerators so an exception in download, parsing or object creation
    // always restores the button and disposes the request. Yield instructions still run in Unity.
    private IEnumerator RunGenerationSafely()
    {
        isRunning = true;
        var routines = new Stack<IEnumerator>();
        routines.Push(GenerateRoutine());
        try
        {
            while (routines.Count > 0)
            {
                IEnumerator routine = routines.Peek();
                bool more = false;
                object current = null;
                System.Exception failure = null;
                try
                {
                    more = routine.MoveNext();
                    if (more) current = routine.Current;
                }
                catch (System.Exception exception) { failure = exception; }
                if (failure != null)
                {
                    StatusMessage = "Generation failed. Check Console and retry.";
                    Debug.LogException(failure, this);
                    yield break;
                }
                if (!more)
                {
                    (routines.Pop() as System.IDisposable)?.Dispose();
                    continue;
                }
                if (current is IEnumerator nested) routines.Push(nested);
                else yield return current;
            }
        }
        finally
        {
            while (routines.Count > 0) (routines.Pop() as System.IDisposable)?.Dispose();
            if (activeRequest != null) activeRequest.Dispose();
            activeRequest = null;
            isRunning = false;
        }
    }

    [ContextMenu("Clear Generated Objects")]
    public void ClearGeneratedObjects()
    {
        DestroyGeneratedObject(modelRoot);
        DestroyGeneratedObject(voxelRoot);
        modelRoot = null;
        voxelRoot = null;
        voxelBatch = null;
        appliedVoxelSize = -1f;
        GeneratedVoxelCount = 0;
    }

    private IEnumerator GenerateRoutine()
    {
        StatusMessage = "Preparing image...";
        Debug.Log("[ModelGenerationServerTest] Encoding image...");

        byte[] imageBytes = EncodeTextureToPng(imageToUpload);
        if (imageBytes == null || imageBytes.Length == 0)
        {
            Debug.LogError("[ModelGenerationServerTest] Failed to encode image.");
            StatusMessage = "Could not read the input image. Check Console.";
            yield break;
        }

        List<IMultipartFormSection> form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", imageBytes, "unity_image.png", "image/png"),
            new MultipartFormDataSection("response_mode", GetResponseModeName()),
            new MultipartFormDataSection("max_voxels", maxVoxels.ToString())
        };

        Debug.Log("[ModelGenerationServerTest] Uploading image to: " + generateUrl);

        StatusMessage = "Generating model. Please wait...";
        UnityWebRequest request = UnityWebRequest.Post(generateUrl, form);
        activeRequest = request;
        request.timeout = requestTimeoutSeconds;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[ModelGenerationServerTest] Upload failed: " + request.error);
            Debug.LogError(request.downloadHandler.text);
            StatusMessage = "Cannot reach generation server. Start the Python server and retry.";
            yield break;
        }

        ServerResponse response = JsonUtility.FromJson<ServerResponse>(request.downloadHandler.text);
        if (response == null || response.status != "done")
        {
            Debug.LogError("[ModelGenerationServerTest] Invalid server response:");
            Debug.LogError(request.downloadHandler.text);
            StatusMessage = "Invalid server response. Check Console and retry.";
            yield break;
        }

        if (ShouldLoadModel())
        {
            if (!string.IsNullOrEmpty(response.model_url))
            {
                yield return LoadModelRoutine(response.model_url);
            }
            else
            {
                Debug.LogWarning("[ModelGenerationServerTest] No model_url returned. " + response.model_error);
            }
        }

        if (ShouldBuildVoxels())
        {
            if (response.voxels != null && response.voxels.Length > 0)
            {
                yield return BuildVoxelsRoutine(response);
            }
            else
            {
                Debug.LogWarning("[ModelGenerationServerTest] No voxel data returned.");
                StatusMessage = "Server returned no voxels. Check the server output mode.";
            }
        }

    }

    private bool ShouldLoadModel()
    {
        return outputMode == OutputMode.Model || outputMode == OutputMode.Both;
    }

    private bool ShouldBuildVoxels()
    {
        return outputMode == OutputMode.Voxels || outputMode == OutputMode.Both;
    }

    private string GetResponseModeName()
    {
        if (outputMode == OutputMode.Model) return "model";
        if (outputMode == OutputMode.Both) return "both";
        return "voxels";
    }

    private byte[] EncodeTextureToPng(Texture2D source)
    {
        // EncodeToPNG cannot encode compressed textures directly.
        // This creates a readable RGBA32 copy first.
        RenderTexture renderTexture = RenderTexture.GetTemporary(
            source.width,
            source.height,
            0,
            RenderTextureFormat.ARGB32
        );

        RenderTexture previous = RenderTexture.active;
        Texture2D readableTexture = null;

        try
        {
            Graphics.Blit(source, renderTexture);
            RenderTexture.active = renderTexture;

            readableTexture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            readableTexture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readableTexture.Apply();

            return readableTexture.EncodeToPNG();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);

            if (readableTexture != null)
            {
                Destroy(readableTexture);
            }
        }
    }

    private IEnumerator LoadModelRoutine(string modelUrl)
    {
        DestroyGeneratedObject(modelRoot);

        modelRoot = new GameObject("Generated_GLB_Model");
        PlaceRoot(modelRoot, modelParent, modelPosition, modelScale);

        GltfImport gltf = new GltfImport();

        var loadTask = gltf.Load(modelUrl);
        while (!loadTask.IsCompleted)
        {
            yield return null;
        }

        if (loadTask.IsFaulted || loadTask.IsCanceled || !loadTask.Result)
        {
            Debug.LogError("[ModelGenerationServerTest] Failed to load GLB: " + modelUrl);
            DestroyGeneratedObject(modelRoot);
            modelRoot = null;
            yield break;
        }

        var instantiateTask = gltf.InstantiateMainSceneAsync(modelRoot.transform);
        while (!instantiateTask.IsCompleted)
        {
            yield return null;
        }

        if (instantiateTask.IsFaulted || instantiateTask.IsCanceled || !instantiateTask.Result)
        {
            Debug.LogError("[ModelGenerationServerTest] Failed to instantiate GLB.");
            DestroyGeneratedObject(modelRoot);
            modelRoot = null;
            yield break;
        }

        Debug.Log("[ModelGenerationServerTest] GLB model loaded.");
        StatusMessage = "GLB model loaded.";
    }

    private IEnumerator BuildVoxelsRoutine(ServerResponse response)
    {
        DestroyGeneratedObject(voxelRoot);

        currentGridSize = response.grid_size > 0 ? response.grid_size : 64;
        voxelRoot = new GameObject("Generated_Voxels");
        PlaceRoot(voxelRoot, voxelParent, voxelRootPosition, voxelRootScale);
        // Each newly created voxel already gets this size. Avoid walking all
        // previously created voxels on every frame while the coroutine builds.
        appliedVoxelSize = voxelSize;
        voxelBatch = null;
        if (voxelPrefab == null && voxelShape == PrimitiveType.Cube)
        {
            voxelBatch = voxelRoot.AddComponent<QuestVoxelBatchRenderer>();
            voxelBatch.Initialize();
        }
        GeneratedVoxelCount = 0;
        int count = Mathf.Min(response.voxels.Length, maxVoxels);
        var slice = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < count; i++)
        {
            VoxelData voxel = response.voxels[i];
            if (voxel == null) continue;
            GameObject voxelObject = CreateVoxelObject(voxel);
            voxelObject.transform.SetParent(voxelRoot.transform, false);

            GeneratedVoxel generatedVoxel = voxelObject.AddComponent<GeneratedVoxel>();
            generatedVoxel.Initialize(voxel.x, voxel.y, voxel.z, voxel.type, currentGridSize, voxelSize, voxelRoot.transform);
            ApplyVoxelTransform(generatedVoxel);
            if (voxelBatch != null)
            {
                Color color = Color.white;
                if (!string.IsNullOrEmpty(voxel.color)) ColorUtility.TryParseHtmlString(voxel.color, out color);
                voxelBatch.Add(generatedVoxel, color);
            }

            if (makeVoxelsGrabbableForXR)
            {
                MakeVoxelGrabbable(voxelObject);
            }
            GeneratedVoxelCount++;

            // Avoid freezing the Unity Editor when many voxels are created.
            if (slice.Elapsed.TotalMilliseconds >= 3 || i % 128 == 127)
            {
                if (voxelBatch != null) voxelBatch.FlushDirty();
                StatusMessage = $"Building voxels: {GeneratedVoxelCount} / {count}";
                yield return null;
                slice.Restart();
            }
        }

        if (voxelBatch != null) voxelBatch.FlushDirty();
        appliedVoxelSize = voxelSize;
        Debug.Log("[ModelGenerationServerTest] Voxels generated: " + GeneratedVoxelCount);
        StatusMessage = $"{GeneratedVoxelCount:N0} voxels ready. Hold Trigger to drag one.";
    }

    private GameObject CreateVoxelObject(VoxelData voxel)
    {
        GameObject obj = voxelBatch != null ? new GameObject() : voxelPrefab != null
            ? Instantiate(voxelPrefab)
            : GameObject.CreatePrimitive(voxelShape);

        obj.name = $"Voxel_{voxel.x}_{voxel.y}_{voxel.z}_{voxel.type}";

        foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>())
        {
            renderer.sharedMaterial = GetMaterial(voxel.color);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        if (obj.GetComponentInChildren<Collider>() == null)
        {
            obj.AddComponent<BoxCollider>();
        }

        return obj;
    }

    private void ApplyVoxelSizeToExistingVoxels()
    {
        if (voxelRoot == null)
        {
            return;
        }

        foreach (GeneratedVoxel voxel in voxelRoot.GetComponentsInChildren<GeneratedVoxel>())
        {
            voxel.UpdateRuntimeData(currentGridSize, voxelSize, voxelRoot.transform);
            ApplyVoxelTransform(voxel);
        }

        appliedVoxelSize = voxelSize;
    }

    private void ApplyVoxelTransform(GeneratedVoxel voxel)
    {
        // Grid spacing equals cube scale.
        // Neighboring coordinates such as x=20 and x=21 touch like Minecraft blocks.
        float center = (currentGridSize - 1) * 0.5f;

        voxel.transform.localPosition = new Vector3(
            (voxel.x - center) * voxelSize,
            voxel.y * voxelSize,
            (voxel.z - center) * voxelSize
        );

        voxel.transform.localScale = Vector3.one * voxelSize;
        voxel.NotifyRenderChanged();
    }

    private Material GetMaterial(string htmlColor)
    {
        if (string.IsNullOrEmpty(htmlColor))
        {
            htmlColor = "#FFFFFF";
        }

        if (materialCache.TryGetValue(htmlColor, out Material cachedMaterial))
        {
            return cachedMaterial;
        }

        Shader shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
            ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");

        Material material = new Material(shader);
        material.enableInstancing = true;

        if (ColorUtility.TryParseHtmlString(htmlColor, out Color color))
        {
            material.color = color;
        }

        materialCache[htmlColor] = material;
        return material;
    }

    private void MakeVoxelGrabbable(GameObject voxelObject)
    {
        if (voxelObject.GetComponentInChildren<Collider>() == null)
        {
            voxelObject.AddComponent<BoxCollider>();
        }

        // Transform dragging avoids thousands of unnecessary Rigidbody simulations.
        Rigidbody body = voxelObject.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.useGravity = false;
            body.isKinematic = true;
        }
        if (voxelObject.GetComponent<QuestVoxelDraggable>() == null)
            voxelObject.AddComponent<QuestVoxelDraggable>();
    }

    private void PlaceRoot(GameObject root, Transform parent, Vector3 position, Vector3 scale)
    {
        if (parent != null)
        {
            root.transform.SetParent(parent, false);
            root.transform.localPosition = position;
        }
        else
        {
            root.transform.position = position;
        }

        root.transform.localScale = scale;
    }

    private void DestroyGeneratedObject(UnityEngine.Object obj)
    {
        if (obj == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(obj);
        }
        else
        {
            DestroyImmediate(obj);
        }
    }
}

/// <summary>
/// Stores one generated voxel's original grid coordinate.
/// Use GetCurrentGridPosition after a user moves a voxel in VR.
/// </summary>
public class GeneratedVoxel : MonoBehaviour
{
    private QuestVoxelBatchRenderer renderBatch;
    private int renderChunk = -1;
    public bool HasRenderBatch => renderBatch != null;
    public bool IsRayHighlighted { get; private set; }
    public void SetRayHighlight(bool value)
    {
        if (IsRayHighlighted == value) return;
        IsRayHighlighted = value;
        if (renderBatch != null) renderBatch.MarkColorsDirty(renderChunk);
    }
    public void SetRenderBatch(QuestVoxelBatchRenderer batch, int chunk) { renderBatch = batch; renderChunk = chunk; }
    public void NotifyRenderChanged() { if (renderBatch != null) renderBatch.MarkDirty(renderChunk); }
    private void OnEnable() => NotifyRenderChanged();
    private void OnDisable() => NotifyRenderChanged();
    private void OnDestroy() => NotifyRenderChanged();
    public int x;
    public int y;
    public int z;
    public string voxelType;

    private int gridSize = 64;
    private float voxelSize = 0.05f;
    private Transform voxelRoot;

    public void Initialize(int gridX, int gridY, int gridZ, string type, int size, float unityVoxelSize, Transform root)
    {
        x = gridX;
        y = gridY;
        z = gridZ;
        voxelType = type;
        UpdateRuntimeData(size, unityVoxelSize, root);
    }

    public void UpdateRuntimeData(int size, float unityVoxelSize, Transform root)
    {
        gridSize = size;
        voxelSize = unityVoxelSize;
        voxelRoot = root;
    }

    public Vector3Int GetCurrentGridPosition()
    {
        // Convert the current Unity position back into a voxel grid coordinate.
        Vector3 localPosition = voxelRoot != null
            ? voxelRoot.InverseTransformPoint(transform.position)
            : transform.localPosition;

        float center = (gridSize - 1) * 0.5f;

        int gx = Mathf.RoundToInt(localPosition.x / voxelSize + center);
        int gy = Mathf.RoundToInt(localPosition.y / voxelSize);
        int gz = Mathf.RoundToInt(localPosition.z / voxelSize + center);

        return new Vector3Int(
            Mathf.Clamp(gx, 0, gridSize - 1),
            Mathf.Clamp(gy, 0, gridSize - 1),
            Mathf.Clamp(gz, 0, gridSize - 1)
        );
    }
}
