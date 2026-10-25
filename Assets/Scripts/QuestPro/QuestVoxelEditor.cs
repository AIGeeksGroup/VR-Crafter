using System.Collections.Generic;
using UnityEngine;

public enum QuestVoxelEditMode { Move, Fill, Erase, Add }

/// <summary>
/// Grid-stepped editing of one generated voxel model: a shared selection, Move/Fill/Erase drags and Add strokes.
/// Lives on the voxel root and is replaced with it on every rebuild; only one drag or stroke runs at a time,
/// and none starts while the generator's request runs, since its rebuild would discard the edit.
/// </summary>
[DisallowMultipleComponent]
public sealed class QuestVoxelEditor : MonoBehaviour
{
    public static QuestVoxelEditMode Mode = QuestVoxelEditMode.Move;
    public static QuestVoxelEditor Active { get; private set; }
    public static readonly Color SelectedColor = new(1f, 0.35f, 0.9f);
    public const float AddTipDistance = 0.05f;
    private const string DefaultType = "occupied";
    private const string DefaultColor = "#C8C8C8";
    private ModelGenerationServerTest_Voxel owner;
    private readonly Dictionary<Vector3Int, List<GeneratedVoxel>> cells = new();
    private readonly HashSet<GeneratedVoxel> selection = new();
    private readonly List<GeneratedVoxel> group = new();
    private readonly HashSet<GeneratedVoxel> groupSet = new();
    private readonly List<Vector3Int> leftCells = new();
    private readonly List<GeneratedVoxel> erased = new();
    private readonly HashSet<GeneratedVoxel> stroke = new();
    private readonly RaycastHit[] hits = new RaycastHit[256];
    private readonly List<(Transform hand, Renderer cube, Material material)> previews = new();
    private Transform busyHand;
    private bool adding;
    private bool limitReached;
    private QuestVoxelEditMode dragMode;
    private Vector3 handOffset;
    private Vector3 grabOrigin;
    private Vector3Int dragOffset;
    private bool hasAirCell;
    private Vector3Int lastAirCell;
    public int SelectionCount => selection.Count;
    public bool IsBusy => busyHand != null;

    public void Initialize(ModelGenerationServerTest_Voxel generator)
    {
        owner = generator;
        cells.Clear();
        selection.Clear();
        foreach (GeneratedVoxel voxel in GetComponentsInChildren<GeneratedVoxel>()) Occupy(voxel);
        Active = this;
    }

    // The previous root is destroyed at the end of the frame, after the next model's editor became Active.
    private void OnDestroy()
    {
        if (Active == this) Active = null;
        foreach (var preview in previews) if (preview.material != null) Destroy(preview.material);
    }

    public void Select(GeneratedVoxel voxel)
    {
        if (Owns(voxel) && selection.Add(voxel)) voxel.SetSelected(true);
    }

    public void ClearSelection()
    {
        foreach (GeneratedVoxel voxel in selection) if (voxel != null) voxel.SetSelected(false);
        selection.Clear();
    }

    // Grabbing a selected voxel drags the whole selection, any other voxel drags alone.
    public bool BeginDrag(GeneratedVoxel grabbed, Transform hand)
    {
        if (IsBusy || Mode == QuestVoxelEditMode.Add || owner == null || owner.IsRunning || hand == null || !Owns(grabbed)) return false;
        group.Clear();
        if (!selection.Contains(grabbed)) group.Add(grabbed);
        else foreach (GeneratedVoxel voxel in selection) if (Owns(voxel)) group.Add(voxel);
        groupSet.Clear();
        groupSet.UnionWith(group);
        busyHand = hand;
        adding = limitReached = false;
        dragMode = Mode;
        // The grab point keeps its offset from the hand; the group follows in whole cells.
        handOffset = hand.InverseTransformPoint(grabbed.transform.position);
        grabOrigin = transform.InverseTransformPoint(grabbed.transform.position);
        dragOffset = Vector3Int.zero;
        return true;
    }

    public void UpdateDrag(Transform hand)
    {
        if (!IsBusy || adding || hand != busyHand) return;
        for (int i = group.Count - 1; i >= 0; i--) if (group[i] == null) group.RemoveAt(i);
        Vector3Int target = Vector3Int.RoundToInt((transform.InverseTransformPoint(hand.TransformPoint(handOffset)) - grabOrigin) / owner.VoxelSize);
        if (target == dragOffset) return;
        // At most GridSize cells per frame, so a tracking jump is walked over the next frames instead of stalling one.
        List<Vector3Int> steps = TrailSteps(dragOffset, target);
        for (int i = 0; i < steps.Count && i < owner.GridSize; i++)
        {
            Step(steps[i] - dragOffset);
            dragOffset = steps[i];
        }
    }

