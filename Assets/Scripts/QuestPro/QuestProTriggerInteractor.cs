using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>OpenXR aim-pose and index-trigger selection for each Touch Pro controller.</summary>
[DefaultExecutionOrder(100)]
public sealed class QuestProTriggerInteractor : MonoBehaviour
{
    [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.RTouch;
    [SerializeField, Min(0.1f)] private float rayDistance = 4f;
    [SerializeField, Min(0f)] private float nearGrabRadius = 0.035f;
    private readonly Collider[] nearHits = new Collider[128];
    private LineRenderer line;
    private Material lineMaterial;
    private QuestVoxelDraggable held;
    private QuestRayButton hoveredButton;
    private QuestVoxelDraggable hoveredVoxel;
    private bool pressed;
    private bool armed;
    private bool recenterPressed;
    private InputDevice inputDevice;
    private bool inputFocus = true;
    private string inputSource;
    public string InputStatus { get; private set; } = "Starting";
    public bool IsTracked { get; private set; }
    public float TriggerValue { get; private set; }
    public Vector2 JoystickValue { get; private set; }
    public event System.Action RecenterRequested;

    public void Initialize(OVRInput.Controller hand) => controller = hand;

    private void Awake()
    {
        line = gameObject.AddComponent<LineRenderer>();
        lineMaterial = new Material(Shader.Find("Sprites/Default"));
        line.sharedMaterial = lineMaterial;
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.startWidth = 0.003f;
        line.endWidth = 0.0015f;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.enabled = false;
    }

    private void LateUpdate()
    {
        if (!QuestProControllerInput.TryRead(inputDevice, out _))
            inputDevice = QuestProControllerInput.FindController(controller == OVRInput.Controller.LTouch);
        // OpenXR supplies the device pose and trigger together. Do not gate the entire
        // interaction on OVRInput's separate action map or a second copy of tracking state.
        if (!QuestProControllerInput.TryRead(inputDevice, out _) &&
            QuestProControllerInput.TryReadXRNode(controller == OVRInput.Controller.LTouch, out var nativeState))
            ApplyControllerState(inputFocus, nativeState, "XR feature API");
        else ReadAndProcessDevice(inputDevice);
    }

    private void ReadAndProcessDevice(InputDevice device)
    {
        var state = default(QuestProControllerInput.State);
        bool tracked = inputFocus && QuestProControllerInput.TryRead(device, out state);
        ApplyControllerState(tracked, state, device?.layout ?? "No device");
    }

    private void ApplyControllerState(bool tracked, QuestProControllerInput.State state, string source)
    {
        // A change between aim/grip sources must not teleport a currently held voxel.
        if (tracked && inputSource != null && inputSource != source) ResetInteraction();
        if (tracked) inputSource = source;
        InputStatus = !inputFocus ? "No focus" : tracked ? "Ready" : source == "No device" ? "No device" : "No pose";
        if (IsTracked != tracked)
            Debug.Log($"[Quest Pro Input] {controller}: {(tracked ? "tracked " + source : InputStatus)}");
        IsTracked = tracked;
        TriggerValue = tracked ? state.trigger : 0f;
        JoystickValue = tracked ? state.joystick : Vector2.zero;
        if (tracked)
        {
            transform.localPosition = state.position;
            transform.localRotation = state.rotation;
            if (state.recenter && !recenterPressed) RecenterRequested?.Invoke();
        }
        recenterPressed = tracked && state.recenter;
        ProcessInput(tracked, TriggerValue);
    }

    private void ProcessInput(bool tracked, float trigger)
    {
        if (!tracked)
        {
            ResetInteraction();
            return;
        }

        // A release is required after tracking/focus returns to avoid accidental grabbing.
        if (trigger <= 0.45f) armed = true;
        bool down = armed && trigger >= (pressed ? 0.45f : 0.65f);
        Vector3 end = transform.position + transform.forward * rayDistance;
        QuestVoxelDraggable candidate = null;
        QuestVoxelDraggable rayVoxel = null;
        QuestRayButton button = null;

        if (held == null)
        {
            if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit,
                rayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                end = hit.point;
                button = hit.collider.GetComponentInParent<QuestRayButton>();
                candidate = hit.collider.GetComponentInParent<QuestVoxelDraggable>();
                if (candidate != null && !candidate.isActiveAndEnabled) candidate = null;
                rayVoxel = candidate;
            }

            // Prefer the visible ray target. Nearby grabbing remains available
            // when the ray does not hit a button or voxel.
            if (button == null && candidate == null)
            {
                var near = FindNearestVoxel();
                if (near != null) candidate = near;
            }
            if (candidate != null && candidate.Owner != null) candidate = null;
        }

        SetHoveredButton(button);
        if (down && !pressed)
        {
            if (button != null) button.Press();
            else if (candidate != null && candidate.TryGrab(transform)) held = candidate;
        }
        if (!down && pressed) Release();
        pressed = down;

        if (held != null)
        {
            held.MoveWithController(transform);
            end = held.transform.position;
        }
        else if (candidate != null) end = candidate.transform.position;
        SetHoveredVoxel(held != null ? held : rayVoxel);

        Color color = held != null ? new Color(1f, 0.75f, 0.1f) :
            candidate != null || button != null ? Color.cyan : new Color(0.4f, 0.65f, 0.8f, 0.65f);
        line.enabled = true;
        line.startColor = line.endColor = color;
        line.SetPosition(0, transform.position);
        line.SetPosition(1, end);
    }

