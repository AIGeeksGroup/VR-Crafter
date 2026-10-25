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
    private QuestVoxelEditor editor;
    private QuestVoxelDraggable editTarget;
    private QuestVoxelEditMode editMode;
    private bool editing;
    private bool pressed;
    private bool armed;
    private bool gripHeld;
    private bool primaryPressed;
    private bool recenterPressed;
    private InputDevice inputDevice;
    private bool inputFocus = true;
    private string inputSource;
    public string InputStatus { get; private set; } = "Starting";
    public bool IsTracked { get; private set; }
    public float TriggerValue { get; private set; }
    public Vector2 JoystickValue { get; private set; }
    public event System.Action RecenterRequested;
    // Ray colors while dragging or placing, indexed by QuestVoxelEditMode; Move keeps the original drag orange.
    private static readonly Color[] ModeColors = { new(1f, 0.75f, 0.1f), new(0.3f, 1f, 0.4f), new(1f, 0.3f, 0.3f), new(0.55f, 0.45f, 1f) };

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
        ProcessSelectionInput(tracked, state.grip, state.primary);
        ProcessInput(tracked, TriggerValue);
    }

    // Grip uses the Trigger thresholds and adds ray-hit voxels to the shared selection; A/X clears it once per press.
    private void ProcessSelectionInput(bool tracked, float grip, bool primary)
    {
        var active = QuestVoxelEditor.Active;
        gripHeld = tracked && grip >= (gripHeld ? 0.45f : 0.65f);
        if (tracked && primary && !primaryPressed && active != null) active.ClearSelection();
        primaryPressed = tracked && primary;
        if (gripHeld && active != null && Physics.Raycast(transform.position, transform.forward, out RaycastHit hit,
            rayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            active.Select(hit.collider.GetComponentInParent<GeneratedVoxel>());
    }

    private void ProcessInput(bool tracked, float trigger)
    {
        if (!tracked)
        {
            ResetInteraction();
            return;
        }

        var active = QuestVoxelEditor.Active;
        // A rebuilt model replaces the editor; treat that like releasing the Trigger.
        if (editing && (editor == null || editor != active)) EndEdit();
        // A release is required after tracking/focus returns to avoid accidental grabbing.
        if (trigger <= 0.45f) armed = true;
        bool down = armed && trigger >= (pressed ? 0.45f : 0.65f);
        Vector3 end = transform.position + transform.forward * rayDistance;
        QuestVoxelDraggable candidate = null;
        QuestVoxelDraggable rayVoxel = null;
        QuestRayButton button = null;

        if (held == null && !(editing && editMode != QuestVoxelEditMode.Add))
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
            else if (QuestVoxelEditor.Mode == QuestVoxelEditMode.Add && active != null)
            {
                if (active.BeginAddStroke(transform)) StartEdit(active, null);
            }
            else if (EditorVoxel(candidate, active) is GeneratedVoxel voxel)
            {
                if (active.BeginDrag(voxel, transform)) StartEdit(active, candidate);
            }
            // A generated model's voxels are never free-dragged, also while it builds and no editor owns them yet.
            else if (candidate != null && !(candidate.TryGetComponent(out GeneratedVoxel model) && model.InGeneratedModel) && candidate.TryGrab(transform))
                held = candidate;
        }
        if (!down && pressed) Release();
        pressed = down;

        if (held != null)
        {
            held.MoveWithController(transform);
            end = held.transform.position;
        }
        else if (editing && editMode == QuestVoxelEditMode.Add) editor.UpdateAddStroke(transform, rayDistance);
        else if (editing)
        {
            editor.UpdateDrag(transform);
            if (editTarget != null) end = editTarget.transform.position;
        }
        else if (candidate != null) end = candidate.transform.position;
        SetHoveredVoxel(held != null ? held : editTarget != null ? editTarget : rayVoxel);
        if (active != null)
        {
            if (QuestVoxelEditor.Mode == QuestVoxelEditMode.Add && button == null) active.UpdatePreview(transform, rayDistance);
            else active.HidePreview(transform);
        }

        Color color = held != null ? ModeColors[0] : editing ? ModeColors[(int)editMode] : gripHeld ? QuestVoxelEditor.SelectedColor :
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

    // Generated voxels of the Active editor are edited on the grid; anything else keeps free dragging.
    private static GeneratedVoxel EditorVoxel(QuestVoxelDraggable candidate, QuestVoxelEditor active)
    {
        if (candidate == null || active == null) return null;
        var voxel = candidate.GetComponent<GeneratedVoxel>();
        return voxel != null && voxel.transform.parent == active.transform ? voxel : null;
    }

    private void StartEdit(QuestVoxelEditor active, QuestVoxelDraggable target)
    {
        editor = active;
        editTarget = target;
        editMode = QuestVoxelEditor.Mode;
        editing = true;
    }

    private void EndEdit()
    {
        if (editing && editor != null) editor.End(transform);
        editor = null;
        editTarget = null;
        editing = false;
    }

    private void Release()
    {
        if (held != null) held.Release(transform);
        held = null;
        EndEdit();
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
        if (QuestVoxelEditor.Active != null) QuestVoxelEditor.Active.HidePreview(transform);
        pressed = armed = gripHeld = false;
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
