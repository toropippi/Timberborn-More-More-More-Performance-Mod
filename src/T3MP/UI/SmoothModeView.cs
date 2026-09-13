using Timberborn.InputSystem;
using Timberborn.TimeSystem;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace T3MP.UI;

// Run after the game's normal LateUpdate speed-button/pause handling. The
// changed Time.timeScale is shared by simulation and scaled animation clocks.
[DefaultExecutionOrder(10000)]
public sealed class SmoothModeView : MonoBehaviour, ISmoothSpeedEnvironment
{
    private SpeedManager _speedManager = null!;
    private InputBlocker _inputBlocker = null!;
    private SmoothSpeedController? _controller;
    private float _lastToggle = float.NegativeInfinity;

    internal bool SmoothEnabled => _controller?.Enabled == true;

    float ISmoothSpeedEnvironment.RequestedSpeed => _speedManager.CurrentSpeed;
    float ISmoothSpeedEnvironment.TimeScale { get => Time.timeScale; set => Time.timeScale = value; }
    int ISmoothSpeedEnvironment.VSyncCount { get => QualitySettings.vSyncCount; set => QualitySettings.vSyncCount = value; }
    int ISmoothSpeedEnvironment.TargetFrameRate { get => Application.targetFrameRate; set => Application.targetFrameRate = value; }

    internal void Initialize(SpeedManager speedManager, InputBlocker inputBlocker)
    {
        _speedManager = speedManager;
        _inputBlocker = inputBlocker;
        _controller = new SmoothSpeedController(this);
    }

    private void Update()
    {
        if (_controller == null || !Application.isFocused ||
            SceneManager.GetActiveScene().buildIndex != 2 || _inputBlocker.IsBlocked) return;
        var keyboard = Keyboard.current;
        if (keyboard == null || !keyboard.oKey.wasPressedThisFrame ||
            !(keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) ||
            keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ||
            Time.realtimeSinceStartup - _lastToggle < 0.5f) return;
        _lastToggle = Time.realtimeSinceStartup;
        _controller.Toggle();
        Debug.Log("[T3MP] Smooth mode " + (SmoothEnabled ? "ON" : "OFF") + " (Shift+O, target 30 fps).");
    }

    private void LateUpdate() => _controller?.Step(Time.unscaledDeltaTime,
        Application.isFocused && SceneManager.GetActiveScene().buildIndex == 2);

    private void OnApplicationFocus(bool focused)
    {
        if (!focused) _controller?.Suspend();
    }

    private void OnDisable() => _controller?.Stop();
}
