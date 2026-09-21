using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.OpenXR.Features.Interactions;
using InputDevice = UnityEngine.InputSystem.InputDevice;

/// <summary>Regresses the real OpenXR device-layout boundary, not just a synthetic trigger bool.</summary>
[InitializeOnLoad]
public static class QuestProInputValidation
{
    private static double nextCheck;
    static QuestProInputValidation() { EditorApplication.update += CheckRequest; }

    private static void CheckRequest()
    {
        if (EditorApplication.timeSinceStartup < nextCheck || EditorApplication.isCompiling) return;
        nextCheck = EditorApplication.timeSinceStartup + 1;
        string request = Path.Combine("Library", "QuestProInputValidation.request");
        if (!File.Exists(request) || EditorApplication.isPlayingOrWillChangePlaymode) return;
        string report = File.ReadAllText(request).Trim();
        File.Delete(request);
        try { File.WriteAllLines(report, Validate()); }
        catch (Exception e) { File.WriteAllText(report, "FAIL: " + e); Debug.LogException(e); }
    }

    [MenuItem("Tools/Quest Pro/Validate OpenXR Controller Input")]
    public static void ValidateFromMenu() => Debug.Log(string.Join("\n", Validate()));

    public static void RunBatch()
    {
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-questInputReport");
        var results = Validate();
        if (index >= 0) File.WriteAllLines(args[index + 1], results);
    }

