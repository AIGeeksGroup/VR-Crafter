# Project compatibility patch (Meta XR Core SDK 201.0.0)

OVRManager.enableLegacyHaptics defaults to true, preserving SDK behavior. When false,
OVRManager.LateUpdate skips only the deprecated OVRHaptics.Process call. Other manager
updates, tracking, OpenXR input and modern haptic APIs remain unchanged.

The Quest Pro voxel scene disables this on Windows/Editor before OVRManager.Awake.
It does not use buffered OVRHaptics. This stops the per-frame failing
xrGetDeviceSampleRateFB descriptor query at its source instead of filtering logs.
Initialization warnings from other SDK call sites may still occur.

This embedded package retains the original license and package contents. When upgrading
Meta XR Core SDK, review whether this compatibility patch is still necessary.
