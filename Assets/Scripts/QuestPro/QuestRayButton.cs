using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Connects a physical controller ray to a standard world-space Unity UI Button.</summary>
[RequireComponent(typeof(Button), typeof(BoxCollider))]
public sealed class QuestRayButton : MonoBehaviour
{
    private readonly HashSet<QuestProTriggerInteractor> pointers = new HashSet<QuestProTriggerInteractor>();
    private Button button;
    private Image background;

    private void Awake()
    {
        button = GetComponent<Button>();
        background = GetComponent<Image>();
    }

    public void SetHover(QuestProTriggerInteractor pointer, bool value)
    {
        if (value) pointers.Add(pointer);
        else pointers.Remove(pointer);
    }

    public void Press()
    {
        if (button != null && button.isActiveAndEnabled && button.IsInteractable())
            button.onClick.Invoke();
    }

    private void LateUpdate()
    {
        if (background == null || button == null) return;
        background.color = !button.IsInteractable() ? new Color(0.2f, 0.25f, 0.3f) :
            pointers.Count > 0 ? new Color(0.1f, 0.65f, 0.85f) : new Color(0.05f, 0.35f, 0.55f);
    }

    private void OnDisable() => pointers.Clear();
}
