using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;

public class ModelGenerationServerTest : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private string generateUrl = "http://127.0.0.1:8000/generate";

    [Header("Input Image")]
    [SerializeField] private Texture2D imageToUpload;

    [Header("Run")]
    [SerializeField] private bool autoRunOnPlay = true;

    [Header("Spawn")]
    [SerializeField] private Transform spawnParent;
    [SerializeField] private Vector3 spawnPosition = new Vector3(0f, 0f, 2f);
    [SerializeField] private Vector3 spawnScale = Vector3.one;

    private GameObject loadedModel;
    private bool isRunning;

    [System.Serializable]
    private class ServerResponse
    {
        public string status;
        public string model_url;
    }

    private void Start()
    {
        Debug.Log("[ModelGenerationServerTest] Started.");

        if (autoRunOnPlay)
        {
            UploadImageAndLoadModel();
        }
    }

    [ContextMenu("Upload Image And Load Model")]
    public void UploadImageAndLoadModel()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[ModelGenerationServerTest] Please enter Play Mode first.");
            return;
        }

        if (isRunning)
        {
            Debug.LogWarning("[ModelGenerationServerTest] Already running.");
            return;
        }

        if (imageToUpload == null)
        {
            Debug.LogError("[ModelGenerationServerTest] Please assign an image in the Inspector.");
            return;
        }

        StartCoroutine(UploadImageRoutine());
    }

    private IEnumerator UploadImageRoutine()
    {
        isRunning = true;

        Debug.Log("[ModelGenerationServerTest] Preparing image...");

        byte[] imageBytes = EncodeTextureToPng(imageToUpload);

        if (imageBytes == null || imageBytes.Length == 0)
        {
            Debug.LogError("[ModelGenerationServerTest] Failed to encode image to PNG.");
            isRunning = false;
            yield break;
        }

        Debug.Log("[ModelGenerationServerTest] Image encoded. Size: " + imageBytes.Length + " bytes");

        List<IMultipartFormSection> form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection(
                "file",
                imageBytes,
                "unity_image.png",
                "image/png"
            )
        };

        Debug.Log("[ModelGenerationServerTest] Uploading image to: " + generateUrl);

        using UnityWebRequest request = UnityWebRequest.Post(generateUrl, form);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[ModelGenerationServerTest] Upload failed: " + request.error);
            Debug.LogError("[ModelGenerationServerTest] Response: " + request.downloadHandler.text);
            isRunning = false;
            yield break;
        }

        Debug.Log("[ModelGenerationServerTest] Server response: " + request.downloadHandler.text);

        ServerResponse response = JsonUtility.FromJson<ServerResponse>(request.downloadHandler.text);

        if (response == null || response.status != "done" || string.IsNullOrEmpty(response.model_url))
        {
            Debug.LogError("[ModelGenerationServerTest] Invalid server response.");
            isRunning = false;
            yield break;
        }

        yield return LoadModelRoutine(response.model_url);

        isRunning = false;
    }

    private byte[] EncodeTextureToPng(Texture2D source)
    {
        // EncodeToPNG does not support compressed textures.
        // This function creates a readable RGBA32 copy first, then encodes that copy.

        Texture2D readableCopy = CreateReadableCopy(source);

        if (readableCopy == null)
        {
            return null;
        }

        byte[] pngBytes = readableCopy.EncodeToPNG();

        Destroy(readableCopy);

        return pngBytes;
    }

    private Texture2D CreateReadableCopy(Texture2D source)
    {
        // Render the source texture into an uncompressed RenderTexture.
        RenderTexture renderTexture = RenderTexture.GetTemporary(
            source.width,
            source.height,
            0,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.sRGB
        );

        RenderTexture previous = RenderTexture.active;

        Graphics.Blit(source, renderTexture);
        RenderTexture.active = renderTexture;

        // Read the pixels back into an uncompressed, readable Texture2D.
        Texture2D readableTexture = new Texture2D(
            source.width,
            source.height,
            TextureFormat.RGBA32,
            false
        );

        readableTexture.ReadPixels(
            new Rect(0, 0, source.width, source.height),
            0,
            0
        );

        readableTexture.Apply();

        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(renderTexture);

        return readableTexture;
    }

    private IEnumerator LoadModelRoutine(string modelUrl)
    {
        Debug.Log("[ModelGenerationServerTest] Loading GLB: " + modelUrl);

        if (loadedModel != null)
        {
            Destroy(loadedModel);
        }

        loadedModel = new GameObject("Generated_Model");

        if (spawnParent != null)
        {
            loadedModel.transform.SetParent(spawnParent, false);
            loadedModel.transform.localPosition = spawnPosition;
        }
        else
        {
            loadedModel.transform.position = spawnPosition;
        }

        loadedModel.transform.localScale = spawnScale;

        GltfImport gltf = new GltfImport();

        var loadTask = gltf.Load(modelUrl);

        while (!loadTask.IsCompleted)
        {
            yield return null;
        }

        if (!loadTask.Result)
        {
            Debug.LogError("[ModelGenerationServerTest] Failed to load GLB.");
            Destroy(loadedModel);
            yield break;
        }

        var instantiateTask = gltf.InstantiateMainSceneAsync(loadedModel.transform);

        while (!instantiateTask.IsCompleted)
        {
            yield return null;
        }

        if (!instantiateTask.Result)
        {
            Debug.LogError("[ModelGenerationServerTest] Failed to instantiate GLB.");
            Destroy(loadedModel);
            yield break;
        }

        Debug.Log("[ModelGenerationServerTest] Model loaded successfully.");
    }
}