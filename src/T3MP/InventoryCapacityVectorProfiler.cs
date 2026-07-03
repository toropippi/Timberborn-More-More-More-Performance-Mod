using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class InventoryCapacityVectorProfiler
{
    private static readonly ModeCounters[] Counters =
    {
        new ModeCounters(),
        new ModeCounters()
    };

    public static void Begin(Vector3 start, GoodAmount goodAmount, out CallState state)
    {
        if (!BenchmarkSettings.EnableInventoryCapacityVectorProfiler ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = CallState.Inactive;
            return;
        }

        state = new CallState(
            true,
            mode,
            Stopwatch.GetTimestamp(),
            new CacheProbeKey(start, goodAmount));
    }

    public static void End(CallState state, Inventory? result, float distance)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var counters = Counters[(int)state.Mode];
        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.StopwatchTicks, elapsedTicks);
        UpdateMax(ref counters.MaxTicks, elapsedTicks);
        if (result)
        {
            Interlocked.Increment(ref counters.Results);
            if (!float.IsNaN(distance) && !float.IsInfinity(distance))
            {
                Interlocked.Add(ref counters.DistanceMilliUnits, (long)(distance * 1000f));
            }
        }
        else
        {
            Interlocked.Increment(ref counters.NullResults);
        }

        lock (counters.KeysLock)
        {
            counters.Keys.TryGetValue(state.Key, out var count);
            counters.Keys[state.Key] = count + 1;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        LogAndReset(aggregateId, BenchmarkMode.Vanilla);
        LogAndReset(aggregateId, BenchmarkMode.Optimized);
    }

    public static void Reset()
    {
        foreach (var counters in Counters)
        {
            counters.Reset();
        }
    }

    private static void LogAndReset(long aggregateId, BenchmarkMode mode)
    {
        var counters = Counters[(int)mode];
        var calls = Interlocked.Exchange(ref counters.Calls, 0);
        var results = Interlocked.Exchange(ref counters.Results, 0);
        var nullResults = Interlocked.Exchange(ref counters.NullResults, 0);
        var ticks = Interlocked.Exchange(ref counters.StopwatchTicks, 0);
        var maxTicks = Interlocked.Exchange(ref counters.MaxTicks, 0);
        var distanceMilliUnits = Interlocked.Exchange(ref counters.DistanceMilliUnits, 0);
        Dictionary<CacheProbeKey, int> keys;
        lock (counters.KeysLock)
        {
            keys = new Dictionary<CacheProbeKey, int>(counters.Keys);
            counters.Keys.Clear();
        }

        if (calls == 0 && keys.Count == 0)
        {
            return;
        }

        var avgMs = calls > 0 ? ToMilliseconds(ticks) / calls : 0.0;
        var maxMs = ToMilliseconds(maxTicks);
        var avgDistance = results > 0 ? distanceMilliUnits / 1000.0 / results : 0.0;
        var topRepeat = 0;
        foreach (var count in keys.Values)
        {
            if (count > topRepeat)
            {
                topRepeat = count;
            }
        }

        var repeatRate = calls > 0 ? 1.0 - ((double)keys.Count / calls) : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] InventoryCapacityVectorProfiler aggregate={0}, mode={1}, calls={2}, results={3}, nullResults={4}, uniqueKeys={5}, repeatRate={6:F3}, topRepeat={7}, avgMs={8:F4}, maxMs={9:F3}, totalMs={10:F2}, avgDistance={11:F2}",
            aggregateId,
            mode,
            calls,
            results,
            nullResults,
            keys.Count,
            repeatRate,
            topRepeat,
            avgMs,
            maxMs,
            ToMilliseconds(ticks),
            avgDistance));
    }

    private static void UpdateMax(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class ModeCounters
    {
        public long Calls;
        public long Results;
        public long NullResults;
        public long StopwatchTicks;
        public long MaxTicks;
        public long DistanceMilliUnits;
        public readonly object KeysLock = new object();
        public readonly Dictionary<CacheProbeKey, int> Keys = new Dictionary<CacheProbeKey, int>();

        public void Reset()
        {
            Interlocked.Exchange(ref Calls, 0);
            Interlocked.Exchange(ref Results, 0);
            Interlocked.Exchange(ref NullResults, 0);
            Interlocked.Exchange(ref StopwatchTicks, 0);
            Interlocked.Exchange(ref MaxTicks, 0);
            Interlocked.Exchange(ref DistanceMilliUnits, 0);
            lock (KeysLock)
            {
                Keys.Clear();
            }
        }
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, BenchmarkMode.Vanilla, 0, default);

        public CallState(bool active, BenchmarkMode mode, long startTimestamp, CacheProbeKey key)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            Key = key;
        }

        public bool Active { get; }

        public BenchmarkMode Mode { get; }

        public long StartTimestamp { get; }

        public CacheProbeKey Key { get; }
    }

    public readonly struct CacheProbeKey : IEquatable<CacheProbeKey>
    {
        public CacheProbeKey(Vector3 start, GoodAmount goodAmount)
        {
            X = Mathf.RoundToInt(start.x);
            Y = Mathf.RoundToInt(start.y);
            Z = Mathf.RoundToInt(start.z);
            GoodId = goodAmount.GoodId ?? string.Empty;
            Amount = goodAmount.Amount;
        }

        public int X { get; }

        public int Y { get; }

        public int Z { get; }

        public string GoodId { get; }

        public int Amount { get; }

        public bool Equals(CacheProbeKey other)
        {
            return X == other.X &&
                Y == other.Y &&
                Z == other.Z &&
                Amount == other.Amount &&
                string.Equals(GoodId, other.GoodId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is CacheProbeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ Amount;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(GoodId);
                return hash;
            }
        }
    }
}
