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
/// 4. Submit Edits posts the edited voxels as a new revision and rebuilds from the response.
///    If the server already has a newer revision (a submit that timed out but was committed),
///    Unity loads that revision from generation_url instead.
///
/// Voxel mode:
/// - The server returns voxel coordinates (with stable ids) in a 64x64x64 grid.
/// - Unity creates one cube or prefab per voxel; x/y/z always hold the voxel's current cell.
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
    [Tooltip("Seconds before Get 3D Model or Submit Edits gives up waiting. TRELLIS.2 timings are not measured yet.")]
    [SerializeField, Min(1)] private int requestTimeoutSeconds = 600;

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
    public Transform VoxelRoot => voxelRoot != null ? voxelRoot.transform : null;
    public int GridSize => currentGridSize;
    public float VoxelSize => voxelSize;
    public int MaxVoxels => maxVoxels;
    // Needs the editor of the current root, which is attached only after a complete build: a build cut short
    // never submits its partial voxel set, whose missing ids the server would delete.
    public bool CanSubmitEdits => !isRunning && voxelRoot != null && !string.IsNullOrEmpty(revisionsUrl) &&
        QuestVoxelEditor.Active != null && QuestVoxelEditor.Active.gameObject == voxelRoot && !QuestVoxelEditor.Active.IsBusy;
    private int currentGridSize = 64;
    private float appliedVoxelSize = -1f;
    private float alignedModelVoxelSize = -1f;
    private QuestVoxelBatchRenderer voxelBatch;
    private string revisionsUrl;
    private string generationUrl;
    private int currentRevision;
    private bool alignModelToVoxels;
    private bool modelLoadFailed;
    private const string ModelLoadFailedNote = " GLB failed to load; see Console.";

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
        public string generation_id;
        public int revision;
        public string model_space;
        public string revisions_url;
        public string generation_url;
        public int grid_size;
        public int voxel_count;
        public VoxelData[] voxels;
    }

    [System.Serializable]
    private class VoxelData
    {
        public int id;
        public int x;
        public int y;
        public int z;
        public string type;
        public string color;
    }

    [System.Serializable]
    private class RevisionRequest
    {
        public int base_revision;
        public int max_voxels;
        public EditedVoxel[] voxels;
    }

    [System.Serializable]
    private class EditedVoxel
    {
        public int id, x, y, z;
        public string type, color;
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
        // Runtime Inspector update: resize existing voxels and the aligned GLB when Voxel Size changes.
        if (voxelRoot != null && !Mathf.Approximately(voxelSize, appliedVoxelSize))
        {
            ApplyVoxelSizeToExistingVoxels();
        }
        if (modelRoot != null && alignModelToVoxels && !Mathf.Approximately(voxelSize, alignedModelVoxelSize))
        {
            ApplyModelAlignment();
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

        StartCoroutine(RunGenerationSafely(GenerateRoutine()));
    }

    // Sends every active in-grid voxel; deleted voxels are already destroyed and
    // ids missing from the list are removed by the server.
    [ContextMenu("Submit Edits")]
    public void SubmitEdits()
    {
        if (!Application.isPlaying || !CanSubmitEdits)
        {
            Debug.LogWarning("[ModelGenerationServerTest] Nothing to submit, or a request is already running.");
            return;
        }
        StartCoroutine(RunGenerationSafely(SubmitEditsRoutine()));
    }

    public void ReportStatus(string message) => StatusMessage = message;

    // Traverse nested enumerators so an exception in download, parsing or object creation
    // always restores the button and disposes the request. Yield instructions still run in Unity.
    private IEnumerator RunGenerationSafely(IEnumerator routine)
    {
        isRunning = true;
        var routines = new Stack<IEnumerator>();
        routines.Push(routine);
        try
        {
            while (routines.Count > 0)
            {
                IEnumerator top = routines.Peek();
                bool more = false;
                object current = null;
                System.Exception failure = null;
                try
                {
                    more = top.MoveNext();
                    if (more) current = top.Current;
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
        float sentAt = Time.realtimeSinceStartup;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[ModelGenerationServerTest] Upload failed: " + request.error);
            Debug.LogError(request.downloadHandler.text);
            StatusMessage = RequestFailureStatus(request, sentAt);
            yield break;
        }

        yield return ApplyServerResponse(ParseServerResponse(request.downloadHandler.text));
    }

    private IEnumerator SubmitEditsRoutine()
    {
        var edited = new List<EditedVoxel>();
        int outside = 0;
        foreach (GeneratedVoxel voxel in voxelRoot.GetComponentsInChildren<GeneratedVoxel>())
        {
            if (voxel.IsOutsideGrid) { outside++; continue; }
            edited.Add(new EditedVoxel { id = voxel.id, x = voxel.x, y = voxel.y, z = voxel.z, type = voxel.voxelType, color = voxel.color });
        }
        var body = new RevisionRequest { base_revision = currentRevision, max_voxels = maxVoxels, voxels = edited.ToArray() };

        Debug.Log($"[ModelGenerationServerTest] Submitting {edited.Count} voxels to: {revisionsUrl}");
        StatusMessage = "Submitting edits. Please wait...";
        UnityWebRequest request = UnityWebRequest.Post(revisionsUrl, JsonUtility.ToJson(body), "application/json");
        activeRequest = request;
        request.timeout = requestTimeoutSeconds;
        float sentAt = Time.realtimeSinceStartup;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[ModelGenerationServerTest] Submit failed: " + request.error);
            Debug.LogError(request.downloadHandler.text);
            StatusMessage = RequestFailureStatus(request, sentAt);
            yield break;
        }

        ServerResponse response = ParseServerResponse(request.downloadHandler.text);
        if (IsStaleRevision(response) && !string.IsNullOrEmpty(generationUrl))
        {
            request.Dispose();
            activeRequest = null;
            yield return LoadLatestRevisionRoutine();
            yield break;
        }
        yield return ApplyServerResponse(response);
        if (outside > 0 && BuiltVoxelsFrom(response)) StatusMessage += $" {outside} voxels outside the grid were removed.";
    }

    // The server committed a revision this client never received, typically a submit that timed out
    // on the client but finished on the server; pressing Submit Edits again then lands here.
    private static bool IsStaleRevision(ServerResponse r) =>
        r != null && r.status == "error" && r.error != null &&
        r.error.StartsWith("Revision ", System.StringComparison.Ordinal) && r.error.Contains(" is stale; latest is ");

    // Rebuilds from the latest committed revision. Edits made after it are replaced; a failed request
    // or an error response leaves the current voxels untouched.
    private IEnumerator LoadLatestRevisionRoutine()
    {
        Debug.Log("[ModelGenerationServerTest] Base revision is stale; loading " + generationUrl);
        StatusMessage = "Loading the server's latest revision...";
        UnityWebRequest request = UnityWebRequest.Get(generationUrl);
        activeRequest = request;
        request.timeout = requestTimeoutSeconds;
        float sentAt = Time.realtimeSinceStartup;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[ModelGenerationServerTest] Loading the latest revision failed: " + request.error);
            StatusMessage = RequestFailureStatus(request, sentAt);
            yield break;
        }

        ServerResponse response = ParseServerResponse(request.downloadHandler.text);
        yield return ApplyServerResponse(response);
        if (BuiltVoxelsFrom(response))
            StatusMessage = $"Server already had revision {currentRevision}; loaded it. Re-apply edits made since then." +
                (modelLoadFailed ? ModelLoadFailedNote : "");
    }

    // A timed-out request may still finish on the server, so it is not reported as an unreachable server.
    private string RequestFailureStatus(UnityWebRequest request, float sentAt)
    {
        string error = request.error ?? "";
        bool timedOut = request.result == UnityWebRequest.Result.ConnectionError &&
            (error.IndexOf("timeout", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
             error.IndexOf("timed out", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
             Time.realtimeSinceStartup - sentAt >= requestTimeoutSeconds);
        return timedOut
            ? $"Request timed out after {requestTimeoutSeconds} s; the server may still be working. Wait, then retry."
            : "Cannot reach generation server. Start the Python server and retry.";
    }

    private bool BuiltVoxelsFrom(ServerResponse r) =>
        ShouldBuildVoxels() && r != null && r.status == "done" && r.voxels != null && r.voxels.Length > 0;

    private ServerResponse ParseServerResponse(string json)
    {
        ServerResponse response = JsonUtility.FromJson<ServerResponse>(json);
        if (response == null || response.status != "done") Debug.LogError("[ModelGenerationServerTest] Server response: " + json);
        return response;
    }

    // An error response leaves the current voxels and GLB untouched, so edits can be resubmitted.
    private IEnumerator ApplyServerResponse(ServerResponse r)
    {
        if (r != null && r.status == "error")
        {
            StatusMessage = string.IsNullOrEmpty(r.error) ? "Server error. Check Console and retry." : r.error;
            yield break;
        }
        if (r == null || r.status != "done")
        {
            StatusMessage = "Invalid server response. Check Console and retry.";
            yield break;
        }

        currentGridSize = r.grid_size > 0 ? r.grid_size : 64;
        revisionsUrl = r.revisions_url;
        generationUrl = r.generation_url;
        currentRevision = r.revision;
        alignModelToVoxels = r.model_space == "trellis_unit_cube";
        modelLoadFailed = false;

        if (ShouldLoadModel())
        {
            if (!string.IsNullOrEmpty(r.model_url))
            {
                yield return LoadModelRoutine(r.model_url);
            }
            else
            {
                // Never leave a previous revision's GLB beside the rebuilt voxels.
                Debug.LogWarning("[ModelGenerationServerTest] No model_url returned. " + r.model_error);
                DestroyGeneratedObject(modelRoot);
                modelRoot = null;
            }
        }

        if (ShouldBuildVoxels())
        {
            if (r.voxels != null && r.voxels.Length > 0)
            {
                yield return BuildVoxelsRoutine(r);
                if (modelLoadFailed) StatusMessage += ModelLoadFailedNote;
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
        ApplyModelAlignment();

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
            modelLoadFailed = true;
            StatusMessage = ModelLoadFailedNote.Trim();
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
            modelLoadFailed = true;
            StatusMessage = ModelLoadFailedNote.Trim();
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
            CreateVoxel(voxel.id, new Vector3Int(voxel.x, voxel.y, voxel.z), voxel.type, voxel.color);

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
        // A fresh editor per build: the selection is cleared and the edit mode is kept.
        voxelRoot.AddComponent<QuestVoxelEditor>().Initialize(this);
        Debug.Log("[ModelGenerationServerTest] Voxels generated: " + GeneratedVoxelCount);
        StatusMessage = $"Revision {response.revision}: {GeneratedVoxelCount} voxels ready.";
    }

    // Returns null when Max Voxels is reached. Keeps GeneratedVoxelCount and the draw batches in sync.
    public GeneratedVoxel CreateVoxel(int id, Vector3Int cell, string type, string color)
    {
        if (voxelRoot == null || GeneratedVoxelCount >= maxVoxels) return null;
        GameObject voxelObject = CreateVoxelObject(cell, type, color);
        voxelObject.transform.SetParent(voxelRoot.transform, false);

        GeneratedVoxel generatedVoxel = voxelObject.AddComponent<GeneratedVoxel>();
        generatedVoxel.Initialize(id, cell.x, cell.y, cell.z, type, color, currentGridSize, voxelSize, voxelRoot.transform);
        ApplyVoxelTransform(generatedVoxel);
        if (voxelBatch != null)
        {
            Color displayColor = Color.white;
            if (!string.IsNullOrEmpty(color)) ColorUtility.TryParseHtmlString(color, out displayColor);
            voxelBatch.Add(generatedVoxel, displayColor);
        }

        if (makeVoxelsGrabbableForXR)
        {
            MakeVoxelGrabbable(voxelObject);
        }
        GeneratedVoxelCount++;
        return generatedVoxel;
    }

    // Deactivate first so the draw batch skips the voxel this frame, before the deferred Destroy.
    public void DestroyVoxel(GeneratedVoxel voxel)
    {
        if (voxel == null || voxelRoot == null || voxel.transform.parent != voxelRoot.transform || !voxel.gameObject.activeSelf) return;
        voxel.gameObject.SetActive(false);
        voxel.NotifyRenderChanged();
        DestroyGeneratedObject(voxel.gameObject);
        GeneratedVoxelCount--;
    }

    public void MoveVoxelToCell(GeneratedVoxel voxel, Vector3Int cell)
    {
        if (voxel == null) return;
        voxel.x = cell.x;
        voxel.y = cell.y;
        voxel.z = cell.z;
        voxel.transform.localRotation = Quaternion.identity;
        ApplyVoxelTransform(voxel);
    }

    private GameObject CreateVoxelObject(Vector3Int cell, string type, string color)
    {
        GameObject obj = voxelBatch != null ? new GameObject() : voxelPrefab != null
            ? Instantiate(voxelPrefab)
            : GameObject.CreatePrimitive(voxelShape);

        obj.name = $"Voxel_{cell.x}_{cell.y}_{cell.z}_{type}";

        foreach (Renderer renderer in obj.GetComponentsInChildren<Renderer>())
        {
            renderer.sharedMaterial = GetMaterial(color);
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
        // Grid spacing equals cube scale. Placing from the current cell keeps edits when Voxel Size changes.
        voxel.transform.localPosition = GeneratedVoxel.GridToLocal(voxel.Cell, currentGridSize, voxelSize);
        voxel.transform.localScale = Vector3.one * voxelSize;
        voxel.NotifyRenderChanged();
    }

    // A trellis_unit_cube GLB spans [-0.5, 0.5]^3 around the grid centre: scale it by G*s and
    // lift it by (G-1)/2*s so it overlaps the voxels, whose lowest layer sits at y=0. Both are
    // in voxel-root units, so the voxel root's scale applies to the lift as well.
    private void ApplyModelAlignment()
    {
        if (modelRoot == null || !alignModelToVoxels) return;
        modelRoot.transform.localPosition = voxelRootPosition +
            Vector3.Scale(voxelRootScale, Vector3.up * ((currentGridSize - 1) * 0.5f * voxelSize));
        modelRoot.transform.localScale = voxelRootScale * (currentGridSize * voxelSize);
        alignedModelVoxelSize = voxelSize;
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
/// Stores one generated voxel's server id, appearance and current grid cell.
/// x/y/z may leave the grid while editing; such voxels are excluded on submit.
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
    public bool IsSelected { get; private set; }
    public void SetSelected(bool value)
    {
        if (IsSelected == value) return;
        IsSelected = value;
        if (renderBatch != null) renderBatch.MarkColorsDirty(renderChunk);
    }
    public void SetRenderBatch(QuestVoxelBatchRenderer batch, int chunk) { renderBatch = batch; renderChunk = chunk; }
    public void NotifyRenderChanged() { if (renderBatch != null) renderBatch.MarkDirty(renderChunk); }
    private void OnEnable() => NotifyRenderChanged();
    private void OnDisable() => NotifyRenderChanged();
    private void OnDestroy() => NotifyRenderChanged();
    public int id;
    public int x;
    public int y;
    public int z;
    public string voxelType;
    public string color;
    public Vector3Int Cell => new Vector3Int(x, y, z);
    public bool IsOutsideGrid => x < 0 || y < 0 || z < 0 || x >= gridSize || y >= gridSize || z >= gridSize;
    // True while parented under the generator root it was created for; such voxels are only edited on the grid.
    public bool InGeneratedModel => modelRoot != null && transform.parent == modelRoot;

    private int gridSize = 64;
    private Transform modelRoot;

    public void Initialize(int voxelId, int gridX, int gridY, int gridZ, string type, string htmlColor, int size, float unityVoxelSize, Transform root)
    {
        id = voxelId;
        x = gridX;
        y = gridY;
        z = gridZ;
        voxelType = type;
        color = htmlColor;
        UpdateRuntimeData(size, unityVoxelSize, root);
    }

    // Voxel size is owned by the generator; the grid size and the generator's voxel root are kept here.
    public void UpdateRuntimeData(int size, float unityVoxelSize, Transform root)
    {
        gridSize = size;
        modelRoot = root;
    }

    // Grid cell to voxel-root local position. Neighbouring cells touch like Minecraft blocks.
    public static Vector3 GridToLocal(Vector3Int cell, int gridSize, float voxelSize)
    {
        float center = (gridSize - 1) * 0.5f;
        return new Vector3((cell.x - center) * voxelSize, cell.y * voxelSize, (cell.z - center) * voxelSize);
    }

    // Inverse of GridToLocal, rounded to the nearest cell and not clamped to the grid.
    public static Vector3Int LocalToCell(Vector3 local, int gridSize, float voxelSize)
    {
        float center = (gridSize - 1) * 0.5f;
        return new Vector3Int(Mathf.RoundToInt(local.x / voxelSize + center), Mathf.RoundToInt(local.y / voxelSize),
            Mathf.RoundToInt(local.z / voxelSize + center));
    }
}
