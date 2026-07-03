using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class TimeSpeedButtonGroupProbe
{
    private static Type? _groupType;
    private static FieldInfo? _buttonsField;
    private static MethodInfo? _setSpeedMethod;
    private static PropertyInfo? _buttonSpeedProperty;
    private static float _nextAttemptRealtime;
    private static int _logCount;
    private static bool _startupGcDone;

    public static void RecordAndMaybeSetFastest(object group)
    {
        if ((!BenchmarkSettings.EnableTimeSpeedButtonGroupProbe && !BenchmarkSettings.EnableTimeSpeedButtonGroupAutoResume) ||
            Time.realtimeSinceStartup < BenchmarkSettings.AutoResumeGameAfterSeconds ||
            Time.realtimeSinceStartup < _nextAttemptRealtime)
        {
            return;
        }

        _nextAttemptRealtime = Time.realtimeSinceStartup + BenchmarkSettings.AutoResumeGameIntervalSeconds;
        EnsureMembers(group.GetType());

        try
        {
            var speeds = GetButtonSpeeds(group);
            var targetIndex = speeds.Count > 0 ? speeds.Count - 1 : -1;
            var invoked = false;
            var optimizedUltraSpeed = BenchmarkSettings.EnableOptimizedUltraSpeed &&
                BenchmarkModeController.CurrentMode == BenchmarkMode.Optimized;
            if (BenchmarkSettings.EnableTimeSpeedButtonGroupAutoResume &&
                !optimizedUltraSpeed &&
                targetIndex >= 0 &&
                _setSpeedMethod is not null)
            {
                RunStartupGcIfNeeded();
                _setSpeedMethod.Invoke(group, new object[] { targetIndex });
                invoked = true;
            }

            if (BenchmarkSettings.EnableTimeSpeedButtonGroupProbe && _logCount++ < 12)
            {
                Debug.Log(string.Format(
                    CultureInfo.InvariantCulture,
                    "[T3MP] TimeSpeedButtonGroup buttons=[{0}], targetIndex={1}, setFastestInvoked={2}",
                    string.Join(",", speeds.Select(speed => speed.ToString(CultureInfo.InvariantCulture))),
                    targetIndex,
                    invoked));
            }
        }
        catch (Exception exception)
        {
            if (_logCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] TimeSpeedButtonGroup probe failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static void RunStartupGcIfNeeded()
    {
        if (!BenchmarkSettings.EnableStartupGcBeforeAutoResume || _startupGcDone)
        {
            return;
        }

        _startupGcDone = true;
        var beforeMemory = GC.GetTotalMemory(false);
        var beforeGc0 = GC.CollectionCount(0);
        var beforeGc1 = GC.CollectionCount(1);
        var beforeGc2 = GC.CollectionCount(2);
        var startTimestamp = Stopwatch.GetTimestamp();
        GC.Collect();
        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        var afterMemory = GC.GetTotalMemory(false);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] StartupGcBeforeAutoResume ms={0:F1}, memoryBeforeMb={1:F1}, memoryAfterMb={2:F1}, gcDelta={3}/{4}/{5}",
            elapsedMilliseconds,
            beforeMemory / 1048576.0,
            afterMemory / 1048576.0,
            GC.CollectionCount(0) - beforeGc0,
            GC.CollectionCount(1) - beforeGc1,
            GC.CollectionCount(2) - beforeGc2));
    }

    private static void EnsureMembers(Type groupType)
    {
        if (_groupType == groupType)
        {
            return;
        }

        _groupType = groupType;
        _buttonsField = groupType.GetField("_buttons", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _setSpeedMethod = groupType.GetMethod("SetSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
        _buttonSpeedProperty = null;
    }

    private static List<int> GetButtonSpeeds(object group)
    {
        var result = new List<int>();
        if (_buttonsField?.GetValue(group) is not IEnumerable buttons)
        {
            return result;
        }

        foreach (var button in buttons)
        {
            if (button is null)
            {
                continue;
            }

            _buttonSpeedProperty ??= button.GetType().GetProperty("TimeSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = _buttonSpeedProperty?.GetValue(button, null);
            if (value is int speed)
            {
                result.Add(speed);
            }
        }

        return result;
    }
}
