using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

public static class QuestProProjectSetup
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/Quest Pro/Configure VR Voxel Scene")]
    public static void Configure()
    {
        string python = Argument("-questPython");
        if (!string.IsNullOrEmpty(python)) EditorPrefs.SetString(QuestProLocalTestServer.PythonPreference, python);
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play Mode before configuring the scene.");
        foreach (BuildTargetGroup target in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
        {
            var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(target);
            if (settings == null || settings.Manager == null)
                throw new InvalidOperationException("XR Management settings missing for " + target);
            settings.InitManagerOnStart = true;
            if (!XRPackageMetadataStore.AssignLoader(settings.Manager,
                "UnityEngine.XR.OpenXR.OpenXRLoader", target))
                throw new InvalidOperationException("Cannot assign OpenXR loader for " + target);
            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(settings.Manager);
            var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(target);
            openXR.GetFeature<OculusTouchControllerProfile>().enabled = true;
            openXR.GetFeature<MetaQuestTouchProControllerProfile>().enabled = true;
            EditorUtility.SetDirty(openXR.GetFeature<OculusTouchControllerProfile>());
            EditorUtility.SetDirty(openXR.GetFeature<MetaQuestTouchProControllerProfile>());
            EditorUtility.SetDirty(openXR);
        }

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var rig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
        var generator = UnityEngine.Object.FindFirstObjectByType<ModelGenerationServerTest_Voxel>();
        if (rig == null || generator == null) throw new InvalidOperationException("Existing rig or generator missing.");
        rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        var manager = new SerializedObject(rig.GetComponent<OVRManager>());
        manager.FindProperty("_trackingOriginType").intValue = (int)OVRManager.TrackingOrigin.FloorLevel;
        manager.ApplyModifiedPropertiesWithoutUndo();
        var serialized = new SerializedObject(generator);
        serialized.FindProperty("autoRunOnPlay").boolValue = false;
        serialized.FindProperty("makeVoxelsGrabbableForXR").boolValue = true;
        serialized.FindProperty("outputMode").enumValueIndex = (int)ModelGenerationServerTest_Voxel.OutputMode.Voxels;
        serialized.FindProperty("voxelSize").floatValue = 0.015f;
        serialized.FindProperty("voxelRootPosition").vector3Value = new Vector3(0f, 0.85f, 1.15f);
        serialized.FindProperty("requestTimeoutSeconds").intValue = 180;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        generator.enabled = true;
        var legacy = generator.GetComponent<ModelGenerationServerTest>();
        if (legacy != null) legacy.enabled = false;
        var experience = generator.GetComponent<QuestProVoxelExperience>();
        if (experience == null) experience = generator.gameObject.AddComponent<QuestProVoxelExperience>();
        var experienceData = new SerializedObject(experience);
        experienceData.FindProperty("cameraRig").objectReferenceValue = rig;
        experienceData.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        var buildScenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
        buildScenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = buildScenes.ToArray();
        PlayerSettings.runInBackground = true;
        AssetDatabase.SaveAssets();
        Debug.Log("[Quest Pro] Configured: OpenXR, Touch Pro, VR button and individual trigger dragging.");
    }

    // Batch entry: validates real Unity transforms, colliders, exclusive two-hand ownership,
    // release behaviour and the existing generator's JSON-to-voxel path without a headset.
    public static void ConfigureAndValidate()
    {
        Configure();
        var results = new System.Collections.Generic.List<string>();
        var sceneGenerator = UnityEngine.Object.FindFirstObjectByType<ModelGenerationServerTest_Voxel>();
        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        Check(settings.InitManagerOnStart && settings.Manager.activeLoaders.Any(l => l is OpenXRLoader), "Standalone OpenXR startup", results);
        var openXR = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        Check(openXR.GetFeature<MetaQuestTouchProControllerProfile>().enabled, "Touch Pro interaction profile", results);
        var serialized = new SerializedObject(sceneGenerator);
        Check(!serialized.FindProperty("autoRunOnPlay").boolValue && serialized.FindProperty("imageToUpload").objectReferenceValue != null,
            "Button-driven generation with existing input image", results);

        var fixture = new GameObject("Validation Fixture");
        var generator = fixture.AddComponent<ModelGenerationServerTest_Voxel>();
        var generatorData = new SerializedObject(generator);
        generatorData.FindProperty("makeVoxelsGrabbableForXR").boolValue = true;
        generatorData.FindProperty("maxVoxels").intValue = 2;
        generatorData.ApplyModifiedPropertiesWithoutUndo();
        generator.SetOutputParent(fixture.transform);
        var responseType = typeof(ModelGenerationServerTest_Voxel).GetNestedType("ServerResponse", BindingFlags.NonPublic);
        var response = JsonUtility.FromJson("{\"status\":\"done\",\"grid_size\":64,\"voxels\":[{\"x\":31,\"y\":5,\"z\":31,\"color\":\"#FF0000\"},{\"x\":32,\"y\":5,\"z\":31,\"color\":\"#00FF00\"},{\"x\":33,\"y\":5,\"z\":31,\"color\":\"#0000FF\"}]}", responseType);
        var build = (IEnumerator)typeof(ModelGenerationServerTest_Voxel).GetMethod("BuildVoxelsRoutine", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(generator, new[] { response });
        while (build.MoveNext()) { }
        var voxels = fixture.GetComponentsInChildren<QuestVoxelDraggable>();
        Check(voxels.Length == 2 && generator.GeneratedVoxelCount == 2, "JSON generation and client-side voxel limit", results);
        Check(voxels.All(v => v.GetComponent<Collider>() != null && v.GetComponent<Rigidbody>() == null), "Each voxel has a collider and lightweight drag component", results);

        var left = new GameObject("Test Left Controller").transform;
        var right = new GameObject("Test Right Controller").transform;
        fixture.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 35f, 0f));
        fixture.transform.localScale = Vector3.one * 1.3f;
        Vector3 original = voxels[0].transform.position;
        Vector3 sibling = voxels[1].transform.position;
        Check(voxels[0].TryGrab(left) && Vector3.Distance(original, voxels[0].transform.position) < 0.0001f, "Grab preserves position without snapping", results);
        Check(!voxels[0].TryGrab(right) && voxels[1].TryGrab(right), "Two hands can move different voxels but cannot steal the same voxel", results);
        left.position += new Vector3(0.2f, 0.3f, -0.1f);
        voxels[0].MoveWithController(left);
        Check(Vector3.Distance(voxels[0].transform.position, original + left.position) < 0.0001f && voxels[1].transform.position == sibling,
            "Individual translation under a rotated and scaled grid root", results);
        Vector3 expectedLocal = left.InverseTransformPoint(voxels[0].transform.position);
        left.rotation = Quaternion.Euler(0f, 45f, 0f);
        voxels[0].MoveWithController(left);
        Check(Vector3.Distance(voxels[0].transform.position, left.TransformPoint(expectedLocal)) < 0.0001f, "Controller rotation preserves grab offset", results);
        voxels[0].Release(left);
        Vector3 released = voxels[0].transform.position;
        left.position += Vector3.one;
        voxels[0].MoveWithController(left);
        Check(voxels[0].transform.position == released && voxels[0].Owner == null, "Release leaves voxel at its last position", results);
        UnityEngine.Object.DestroyImmediate(left.gameObject);
        UnityEngine.Object.DestroyImmediate(right.gameObject);
        UnityEngine.Object.DestroyImmediate(fixture);
        Debug.Log("[Quest Pro Validation] PASS\n" + string.Join("\n", results));
        string report = Argument("-questReport");
        if (!string.IsNullOrEmpty(report)) File.WriteAllLines(report, results);
    }

    private static void Check(bool valid, string description, System.Collections.Generic.List<string> results)
    {
        if (!valid) throw new InvalidOperationException("FAIL: " + description);
        results.Add("PASS: " + description);
    }

    private static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
