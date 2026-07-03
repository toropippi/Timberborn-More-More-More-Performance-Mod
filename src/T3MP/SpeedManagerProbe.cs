using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class SpeedManagerProbe
{
    private static Type? _speedManagerType;
    private static PropertyInfo? _currentSpeedProperty;
    private static float _nextLogRealtime;
    private static int _logCount;

    public static void Record(object speedManager)
    {
        if (!BenchmarkSettings.EnableSpeedManagerProbe ||
            !BenchmarkSettings.EnableSpeedManagerLogging ||
            Time.realtimeSinceStartup < _nextLogRealtime ||
            _logCount >= 24)
        {
            return;
        }

        _nextLogRealtime = Time.realtimeSinceStartup + 5f;
        EnsureMembers(speedManager.GetType());

        try
        {
            var currentSpeed = _currentSpeedProperty?.GetValue(speedManager, null);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] SpeedManager currentSpeed={0}",
                currentSpeed ?? "<unknown>"));
            _logCount++;
        }
        catch (Exception exception)
        {
            if (_logCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] SpeedManager probe failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void EnsureMembers(Type speedManagerType)
    {
        if (_speedManagerType == speedManagerType)
        {
            return;
        }

        _speedManagerType = speedManagerType;
        _currentSpeedProperty = speedManagerType.GetProperty("CurrentSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
