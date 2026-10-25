using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Batch smoke test through the actual scene buttons, HTTP service, revision round trip and Play Mode.</summary>
[InitializeOnLoad]
public static class QuestProPlayModeValidation
{
    private const string Key = "QuestPro.BatchValidation";
    private static int stage;
    private static double since;
    private static ModelGenerationServerTest_Voxel generator;
    private static QuestRayButton button;
    private static QuestRayButton submit;
    private static bool submitted;
    private static int baseCount;
    private static int movedId;
    private static Vector3Int movedCell;
    private static Vector3Int addedCell;

    static QuestProPlayModeValidation()
    {
        if (SessionState.GetBool(Key, false))
        {
            since = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }
    }

    public static void Run()
    {
        QuestProProjectSetup.ConfigureAndValidate();
        SessionState.SetBool(Key, true);
        SessionState.SetString(Key + ".Report", Argument("-questPlayReport"));
        SessionState.SetString(Key + ".Screenshot", Argument("-questScreenshot"));
        EditorApplication.EnterPlaymode();
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        try
        {
            if (EditorApplication.timeSinceStartup - since > 150) throw new Exception("Play Mode validation timed out at stage " + stage);
            if (stage == 0)
            {
                if (Time.time < 4f) return;
                generator = UnityEngine.Object.FindFirstObjectByType<ModelGenerationServerTest_Voxel>();
                button = GameObject.Find("Get 3D Model").GetComponent<QuestRayButton>();
                submit = GameObject.Find("Submit Edits").GetComponent<QuestRayButton>();
                Require(button != null && generator != null, "VR Get 3D Model button exists in the real scene");
                Require(submit != null && !submit.GetComponent<Button>().interactable, "VR Submit Edits button exists and waits for a model");
                Require(UnityEngine.Object.FindObjectsByType<QuestProTriggerInteractor>(FindObjectsSortMode.None).Length == 2, "Both controller anchors have Trigger interactors");
                Require(!generator.IsRunning && generator.GeneratedVoxelCount == 0, "Play waits for the button without generating automatically");
                ValidateTriggerInput();
                Require(generator.IsRunning, "Controller ray and Trigger activate the VR button and HTTP image upload");
                stage = 1;
                return;
            }
            if (stage == 1)
            {
                if (generator.IsRunning) return;
                if (!button.GetComponent<Button>().interactable) return;
                Require(generator.GeneratedVoxelCount > 100, "Real notebook HTTP response generated " + generator.GeneratedVoxelCount + " voxels");
                var voxels = UnityEngine.Object.FindObjectsByType<QuestVoxelDraggable>(FindObjectsSortMode.None);
                Require(voxels.Length == generator.GeneratedVoxelCount, "Every returned voxel can be dragged independently");
                var hand = new GameObject("Validation Hand").transform;
                Require(voxels[0].TryGrab(hand), "Runtime grab acquires a voxel");
                voxels[0].enabled = false;
                Require(voxels[0].Owner == null, "Real Play Mode disable releases ownership");
                voxels[0].enabled = true;
                UnityEngine.Object.Destroy(hand.gameObject);
                CaptureScene();
                SetUrl("http://127.0.0.1:8000/missing-validation-route");
                button.Press();
                // The UI updates its interactability the frame after generation ends.
                if (!generator.IsRunning) return;
                stage = 2;
                return;
            }
            if (stage == 2)
            {
                if (generator.IsRunning) return;
                Require(generator.StatusMessage.Contains("server"), "HTTP failure shows a visible error and resets request state");
                SetUrl("http://127.0.0.1:8000/generate");
                stage = 3;
                return;
            }
            if (stage == 3)
            {
                if (!button.GetComponent<Button>().interactable) return;
                button.Press();
                Require(generator.IsRunning, "Get 3D Model can retry after a failed request");
                stage = 4;
                return;
            }
            if (stage == 4)
            {
                if (generator.IsRunning) return;
                Require(generator.GeneratedVoxelCount > 100 && generator.StatusMessage.Contains("ready"), "Retry successfully rebuilds the model");
                Require(UnityEngine.Object.FindObjectsByType<QuestVoxelDraggable>(FindObjectsSortMode.None).Length == generator.GeneratedVoxelCount,
                    "Regeneration cleans up old voxels");
                stage = 5;
                return;
            }
            // Stages 5-6 edit voxels through the generator API (no VR input) and need the fixture backend.
            if (stage == 5)
            {
                if (!submitted)
                {
                    if (!submit.GetComponent<Button>().interactable) return;
                    var voxels = generator.VoxelRoot.GetComponentsInChildren<GeneratedVoxel>();
                    var occupied = new HashSet<Vector3Int>(voxels.Select(v => v.Cell));
                    var moved = voxels.FirstOrDefault(v => v.x + 1 < generator.GridSize && !occupied.Contains(v.Cell + Vector3Int.right));
                    Require(moved != null, "Generated model has a voxel whose +x cell is empty");
                    baseCount = generator.GeneratedVoxelCount;
                    movedId = moved.id;
                    movedCell = moved.Cell + Vector3Int.right;
                    generator.MoveVoxelToCell(moved, movedCell);
                    occupied.Add(movedCell);
                    addedCell = FindEmptyCell(occupied, generator.GridSize);
                    Require(generator.CreateVoxel(-1, addedCell, "test", "#00FF00") != null && generator.GeneratedVoxelCount == baseCount + 1,
                        "Code edits move voxel " + movedId + " one cell and add a new voxel at " + addedCell);
                    submit.Press();
                    Require(generator.IsRunning, "Submit Edits posts the edited voxels");
                    submitted = true;
                    return;
                }
                if (generator.IsRunning) return;
                var result = generator.VoxelRoot.GetComponentsInChildren<GeneratedVoxel>();
                var movedVoxel = result.FirstOrDefault(v => v.id == movedId);
                var added = result.FirstOrDefault(v => v.Cell == addedCell);
                Require(generator.StatusMessage.Contains("Revision 1"), "Submit rebuilds from revision 1: " + generator.StatusMessage);
                Require(movedVoxel != null && movedVoxel.Cell == movedCell, "Moved voxel keeps id " + movedId + " at its new cell");
                Require(generator.GeneratedVoxelCount == baseCount + 1, "Revision 1 has exactly one more voxel");
                Require(added != null && added.id >= 9791 && added.color == "#00FF00", "New voxel gets a backend id and keeps its color");
                submitted = false;
                stage = 6;
                return;
            }
            if (stage == 6)
            {
                if (!submitted)
                {
                    if (!submit.GetComponent<Button>().interactable) return;
                    var voxel = generator.VoxelRoot.GetComponentInChildren<GeneratedVoxel>();
                    generator.MoveVoxelToCell(voxel, new Vector3Int(-3, voxel.y, voxel.z));
                    Require(voxel.IsOutsideGrid, "A voxel moved to x=-3 is outside the grid");
                    baseCount = generator.GeneratedVoxelCount;
                    submit.Press();
                    Require(generator.IsRunning, "Submit Edits can post a second revision");
                    submitted = true;
                    return;
                }
                if (generator.IsRunning) return;
                Require(generator.GeneratedVoxelCount == baseCount - 1 && generator.StatusMessage.Contains("Revision 2") &&
                    generator.StatusMessage.Contains("removed"), "Out-of-grid voxels are removed on submit: " + generator.StatusMessage);
                Finish(0);
            }
        }
        catch (Exception exception)
        {
            File.AppendAllText(SessionState.GetString(Key + ".Report", ""), "FAIL: " + exception + Environment.NewLine);
            Debug.LogException(exception);
            Finish(1);
        }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        File.AppendAllText(SessionState.GetString(Key + ".Report", ""), "PASS: " + description + Environment.NewLine);
    }

