using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class PathFollowerProfiler
{
    private const int MaxEntriesToLog = 16;
    private static readonly object Lock = new object();
    private static readonly Dictionary<Key, Counter> Counters = new Dictionary<Key, Counter>(64);

    public static void Begin(MethodBase method, out CallState state)
    {
        state = CallState.Inactive;
        if (!BenchmarkSettings.EnablePathFollowerProfiler ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            return;
        }

        var declaringType = method.DeclaringType?.FullName ?? "<unknown>";
        state = new CallState(
            true,
            mode,
            string.Concat(declaringType, ".", method.Name),
            Stopwatch.GetTimestamp());
    }

    public static void End(CallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        lock (Lock)
        {
            var key = new Key(state.Mode, state.MethodName);
            if (!Counters.TryGetValue(key, out var counter))
            {
                counter = new Counter();
                Counters.Add(key, counter);
            }

            counter.Calls++;
            counter.StopwatchTicks += ticks;
            if (ticks > counter.MaxStopwatchTicks)
            {
                counter.MaxStopwatchTicks = ticks;
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnablePathFollowerProfiler)
        {
            return;
        }

        List<Entry> entries;
        lock (Lock)
        {
            entries = new List<Entry>(Counters.Count);
            foreach (var pair in Counters)
            {
                entries.Add(new Entry(pair.Key.Mode, pair.Key.MethodName, pair.Value.Calls, pair.Value.StopwatchTicks, pair.Value.MaxStopwatchTicks));
            }

            Counters.Clear();
        }

        entries.Sort((left, right) => right.StopwatchTicks.CompareTo(left.StopwatchTicks));
        var limit = Math.Min(MaxEntriesToLog, entries.Count);
        for (var i = 0; i < limit; i++)
        {
            var entry = entries[i];
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] PathFollowerProfiler aggregate={0}, mode={1}, rank={2}, method={3}, calls={4}, totalMs={5:F3}, avgUs={6:F3}, maxMs={7:F3}",
                aggregateId,
                entry.Mode,
                i + 1,
                entry.MethodName,
                entry.Calls,
                ToMilliseconds(entry.StopwatchTicks),
                entry.Calls > 0 ? ToMilliseconds(entry.StopwatchTicks) * 1000.0 / entry.Calls : 0.0,
                ToMilliseconds(entry.MaxStopwatchTicks)));
        }
    }

    public static void Reset()
    {
        lock (Lock)
        {
            Counters.Clear();
        }
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, BenchmarkMode.Vanilla, string.Empty, 0);

        public CallState(bool active, BenchmarkMode mode, string methodName, long startTimestamp)
        {
            Active = active;
            Mode = mode;
            MethodName = methodName;
            StartTimestamp = startTimestamp;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public string MethodName { get; }
        public long StartTimestamp { get; }
    }

    private readonly struct Key : IEquatable<Key>
    {
        public Key(BenchmarkMode mode, string methodName)
        {
            Mode = mode;
            MethodName = methodName;
        }

        public BenchmarkMode Mode { get; }
        public string MethodName { get; }

        public bool Equals(Key other)
        {
            return Mode == other.Mode && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is Key other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName);
            }
        }
    }

    private sealed class Counter
    {
        public long Calls;
        public long StopwatchTicks;
        public long MaxStopwatchTicks;
    }

    private readonly struct Entry
    {
        public Entry(BenchmarkMode mode, string methodName, long calls, long stopwatchTicks, long maxStopwatchTicks)
        {
            Mode = mode;
            MethodName = methodName;
            Calls = calls;
            StopwatchTicks = stopwatchTicks;
            MaxStopwatchTicks = maxStopwatchTicks;
        }

        public BenchmarkMode Mode { get; }
        public string MethodName { get; }
        public long Calls { get; }
        public long StopwatchTicks { get; }
        public long MaxStopwatchTicks { get; }
    }
}
