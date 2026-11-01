#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Mouse controls for the generation pipeline in Editor Play Mode without a headset.</summary>
public sealed class QuestDesktopPreviewControls : MonoBehaviour
{
    private ModelGenerationServerTest_Voxel generator;
    private Camera previewCamera;
    private Transform pointer;
    private QuestVoxelEditor heldEditor;
    private QuestVoxelEditor framedEditor;
    private QuestVoxelEditor eraseEditor;
    private Vector2 lastErasePosition;
    private int erasedInStroke;
    private float dragDistance;
    private Vector3 orbitTarget;
    private Vector2 rightPressPosition;
    private bool rightDragged;
    private Texture2D chosenImage;

    public void Initialize(ModelGenerationServerTest_Voxel owner, Camera camera)
    {
        generator = owner;
        previewCamera = camera;
        orbitTarget = camera.transform.position + camera.transform.forward * 1.5f;
        pointer = new GameObject("Desktop Voxel Pointer").transform;
        pointer.SetParent(transform, false);
    }

    private void OnDestroy()
    {
        if (heldEditor != null) heldEditor.End(pointer);
        FinishEraseStroke();
        if (chosenImage != null) Destroy(chosenImage);
    }

    private void OnGUI()
    {
        if (generator == null) return;
        float left = Mathf.Max(8f, Screen.width - 298f);
        GUILayout.BeginArea(new Rect(left, 8f, 290f, 300f), "DESKTOP CONTROLS", GUI.skin.window);
        GUILayout.Label(generator.UsesLocalModel ? "Local: bundled house; no backend" :
            "Backend: " + (generator.RequiresTrellis2 ? "TRELLIS.2 (GPU)" : "fixture (fixed house)"));
        GUI.enabled = !generator.IsRunning;
        if (!generator.UsesLocalModel)
        {
            if (GUILayout.Button("Choose input image (PNG/JPG)", GUILayout.Height(24f))) ChooseInputImage();
            GUILayout.Label("Image: " + generator.InputImageName);
        }
        if (GUILayout.Button(generator.UsesLocalModel ? (generator.IsRunning ? "Loading..." : "Load House") :
            (generator.IsRunning ? "Generating..." : "Get 3D Model"), GUILayout.Height(30f)))
            generator.UploadImageAndGenerate();
        GUI.enabled = generator.CanSubmitEdits;
        if (GUILayout.Button(generator.UsesLocalModel ? "Save Edits" : "Submit Edits", GUILayout.Height(26f))) generator.SubmitEdits();
        GUI.enabled = true;
        GUILayout.BeginHorizontal();
        foreach (QuestVoxelEditMode mode in System.Enum.GetValues(typeof(QuestVoxelEditMode)))
            if (GUILayout.Toggle(QuestVoxelEditor.Mode == mode, mode.ToString(), GUI.skin.button)) QuestVoxelEditor.Mode = mode;
        GUILayout.EndHorizontal();
        GUILayout.Label("Wheel: zoom | Middle drag: pan");
        GUILayout.Label("Right drag: orbit | Right click: select");
        GUILayout.Label("Left drag: Move/Fill/Add | Erase: click/drag");
        GUILayout.Label("C: clear selection | F: frame model");
        GUILayout.Label(generator.StatusMessage, GUI.skin.box);
        GUILayout.EndArea();
    }

