using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class AutoRuntimeControl
{
    private static Type? _speedManagerType;
    private static MethodInfo? _unlockSpeedMethod;
    private static MethodInfo? _changeSpeedMethod;
    private static float _nextAttemptRealtime;
    private static int _logCount;
    private static bool _optimizedUltraApplied;
    private static bool _manualUltraRequested;

    public static void RequestOptimizedUltraSpeed()
    {
        _manualUltraRequested = true;
        _optimizedUltraApplied = false;
        _nextAttemptRealtime = 0f;
    }

    public static void TryResumeGameSpeed(object speedManager)
    {
        var optimizedUltraSpeed = BenchmarkSettings.EnableOptimizedUltraSpeed &&
            (BenchmarkModeController.CurrentMode == BenchmarkMode.Optimized ||
                BenchmarkSettings.MeasureVanillaBaseline ||
                BenchmarkSettings.MeasureAbAtUltra);

        if (!optimizedUltraSpeed)
        {
            _optimizedUltraApplied = false;
        }

        var shouldApplyOptimizedUltra = optimizedUltraSpeed && _manualUltraRequested && !_optimizedUltraApplied;
        var shouldApplyNormalResume = BenchmarkSettings.EnableAutoResumeGameSpeed && !optimizedUltraSpeed;
        if ((!shouldApplyNormalResume && !shouldApplyOptimizedUltra) ||
            Time.realtimeSinceStartup < BenchmarkSettings.AutoResumeGameAfterSeconds ||
            (!_manualUltraRequested && Time.realtimeSinceStartup < _nextAttemptRealtime))
        {
            return;
        }

        _nextAttemptRealtime = Time.realtimeSinceStartup + BenchmarkSettings.AutoResumeGameIntervalSeconds;
        EnsureSpeedManagerMembers(speedManager.GetType());

        try
        {
            _unlockSpeedMethod?.Invoke(speedManager, null);
            var targetSpeed = optimizedUltraSpeed
                ? BenchmarkSettings.OptimizedUltraSpeed
                : BenchmarkSettings.AutoResumeTargetSpeed;
            if (_changeSpeedMethod is not null)
            {
                _changeSpeedMethod.Invoke(speedManager, new object[] { targetSpeed });
            }

            if (shouldApplyOptimizedUltra)
            {
                _optimizedUltraApplied = true;
                _manualUltraRequested = false;
            }

            if (_logCount++ < 3)
            {
                Debug.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[T3MP] Auto runtime control attempted SpeedManager resume. targetSpeed={0}, optimizedUltra={1}",
                    targetSpeed,
                    optimizedUltraSpeed));
            }
        }
        catch (Exception exception)
        {
            if (_logCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] Auto runtime control failed to resume SpeedManager: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void EnsureSpeedManagerMembers(Type speedManagerType)
    {
        if (_speedManagerType == speedManagerType)
        {
            return;
        }

        _speedManagerType = speedManagerType;
        _unlockSpeedMethod = speedManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "UnlockSpeed" && method.GetParameters().Length == 0);
        _changeSpeedMethod = speedManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "ChangeSpeed" &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(float);
            });
    }
}
