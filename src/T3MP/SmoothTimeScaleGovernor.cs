using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Smooth pacing v2 (opt-in, Shift+O): governs Time.timeScale itself instead
// of capping the sim ticker's per-frame time (the v1 approach, which broke
// the frame-time == sim-time invariant and made characters visibly run ahead
// of their paths). Lowering timeScale keeps EVERY scaled clock consistent by
// construction - the game simply runs "as if" a lower speed button were
// pressed, so simulation results are exactly vanilla for the speed actually
// achieved.
//
// Control law: multiplicative adjustment toward a target frame time. The
// governed scale is clamped to [max(1, requested * MinFraction), requested]:
// if even that floor cannot reach the target fps, fps settles lower instead
// of collapsing the game to x1. Toggling OFF (or the Shift+P blackout, or a
// pause) restores the requested speed immediately.
internal static class SmoothTimeScaleGovernor
{
    private static object? _speedManager;
    private static Type? _speedManagerType;
    private static PropertyInfo? _currentSpeedProperty;

    private static bool _enabled = BenchmarkSettings.BenchSmoothModeRequested;
    private static bool _overriding;
    private static float _governedScale = 1f;
    private static float _smoothedFrameSeconds;
    private static float _nextLogRealtime;

    public static bool Enabled => _enabled;

    public static void RecordSpeedManager(object instance)
    {
        _speedManager = instance;
    }

    public static void Toggle()
    {
        _enabled = !_enabled;
        if (_enabled)
        {
            _governedScale = Mathf.Max(1f, Time.timeScale);
            _smoothedFrameSeconds = Mathf.Max(Time.unscaledDeltaTime, 1f / 240f);
        }
        else
        {
            RestoreRequestedSpeed();
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] Smooth mode (fps-priority timeScale governor) {0}. targetFps={1}",
            _enabled ? "ON" : "OFF",
            BenchmarkSettings.GovernorTargetFps));
    }

    // Called every frame from the controller. Governs only while enabled, in
    // the game scene, rendered (no blackout) and unpaused.
    public static void Tick(bool inGameScene, bool renderBlackoutActive)
    {
        if (!BenchmarkSettings.EnableSmoothTimeScaleGovernor)
        {
            return;
        }

        if (!_enabled || !inGameScene || renderBlackoutActive)
        {
            if (_overriding)
            {
                RestoreRequestedSpeed();
            }

            return;
        }

        var requestedSpeed = ReadRequestedSpeed();
        if (requestedSpeed <= 1f || Time.timeScale <= 0f)
        {
            // Paused or at x1: nothing to shave. Do not touch the clock.
            if (_overriding)
            {
                RestoreRequestedSpeed();
            }

            return;
        }

        _smoothedFrameSeconds = Mathf.Lerp(_smoothedFrameSeconds, Time.unscaledDeltaTime, 0.1f);
        var targetSeconds = 1f / BenchmarkSettings.GovernorTargetFps;
        if (_smoothedFrameSeconds > targetSeconds * 1.05f)
        {
            _governedScale *= BenchmarkSettings.GovernorAdjustDownFactor;
        }
        else if (_smoothedFrameSeconds < targetSeconds * 0.90f)
        {
            _governedScale *= BenchmarkSettings.GovernorAdjustUpFactor;
        }

        var floor = Mathf.Max(
            requestedSpeed * BenchmarkSettings.GovernorMinScaleFraction,
            Mathf.Min(requestedSpeed, BenchmarkSettings.GovernorAbsoluteMinSpeed));
        _governedScale = Mathf.Clamp(_governedScale, floor, requestedSpeed);
        Time.timeScale = _governedScale;
        _overriding = true;

        var now = Time.realtimeSinceStartup;
        if (now >= _nextLogRealtime)
        {
            _nextLogRealtime = now + 10f;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] Smooth mode governing: requested={0:F1} governed={1:F1} fps={2:F1}",
                requestedSpeed,
                _governedScale,
                1f / Mathf.Max(_smoothedFrameSeconds, 0.001f)));
        }
    }

    private static void RestoreRequestedSpeed()
    {
        _overriding = false;
        var requestedSpeed = ReadRequestedSpeed();
        if (requestedSpeed > 0f)
        {
            Time.timeScale = requestedSpeed;
        }
    }

    private static float ReadRequestedSpeed()
    {
        var speedManager = _speedManager;
        if (speedManager is null)
        {
            return -1f;
        }

        try
        {
            EnsureMembers(speedManager.GetType());
            var value = _currentSpeedProperty?.GetValue(speedManager, null);
            return value is float speed ? speed : -1f;
        }
        catch (Exception)
        {
            return -1f;
        }
    }

    private static void EnsureMembers(Type speedManagerType)
    {
        if (_speedManagerType == speedManagerType)
        {
            return;
        }

        _speedManagerType = speedManagerType;
        _currentSpeedProperty = speedManagerType.GetProperty(
            "CurrentSpeed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
