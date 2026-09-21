using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using InputDevice = UnityEngine.InputSystem.InputDevice;
using CommonUsages = UnityEngine.InputSystem.CommonUsages;

/// <summary>Reads the device controls supplied by the enabled Unity OpenXR profiles.</summary>
public static class QuestProControllerInput
{
    public struct State
    {
        public Vector3 position;
        public Quaternion rotation;
        public float trigger;
        public bool recenter;
        public Vector2 joystick;
    }

    // Meta SDK 201 resets this map at SubsystemRegistration. Ensure it is enabled again
    // before OVRCameraRig polls its hand anchors, including when domain reload is disabled.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnableMetaTracking()
    {
        MetaQuestActionMap.Enable();
    }

    public static InputDevice FindController(bool left)
    {
        var usage = left ? CommonUsages.LeftHand : CommonUsages.RightHand;
        InputDevice untracked = null;
        foreach (var device in InputSystem.devices)
        {
            if (!(device is XRController) || !device.added || !device.enabled) continue;
            bool correctHand = false;
            foreach (var item in device.usages) if (item == usage) correctHand = true;
            if (!correctHand) continue;
            if (TryRead(device, out _)) return device;
            if (untracked == null) untracked = device;
        }
        return untracked;
    }

    public static bool TryRead(InputDevice device, out State state)
    {
        state = default;
        if (device == null || !device.added || !device.enabled) return false;
        // Prefer OpenXR's aim pose, not the grip orientation of an OVR hand anchor.
        // OpenXR 1.16 Touch/Touch Pro layouts name this control "pointer" (alias aimPose).
        // Some other layouts expose "pointerPose" instead.
        string pose = HasValidPose(device, "pointer") ? "pointer" :
            HasValidPose(device, "pointerPose") ? "pointerPose" : "devicePose";
        if (!HasValidPose(device, pose)) return false;
        var position = device.TryGetChildControl<Vector3Control>(pose + "/position");
        var rotation = device.TryGetChildControl<QuaternionControl>(pose + "/rotation");
        var trigger = device.TryGetChildControl<AxisControl>("trigger");
        if (position == null || rotation == null || trigger == null) return false;
        state.position = position.ReadValue();
        state.rotation = rotation.ReadValue();
        state.trigger = trigger.ReadValue();
        state.recenter = device.TryGetChildControl<ButtonControl>("secondaryButton")?.isPressed == true;
        state.joystick = device.TryGetChildControl<Vector2Control>("thumbstick")?.ReadValue() ?? Vector2.zero;
        return true;
    }

    // Unity's XR feature API reads the same OpenXR runtime without depending on
    // the Input System's generated device layout or its Editor focus routing.
    public static bool TryReadXRNode(bool left, out State state)
    {
        state = default;
        var device = InputDevices.GetDeviceAtXRNode(left ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid) return false;
        if (!device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trackingState, out InputTrackingState flags) ||
            (flags & (InputTrackingState.Position | InputTrackingState.Rotation)) != (InputTrackingState.Position | InputTrackingState.Rotation))
            return false;
        if (!device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out state.trigger)) return false;
        // OpenXR exports aim pose as PointerPosition / PointerRotation in XRSDK.
        if (!device.TryGetFeatureValue(new InputFeatureUsage<Vector3>("PointerPosition"), out state.position) ||
            !device.TryGetFeatureValue(new InputFeatureUsage<Quaternion>("PointerRotation"), out state.rotation))
        {
            if (!device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out state.position) ||
                !device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out state.rotation)) return false;
        }
        device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out state.recenter);
        device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out state.joystick);
        return true;
    }

    private static bool HasValidPose(InputDevice device, string pose)
    {
        var flags = device.TryGetChildControl<IntegerControl>(pose + "/trackingState");
        const int required = (int)(InputTrackingState.Position | InputTrackingState.Rotation);
        // isTracked describes active tracking quality, not pose-field validity.
        // The physical Quest Pro Link log reports isTracked=0 and trackingState=15
        // while aim position/rotation and Trigger keep updating. Accept the valid
        // pose fields; invalid flags, disconnect or lost input focus still release.
        return flags != null && (flags.ReadValue() & required) == required;
    }
}
