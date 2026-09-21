using UnityEngine;

/// <summary>A single independently movable voxel. Kept under its grid root when held.</summary>
[DisallowMultipleComponent]
public sealed class QuestVoxelDraggable : MonoBehaviour
{
    public Transform Owner { get; private set; }
    private Vector3 controllerLocalPosition;
    private Quaternion controllerLocalRotation;
    private GeneratedVoxel generated;
    private readonly System.Collections.Generic.HashSet<Transform> hoverSources = new();
    public static readonly Color HoverColor = new Color(0.1f, 1f, 1f, 1f);
    public bool IsHovered => hoverSources.Count > 0;
    private Renderer[] visualRenderers;
    private MaterialPropertyBlock[] originalBlocks;
    private MaterialPropertyBlock highlightBlock;
    private void Awake() => generated = GetComponent<GeneratedVoxel>();

    public void SetHover(Transform source, bool value)
    {
        if (source == null) return;
        bool wasHovered = IsHovered;
        if (value && isActiveAndEnabled) hoverSources.Add(source);
        else hoverSources.Remove(source);
        if (wasHovered != IsHovered) RefreshHighlight();
    }

    private void RefreshHighlight()
    {
        if (generated != null && generated.HasRenderBatch)
        {
            generated.SetRayHighlight(IsHovered);
            return;
        }
        // Individually rendered voxel/point prefabs share materials. Override the
        // hit object's color only, preserving its property block on hover exit.
        if (visualRenderers == null)
        {
            visualRenderers = GetComponentsInChildren<Renderer>();
            originalBlocks = new MaterialPropertyBlock[visualRenderers.Length];
            highlightBlock = new MaterialPropertyBlock();
            for (int i = 0; i < visualRenderers.Length; i++) originalBlocks[i] = new MaterialPropertyBlock();
        }
        for (int i = 0; i < visualRenderers.Length; i++)
        {
            var visual = visualRenderers[i];
            if (visual == null) continue;
            if (IsHovered)
            {
                visual.GetPropertyBlock(originalBlocks[i]);
                visual.GetPropertyBlock(highlightBlock);
                highlightBlock.SetColor("_Color", HoverColor);
                highlightBlock.SetColor("_BaseColor", HoverColor);
                visual.SetPropertyBlock(highlightBlock);
            }
            else visual.SetPropertyBlock(originalBlocks[i]);
        }
    }

    public bool TryGrab(Transform controller)
    {
        if (controller == null || Owner != null || !isActiveAndEnabled) return false;
        Owner = controller;
        // Preserve the hit distance and offset: grabbing never snaps a voxel into the hand.
        controllerLocalPosition = controller.InverseTransformPoint(transform.position);
        controllerLocalRotation = Quaternion.Inverse(controller.rotation) * transform.rotation;
        return true;
    }

    public void MoveWithController(Transform controller)
    {
        if (controller == null || Owner != controller) return;
        transform.SetPositionAndRotation(controller.TransformPoint(controllerLocalPosition),
            controller.rotation * controllerLocalRotation);
        if (generated != null) generated.NotifyRenderChanged();
    }

    public void Release(Transform controller)
    {
        if (Owner == controller) Owner = null;
    }

    private void OnDisable()
    {
        Owner = null;
        if (!IsHovered) return;
        hoverSources.Clear();
        RefreshHighlight();
    }
}