    public bool BeginAddStroke(Transform hand)
    {
        if (IsBusy || Mode != QuestVoxelEditMode.Add || owner == null || owner.IsRunning || hand == null) return false;
        busyHand = hand;
        adding = true;
        limitReached = hasAirCell = false;
        stroke.Clear();
        return true;
    }

    // Voxels placed in this stroke are never a placement source, so sweeping a surface adds a single layer.
    public void UpdateAddStroke(Transform hand, float rayDistance)
    {
        if (!IsBusy || !adding || hand != busyHand || limitReached || !TryGetAddTarget(hand, rayDistance, out Vector3Int cell, out GeneratedVoxel source)) return;
        if (source != null)
        {
            hasAirCell = false;
            TryAdd(cell, source.voxelType, source.color);
            return;
        }
        if (hasAirCell && cell != lastAirCell)
        {
            // At most GridSize cells per frame, like UpdateDrag; the rest of a long line continues next frame.
            List<Vector3Int> steps = TrailSteps(lastAirCell, cell);
            int count = Mathf.Min(steps.Count, owner.GridSize);
            for (int i = 0; i < count; i++) if (!TryAdd(steps[i], DefaultType, DefaultColor)) break;
            cell = steps[count - 1];
        }
        else TryAdd(cell, DefaultType, DefaultColor);
        hasAirCell = true;
        lastAirCell = cell;
    }

