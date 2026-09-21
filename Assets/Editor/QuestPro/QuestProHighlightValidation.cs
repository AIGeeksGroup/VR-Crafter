using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class QuestProHighlightValidation
{
    public static void RunBatch()
    {
        QuestProPerformanceValidation.RunBatch();
        var results = new List<string>();
        var root = new GameObject("Highlight validation");
        root.transform.position = new Vector3(30, 2, 30);
        Material pointMaterial = null;
        try
        {
            var batch = root.AddComponent<QuestVoxelBatchRenderer>();
            batch.Initialize();
            var voxel = MakeVoxel(root.transform, new Vector3(0, 0, 0.4f));
            var neighbor = MakeVoxel(root.transform, new Vector3(0.2f, 0, 0.4f));
            batch.Add(voxel.GetComponent<GeneratedVoxel>(), Color.red);
            batch.Add(neighbor.GetComponent<GeneratedVoxel>(), Color.blue);
            batch.FlushDirty();
            var mesh = root.GetComponentInChildren<MeshFilter>().sharedMesh;
            var a = MakePointer(root.transform, "Left");
            var b = MakePointer(root.transform, "Right");
            void Sample(QuestProTriggerInteractor p, bool tracked = true, float trigger = 0)
            {
                Physics.SyncTransforms();
                Call(p, "ProcessInput", tracked, trigger);
                batch.FlushDirty();
            }
            int rebuilds = batch.RebuildCount;
            int colorUpdates = batch.ColorUpdateCount;
            Sample(a);
            Color expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? QuestVoxelDraggable.HoverColor.linear : QuestVoxelDraggable.HoverColor;
            Check(voxel.IsHovered && mesh.colors[0] == expected && mesh.colors[24] == Color.blue,
                "Ray hit highlights only the hit voxel", results);
            Check(batch.RebuildCount == rebuilds && batch.ColorUpdateCount == colorUpdates + 1,
                "Hover updates colors without rebuilding geometry", results);
            colorUpdates = batch.ColorUpdateCount;
            Sample(a);
            Check(batch.ColorUpdateCount == colorUpdates, "Steady hover causes no repeated GPU color upload", results);
            Sample(b);
            Sample(a, false);
            Check(voxel.IsHovered, "One hand losing tracking does not clear the other hand's highlight", results);
            Sample(b, false);
            Check(!voxel.IsHovered && mesh.colors[0] == Color.red, "Last ray exit restores original voxel color", results);
            Sample(a);
            Sample(a, true, 1);
            Check(voxel.Owner == a.transform && voxel.IsHovered, "Trigger grab keeps the selected voxel highlighted", results);
            a.transform.localPosition += Vector3.right * 0.05f;
            Sample(a, true, 1);
            Check(voxel.IsHovered && mesh.colors[0] == expected, "Dragged geometry retains the highlight", results);
            Sample(a, false);
            Check(voxel.Owner == null && !voxel.IsHovered, "Tracking loss releases the voxel and clears highlight", results);
            a.transform.localPosition = new Vector3(0.2f, 0, 0);
            Sample(a);
            Check(neighbor.IsHovered && !voxel.IsHovered, "Moving the ray highlights the new target only", results);
            Call(a, "OnDisable");
            batch.FlushDirty();
            Check(!neighbor.IsHovered, "Disabling a controller clears its hover", results);

            // The separately rendered sphere path also represents point prefabs.
            var point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            point.transform.SetParent(root.transform, false);
            point.transform.localPosition = new Vector3(0.8f, 0, 0.4f);
            point.transform.localScale = Vector3.one * 0.05f;
            var pointRenderer = point.GetComponent<Renderer>();
            pointMaterial = new Material(Shader.Find("Standard"));
            pointMaterial.color = Color.magenta;
            pointRenderer.sharedMaterial = pointMaterial;
            var initial = new MaterialPropertyBlock();
            initial.SetColor("_Color", Color.green);
            initial.SetFloat("_Glossiness", 0.37f);
            pointRenderer.SetPropertyBlock(initial);
            var pointGrab = point.AddComponent<QuestVoxelDraggable>();
            Call(pointGrab, "Awake");
            b.transform.localPosition = new Vector3(0.8f, 0, 0);
            Sample(b);
            var read = new MaterialPropertyBlock();
            pointRenderer.GetPropertyBlock(read);
            Check(pointGrab.IsHovered && read.GetColor("_Color") == QuestVoxelDraggable.HoverColor,
                "Ray highlights an individually rendered point/sphere", results);
            Check(pointRenderer.sharedMaterial == pointMaterial && pointMaterial.color == Color.magenta,
                "Point highlighting neither clones nor mutates its shared material", results);
            Sample(b, false);
            pointRenderer.GetPropertyBlock(read);
            Check(read.GetColor("_Color") == Color.green && Mathf.Approximately(read.GetFloat("_Glossiness"), 0.37f),
                "Original point color and existing property block are restored", results);
            Sample(b);
            Call(pointGrab, "OnDisable");
            pointRenderer.GetPropertyBlock(read);
            Check(!pointGrab.IsHovered && read.GetColor("_Color") == Color.green, "Disabling a highlighted point restores its appearance", results);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            if (pointMaterial != null) UnityEngine.Object.DestroyImmediate(pointMaterial);
        }
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "-questHighlightReport");
        if (i >= 0) File.WriteAllLines(args[i + 1], results);
        Debug.Log("[Quest Pro Highlight Validation] PASS " + results.Count);
    }

    private static QuestVoxelDraggable MakeVoxel(Transform root, Vector3 position)
    {
        var obj = new GameObject("Voxel", typeof(BoxCollider), typeof(GeneratedVoxel), typeof(QuestVoxelDraggable));
        obj.transform.SetParent(root, false);
        obj.transform.localPosition = position;
        obj.transform.localScale = Vector3.one * 0.05f;
        var result = obj.GetComponent<QuestVoxelDraggable>();
        Call(result, "Awake");
        return result;
    }
    private static QuestProTriggerInteractor MakePointer(Transform root, string name)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(root, false);
        var result = obj.AddComponent<QuestProTriggerInteractor>();
        Call(result, "Awake");
        return result;
    }
    private static void Call(object o, string name, params object[] args) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(o, args);
    private static void Check(bool condition, string name, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS: " + name);
    }
}
