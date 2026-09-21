#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

/// <summary>Records real runtime input, never injected test events. Editor only.</summary>
[DefaultExecutionOrder(300)]
public sealed class QuestProInputDiagnostics : MonoBehaviour
{
    private string reportPath;
    private float nextSnapshot;
    private float stopAt;
    private float frameTimeSum;
    private float maxFrameTime;
    private int frameCount;
    private QuestProTriggerInteractor[] pointers;
    private ModelGenerationServerTest_Voxel generator;
    private readonly System.Collections.Generic.List<UnityEngine.XR.InputDevice> xrDevices = new();
    private static readonly string[] Controls = { "isTracked", "trackingState", "trigger", "secondaryButton",
        "pointer/isTracked", "pointer/trackingState", "pointer/position", "pointer/rotation",
        "devicePose/isTracked", "devicePose/trackingState", "devicePose/position" };

    private void Start()
    {
        reportPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "QuestProInputDiagnostics.log"));
        stopAt = Time.unscaledTime + 120f;
        generator = GetComponent<ModelGenerationServerTest_Voxel>();
        try { File.WriteAllText(reportPath, $"Quest Pro input diagnostics v4 — {DateTime.Now:O}\n"); }
        catch (IOException e) { Debug.LogWarning("[Quest Pro Input] Diagnostics unavailable: " + e.Message); enabled = false; return; }
        Debug.Log("[Quest Pro Input] v4 loaded (batched voxels + joystick). Input/performance recording for 120 seconds: " + reportPath);
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime > stopAt) { enabled = false; return; }
        frameTimeSum += Time.unscaledDeltaTime;
        maxFrameTime = Mathf.Max(maxFrameTime, Time.unscaledDeltaTime);
        frameCount++;
        if (Time.unscaledTime < nextSnapshot || Time.unscaledTime > stopAt) return;
        nextSnapshot = Time.unscaledTime + 3f;
        var b = new StringBuilder();
        b.AppendLine($"Performance: meanFrameMs={1000f * frameTimeSum / frameCount:0.00} maxFrameMs={1000f * maxFrameTime:0.00} voxels={generator?.GeneratedVoxelCount} drawBatches={generator?.VoxelDrawBatchCount}");
        frameTimeSum = maxFrameTime = 0f;
        frameCount = 0;
        b.AppendLine($"\n{DateTime.Now:O} appFocus={Application.isFocused} hmd={OVRManager.isHmdPresent} vrFocus={OVRManager.hasVrFocus} inputFocus={OVRManager.hasInputFocus} metaMap={MetaQuestActionMap.Instance.ActionMap.enabled}");
        b.AppendLine($"update={InputSystem.settings.updateMode} background={InputSystem.settings.backgroundBehavior} editorInput={InputSystem.settings.editorInputBehaviorInPlayMode} devices={InputSystem.devices.Count}");
        foreach (var d in InputSystem.devices)
        {
            b.AppendLine($"IS device={d.name} layout={d.layout} type={d.GetType().Name} enabled={d.enabled} usages={string.Join(",", d.usages)} lastUpdate={d.lastUpdateTime}");
            if (!(d is XRController)) continue;
            foreach (string path in Controls)
            {
                var c = d.TryGetChildControl<InputControl>(path);
                b.AppendLine($"  {path}={c?.ReadValueAsObject()} type={c?.GetType().Name} offset={c?.stateBlock.byteOffset}");
            }
            b.AppendLine($"  productionReader={QuestProControllerInput.TryRead(d, out _)}");
        }
        UnityEngine.XR.InputDevices.GetDevices(xrDevices);
        foreach (var d in xrDevices)
        {
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trackingState, out UnityEngine.XR.InputTrackingState flags);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out float trigger);
            d.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 position);
            b.AppendLine($"XR device={d.name} valid={d.isValid} characteristics={d.characteristics} tracked={tracked} flags={flags} trigger={trigger} position={position}");
        }
        foreach (var hand in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch })
            b.AppendLine($"OVR {hand} connected={OVRInput.IsControllerConnected(hand)} tracked={OVRInput.GetControllerPositionTracked(hand)} trigger={OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, hand)}");
        if (pointers == null || pointers.Length == 0) pointers = FindObjectsByType<QuestProTriggerInteractor>(FindObjectsSortMode.None);
        foreach (var pointer in pointers)
            if (pointer != null) b.AppendLine($"Pointer {pointer.name}: {pointer.InputStatus} trigger={pointer.TriggerValue} joystick={pointer.JoystickValue}");
        try { File.AppendAllText(reportPath, b.ToString()); }
        catch (IOException e) { Debug.LogWarning("[Quest Pro Input] Diagnostics stopped: " + e.Message); enabled = false; }
    }
}
#endif
