using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Batch regression of the grid editor on a real generator build, with plain Transforms as hands.</summary>
public static class QuestVoxelEditorValidation
{
    private static ModelGenerationServerTest_Voxel generator;
    private static Transform root;
    private static QuestVoxelEditor editor;

    public static void RunBatch()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var results = new List<string>();
        var fixture = new GameObject("Voxel editor validation");
        var left = new GameObject("Validation Left Hand").transform;
        var right = new GameObject("Validation Right Hand").transform;
        var pointerObject = new GameObject("Validation Pointer");
        var otherPointerObject = new GameObject("Validation Other Pointer");
        try
        {
            // A rotated, scaled parent checks that every step is taken in voxel-root cells, not world units. Above 45 degrees
            // a hit normal left in world space has a different dominant axis, so the Add-on-a-face rows would fail.
            fixture.transform.SetPositionAndRotation(new Vector3(200f, 100f, 200f), Quaternion.Euler(0f, 60f, 0f));
            fixture.transform.localScale = Vector3.one * 1.3f;
            generator = fixture.AddComponent<ModelGenerationServerTest_Voxel>();
            SetSerialized("makeVoxelsGrabbableForXR", true);
            generator.SetOutputParent(fixture.transform);

            Check(IsTrail(Vector3Int.zero, QuestVoxelEditor.TrailSteps(Vector3Int.zero, new Vector3Int(2, 1, 0)), new Vector3Int(2, 1, 0)) &&
                QuestVoxelEditor.TrailSteps(Vector3Int.zero, new Vector3Int(2, 1, 0)).SequenceEqual(new[] { new Vector3Int(1, 0, 0), new Vector3Int(1, 1, 0), new Vector3Int(2, 1, 0) }) &&
                QuestVoxelEditor.TrailSteps(new Vector3Int(4, 5, 6), new Vector3Int(4, 5, 6)).Count == 0 &&
                QuestVoxelEditor.TrailSteps(Vector3Int.zero, -Vector3Int.one).SequenceEqual(new[] { new Vector3Int(-1, 0, 0), new Vector3Int(-1, -1, 0), -Vector3Int.one }) &&
                IsTrail(new Vector3Int(3, 9, 2), QuestVoxelEditor.TrailSteps(new Vector3Int(3, 9, 2), new Vector3Int(8, 6, -5)), new Vector3Int(8, 6, -5)),
                "TrailSteps shape", results);

            QuestVoxelEditor.Mode = QuestVoxelEditMode.Fill;
            Build((10, 5, 10, "#FF0000"), (10, 6, 10, "#00FF00"), (20, 5, 10, "#0000FF"), (5, 20, 5, "#FFFF00"));
            var first = editor;
            Build((10, 5, 10, "#FF0000"), (10, 6, 10, "#00FF00"), (20, 5, 10, "#0000FF"), (5, 20, 5, "#FFFF00"));
            Check(editor != null && first == null && QuestVoxelEditor.Active == editor && QuestVoxelEditor.Mode == QuestVoxelEditMode.Fill && editor.SelectionCount == 0,
                "Each build attaches a fresh Active editor and keeps the mode", results);
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Move;

            GeneratedVoxel a = At(10, 5, 10), b = At(10, 6, 10), c = At(20, 5, 10), d = At(5, 20, 5);
            var batch = root.GetComponent<QuestVoxelBatchRenderer>();
            var mesh = root.GetComponentInChildren<MeshFilter>().sharedMesh;
            editor.Select(a);
            editor.Select(b);
            editor.Select(a);
            batch.FlushDirty();
            bool selectedColor = mesh.colors[0] == Display(QuestVoxelEditor.SelectedColor) && mesh.colors[48] == Color.blue;
            a.SetRayHighlight(true);
            batch.FlushDirty();
            bool hoverWins = mesh.colors[0] == Display(QuestVoxelDraggable.HoverColor);
            a.SetRayHighlight(false);
            Check(editor.SelectionCount == 2 && a.IsSelected && b.IsSelected && !c.IsSelected && selectedColor && hoverWins,
                "Grip select: magenta selection, hover shown above selection", results);
            editor.ClearSelection();
            batch.FlushDirty();
            Check(editor.SelectionCount == 0 && !a.IsSelected && !b.IsSelected && mesh.colors[0] == Color.red, "A/X clear restores original colors", results);

            editor.Select(a);
            editor.Select(b);
            Check(editor.BeginDrag(a, left), "Move drag starts on a selected voxel", results);
            MoveHand(left, new Vector3(3f, 0f, 0f));
            editor.UpdateDrag(left);
            editor.End(left);
            Check(a.Cell == new Vector3Int(13, 5, 10) && b.Cell == new Vector3Int(13, 6, 10) && c.Cell == new Vector3Int(20, 5, 10) && d.Cell == new Vector3Int(5, 20, 5) &&
                generator.GeneratedVoxelCount == 4 && a.transform.localPosition == GeneratedVoxel.GridToLocal(a.Cell, 64, generator.VoxelSize) && !editor.IsBusy,
                "Move drags the whole selection", results);

            Check(editor.BeginDrag(d, left), "Unselected voxel drag starts", results);
            MoveHand(left, new Vector3(0f, 0f, 2f));
            editor.UpdateDrag(left);
            editor.End(left);
            Check(d.Cell == new Vector3Int(5, 20, 7) && a.Cell == new Vector3Int(13, 5, 10) && b.Cell == new Vector3Int(13, 6, 10) && editor.SelectionCount == 2,
                "Unselected voxel drags alone", results);

            SetRevisionsUrl("http://127.0.0.1:8000/validation");
            bool idleSubmit = generator.CanSubmitEdits;
            bool started = editor.BeginDrag(c, left);
            bool busySubmit = generator.CanSubmitEdits;
            bool secondHand = editor.BeginDrag(a, right);
            editor.End(right);
            bool stillBusy = editor.IsBusy;
            editor.End(left);
            Check(idleSubmit && started && !busySubmit && !secondHand && stillBusy && !editor.IsBusy && generator.CanSubmitEdits,
                "One drag at a time; Submit Edits waits for it", results);
            SetRevisionsUrl(null);

            // A running request (Get 3D Model or Submit Edits) rebuilds the voxels, so no edit may start meanwhile.
            var running = typeof(ModelGenerationServerTest_Voxel).GetField("isRunning", BindingFlags.NonPublic | BindingFlags.Instance);
            running.SetValue(generator, true);
            bool dragWhileRunning = editor.BeginDrag(c, left);
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Add;
            bool strokeWhileRunning = editor.BeginAddStroke(left);
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Move;
            editor.Select(c);
            bool selectWhileRunning = c.IsSelected;
            running.SetValue(generator, false);
            bool dragAfterwards = editor.BeginDrag(c, left);
            editor.End(left);
            Check(!dragWhileRunning && !strokeWhileRunning && selectWhileRunning && dragAfterwards && !editor.IsBusy,
                "No drag or Add stroke starts while a request runs; Grip selection still works", results);

            QuestVoxelEditor.Mode = QuestVoxelEditMode.Fill;
            Build((10, 5, 10, "#FF0000"), (10, 6, 10, "#00FF00"));
            a = At(10, 5, 10);
            Check(editor.BeginDrag(a, left), "Fill drag starts", results);
            MoveHand(left, new Vector3(4f, 0f, 0f));
            editor.UpdateDrag(left);
            editor.End(left);
            var copies = Voxels().Where(v => v.id == -1).ToList();
            Check(Enumerable.Range(10, 5).All(x => At(x, 5, 10) != null) && a.Cell == new Vector3Int(14, 5, 10) && copies.Count == 4 &&
                copies.All(v => v.color == "#FF0000" && v.voxelType == "occupied") && generator.GeneratedVoxelCount == 6,
                "Fast drag fills every intermediate cell", results);

            Build((10, 5, 10, "#FF0000"), (10, 6, 10, "#00FF00"));
            a = At(10, 5, 10);
            SetSerialized("maxVoxels", generator.GeneratedVoxelCount + 2);
            editor.BeginDrag(a, left);
            MoveHand(left, new Vector3(5f, 0f, 0f));
            editor.UpdateDrag(left);
            editor.End(left);
            Check(generator.GeneratedVoxelCount == 4 && Voxels().Count(v => v.id == -1) == 2 && a.Cell == new Vector3Int(15, 5, 10) &&
                generator.StatusMessage.Contains("Max Voxels reached"), "Fill stops at Max Voxels: " + generator.StatusMessage, results);
            SetSerialized("maxVoxels", 12000);

            QuestVoxelEditor.Mode = QuestVoxelEditMode.Erase;
            Build((10, 5, 10, "#FF0000"), (11, 5, 10, "#00FF00"), (12, 5, 10, "#00FF00"), (13, 5, 10, "#00FF00"), (10, 6, 10, "#0000FF"));
            a = At(10, 5, 10);
            editor.Select(At(12, 5, 10));
            editor.BeginDrag(a, left);
            MoveHand(left, new Vector3(4f, 0f, 0f));
            editor.UpdateDrag(left);
            editor.End(left);
            Check(At(11, 5, 10) == null && At(12, 5, 10) == null && At(13, 5, 10) == null && a.Cell == new Vector3Int(14, 5, 10) &&
                At(10, 6, 10) != null && generator.GeneratedVoxelCount == 2 && editor.SelectionCount == 0, "Erase clears the trail", results);

            QuestVoxelEditor.Mode = QuestVoxelEditMode.Fill;
            Build((1, 5, 10, "#FF0000"));
            a = At(1, 5, 10);
            editor.BeginDrag(a, left);
            MoveHand(left, new Vector3(-3f, 0f, 0f));
            editor.UpdateDrag(left);
            editor.End(left);
            Check(a.x == -2 && a.IsOutsideGrid && At(1, 5, 10) != null && At(0, 5, 10) != null && generator.GeneratedVoxelCount == 3 &&
                Voxels().Count(v => v.IsOutsideGrid) == 1, "Out-of-grid voxels are excluded: dragged voxel leaves the grid, Fill copies stay inside", results);

            // A teleporting hand is walked at most GridSize cells per update; the rest follows next frame.
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Move;
            Build((1, 5, 10, "#FF0000"));
            a = At(1, 5, 10);
            editor.BeginDrag(a, left);
            MoveHand(left, new Vector3(100f, 0f, 0f));
            editor.UpdateDrag(left);
            bool bounded = a.x == 1 + 64;
            editor.UpdateDrag(left);
            editor.End(left);
            Check(bounded && a.x == 101, "Large tracking jumps are spread over frames", results);

            QuestVoxelEditor.Mode = QuestVoxelEditMode.Add;
            Build((20, 5, 10, "#0000FF"));
            c = At(20, 5, 10);
            Aim(right, new Vector3Int(23, 5, 10), Vector3.left);
            bool dragRejected = !editor.BeginDrag(c, left);
            bool hasTarget = editor.TryGetAddTarget(right, 4f, out Vector3Int target, out GeneratedVoxel source);
            Check(dragRejected && hasTarget && target == new Vector3Int(21, 5, 10) && source == c, "Add mode rejects drags and targets the face the ray hits", results);
            editor.UpdatePreview(right, 4f);
            var preview = root.Find("Add Preview");
            Check(preview != null && preview.gameObject.activeSelf && preview.GetComponent<Collider>() == null &&
                preview.localPosition == GeneratedVoxel.GridToLocal(target, 64, generator.VoxelSize), "Add preview marks the target cell", results);
            Check(editor.BeginAddStroke(right), "Add stroke starts", results);
            editor.UpdateAddStroke(right, 4f);
            Physics.SyncTransforms();
            editor.UpdateAddStroke(right, 4f);
            editor.End(right);
            editor.HidePreview();
            var placed = At(21, 5, 10);
            Check(placed != null && placed.id == -1 && placed.color == "#0000FF" && At(22, 5, 10) == null && generator.GeneratedVoxelCount == 2 &&
                !preview.gameObject.activeSelf, "Add on a face", results);

            Aim(left, new Vector3Int(30, 30, 30), Vector3.up);
            Check(editor.BeginAddStroke(left) && !editor.BeginAddStroke(right), "One Add stroke at a time", results);
            editor.UpdateAddStroke(left, 4f);
            MoveHand(left, new Vector3(3f, 0f, 0f));
            Physics.SyncTransforms();
            editor.UpdateAddStroke(left, 4f);
            editor.End(left);
            Check(generator.GeneratedVoxelCount == 6 && Enumerable.Range(30, 4).All(x => At(x, 30, 30) != null && At(x, 30, 30).color == "#C8C8C8"),
                "Add in the air without gaps", results);

            int beforeLimit = generator.GeneratedVoxelCount;
            SetSerialized("maxVoxels", beforeLimit + 2);
            Aim(left, new Vector3Int(40, 40, 40), Vector3.up);
            editor.BeginAddStroke(left);
            editor.UpdateAddStroke(left, 4f);
            MoveHand(left, new Vector3(4f, 0f, 0f));
            Physics.SyncTransforms();
            editor.UpdateAddStroke(left, 4f);
            editor.UpdateAddStroke(left, 4f);
            editor.End(left);
            Check(generator.GeneratedVoxelCount == beforeLimit + 2 && At(41, 40, 40) != null && At(42, 40, 40) == null &&
                generator.StatusMessage.Contains("Max Voxels reached"), "Add stops at Max Voxels: " + generator.StatusMessage, results);
            SetSerialized("maxVoxels", 12000);

            // Rebuilding destroys the root that owns the Add preview; its teardown must not call Destroy in edit mode.
            var editModeDestroyErrors = new List<string>();
            Application.LogCallback onLog = (message, stack, type) =>
            {
                if (type == LogType.Error && message.Contains("edit mode")) editModeDestroyErrors.Add(message);
            };
            Application.logMessageReceived += onLog;
            try { Build((20, 5, 10, "#0000FF")); }
            finally { Application.logMessageReceived -= onLog; }
            Check(preview == null && editModeDestroyErrors.Count == 0, "Rebuilding after Add tears down the preview without edit-mode Destroy errors", results);

            // A 189-cell air line is laid at most GridSize cells per update and continues on the next ones without gaps.
            Aim(left, Vector3Int.zero, Vector3.up);
            editor.BeginAddStroke(left);
            editor.UpdateAddStroke(left, 4f);
            Aim(left, new Vector3Int(63, 63, 63), Vector3.up);
            var laid = new List<int>();
            for (int i = 0; i < 4; i++)
            {
                editor.UpdateAddStroke(left, 4f);
                Physics.SyncTransforms();
                laid.Add(generator.GeneratedVoxelCount - 1);
            }
            editor.End(left);
            Check(laid.SequenceEqual(new[] { 65, 129, 190, 190 }) && At(63, 63, 63) != null && At(0, 0, 0) != null,
                "Long air lines are laid over frames: " + string.Join(",", laid), results);

            // The production controller path: Trigger routing, Grip selection and A/X clear.
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Move;
            Build((10, 5, 10, "#FF0000"), (10, 6, 10, "#00FF00"));
            a = At(10, 5, 10);
            b = At(10, 6, 10);
            var pointer = pointerObject.AddComponent<QuestProTriggerInteractor>();
            Call(pointer, "Awake");
            var otherPointer = otherPointerObject.AddComponent<QuestProTriggerInteractor>();
            Call(otherPointer, "Awake");
            Aim(pointer.transform, new Vector3Int(10, 5, 7), Vector3.forward);
            Call(pointer, "ProcessInput", true, 0f);
            Call(pointer, "ProcessInput", true, 1f);
            bool routed = editor.IsBusy && a.GetComponent<QuestVoxelDraggable>().Owner == null;
            MoveHand(pointer.transform, new Vector3(2f, 0f, 0f));
            Call(pointer, "ProcessInput", true, 1f);
            Call(pointer, "ProcessInput", true, 0f);
            Check(routed && a.Cell == new Vector3Int(12, 5, 10) && !editor.IsBusy, "Trigger on an editor voxel drags it on the grid", results);
            Physics.SyncTransforms();
            Call(pointer, "ProcessInput", true, 1f);
            Aim(otherPointer.transform, new Vector3Int(10, 6, 7), Vector3.forward);
            Call(otherPointer, "ProcessInput", true, 0f);
            Call(otherPointer, "ProcessInput", true, 1f);
            MoveHand(otherPointer.transform, new Vector3(2f, 0f, 0f));
            Call(otherPointer, "ProcessInput", true, 1f);
            bool otherBlocked = b.GetComponent<QuestVoxelDraggable>().Owner == null && b.Cell == new Vector3Int(10, 6, 10) && editor.IsBusy;
            MoveHand(pointer.transform, new Vector3(1f, 0f, 0f));
            Call(pointer, "ProcessInput", true, 1f);
            Call(otherPointer, "ProcessInput", true, 0f);
            Call(pointer, "ProcessInput", true, 0f);
            Check(otherBlocked && a.Cell == new Vector3Int(13, 5, 10) && !editor.IsBusy, "Busy editor never free-drags: the other hand's Trigger leaves its voxel alone", results);
            Physics.SyncTransforms();
            Call(pointer, "ProcessSelectionInput", true, 0.8f, false);
            bool gripSelected = editor.SelectionCount == 1 && a.IsSelected;
            Call(pointer, "ProcessSelectionInput", true, 0f, true);
            Call(pointer, "ProcessSelectionInput", true, 0f, false);
            Check(gripSelected && editor.SelectionCount == 0, "Controller Grip selects the ray target and A/X clears", results);
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Add;
            Call(pointer, "ProcessInput", true, 1f);
            Call(pointer, "ProcessInput", true, 0f);
            Check(At(13, 5, 9) != null && !editor.IsBusy, "Controller Trigger in Add mode places on the hit face", results);
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Move;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.position = new Vector3(260f, 100f, 260f);
            cube.transform.localScale = Vector3.one * 0.04f;
            cube.AddComponent<GeneratedVoxel>();
            var plain = cube.AddComponent<QuestVoxelDraggable>();
            Call(plain, "Awake");
            pointer.transform.SetPositionAndRotation(cube.transform.position - Vector3.forward * 0.3f, Quaternion.identity);
            Physics.SyncTransforms();
            Call(pointer, "ProcessInput", true, 1f);
            bool freeGrab = plain.Owner == pointer.transform && !editor.IsBusy;
            Call(pointer, "ProcessInput", true, 0f);
            UnityEngine.Object.DestroyImmediate(cube);
            Check(freeGrab && plain.Owner == null, "Plain objects and loose GeneratedVoxels keep free dragging while an editor is Active", results);

            // While a model builds, its root has no editor yet; its voxels must still not be free-dragged.
            UnityEngine.Object.DestroyImmediate(editor);
            Aim(pointer.transform, new Vector3Int(13, 5, 6), Vector3.forward);
            Call(pointer, "ProcessInput", true, 0f);
            Call(pointer, "ProcessInput", true, 1f);
            bool notFreeDragged = QuestVoxelEditor.Active == null && Voxels().All(v => v.GetComponent<QuestVoxelDraggable>().Owner == null);
            Call(pointer, "ProcessInput", true, 0f);
            Check(notFreeDragged, "Voxels of a model without an editor (still building) are not free-dragged", results);

            // Submit Edits needs the editor of the current root. A build cut short has none, so its partial voxel set is never
            // sent (the server would delete every missing id); an editor still Active on another root does not count either.
            SetRevisionsUrl("http://127.0.0.1:8000/validation");
            var partial = StartBuild(Enumerable.Range(0, 300).Select(i => (i % 64, 5 + i / 64, 10, "#FF0000")).ToArray());
            partial.MoveNext();
            bool partialBlocked = generator.GeneratedVoxelCount < 300 && generator.VoxelRoot.GetComponent<QuestVoxelEditor>() == null &&
                !generator.CanSubmitEdits;
            while (partial.MoveNext()) { }
            bool builtAllowed = generator.GeneratedVoxelCount == 300 && generator.CanSubmitEdits;
            var stray = new GameObject("Validation Stray Editor").AddComponent<QuestVoxelEditor>();
            stray.Initialize(generator);
            bool strayBlocked = QuestVoxelEditor.Active == stray && !generator.CanSubmitEdits;
            UnityEngine.Object.DestroyImmediate(stray.gameObject);
            SetRevisionsUrl(null);
            Check(partialBlocked && builtAllowed && strayBlocked, "Submit Edits waits for the finished build's own editor", results);
        }
        finally
        {
            QuestVoxelEditor.Mode = QuestVoxelEditMode.Move;
            UnityEngine.Object.DestroyImmediate(pointerObject);
            UnityEngine.Object.DestroyImmediate(otherPointerObject);
            UnityEngine.Object.DestroyImmediate(left.gameObject);
            UnityEngine.Object.DestroyImmediate(right.gameObject);
            UnityEngine.Object.DestroyImmediate(fixture);
        }
        Debug.Log("[Quest Pro Voxel Editor Validation] PASS\n" + string.Join("\n", results));
        string report = Argument("-questEditorReport");
        if (!string.IsNullOrEmpty(report)) File.WriteAllLines(report, results);
    }

    // Rebuilds through the private BuildVoxelsRoutine exactly as ConfigureAndValidate does.
    private static void Build(params (int x, int y, int z, string color)[] voxels)
    {
        var build = StartBuild(voxels);
        while (build.MoveNext()) { }
        root = generator.VoxelRoot;
        editor = root.GetComponent<QuestVoxelEditor>();
        Physics.SyncTransforms();
    }

    // The build routine before its first step; each MoveNext builds one frame's slice.
    private static IEnumerator StartBuild((int x, int y, int z, string color)[] voxels)
    {
        string json = "{\"status\":\"done\",\"grid_size\":64,\"voxels\":[" + string.Join(",", voxels.Select((v, i) =>
            $"{{\"id\":{i},\"x\":{v.x},\"y\":{v.y},\"z\":{v.z},\"type\":\"occupied\",\"color\":\"{v.color}\"}}")) + "]}";
        var responseType = typeof(ModelGenerationServerTest_Voxel).GetNestedType("ServerResponse", BindingFlags.NonPublic);
        return (IEnumerator)typeof(ModelGenerationServerTest_Voxel).GetMethod("BuildVoxelsRoutine", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(generator, new[] { JsonUtility.FromJson(json, responseType) });
    }

    private static void SetRevisionsUrl(string url) =>
        typeof(ModelGenerationServerTest_Voxel).GetField("revisionsUrl", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(generator, url);

    private static GeneratedVoxel[] Voxels() => root.GetComponentsInChildren<GeneratedVoxel>();
    private static GeneratedVoxel At(int x, int y, int z) => Voxels().FirstOrDefault(v => v.Cell == new Vector3Int(x, y, z));
    private static Color Display(Color color) => QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;

    // Moves a hand by whole cells along the voxel root's axes.
    private static void MoveHand(Transform hand, Vector3 cells) => hand.position += root.TransformVector(cells * generator.VoxelSize);

    // Places a hand at a cell centre, pointing along a voxel-root axis; a hand aimed up sits AddTipDistance below its tip cell.
    private static void Aim(Transform hand, Vector3Int cell, Vector3 localDirection)
    {
        Vector3 forward = root.TransformDirection(localDirection);
        Vector3 position = root.TransformPoint(GeneratedVoxel.GridToLocal(cell, 64, generator.VoxelSize));
        if (localDirection == Vector3.up) position -= forward * QuestVoxelEditor.AddTipDistance;
        hand.SetPositionAndRotation(position, Quaternion.LookRotation(forward, localDirection == Vector3.up ? root.forward : root.up));
        Physics.SyncTransforms();
    }

    private static bool IsTrail(Vector3Int from, List<Vector3Int> steps, Vector3Int to)
    {
        Vector3Int previous = from;
        foreach (Vector3Int step in steps)
        {
            Vector3Int delta = step - previous;
            if (Mathf.Abs(delta.x) + Mathf.Abs(delta.y) + Mathf.Abs(delta.z) != 1) return false;
            previous = step;
        }
        Vector3Int total = to - from;
        return previous == to && steps.Count == Mathf.Abs(total.x) + Mathf.Abs(total.y) + Mathf.Abs(total.z);
    }

    private static void SetSerialized(string property, object value)
    {
        var data = new SerializedObject(generator);
        var field = data.FindProperty(property);
        if (value is bool flag) field.boolValue = flag;
        else field.intValue = (int)value;
        data.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void Call(object target, string name, params object[] values) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, values);

    private static void Check(bool valid, string description, List<string> results)
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
