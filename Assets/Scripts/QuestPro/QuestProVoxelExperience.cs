using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>World-space generation controls and controller rays for the existing OVRCameraRig.</summary>
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(ModelGenerationServerTest_Voxel))]
public sealed class QuestProVoxelExperience : MonoBehaviour
{
    [SerializeField] private OVRCameraRig cameraRig;
    private ModelGenerationServerTest_Voxel generator;
    private Transform workspace;
    private Button generateButton;
    private Button submitButton;
    private readonly QuestRayButton[] modeButtons = new QuestRayButton[4];
    private Text statusText;
    private Text buttonLabel;
    private Font font;
    private bool placedForHeadset;
    private Transform menu;
    private Text controllerStatus;
    private QuestProTriggerInteractor leftPointer;
    private QuestProTriggerInteractor rightPointer;
    private bool hasGeneratedModel;
    private float nextControllerStatusUpdate;
#if UNITY_EDITOR
    private Camera desktopPreviewCamera;
#endif

    private void Awake()
    {
        generator = GetComponent<ModelGenerationServerTest_Voxel>();
        if (cameraRig == null) cameraRig = FindFirstObjectByType<OVRCameraRig>();
        workspace = new GameObject("VR Model Workspace").transform;
        workspace.SetParent(transform, false);
        generator.SetOutputParent(workspace);
        if (GetComponent<QuestProFloor>() == null) gameObject.AddComponent<QuestProFloor>();
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#if UNITY_EDITOR
        gameObject.AddComponent<QuestProInputDiagnostics>();
#endif
        // Local dimming is a standalone-headset feature; this scene runs through PC Link.
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        if (cameraRig != null && cameraRig.TryGetComponent<OVRManager>(out var manager))
        {
            manager.localDimming = false;
            manager.enableLegacyHaptics = false;
        }
#endif
    }

    private IEnumerator Start()
    {
        if (cameraRig == null)
        {
            Debug.LogError("[Quest Pro] An OVRCameraRig is required.");
            enabled = false;
            yield break;
        }
#if UNITY_EDITOR
        CreateDesktopPreviewIfNeeded();
#endif
        // OVRCameraRig creates these anchors in Awake.
        CreateUI();
        leftPointer = AddInteractor("Left Quest Controller Pointer", OVRInput.Controller.LTouch);
        rightPointer = AddInteractor("Right Quest Controller Pointer", OVRInput.Controller.RTouch);
        var locomotion = cameraRig.gameObject.AddComponent<QuestProLocomotion>();
        locomotion.Initialize(cameraRig.centerEyeAnchor, leftPointer, rightPointer);
        PlaceWorkspace(false);
        PlaceMenu(new Vector3(0f, 1.55f, 0f), Quaternion.identity);
        while (!placedForHeadset)
        {
            if (OVRManager.isHmdPresent && cameraRig.centerEyeAnchor.position.y > cameraRig.transform.position.y + 0.2f)
            {
                // Let the first tracked head pose settle, rather than fixing the panel to
                // the editor rig's forward axis during the first initialization frame.
                yield return new WaitForSecondsRealtime(0.75f);
                // Never move content already being edited if the user connects Link late.
                if (generator.GeneratedVoxelCount == 0 && !generator.IsRunning) PlaceWorkspace(true);
                RecenterMenu();
                placedForHeadset = true;
            }
            yield return null;
        }
    }

