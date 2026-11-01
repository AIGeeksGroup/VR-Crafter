using System.Collections;
using UnityEngine;
using UnityEngine.XR;

/// <summary>One-time evidence that Link supplied an XR display and tracked input in Windows Play Mode.</summary>
public sealed class Quest3SPCVRRuntimeStatus : MonoBehaviour
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartCheck()
    {
        if (FindFirstObjectByType<Quest3SPCVRRuntimeStatus>() != null) return;
        var probe = new GameObject("Quest 3S PC VR status probe");
        DontDestroyOnLoad(probe);
        probe.AddComponent<Quest3SPCVRRuntimeStatus>();
    }

    private IEnumerator Start()
    {
        // Link may connect shortly after Play starts. Do not report a transient first frame as a pass.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            yield return new WaitForSecondsRealtime(1f);
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (XRSettings.isDeviceActive && Tracked(head) && (Tracked(left) || Tracked(right)))
            {
                Debug.Log($"[Quest 3S PC VR] LIVE PASS: OpenXR={XRSettings.loadedDeviceName}, GPU={SystemInfo.graphicsDeviceName}, " +
                    $"head={head.name}, left={Tracked(left)}, right={Tracked(right)}. This Windows process is running the Unity scene.");
                yield break;
            }
        }
        Debug.LogWarning($"[Quest 3S PC VR] LIVE NOT VERIFIED: XR active={XRSettings.isDeviceActive}, " +
            $"runtime={XRSettings.loadedDeviceName}, GPU={SystemInfo.graphicsDeviceName}. " +
            "Start Quest Link in the headset, then stop and restart Unity Play Mode.");
    }

    private static bool Tracked(InputDevice device)
    {
        return device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
    }
#endif
}
