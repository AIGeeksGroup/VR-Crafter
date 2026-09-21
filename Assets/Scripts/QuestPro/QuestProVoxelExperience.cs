using System.Collections;
using UnityEngine;
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
        if (generateButton == null) return;
        generateButton.interactable = !generator.IsRunning;
        buttonLabel.text = generator.IsRunning ? "Generating..." : "Get 3D Model";
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
        menu.SetPositionAndRotation(eyePosition + eyeRotation * Vector3.forward * 1.05f, eyeRotation);
    }

    private void CreateUI()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
            new GameObject("VR UI EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

        var canvasObject = new GameObject("VR Generation Panel", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        canvasObject.layer = 5;
        canvasObject.transform.SetParent(transform, false);
        menu = canvasObject.transform;
        canvasObject.transform.localScale = Vector3.one * 0.001f;
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        if (cameraRig != null) canvas.worldCamera = cameraRig.centerEyeAnchor.GetComponent<Camera>();
        canvasObject.GetComponent<RectTransform>().sizeDelta = new Vector2(540f, 400f);

        var background = new GameObject("Background", typeof(RectTransform), typeof(Image));
        background.transform.SetParent(canvasObject.transform, false);
        Stretch(background.GetComponent<RectTransform>());
        background.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.085f, 0.97f);
        background.GetComponent<Image>().raycastTarget = false;
        CreateText(canvasObject.transform, "Title", "3D MODEL WORKSPACE", new Vector2(0f, 155f), new Vector2(500f, 55f), 30);
        CreateText(canvasObject.transform, "Instructions", "Point at one voxel and hold Trigger.\nMove your controller; release to place.",
            new Vector2(0f, 75f), new Vector2(500f, 85f), 24);

        var buttonObject = new GameObject("Get 3D Model", typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.layer = 5;
        buttonObject.transform.SetParent(canvasObject.transform, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(460f, 80f);
        rect.anchoredPosition = new Vector2(0f, -15f);
        generateButton = buttonObject.GetComponent<Button>();
        generateButton.targetGraphic = buttonObject.GetComponent<Image>();
        generateButton.transition = Selectable.Transition.None;
        generateButton.onClick.AddListener(generator.UploadImageAndGenerate);
        var collider = buttonObject.AddComponent<BoxCollider>();
        collider.size = new Vector3(460f, 80f, 12f);
        buttonObject.AddComponent<QuestRayButton>();
        buttonLabel = CreateText(buttonObject.transform, "Label", "Get 3D Model", Vector2.zero, new Vector2(450f, 75f), 30);
        statusText = CreateText(canvasObject.transform, "Status", "Ready", new Vector2(0f, -120f), new Vector2(490f, 110f), 22);
        controllerStatus = CreateText(canvasObject.transform, "Controller Status", "Waiting for controllers", new Vector2(0f, -177f), new Vector2(525f, 45f), 18);
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