    public static List<string> Validate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play before running this isolated input test.");
        var results = new List<string>();
        var previousScene = SceneManager.GetActiveScene();
        var testScene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
        var root = new GameObject("Input regression fixture");
        SceneManager.MoveGameObjectToScene(root, testScene);
        var devices = new List<InputDevice>();
        bool metaWasEnabled = MetaQuestActionMap.Instance.ActionMap.enabled;
        try
        {
            MetaQuestActionMap.Instance.ActionMap.Disable();
            InputSystem.RegisterLayout<UnityEngine.XR.OpenXR.Input.HapticControl>("Haptic");
            InputSystem.RegisterLayout(typeof(MetaQuestTouchProControllerProfile.QuestProTouchController).GetProperty("pointer").PropertyType, "Pose");
            // Unity's OpenXR layout builder assigns pose offsets from the runtime device
            // descriptor. Give synthetic devices distinct offsets as real devices receive.
            InputSystem.RegisterLayout<MetaQuestTouchProControllerProfile.QuestProTouchController>();
            InputSystem.RegisterLayout<OculusTouchControllerProfile.OculusTouchController>();
            foreach (string baseLayout in new[] { "QuestProTouchController", "OculusTouchController" })
                InputSystem.RegisterLayout("{\"name\":\"Validation" + baseLayout + "\",\"extend\":\"" + baseLayout +
                    "\",\"controls\":[{\"name\":\"pointer\",\"offset\":64},{\"name\":\"devicePose\",\"offset\":128},{\"name\":\"trigger\",\"offset\":0},{\"name\":\"secondaryButton\",\"offset\":4},{\"name\":\"thumbstick\",\"offset\":8}]}");
            foreach (bool pro in new[] { true, false })
            foreach (bool left in new[] { true, false })
            {
                InputDevice device = InputSystem.AddDevice(pro ? "ValidationQuestProTouchController" : "ValidationOculusTouchController");
                devices.Add(device);
                InputSystem.SetDeviceUsage(device, left ? UnityEngine.InputSystem.CommonUsages.LeftHand : UnityEngine.InputSystem.CommonUsages.RightHand);
                SetPose(device, "pointer", true, new Vector3(0.2f, 1.2f, 0.3f), Quaternion.Euler(5f, 20f, 0f));
                SetPose(device, "devicePose", true, new Vector3(-1f, 0f, 0f), Quaternion.identity);
                InputState.Change(device.GetChildControl<AxisControl>("trigger"), 0.8f);
                InputState.Change(device.GetChildControl<Vector2Control>("thumbstick"), new Vector2(0.6f, -0.8f));
                Debug.Log($"[Input regression] {device.layout} added={device.added} enabled={device.enabled} " +
                    $"tracked={device.GetChildControl<ButtonControl>("pointer/isTracked").ReadValue()} pressed={device.GetChildControl<ButtonControl>("pointer/isTracked").isPressed} " +
                    $"flags={device.GetChildControl<IntegerControl>("pointer/trackingState").ReadValue()} trigger={device.GetChildControl<AxisControl>("trigger").ReadValue()}");
                Check(QuestProControllerInput.TryRead(device, out var state) && Mathf.Approximately(state.trigger, 0.8f),
                    device.layout + (left ? " left" : " right") + ": tracked Trigger value works with Meta action map disabled", results);
                Check(state.position == new Vector3(0.2f, 1.2f, 0.3f) && Quaternion.Angle(state.rotation, Quaternion.Euler(5f, 20f, 0f)) < 0.01f,
                    "OpenXR aim pose is used instead of grip pose", results);
                Check((state.joystick - new Vector2(0.6f, -0.8f)).magnitude < 0.001f, "Joystick axes read correctly for " + device.layout + (left ? " left" : " right"), results);
                // Reproduce the user's physical Link session: isTracked=0 while
                // trackingState=15, a changing aim pose, and trigger=1 are received.
                SetPose(device, "devicePose", false, Vector3.zero, Quaternion.identity);
                InputState.Change(device.GetChildControl<UnityEngine.InputSystem.XR.PoseControl>("pointer"),
                    new UnityEngine.InputSystem.XR.PoseState(false, (UnityEngine.XR.InputTrackingState)15,
                        new Vector3(-0.14f, 0.79f, 0.04f), new Quaternion(-0.29976f, -0.53841f, -0.19938f, 0.76191f), Vector3.zero, Vector3.zero));
                InputState.Change(device.GetChildControl<AxisControl>("trigger"), 1f);
                Check(QuestProControllerInput.TryRead(device, out state) && state.trigger == 1f && state.position.y == 0.79f,
                    device.layout + (left ? " left" : " right") + ": physical Link valid pose with isTracked=false is accepted", results);
                foreach (var flags in new[] { UnityEngine.XR.InputTrackingState.Position, UnityEngine.XR.InputTrackingState.Rotation })
                {
                    InputState.Change(device.GetChildControl<UnityEngine.InputSystem.XR.PoseControl>("pointer"),
                        new UnityEngine.InputSystem.XR.PoseState(true, flags, Vector3.one, Quaternion.identity, Vector3.zero, Vector3.zero));
                    Check(!QuestProControllerInput.TryRead(device, out _),
                        device.layout + (left ? " left" : " right") + ": incomplete pose rejected even when isTracked=true (" + flags + ")", results);
                }
            }

            var active = devices[0];
            var pointerObject = new GameObject("Regression Pointer");
            pointerObject.transform.SetParent(root.transform);
            var pointer = pointerObject.AddComponent<QuestProTriggerInteractor>();
            Call(pointer, "Awake");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform);
            cube.transform.position = new Vector3(30f, 2f, 30f);
            cube.transform.localScale = Vector3.one * 0.04f;
            var voxel = cube.AddComponent<QuestVoxelDraggable>();
            root.transform.SetPositionAndRotation(new Vector3(2f, 0.5f, -3f), Quaternion.Euler(0f, 35f, 0f));
            Vector3 localPointer = root.transform.InverseTransformPoint(cube.transform.position) - Vector3.forward * 0.3f;
            Action<float, bool> sample = (trigger, tracked) =>
            {
                SetPose(active, "pointer", tracked, localPointer, Quaternion.identity);
                if (tracked)
                    InputState.Change(active.GetChildControl<UnityEngine.InputSystem.XR.PoseControl>("pointer"),
                        new UnityEngine.InputSystem.XR.PoseState(false, (UnityEngine.XR.InputTrackingState)15,
                            localPointer, Quaternion.identity, Vector3.zero, Vector3.zero));
                SetPose(active, "devicePose", false, Vector3.zero, Quaternion.identity);
                InputState.Change(active.GetChildControl<AxisControl>("trigger"), trigger);
                Physics.SyncTransforms();
                Call(pointer, "ReadAndProcessDevice", active);
            };
            sample(0f, true);
            sample(1f, true);
            Check(voxel.Owner == pointer.transform, "Actual OpenXR controls drive ray selection through the production reader", results);
            Vector3 original = cube.transform.position;
            localPointer += Vector3.right * 0.2f;
            sample(1f, true);
            Check(Vector3.Distance(cube.transform.position, original + root.transform.right * 0.2f) < 0.001f,
                "Tracked motion drags the voxel correctly under a rotated tracking origin", results);
            sample(0f, true);
            Check(voxel.Owner == null, "Actual Trigger release drops the voxel", results);
            sample(1f, true);
            sample(1f, false);
            Check(voxel.Owner == null && !pointer.IsTracked, "Tracking loss stops interaction and releases the voxel", results);
            sample(1f, true);
            Check(voxel.Owner == null, "Tracking recovery requires a fresh Trigger press", results);

