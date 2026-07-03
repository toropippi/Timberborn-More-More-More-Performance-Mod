using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BehaviorSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NoActionCooldown
{
    private static readonly object Lock = new object();
    private static readonly Dictionary<int, int> CooldownUntilFrameByPicker = new Dictionary<int, int>();
    private static readonly Dictionary<Type, PropertyInfo?> OnlyCriticalStateNeedsPropertyByType = new Dictionary<Type, PropertyInfo?>();

    private static long _attempts;
    private static long _skips;
    private static long _arms;
    private static long _clears;
    private static long _criticalBypasses;
    private static long _unknownFilterBypasses;
    private static long _expiredRemovals;

    public static bool TrySkip(object picker, object needFilter, out Behavior? result, out CallState state)
    {
        result = null;
        state = CallState.Inactive;
        if (!BenchmarkSettings.EnableNoActionCooldown ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _attempts);
        if (!TryIsCriticalOnlyFilter(needFilter, out var criticalOnly))
        {
            Interlocked.Increment(ref _unknownFilterBypasses);
            return false;
        }

        if (criticalOnly)
        {
            Interlocked.Increment(ref _criticalBypasses);
            return false;
        }

        var key = RuntimeHelpers.GetHashCode(picker);
        var frame = Time.frameCount;
        lock (Lock)
        {
            if (!CooldownUntilFrameByPicker.TryGetValue(key, out var untilFrame))
            {
                state = new CallState(true, false, key);
                return false;
            }

            if (untilFrame >= frame)
            {
                Interlocked.Increment(ref _skips);
                state = new CallState(true, true, key);
                return true;
            }

            CooldownUntilFrameByPicker.Remove(key);
            Interlocked.Increment(ref _expiredRemovals);
            state = new CallState(true, false, key);
            return false;
        }
    }

    public static void RecordReturn(object picker, object needFilter, Behavior? result, CallState state)
    {
        if (!state.Active ||
            state.Skipped ||
            !BenchmarkSettings.EnableNoActionCooldown ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return;
        }

        if (!TryIsCriticalOnlyFilter(needFilter, out var criticalOnly) || criticalOnly)
        {
            return;
        }

        var key = state.PickerKey != 0 ? state.PickerKey : RuntimeHelpers.GetHashCode(picker);
        lock (Lock)
        {
            if (result is null)
            {
                CooldownUntilFrameByPicker[key] = Time.frameCount + BenchmarkSettings.NoActionCooldownFrames;
                Interlocked.Increment(ref _arms);
                return;
            }

            if (CooldownUntilFrameByPicker.Remove(key))
            {
                Interlocked.Increment(ref _clears);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var skips = Interlocked.Exchange(ref _skips, 0);
        var arms = Interlocked.Exchange(ref _arms, 0);
        var clears = Interlocked.Exchange(ref _clears, 0);
        var criticalBypasses = Interlocked.Exchange(ref _criticalBypasses, 0);
        var unknownFilterBypasses = Interlocked.Exchange(ref _unknownFilterBypasses, 0);
        var expiredRemovals = Interlocked.Exchange(ref _expiredRemovals, 0);
        if (attempts == 0 && skips == 0 && arms == 0 && clears == 0)
        {
            return;
        }

        int activeEntries;
        lock (Lock)
        {
            activeEntries = CooldownUntilFrameByPicker.Count;
        }

        var skipRate = attempts > 0 ? (double)skips / attempts : 0;
        Debug.Log(
            $"[T3MP] NoActionCooldown aggregate={aggregateId}, ttlFrames={BenchmarkSettings.NoActionCooldownFrames}, attempts={attempts}, skips={skips}, skipRate={skipRate:F3}, arms={arms}, clears={clears}, criticalBypasses={criticalBypasses}, unknownFilterBypasses={unknownFilterBypasses}, expiredRemovals={expiredRemovals}, activeEntries={activeEntries}");
    }

    private static bool TryIsCriticalOnlyFilter(object needFilter, out bool criticalOnly)
    {
        criticalOnly = true;
        try
        {
            var type = needFilter.GetType();
            PropertyInfo? property;
            lock (Lock)
            {
                if (!OnlyCriticalStateNeedsPropertyByType.TryGetValue(type, out property))
                {
                    property = type.GetProperty("OnlyCriticalStateNeeds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    OnlyCriticalStateNeedsPropertyByType[type] = property;
                }
            }

            if (property?.GetValue(needFilter) is not bool value)
            {
                return false;
            }

            criticalOnly = value;
            return true;
        }
        catch (Exception)
        {
            criticalOnly = true;
            return false;
        }
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, false, 0);

        public CallState(bool active, bool skipped, int pickerKey)
        {
            Active = active;
            Skipped = skipped;
            PickerKey = pickerKey;
        }

        public bool Active { get; }
        public bool Skipped { get; }
        public int PickerKey { get; }
    }
}