    private void ChooseInputImage()
    {
        string path = EditorUtility.OpenFilePanel("Image for 3D generation", "", "png,jpg,jpeg");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!image.LoadImage(File.ReadAllBytes(path)))
            {
                Destroy(image);
                generator.ReportStatus("Could not read image. Choose a PNG or JPG.");
                return;
            }
            image.name = Path.GetFileName(path);
            generator.SetInputImage(image);
            if (chosenImage != null) Destroy(chosenImage);
            chosenImage = image;
        }
        catch (System.Exception exception)
        {
            generator.ReportStatus("Could not open image. Check Console.");
            Debug.LogException(exception);
        }
    }

    private void Update()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || previewCamera == null || pointer == null) return;
        QuestVoxelEditor editor = QuestVoxelEditor.Active;
        Vector2 position = mouse.position.ReadValue();
        if (editor != null && framedEditor != editor)
        {
            FrameModel(editor);
            framedEditor = editor;
        }
        if (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame && editor != null)
            FrameModel(editor);
        Navigate(mouse, position, editor);
        Ray ray = previewCamera.ScreenPointToRay(position);
        if (eraseEditor != null)
        {
            if (!mouse.leftButton.isPressed || eraseEditor != editor || generator.IsRunning ||
                QuestVoxelEditor.Mode != QuestVoxelEditMode.Erase)
                FinishEraseStroke();
            else if (OverControls(position))
                lastErasePosition = position;
            else
                EraseAlongStroke(position);
        }
        if (heldEditor != null)
        {
            if (mouse.leftButton.isPressed && heldEditor == editor)
            {
                if (QuestVoxelEditor.Mode == QuestVoxelEditMode.Add)
                {
                    SetPointerRay(ray);
                    heldEditor.UpdateAddStroke(pointer, 4f);
                }
                else
                {
                    pointer.position = ray.GetPoint(dragDistance);
                    heldEditor.UpdateDrag(pointer);
                }
            }
            if (!mouse.leftButton.isPressed || heldEditor != editor)
            {
                heldEditor.End(pointer);
                heldEditor = null;
            }
        }

        if (editor == null || generator.IsRunning) return;
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame) editor.ClearSelection();
        if (OverControls(position)) return;
        if (!mouse.leftButton.wasPressedThisFrame || heldEditor != null) return;
        if (QuestVoxelEditor.Mode == QuestVoxelEditMode.Erase)
        {
            eraseEditor = editor;
            lastErasePosition = position;
            erasedInStroke = 0;
            EraseAt(position);
            return;
        }
        if (!TryFindVoxel(ray, out GeneratedVoxel voxel, out RaycastHit hit)) return;
        if (QuestVoxelEditor.Mode == QuestVoxelEditMode.Add)
        {
            SetPointerRay(ray);
            if (!editor.BeginAddStroke(pointer)) return;
            editor.UpdateAddStroke(pointer, 4f);
        }
        else
        {
            dragDistance = hit.distance;
            pointer.position = ray.GetPoint(dragDistance);
            pointer.rotation = Quaternion.identity;
            if (!editor.BeginDrag(voxel, pointer)) return;
        }
        heldEditor = editor;
    }

    private void EraseAlongStroke(Vector2 position)
    {
        float distance = Vector2.Distance(lastErasePosition, position);
        if (distance < 4f) return;
        int steps = Mathf.Clamp(Mathf.CeilToInt(distance / 6f), 1, 32);
        Vector2 from = lastErasePosition;
        for (int i = 1; i <= steps; i++)
            EraseAt(Vector2.Lerp(from, position, i / (float)steps));
        lastErasePosition = position;
    }

    private void EraseAt(Vector2 screenPosition)
    {
        if (eraseEditor == null || OverControls(screenPosition)) return;
        Ray ray = previewCamera.ScreenPointToRay(screenPosition);
        if (TryFindVoxel(ray, out GeneratedVoxel voxel, out _) && eraseEditor.EraseSingle(voxel))
            erasedInStroke++;
    }

    private void FinishEraseStroke()
    {
        if (eraseEditor == null) return;
        if (erasedInStroke > 0)
            Debug.Log($"[Quest Pro Desktop] Erased {erasedInStroke} voxel(s) in one stroke.");
        eraseEditor = null;
        erasedInStroke = 0;
    }

    private void SetPointerRay(Ray ray)
    {
        pointer.position = ray.origin;
        pointer.rotation = Quaternion.LookRotation(ray.direction);
    }

    private void Navigate(Mouse mouse, Vector2 position, QuestVoxelEditor editor)
    {
        Vector2 delta = mouse.delta.ReadValue();
        if (mouse.rightButton.wasPressedThisFrame)
        {
            rightPressPosition = position;
            rightDragged = false;
        }
        if (mouse.rightButton.isPressed && !OverControls(position) &&
            (position - rightPressPosition).sqrMagnitude > 16f)
            rightDragged = true;
        if (mouse.rightButton.isPressed && rightDragged)
        {
            previewCamera.transform.RotateAround(orbitTarget, Vector3.up, delta.x * 0.2f);
            previewCamera.transform.RotateAround(orbitTarget, previewCamera.transform.right, -delta.y * 0.2f);
            previewCamera.transform.LookAt(orbitTarget);
        }
        if (mouse.rightButton.wasReleasedThisFrame && !rightDragged && editor != null && !OverControls(position) &&
            TryFindVoxel(previewCamera.ScreenPointToRay(position), out GeneratedVoxel selected, out _))
            editor.Select(selected);

        if (mouse.middleButton.isPressed && !OverControls(position))
        {
            float distance = Vector3.Distance(previewCamera.transform.position, orbitTarget);
            float unitsPerPixel = 2f * distance * Mathf.Tan(previewCamera.fieldOfView * Mathf.Deg2Rad * 0.5f) /
                Mathf.Max(1, Screen.height);
            Vector3 pan = (-previewCamera.transform.right * delta.x - previewCamera.transform.up * delta.y) * unitsPerPixel;
            previewCamera.transform.position += pan;
            orbitTarget += pan;
        }
        float wheel = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(wheel) > 0.01f && !OverControls(position))
        {
            Vector3 offset = previewCamera.transform.position - orbitTarget;
            float distance = Mathf.Clamp(offset.magnitude * Mathf.Exp(-wheel * 0.003f), 0.2f, 8f);
            previewCamera.transform.position = orbitTarget + offset.normalized * distance;
        }
    }

    private void FrameModel(QuestVoxelEditor editor)
    {
        Bounds bounds = new Bounds(editor.transform.position, Vector3.zero);
        bool hasRenderer = false;
        foreach (Renderer renderer in editor.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.enabled) continue;
            if (!hasRenderer) bounds = renderer.bounds;
            else bounds.Encapsulate(renderer.bounds);
            hasRenderer = true;
        }
        orbitTarget = bounds.center;
        float radius = hasRenderer ? Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z) : 0.4f;
        float distance = Mathf.Clamp(radius / Mathf.Tan(previewCamera.fieldOfView * Mathf.Deg2Rad * 0.5f) * 1.35f,
            0.55f, 4f);
        previewCamera.transform.position = orbitTarget - previewCamera.transform.forward * distance;
        previewCamera.transform.LookAt(orbitTarget);
    }

    private static bool OverControls(Vector2 position)
    {
        return position.x >= Mathf.Max(8f, Screen.width - 298f) && position.y >= Screen.height - 230f;
    }

    private static bool TryFindVoxel(Ray ray, out GeneratedVoxel voxel, out RaycastHit nearest)
    {
        voxel = null;
        nearest = default;
        float distance = float.PositiveInfinity;
        foreach (RaycastHit hit in Physics.RaycastAll(ray, 4f))
        {
            GeneratedVoxel candidate = hit.collider.GetComponentInParent<GeneratedVoxel>();
            if (candidate == null || hit.distance >= distance) continue;
            voxel = candidate;
            nearest = hit;
            distance = hit.distance;
        }
        return voxel != null;
    }
}
#endif
