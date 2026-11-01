using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>Runs the VR-Crafter FastAPI backend during local Editor play mode.</summary>
[InitializeOnLoad]
public static class QuestProLocalTestServer
{
    public const string PythonPreference = "QuestPro.VoxelTestServer.Python";
    private static Process ownedProcess;

    static QuestProLocalTestServer()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode) StartIfNeeded();
            if (state == PlayModeStateChange.ExitingPlayMode) StopOwnedServer();
        };
        EditorApplication.quitting += StopOwnedServer;
        AssemblyReloadEvents.beforeAssemblyReload += StopOwnedServer;
    }

    [MenuItem("Tools/Quest Pro/Select Python for VR-Crafter Backend")]
    private static void SelectPython()
    {
        string path = EditorUtility.OpenFilePanel("Python with fastapi, uvicorn and python-multipart", "", "exe");
        if (!string.IsNullOrEmpty(path)) EditorPrefs.SetString(PythonPreference, path);
    }

    public static void StartIfNeeded()
    {
        var experience = UnityEngine.Object.FindFirstObjectByType<QuestProVoxelExperience>();
        if (experience == null) return;
        var generator = experience.GetComponent<ModelGenerationServerTest_Voxel>();
        if (generator.UsesLocalModel) return;
        if (generator.RequiresTrellis2)
        {
            Debug.Log("[Quest Pro] TRELLIS.2 mode selected; local fixture startup disabled.");
            return;
        }
        string url = new SerializedObject(generator).FindProperty("generateUrl").stringValue;
        if (url != "http://127.0.0.1:8000/generate" || IsListening()) return;
        if (ownedProcess != null && !ownedProcess.HasExited) return;
        string project = Path.GetDirectoryName(Application.dataPath);
        string backend = Path.Combine(project, "backend");
        string app = Path.Combine(backend, "app", "main.py");
        if (!File.Exists(app))
        {
            Debug.LogError("[Quest Pro] Cannot find backend/app/main.py.");
            return;
        }

        string localPython = Path.Combine(backend, ".venv", "Scripts", "python.exe");
        string python = File.Exists(localPython)
            ? localPython
            : EditorPrefs.GetString(PythonPreference, "python");
        var info = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-m uvicorn app.main:app --host 127.0.0.1 --port 8000",
            WorkingDirectory = backend,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            ownedProcess = Process.Start(info);
            Debug.Log("[Quest Pro] Starting VR-Crafter backend on http://127.0.0.1:8000.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Quest Pro] Could not start the VR-Crafter backend. Create backend/.venv or select a Python environment with the backend requirements installed. " + exception.Message);
        }
    }

    private static bool IsListening()
    {
        try
        {
            using (var client = new TcpClient())
                return client.ConnectAsync("127.0.0.1", 8000).Wait(100) && client.Connected;
        }
        catch { return false; }
    }

    private static void StopOwnedServer()
    {
        if (ownedProcess == null) return;
        try { if (!ownedProcess.HasExited) ownedProcess.Kill(); }
        catch (InvalidOperationException) { }
        finally { ownedProcess.Dispose(); ownedProcess = null; }
    }
}

/// <summary>Scene-level connection settings for the same Unity generation client.</summary>
public sealed class QuestProBackendConnectionWindow : EditorWindow
{
    private string url = "http://127.0.0.1:8000/generate";
    private bool trellis2 = true;

    [MenuItem("Tools/Quest Pro/Connect Generation Backend")]
    private static void Open()
    {
        var window = GetWindow<QuestProBackendConnectionWindow>("VR-Crafter Backend");
        var generator = UnityEngine.Object.FindFirstObjectByType<ModelGenerationServerTest_Voxel>();
        if (generator != null)
        {
            window.url = generator.GenerateUrl;
            window.trellis2 = generator.RequiresTrellis2;
        }
        window.minSize = new Vector2(440f, 185f);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Generation backend", EditorStyles.boldLabel);
        trellis2 = EditorGUILayout.Toggle("Require real TRELLIS.2", trellis2);
        url = EditorGUILayout.TextField("Generate URL", url);
        EditorGUILayout.HelpBox(trellis2
            ? "A Linux GPU service must answer /status with generator=trellis2. Unity will not launch or accept the fixed house."
            : "Fixture mode uses the local fixed house for desktop testing.", MessageType.Info);
        var generator = UnityEngine.Object.FindFirstObjectByType<ModelGenerationServerTest_Voxel>();
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || generator == null))
        {
            if (GUILayout.Button("Save connection to open scene"))
            {
                try
                {
                    Undo.RecordObject(generator, "Set VR-Crafter generation backend");
                    generator.ConfigureBackend(url.Trim(), trellis2);
                    EditorUtility.SetDirty(generator);
                    EditorSceneManager.MarkSceneDirty(generator.gameObject.scene);
                    EditorSceneManager.SaveScene(generator.gameObject.scene);
                    Debug.Log("[Quest Pro] Backend saved: " + (trellis2 ? "TRELLIS.2 " : "fixture ") + generator.GenerateUrl);
                }
                catch (Exception exception)
                {
                    EditorUtility.DisplayDialog("Backend settings", exception.Message, "OK");
                }
            }
        }
        if (generator == null) EditorGUILayout.HelpBox("Open Assets/Scenes/SampleScene.unity first.", MessageType.Warning);
    }
}
