using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class RangedEffectSubjectProfiler
{
    private static readonly ModeStats[] Stats =
    {
        new ModeStats(),
        new ModeStats()
    };

    private static readonly ConcurrentDictionary<Type, Func<object, int>> CountAccessors = new ConcurrentDictionary<Type, Func<object, int>>();

    public static void BeginTick(out CallState state)
    {
        if (!BenchmarkSettings.EnableRangedEffectSubjectProfiler ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = CallState.Inactive;
            return;
        }

        state = new CallState(true, mode, Stopwatch.GetTimestamp());
    }

    public static void EndTick(CallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var stats = Stats[(int)state.Mode];
        Interlocked.Increment(ref stats.TickCalls);
        Interlocked.Add(ref stats.TickStopwatchTicks, elapsedTicks);
        UpdateMax(ref stats.TickMaxStopwatchTicks, elapsedTicks);
    }

    public static void BeginGetEffects(out CallState state)
    {
        if (!BenchmarkSettings.EnableRangedEffectSubjectProfiler ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = CallState.Inactive;
            return;
        }

        state = new CallState(true, mode, Stopwatch.GetTimestamp());
    }

    public static void EndGetEffects(CallState state, object? result)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var count = TryGetCount(result);
        var stats = Stats[(int)state.Mode];
        Interlocked.Increment(ref stats.GetEffectsCalls);
        Interlocked.Add(ref stats.GetEffectsStopwatchTicks, elapsedTicks);
        UpdateMax(ref stats.GetEffectsMaxStopwatchTicks, elapsedTicks);

        if (count == 0)
        {
            Interlocked.Increment(ref stats.ZeroEffectResults);
        }
        else if (count > 0)
        {
            Interlocked.Increment(ref stats.NonZeroEffectResults);
            Interlocked.Add(ref stats.EffectCountTotal, count);
            UpdateMax(ref stats.EffectCountMax, count);
        }
        else
        {
            Interlocked.Increment(ref stats.UnknownCountResults);
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableRangedEffectSubjectProfiler)
        {
            return;
        }

        LogAndReset(aggregateId, BenchmarkMode.Vanilla);
        LogAndReset(aggregateId, BenchmarkMode.Optimized);
    }

    private static void LogAndReset(long aggregateId, BenchmarkMode mode)
    {
        var stats = Stats[(int)mode];
        var tickCalls = Interlocked.Exchange(ref stats.TickCalls, 0);
        var tickTicks = Interlocked.Exchange(ref stats.TickStopwatchTicks, 0);
        var tickMaxTicks = Interlocked.Exchange(ref stats.TickMaxStopwatchTicks, 0);
        var getEffectsCalls = Interlocked.Exchange(ref stats.GetEffectsCalls, 0);
        var getEffectsTicks = Interlocked.Exchange(ref stats.GetEffectsStopwatchTicks, 0);
        var getEffectsMaxTicks = Interlocked.Exchange(ref stats.GetEffectsMaxStopwatchTicks, 0);
        var zeroEffects = Interlocked.Exchange(ref stats.ZeroEffectResults, 0);
        var nonZeroEffects = Interlocked.Exchange(ref stats.NonZeroEffectResults, 0);
        var unknownCounts = Interlocked.Exchange(ref stats.UnknownCountResults, 0);
        var effectCountTotal = Interlocked.Exchange(ref stats.EffectCountTotal, 0);
        var effectCountMax = Interlocked.Exchange(ref stats.EffectCountMax, 0);
        if (tickCalls == 0 && getEffectsCalls == 0)
        {
            return;
        }

        var tickMs = ToMilliseconds(tickTicks);
        var tickAvgUs = tickCalls > 0 ? tickMs * 1000.0 / tickCalls : 0.0;
        var getEffectsMs = ToMilliseconds(getEffectsTicks);
        var getEffectsAvgUs = getEffectsCalls > 0 ? getEffectsMs * 1000.0 / getEffectsCalls : 0.0;
        var zeroRate = getEffectsCalls > 0 ? (double)zeroEffects / getEffectsCalls : 0.0;
        var avgEffectCount = nonZeroEffects > 0 ? (double)effectCountTotal / nonZeroEffects : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RangedEffectSubjectProfiler aggregate={0}, mode={1}, tickCalls={2}, tickMs={3:F3}, tickAvgUs={4:F3}, tickMaxMs={5:F3}, getEffectsCalls={6}, getEffectsMs={7:F3}, getEffectsAvgUs={8:F3}, getEffectsMaxMs={9:F3}, zeroEffects={10}, zeroRate={11:F3}, nonZeroEffects={12}, unknownCounts={13}, avgEffectCountWhenNonZero={14:F2}, maxEffectCount={15}",
            aggregateId,
            mode,
            tickCalls,
            tickMs,
            tickAvgUs,
            ToMilliseconds(tickMaxTicks),
            getEffectsCalls,
            getEffectsMs,
            getEffectsAvgUs,
            ToMilliseconds(getEffectsMaxTicks),
            zeroEffects,
            zeroRate,
            nonZeroEffects,
            unknownCounts,
            avgEffectCount,
            effectCountMax));
    }

    public static void Reset()
    {
        foreach (var stats in Stats)
        {
            Interlocked.Exchange(ref stats.TickCalls, 0);
            Interlocked.Exchange(ref stats.TickStopwatchTicks, 0);
            Interlocked.Exchange(ref stats.TickMaxStopwatchTicks, 0);
            Interlocked.Exchange(ref stats.GetEffectsCalls, 0);
            Interlocked.Exchange(ref stats.GetEffectsStopwatchTicks, 0);
            Interlocked.Exchange(ref stats.GetEffectsMaxStopwatchTicks, 0);
            Interlocked.Exchange(ref stats.ZeroEffectResults, 0);
            Interlocked.Exchange(ref stats.NonZeroEffectResults, 0);
            Interlocked.Exchange(ref stats.UnknownCountResults, 0);
            Interlocked.Exchange(ref stats.EffectCountTotal, 0);
            Interlocked.Exchange(ref stats.EffectCountMax, 0);
        }
    }

    private static int TryGetCount(object? value)
    {
        if (value is null)
        {
            return -1;
        }

        if (value is ICollection collection)
        {
            return collection.Count;
        }

        try
        {
            var accessor = CountAccessors.GetOrAdd(value.GetType(), CreateCountAccessor);
            return accessor(value);
        }
        catch
        {
            return -1;
        }
    }

    private static Func<object, int> CreateCountAccessor(Type type)
    {
        var property = type.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is not null && property.PropertyType == typeof(int))
        {
            return value => (int)(property.GetValue(value) ?? -1);
        }

        return _ => -1;
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, BenchmarkMode.Vanilla, 0);

        public CallState(bool active, BenchmarkMode mode, long startTimestamp)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
    }

    private sealed class ModeStats
    {
        public long TickCalls;
        public long TickStopwatchTicks;
        public long TickMaxStopwatchTicks;
        public long GetEffectsCalls;
        public long GetEffectsStopwatchTicks;
        public long GetEffectsMaxStopwatchTicks;
        public long ZeroEffectResults;
        public long NonZeroEffectResults;
        public long UnknownCountResults;
        public long EffectCountTotal;
        public long EffectCountMax;
    }
}