    private QuestVoxelDraggable FindNearestVoxel()
    {
        int count = Physics.OverlapSphereNonAlloc(transform.position, nearGrabRadius, nearHits,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        QuestVoxelDraggable nearest = null;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            var voxel = nearHits[i].GetComponentInParent<QuestVoxelDraggable>();
            if (voxel == null || voxel.Owner != null) continue;
            float d = (voxel.transform.position - transform.position).sqrMagnitude;
            if (d >= distance) continue;
            nearest = voxel;
            distance = d;
        }
        return nearest;
    }

    private void SetHoveredButton(QuestRayButton button)
    {
        if (hoveredButton == button) return;
        if (hoveredButton != null) hoveredButton.SetHover(this, false);
        hoveredButton = button;
        if (hoveredButton != null) hoveredButton.SetHover(this, true);
    }

    private void Release()
    {
        if (held != null) held.Release(transform);
        held = null;
    }

    private void SetHoveredVoxel(QuestVoxelDraggable voxel)
    {
        if (hoveredVoxel != voxel)
        {
            if (hoveredVoxel != null) hoveredVoxel.SetHover(transform, false);
            hoveredVoxel = voxel;
        }
        // SetHover is idempotent, also restoring hover if an object was re-enabled.
        if (hoveredVoxel != null) hoveredVoxel.SetHover(transform, true);
    }

    private void ResetInteraction()
    {
        Release();
        SetHoveredButton(null);
        SetHoveredVoxel(null);
        pressed = armed = false;
        if (line != null) line.enabled = false;
    }

    private void OnDisable()
    {
        OVRManager.InputFocusAcquired -= OnInputFocusAcquired;
        OVRManager.InputFocusLost -= OnInputFocusLost;
        ResetInteraction();
        IsTracked = false;
        TriggerValue = 0f;
        JoystickValue = Vector2.zero;
        recenterPressed = false;
        inputSource = null;
        InputStatus = "Disabled";
    }
    private void OnEnable()
    {
        inputFocus = true;
        OVRManager.InputFocusAcquired += OnInputFocusAcquired;
        OVRManager.InputFocusLost += OnInputFocusLost;
    }
    private void OnInputFocusAcquired() => inputFocus = true;
    private void OnInputFocusLost() { inputFocus = false; ResetInteraction(); }
    private void OnApplicationFocus(bool focus) { if (!focus) ResetInteraction(); }
    private void OnDestroy() { if (lineMaterial != null) Destroy(lineMaterial); }
}