    private void Update()
    {
#if UNITY_EDITOR
        if (desktopPreviewCamera != null && OVRManager.isHmdPresent)
        {
            if (menu != null) menu.GetComponent<Canvas>().worldCamera = cameraRig.centerEyeAnchor.GetComponent<Camera>();
            Destroy(desktopPreviewCamera.gameObject);
            desktopPreviewCamera = null;
        }
#endif
        if (generateButton == null) return;
        generateButton.interactable = !generator.IsRunning;
        buttonLabel.text = generator.UsesLocalModel
            ? (generator.IsRunning ? "Loading..." : "Load House")
            : (generator.IsRunning ? "Generating..." : "Get 3D Model");
        submitButton.interactable = generator.CanSubmitEdits;
        for (int i = 0; i < modeButtons.Length; i++) modeButtons[i].IsActive = i == (int)QuestVoxelEditor.Mode;
        statusText.text = generator.StatusMessage;
        if (Time.unscaledTime >= nextControllerStatusUpdate)
        {
            nextControllerStatusUpdate = Time.unscaledTime + 0.2f;
            controllerStatus.text = $"L: {leftPointer?.InputStatus}   R: {rightPointer?.InputStatus}\nRight stick: Move | Left stick: Height | B/Y: Menu";
        }
        if (!hasGeneratedModel && generator.GeneratedVoxelCount > 0)
        {
            // Keep the menu horizontally centered, with room to see the generated model.
            menu.position += Vector3.up * 0.32f;
            hasGeneratedModel = true;
        }
    }

    private void PlaceWorkspace(bool tracked)
    {
        Transform eye = cameraRig.centerEyeAnchor;
        Vector3 forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.1f) forward = cameraRig.transform.forward;
        Vector3 position = eye.position + forward * 1.15f;
        position.y = tracked ? Mathf.Max(cameraRig.transform.position.y + 0.45f, eye.position.y - 0.65f) : 0.85f;
        workspace.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
    }

    private QuestProTriggerInteractor AddInteractor(string name, OVRInput.Controller hand)
    {
        // Independent tracking-space children: OVR's grip/hand anchor update must not
        // overwrite the OpenXR aim pose used by the pointer.
        var pointer = new GameObject(name);
        pointer.transform.SetParent(cameraRig.trackingSpace, false);
        var interactor = pointer.AddComponent<QuestProTriggerInteractor>();
        interactor.Initialize(hand);
        interactor.RecenterRequested += RecenterMenu;
        return interactor;
    }

    [ContextMenu("Center VR Menu In View")]
    public void RecenterMenu()
    {
        if (menu == null || cameraRig == null || cameraRig.centerEyeAnchor == null) return;
        var eye = cameraRig.centerEyeAnchor;
        PlaceMenu(eye.position, eye.rotation);
    }

    private void PlaceMenu(Vector3 eyePosition, Quaternion eyeRotation)
    {
        Vector3 position = eyePosition + eyeRotation * Vector3.forward * 1.05f;
#if UNITY_EDITOR
        // Leave the model visible beside the controls when using the desktop preview.
        if (desktopPreviewCamera != null) position += eyeRotation * Vector3.left * 0.65f;
#endif
        menu.SetPositionAndRotation(position, eyeRotation);
    }

#if UNITY_EDITOR
    private void CreateDesktopPreviewIfNeeded()
    {
        // The Quest rig does not render a useful Game view when no OpenXR runtime is running.
        // Keep this camera out of batch tests and standalone headset builds.
        if (Application.isBatchMode || OVRManager.isHmdPresent) return;
        var preview = new GameObject("Desktop Preview Camera", typeof(Camera));
        desktopPreviewCamera = preview.GetComponent<Camera>();
        desktopPreviewCamera.clearFlags = CameraClearFlags.SolidColor;
        desktopPreviewCamera.backgroundColor = new Color(0.08f, 0.11f, 0.16f);
        desktopPreviewCamera.depth = 100f;
        desktopPreviewCamera.fieldOfView = 65f;
        desktopPreviewCamera.nearClipPlane = 0.01f;
        preview.transform.position = cameraRig.transform.position + new Vector3(0f, 1.4f, -0.5f);
        preview.transform.LookAt(cameraRig.transform.position + new Vector3(0f, 1.1f, 1.15f));
        preview.AddComponent<QuestDesktopPreviewControls>().Initialize(generator, desktopPreviewCamera);
        Debug.Log("[Quest Pro] Desktop preview camera active (no headset runtime). Use the Game view controls.");
    }