            var buttonObject = new GameObject("Regression VR Button", typeof(RectTransform), typeof(Image), typeof(Button), typeof(BoxCollider), typeof(QuestRayButton));
            buttonObject.transform.SetParent(root.transform);
            buttonObject.transform.localPosition = localPointer + Vector3.forward * 0.2f;
            buttonObject.transform.localScale = Vector3.one * 0.08f;
            cube.SetActive(false);
            Call(buttonObject.GetComponent<QuestRayButton>(), "Awake");
            int clicks = 0;
            buttonObject.GetComponent<Button>().onClick.AddListener(() => clicks++);
            sample(0f, true);
            sample(1f, true);
            sample(1f, true);
            Check(clicks == 1, "OpenXR aim ray and Trigger click a world-space UI button once per press", results);

            foreach (var device in devices) InputSystem.RemoveDevice(device);
            devices.Clear();
            Check(!QuestProControllerInput.TryRead(active, out _), "Disconnected devices stop supplying stale input", results);
            var experienceObject = new GameObject("Menu placement fixture");
            experienceObject.transform.SetParent(root.transform);
            var experience = experienceObject.AddComponent<QuestProVoxelExperience>();
            var menu = new GameObject("Menu").transform;
            menu.SetParent(root.transform);
            typeof(QuestProVoxelExperience).GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(experience, menu);
            Vector3 eyePosition = new Vector3(1.5f, 1.7f, -2f);
            Quaternion eyeRotation = Quaternion.Euler(12f, 75f, 0f);
            Call(experience, "PlaceMenu", eyePosition, eyeRotation);
            Check(Vector3.Distance(menu.position, eyePosition + eyeRotation * Vector3.forward * 1.05f) < 0.001f && Quaternion.Angle(menu.rotation, eyeRotation) < 0.01f,
                "Menu is centered on the current head pose, including a rotated starting view", results);
        }
        finally
        {
            foreach (var device in devices) if (device.added) InputSystem.RemoveDevice(device);
            InputSystem.RemoveLayout("ValidationQuestProTouchController");
            InputSystem.RemoveLayout("ValidationOculusTouchController");
            if (metaWasEnabled) MetaQuestActionMap.Enable();
            UnityEngine.Object.DestroyImmediate(root);
            UnityEditor.SceneManagement.EditorSceneManager.CloseScene(testScene, true);
            SceneManager.SetActiveScene(previousScene);
        }
        Debug.Log("[Quest Pro Input Validation] PASS: " + results.Count + " checks");
        return results;
    }

    private static void SetPose(InputDevice device, string name, bool tracked, Vector3 position, Quaternion rotation)
    {
        var pose = device.GetChildControl<UnityEngine.InputSystem.XR.PoseControl>(name);
        InputState.Change(pose, new UnityEngine.InputSystem.XR.PoseState(tracked,
            tracked ? UnityEngine.XR.InputTrackingState.Position | UnityEngine.XR.InputTrackingState.Rotation : 0,
            position, rotation, Vector3.zero, Vector3.zero));
    }
    private static void Call(object target, string name, params object[] values) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, values);
    private static void Check(bool value, string description, List<string> results)
    {
        if (!value) throw new InvalidOperationException(description);
        results.Add("PASS: " + description);
    }
}
