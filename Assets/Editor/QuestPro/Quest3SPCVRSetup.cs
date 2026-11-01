using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

/// <summary>Windows OpenXR configuration for the offline Quest 3S Link test.</summary>
public static class Quest3SPCVRSetup
{
    private const string LoaderName = "UnityEngine.XR.OpenXR.OpenXRLoader";

    [MenuItem("VR-Crafter/PC VR/Switch editor to Windows")]
    public static void SwitchEditorToWindows()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play Mode before switching build targets.");
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64) return;
        if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            throw new InvalidOperationException("Unity could not switch the editor to Windows.");
    }

    [MenuItem("VR-Crafter/PC VR/Enable Quest 3S Link profiles")]
    public static void EnableLinkProfiles()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play Mode before changing XR settings.");

        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (settings == null || settings.Manager == null)
            throw new InvalidOperationException("Standalone XR Management settings are missing.");

        settings.InitManagerOnStart = true;
        if (!XRPackageMetadataStore.AssignLoader(settings.Manager, LoaderName, BuildTargetGroup.Standalone))
            throw new InvalidOperationException("The Windows OpenXR loader could not be assigned.");

        var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        if (openXR == null) throw new InvalidOperationException("Standalone OpenXR settings are missing.");
        Enable<OculusTouchControllerProfile>(openXR);
        Enable<MetaQuestTouchPlusControllerProfile>(openXR);

        EditorUtility.SetDirty(settings);
        EditorUtility.SetDirty(settings.Manager);
        EditorUtility.SetDirty(openXR);
        AssetDatabase.SaveAssets();
        ValidateOfflinePCVR();
    }

    [MenuItem("VR-Crafter/PC VR/Validate offline PC VR configuration")]
    public static void ValidateOfflinePCVR()
    {
        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        Require(settings != null && settings.Manager != null && settings.InitManagerOnStart,
            "Windows XR auto-initialization is disabled.");
        Require(settings.Manager.activeLoaders.Any(loader => loader is OpenXRLoader),
            "The Windows OpenXR loader is missing.");
        Require(openXR != null && openXR.GetFeature<OculusTouchControllerProfile>()?.enabled == true,
            "The Oculus Touch profile is disabled for Windows.");
        Require(openXR.GetFeature<MetaQuestTouchPlusControllerProfile>()?.enabled == true,
            "The Quest 3S Touch Plus profile is disabled for Windows.");

        string scenePath = Path.Combine(Application.dataPath, "Scenes", "SampleScene.unity");
        Require(File.Exists(scenePath), "SampleScene.unity is missing.");
        string scene = File.ReadAllText(scenePath);
        Require(scene.Contains("loadLocalMesh: 1") && scene.Contains("generateUrl: ''"),
            "SampleScene is not configured for offline loading.");
        Require(scene.Contains("voxelShape: 3") && scene.Contains("outputMode: 1"),
            "SampleScene needs Cube voxels and voxel-only output.");
        Require(Resources.Load<TextAsset>("PropertyDemoVoxels") != null,
            "The bundled PropertyDemoVoxels.json cache is missing.");

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            Debug.LogWarning("[Quest 3S PC VR] Switch the active build target to Windows, Mac, Linux > Windows before pressing Play through Link.");
        Debug.Log("[Quest 3S PC VR] Configuration PASS: Windows OpenXR, Touch/Touch Plus, offline house cache. " +
            "A headset Link session and live controller tracking still need a Play-mode check.");
    }

    private static void Enable<T>(OpenXRSettings settings) where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
    {
        var feature = settings.GetFeature<T>();
        if (feature == null) throw new InvalidOperationException("Missing OpenXR feature " + typeof(T).Name);
        feature.enabled = true;
        EditorUtility.SetDirty(feature);
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException("[Quest 3S PC VR] " + failure);
    }
}
