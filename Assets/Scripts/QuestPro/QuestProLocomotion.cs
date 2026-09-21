using UnityEngine;

/// <summary>Right stick moves horizontally relative to view; left stick Y changes height.</summary>
[DefaultExecutionOrder(120)]
public sealed class QuestProLocomotion : MonoBehaviour
{
    [SerializeField, Min(0f)] private float moveSpeed = 1.2f;
    [SerializeField, Min(0f)] private float heightSpeed = 0.7f;
    [SerializeField, Range(0f, 0.9f)] private float deadzone = 0.2f;
    private Transform eye;
    private QuestProTriggerInteractor left;
    private QuestProTriggerInteractor right;

    public void Initialize(Transform head, QuestProTriggerInteractor leftHand, QuestProTriggerInteractor rightHand)
    {
        eye = head;
        left = leftHand;
        right = rightHand;
    }

    private void LateUpdate()
    {
        if (eye == null) return;
        Vector2 planar = right != null && right.IsTracked ? right.JoystickValue : Vector2.zero;
        float vertical = left != null && left.IsTracked ? left.JoystickValue.y : 0f;
        Vector3 delta = CalculateMotion(eye.forward, planar, vertical, moveSpeed, heightSpeed, deadzone, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
        if (delta.sqrMagnitude > 0f) transform.position += delta;
    }

    public static Vector3 CalculateMotion(Vector3 headForward, Vector2 rightStick, float leftY,
        float horizontalSpeed, float verticalSpeed, float threshold, float deltaTime)
    {
        Vector3 forward = Vector3.ProjectOnPlane(headForward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f) return Vector3.up * Filter(leftY, threshold) * verticalSpeed * deltaTime;
        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        float magnitude = Mathf.Min(rightStick.magnitude, 1f);
        Vector2 planar = rightStick.sqrMagnitude > 0f ? rightStick.normalized * Filter(magnitude, threshold) : Vector2.zero;
        return ((right * planar.x + forward * planar.y) * horizontalSpeed + Vector3.up * Filter(leftY, threshold) * verticalSpeed) * deltaTime;
    }

    private static float Filter(float value, float threshold) =>
        Mathf.Abs(value) <= threshold ? 0f : Mathf.Sign(value) * Mathf.Clamp01((Mathf.Abs(value) - threshold) / (1f - threshold));
}