    private static Vector3Int FindEmptyCell(HashSet<Vector3Int> occupied, int size)
    {
        for (int y = size - 1; y >= 0; y--)
            for (int x = 0; x < size; x++)
                for (int z = 0; z < size; z++)
                    if (!occupied.Contains(new Vector3Int(x, y, z))) return new Vector3Int(x, y, z);
        throw new InvalidOperationException("No empty grid cell for a new voxel");
    }

    private static void SetUrl(string url)
    {
        var data = new SerializedObject(generator);
        data.FindProperty("generateUrl").stringValue = url;
        data.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void ValidateTriggerInput()
    {
        var hand = new GameObject("Validation Trigger Controller");
        var interactor = hand.AddComponent<QuestProTriggerInteractor>();
        interactor.enabled = false; // Feed deterministic tracking/trigger samples without hardware.
        var process = typeof(QuestProTriggerInteractor).GetMethod("ProcessInput",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Action<bool, float> sample = (tracked, trigger) => process.Invoke(interactor, new object[] { tracked, trigger });
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.position = new Vector3(20f, 2f, 20f);
        cube.transform.localScale = Vector3.one * 0.02f;
        var voxel = cube.AddComponent<QuestVoxelDraggable>();
        hand.transform.position = cube.transform.position - Vector3.forward * 0.3f;
        Physics.SyncTransforms();
        sample(true, 1f);
        Require(voxel.Owner == null, "A Trigger already held on tracking acquisition cannot grab accidentally");
        sample(true, 0f);
        sample(true, 1f);
        Require(voxel.Owner == hand.transform, "Trigger press selects the ray-hit individual voxel");
        Vector3 original = cube.transform.position;
        hand.transform.position += Vector3.right * 0.1f;
        sample(true, 1f);
        Require(Vector3.Distance(cube.transform.position, original + Vector3.right * 0.1f) < 0.0001f, "Held Trigger moves that voxel with the controller");
        sample(true, 0f);
        Require(voxel.Owner == null, "Trigger release drops the voxel in place");
        Physics.SyncTransforms();
        sample(true, 1f);
        Require(voxel.Owner == hand.transform, "Released voxel can be grabbed again");
        sample(false, 1f);
        Require(voxel.Owner == null, "Tracking or input-focus loss releases the held voxel");
        hand.transform.position = cube.transform.position + Vector3.right * 0.025f;
        hand.transform.rotation = Quaternion.LookRotation(Vector3.right);
        Physics.SyncTransforms();
        sample(true, 0f);
        sample(true, 1f);
        Require(voxel.Owner == hand.transform, "Near-controller Trigger grabbing works without a ray hit");
        sample(true, 0f);
        cube.SetActive(false);

        hand.transform.position = button.transform.position - button.transform.forward * 0.3f;
        hand.transform.rotation = button.transform.rotation;
        Physics.SyncTransforms();
        sample(true, 0f);
        sample(true, 1f);
        UnityEngine.Object.Destroy(cube);
        UnityEngine.Object.Destroy(hand);
    }

    private static void CaptureScene()
    {
        string path = SessionState.GetString(Key + ".Screenshot", "");
        if (string.IsNullOrEmpty(path)) return;
        var workspace = GameObject.Find("VR Model Workspace").transform;
        var cameraObject = new GameObject("Validation Preview Camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.transform.position = workspace.position + new Vector3(-0.25f, 0.75f, -1.5f);
        camera.transform.LookAt(workspace.position + new Vector3(-0.25f, 0.3f, 0f));
        camera.fieldOfView = 65f;
        var target = new RenderTexture(1600, 1000, 24);
        var image = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(image);
            UnityEngine.Object.Destroy(cameraObject);
        }
    }

    private static void Finish(int exitCode)
    {
        SessionState.SetBool(Key, false);
        EditorApplication.update -= Tick;
        EditorApplication.Exit(exitCode);
    }

    private static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
    }
}