#endif

    private void CreateUI()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("VR UI EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        var canvasObject = new GameObject("VR Generation Panel", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        canvasObject.layer = 5;
        canvasObject.transform.SetParent(transform, false);
        menu = canvasObject.transform;
        canvasObject.transform.localScale = Vector3.one * 0.001f;
#if UNITY_EDITOR
        if (desktopPreviewCamera != null) canvasObject.transform.localScale = Vector3.one * 0.0015f;
#endif
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        if (cameraRig != null) canvas.worldCamera = cameraRig.centerEyeAnchor.GetComponent<Camera>();
#if UNITY_EDITOR
        if (desktopPreviewCamera != null) canvas.worldCamera = desktopPreviewCamera;
#endif
        canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(540f, 480f);

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasObject.transform, false);
        Stretch(background.GetComponent<RectTransform>());
        background.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.085f, 0.97f);
        background.GetComponent<Image>().raycastTarget = false;
        CreateText(canvasObject.transform, "Title", "3D MODEL WORKSPACE", new Vector2(0f, 195f), new Vector2(500f, 55f), 30);
        string instructions = "Grip: select   A/X: clear   Trigger: use mode\nMove/Fill/Erase drag the selection. Add places voxels.";
#if UNITY_EDITOR
        if (desktopPreviewCamera != null) instructions = "Desktop preview: use DESKTOP CONTROLS in the Game view.\nWheel zooms; right drag orbits; left drag edits voxels.";
#endif
        CreateText(canvasObject.transform, "Instructions", instructions,
            new Vector2(0f, 125f), new Vector2(520f, 70f), 20);

        for (int i = 0; i < modeButtons.Length; i++)
        {
            var mode = (QuestVoxelEditMode)i;
            modeButtons[i] = CreateButton(mode + " Mode", mode.ToString(), new Vector2(-195f + 130f * i, 45f), new Vector2(120f, 64f),
                () => QuestVoxelEditor.Mode = mode).Item1.GetComponent<QuestRayButton>();
        }
        string loadLabel = generator.UsesLocalModel ? "Load House" : "Get 3D Model";
        string saveLabel = generator.UsesLocalModel ? "Save Edits" : "Submit Edits";
        (generateButton, buttonLabel) = CreateButton(loadLabel, loadLabel, new Vector2(-120f, -45f), new Vector2(220f, 80f), generator.UploadImageAndGenerate);
        submitButton = CreateButton(saveLabel, saveLabel, new Vector2(120f, -45f), new Vector2(220f, 80f), generator.SubmitEdits).Item1;
        statusText = CreateText(canvasObject.transform, "Status", "Ready", new Vector2(0f, -140f), new Vector2(490f, 110f), 22);
        controllerStatus = CreateText(canvasObject.transform, "Controller Status", "Waiting for controllers", new Vector2(0f, -207f), new Vector2(525f, 45f), 18);
    }

    private (Button, Text) CreateButton(string name, string label, Vector2 position, Vector2 size, UnityAction onClick)
    {
        var buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.layer = 5;
        buttonObject.transform.SetParent(menu, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = buttonObject.GetComponent<Image>();
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(onClick);
        var collider = buttonObject.AddComponent<BoxCollider>();
        collider.size = new Vector3(size.x, size.y, 12f);
        buttonObject.AddComponent<QuestRayButton>();
        return (button, CreateText(buttonObject.transform, "Label", label, Vector2.zero, size - new Vector2(10f, 5f), 30));
    }

    private Text CreateText(Transform parent, string name, string value, Vector2 position, Vector2 size, int fontSize)
    {
        var obj = new GameObject(name, typeof(RectTransform), typeof(Text));
        obj.layer = 5;
        obj.transform.SetParent(parent, false);
        var rect = obj.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        var text = obj.GetComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
