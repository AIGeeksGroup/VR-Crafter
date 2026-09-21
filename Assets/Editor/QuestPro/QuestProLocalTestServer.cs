using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UnityEditor;
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