    // A ray hit on a voxel face targets the neighbouring cell with that voxel as source; otherwise the cell at the hand tip.
    public bool TryGetAddTarget(Transform hand, float rayDistance, out Vector3Int cell, out GeneratedVoxel source)
    {
        cell = default;
        source = null;
        if (hand == null || owner == null) return false;
        int count = Physics.RaycastNonAlloc(hand.position, hand.forward, hits, rayDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
        float nearest = float.PositiveInfinity;
        Vector3 normal = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            if (hits[i].distance >= nearest) continue;
            var voxel = hits[i].collider.GetComponentInParent<GeneratedVoxel>();
            if (!Owns(voxel) || stroke.Contains(voxel)) continue;
            nearest = hits[i].distance;
            normal = transform.InverseTransformDirection(hits[i].normal);
            source = voxel;
        }
        if (source == null)
        {
            cell = GeneratedVoxel.LocalToCell(transform.InverseTransformPoint(hand.position + hand.forward * AddTipDistance), owner.GridSize, owner.VoxelSize);
            return true;
        }
        Vector3 abs = new(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z));
        cell = source.Cell + (abs.x >= abs.y && abs.x >= abs.z ? new Vector3Int(normal.x > 0f ? 1 : -1, 0, 0) :
            abs.y >= abs.z ? new Vector3Int(0, normal.y > 0f ? 1 : -1, 0) : new Vector3Int(0, 0, normal.z > 0f ? 1 : -1));
        return true;
    }

    // One translucent cube per hand, shown only where Add would place a voxel.
    public void UpdatePreview(Transform hand, float rayDistance)
    {
        if (Mode != QuestVoxelEditMode.Add || !TryGetAddTarget(hand, rayDistance, out Vector3Int cell, out GeneratedVoxel source) || !CanPlace(cell))
        {
            HidePreview(hand);
            return;
        }
        var preview = PreviewFor(hand);
        preview.cube.transform.localPosition = GeneratedVoxel.GridToLocal(cell, owner.GridSize, owner.VoxelSize);
        preview.cube.transform.localScale = Vector3.one * (0.98f * owner.VoxelSize);
        if (!ColorUtility.TryParseHtmlString(source != null ? source.color : DefaultColor, out Color color)) color = Color.white;
        color.a = 0.35f;
        preview.material.color = color;
        preview.cube.gameObject.SetActive(true);
    }

    // Without a hand, hides every hand's preview.
    public void HidePreview(Transform hand = null)
    {
        foreach (var preview in previews)
            if ((hand == null || preview.hand == hand) && preview.cube != null) preview.cube.gameObject.SetActive(false);
    }

    public void End(Transform hand)
    {
        if (hand == null || hand != busyHand) return;
        busyHand = null;
        adding = false;
        group.Clear();
        groupSet.Clear();
        stroke.Clear();
    }

    // Unit steps from one cell to another (start excluded, end included), crossing the next cell
    // boundary of the from-to segment as in Amanatides-Woo 3D DDA; ties resolve x, then y, then z.
    public static List<Vector3Int> TrailSteps(Vector3Int from, Vector3Int to)
    {
        Vector3Int delta = to - from;
        Vector3Int length = new(Mathf.Abs(delta.x), Mathf.Abs(delta.y), Mathf.Abs(delta.z));
        Vector3Int taken = Vector3Int.zero;
        var steps = new List<Vector3Int>(length.x + length.y + length.z);
        Vector3Int cell = from;
        for (int s = length.x + length.y + length.z; s > 0; s--)
        {
            // Axis a crosses its next boundary at t = (2*taken[a] + 1) / (2*length[a]); compared exactly in integers.
            int axis = -1;
            for (int a = 0; a < 3; a++)
                if (taken[a] < length[a] && (axis < 0 || (2L * taken[a] + 1) * length[axis] < (2L * taken[axis] + 1) * length[a])) axis = a;
            taken[axis]++;
            cell[axis] += delta[axis] > 0 ? 1 : -1;
            steps.Add(cell);
        }
        return steps;
    }

    // Moves the dragged group one cell, then applies the drag's mode to the cells it left or entered.
    private void Step(Vector3Int direction)
    {
        leftCells.Clear();
        foreach (GeneratedVoxel voxel in group)
        {
            leftCells.Add(voxel.Cell);
            Vacate(voxel);
            owner.MoveVoxelToCell(voxel, voxel.Cell + direction);
            Occupy(voxel);
        }
        if (dragMode == QuestVoxelEditMode.Fill)
        {
            for (int i = 0; i < group.Count && !limitReached; i++)
                if (CanPlace(leftCells[i])) Create(leftCells[i], group[i].voxelType, group[i].color, "Fill");
        }
        else if (dragMode == QuestVoxelEditMode.Erase)
        {
            foreach (GeneratedVoxel voxel in group)
            {
                if (!IsOccupied(voxel.Cell)) continue;
                erased.Clear();
                foreach (GeneratedVoxel other in cells[voxel.Cell]) if (!groupSet.Contains(other)) erased.Add(other);
                foreach (GeneratedVoxel other in erased) Remove(other);
            }
        }
    }

    private bool TryAdd(Vector3Int cell, string type, string color) => !CanPlace(cell) || Create(cell, type, color, "Add");

    // Returns false once Max Voxels stops this drag or stroke.
    private bool Create(Vector3Int cell, string type, string color, string operation)
    {
        GeneratedVoxel voxel = owner.CreateVoxel(-1, cell, type, color);
        if (voxel == null)
        {
            if (!limitReached) owner.ReportStatus($"Max Voxels reached; {operation} stopped.");
            limitReached = true;
            return false;
        }
        Occupy(voxel);
        if (adding) stroke.Add(voxel);
        return true;
    }

    private void Remove(GeneratedVoxel voxel)
    {
        Vacate(voxel);
        selection.Remove(voxel);
        stroke.Remove(voxel);
        owner.DestroyVoxel(voxel);
    }

    private bool Owns(GeneratedVoxel voxel) => voxel != null && voxel.gameObject.activeSelf && voxel.transform.parent == transform;
    private bool CanPlace(Vector3Int cell) => !IsOutside(cell) && !IsOccupied(cell);
    private bool IsOutside(Vector3Int cell) =>
        cell.x < 0 || cell.y < 0 || cell.z < 0 || cell.x >= owner.GridSize || cell.y >= owner.GridSize || cell.z >= owner.GridSize;

    private void Occupy(GeneratedVoxel voxel)
    {
        if (!cells.TryGetValue(voxel.Cell, out List<GeneratedVoxel> here)) cells[voxel.Cell] = here = new List<GeneratedVoxel>(1);
        here.Add(voxel);
    }

    private void Vacate(GeneratedVoxel voxel)
    {
        if (cells.TryGetValue(voxel.Cell, out List<GeneratedVoxel> here) && here.Remove(voxel) && here.Count == 0) cells.Remove(voxel.Cell);
    }

    // Prunes entries of voxels destroyed or moved through the generator API directly.
    private bool IsOccupied(Vector3Int cell)
    {
        if (!cells.TryGetValue(cell, out List<GeneratedVoxel> here)) return false;
        for (int i = here.Count - 1; i >= 0; i--)
            if (here[i] == null || !here[i].gameObject.activeSelf || here[i].Cell != cell) here.RemoveAt(i);
        if (here.Count == 0) cells.Remove(cell);
        return here.Count > 0;
    }

    private (Transform hand, Renderer cube, Material material) PreviewFor(Transform hand)
    {
        foreach (var preview in previews) if (preview.hand == hand) return preview;
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Add Preview";
        DestroyImmediate(cube.GetComponent<Collider>());
        cube.transform.SetParent(transform, false);
        var renderer = cube.GetComponent<Renderer>();
        var material = new Material(Shader.Find("Sprites/Default"));
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        previews.Add((hand, renderer, material));
        return previews[previews.Count - 1];
    }
}
