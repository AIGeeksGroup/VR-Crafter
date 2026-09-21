using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class QuestProPerformanceValidation
{
    public static void RunBatch()
    {
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var results = QuestProInputValidation.Validate();
        var root = new GameObject("Batch validation");
        try
        {
            var batch = root.AddComponent<QuestVoxelBatchRenderer>();
            batch.Initialize();
            for (int i = 0; i < 260; i++)
            {
                var obj = new GameObject("Voxel", typeof(BoxCollider), typeof(GeneratedVoxel), typeof(QuestVoxelDraggable));
                obj.transform.SetParent(root.transform, false);
                obj.transform.localPosition = new Vector3(i % 20, i / 20, 0) * 0.02f;
                obj.transform.localScale = Vector3.one * 0.015f;
                batch.Add(obj.GetComponent<GeneratedVoxel>(), i == 0 ? Color.red : Color.blue);
                typeof(QuestVoxelDraggable).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(obj.GetComponent<QuestVoxelDraggable>(), null);
            }
            batch.FlushDirty();
            Check(batch.ChunkCount == 3 && root.GetComponentsInChildren<MeshRenderer>().Length == 3, "260 voxels use three shared draw meshes", results);
            Check(root.GetComponentsInChildren<Collider>().Length == 260, "Every batched voxel retains an independent collider", results);
            int rebuilt = batch.RebuildCount;
            batch.FlushDirty();
            Check(batch.RebuildCount == rebuilt, "Idle frames do not rebuild voxel geometry", results);
            var meshes = root.GetComponentsInChildren<MeshFilter>();
            Vector3 firstVertex = meshes[0].sharedMesh.vertices[0];
            Vector3 siblingVertex = meshes[0].sharedMesh.vertices[24];
            Check(meshes[0].sharedMesh.colors[0] == Color.red && meshes[0].sharedMesh.colors[24] == Color.blue,
                "Per-voxel colors survive batching with a single shared material", results);
            root.transform.SetPositionAndRotation(new Vector3(1, 2, 3), Quaternion.Euler(0, 35, 0));
            root.transform.localScale = Vector3.one * 1.3f;
            var hand = new GameObject("Test hand");
            hand.transform.SetParent(root.transform, false);
            var voxel = root.GetComponentInChildren<QuestVoxelDraggable>();
            voxel.TryGrab(hand.transform);
            Vector3 delta = new Vector3(0.12f, 0.06f, -0.04f);
            hand.transform.localPosition += delta;
            voxel.MoveWithController(hand.transform);
            batch.FlushDirty();
            Check((meshes[0].sharedMesh.vertices[0] - firstVertex - delta).magnitude < 0.0001f &&
                meshes[0].sharedMesh.vertices[24] == siblingVertex, "Dragged voxel mesh moves under a rotated/scaled root without moving its neighbor", results);
            Check(batch.RebuildCount == rebuilt + 1, "A drag rebuilds only its affected chunk", results);
            voxel.Release(hand.transform);
            rebuilt = batch.RebuildCount;
            batch.FlushDirty();
            Check(batch.RebuildCount == rebuilt, "Released geometry stays cached", results);
            voxel.gameObject.SetActive(false);
            voxel.GetComponent<GeneratedVoxel>().NotifyRenderChanged();
            batch.FlushDirty();
            Check(meshes[0].sharedMesh.vertexCount == 127 * 24, "Disabled voxel is removed from the draw mesh", results);

            Vector3 Motion(Vector3 f, Vector2 r, float l) => QuestProLocomotion.CalculateMotion(f, r, l, 1.2f, 0.7f, 0.2f, 1f);
            Check(Motion(Vector3.forward, new Vector2(0.1f, 0.1f), 0.1f) == Vector3.zero, "Joystick deadzone prevents idle drift", results);
            Check((Motion(Vector3.right, Vector2.up, 0) - Vector3.right * 1.2f).magnitude < 0.001f, "Right stick forward follows head yaw", results);
            Check((Motion(new Vector3(0, 1, 1), Vector2.right, 0) - Vector3.right * 1.2f).magnitude < 0.001f,
                "Right stick strafe stays horizontal when looking up", results);
            Check(Mathf.Abs(Motion(Vector3.forward, Vector2.one, 0).magnitude - 1.2f) < 0.001f, "Diagonal movement is speed limited", results);
            Check(Motion(Vector3.forward, Vector2.zero, 1) == Vector3.up * 0.7f && Motion(Vector3.forward, Vector2.zero, -1) == Vector3.down * 0.7f,
                "Left stick up/down changes height in both directions", results);
            Check(Motion(Vector3.forward, Vector2.up, 1) == new Vector3(0, 0.7f, 1.2f), "Both joysticks can move horizontally and vertically together", results);
            Check(typeof(OVRManager).GetField("enableLegacyHaptics") != null, "Embedded Meta SDK exposes the legacy haptics processing switch", results);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-questPerformanceReport");
        if (index >= 0) File.WriteAllLines(args[index + 1], results);
        Debug.Log("[Quest Pro Performance Validation] PASS " + results.Count);
    }

    private static void Check(bool condition, string name, List<string> results)
    {
        if (!condition) throw new InvalidOperationException(name);
        results.Add("PASS: " + name);
    }
}
