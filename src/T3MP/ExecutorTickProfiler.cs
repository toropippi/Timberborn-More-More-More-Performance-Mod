using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.BehaviorSystem;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class ExecutorTickProfiler
{
    private static readonly ModeCounters[] Counters =
    {
        new ModeCounters(),
        new ModeCounters()
    };

    public static void Begin(object? instance, MethodBase method, out CallState state)
    {
        if (!BenchmarkSettings.EnableExecutorTickProfiler ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = CallState.Inactive;
            return;
        }

        state = new CallState(
            true,
            mode,
            Stopwatch.GetTimestamp(),
            instance?.GetType().FullName ?? method.DeclaringType?.FullName ?? "<unknown>");
    }

    public static void End(CallState state, ExecutorStatus result)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var counters = Counters[(int)state.Mode];
        lock (counters.LockObject)
        {
            if (!counters.ByType.TryGetValue(state.TypeName, out var typeCounters))
            {
                typeCounters = new TypeCounters();
                counters.ByType.Add(state.TypeName, typeCounters);
            }

            typeCounters.Calls++;
            typeCounters.StopwatchTicks += elapsedTicks;
            if (elapsedTicks > typeCounters.MaxStopwatchTicks)
            {
                typeCounters.MaxStopwatchTicks = elapsedTicks;
            }

            switch (result)
            {
                case ExecutorStatus.Running:
                    typeCounters.Running++;
                    break;
                case ExecutorStatus.Success:
                    typeCounters.Success++;
                    break;
                case ExecutorStatus.Failure:
                    typeCounters.Failure++;
                    break;
            }
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
            lock (counters.LockObject)
            {
                counters.ByType.Clear();
            }
        }
    }

    private static void LogAndReset(long aggregateId, BenchmarkMode mode)
    {
        var counters = Counters[(int)mode];
        List<KeyValuePair<string, TypeCounters>> snapshot;
        lock (counters.LockObject)
        {
            if (counters.ByType.Count == 0)
            {
                return;
            }

            snapshot = counters.ByType
                .Select(pair => new KeyValuePair<string, TypeCounters>(pair.Key, pair.Value.Copy()))
                .ToList();
            counters.ByType.Clear();
        }

        var top = string.Join(
            "; ",
            snapshot
                .OrderByDescending(pair => pair.Value.StopwatchTicks)
                .Take(BenchmarkSettings.ExecutorTickProfilerTopEntries)
                .Select(pair => string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:calls={1},ms={2:F2},avgUs={3:F2},maxMs={4:F3},running={5},success={6},failure={7}",
                    CleanTypeName(pair.Key),
                    pair.Value.Calls,
                    ToMilliseconds(pair.Value.StopwatchTicks),
                    pair.Value.Calls > 0 ? ToMicroseconds(pair.Value.StopwatchTicks) / pair.Value.Calls : 0.0,
                    ToMilliseconds(pair.Value.MaxStopwatchTicks),
                    pair.Value.Running,
                    pair.Value.Success,
                    pair.Value.Failure)));

        var totalCalls = snapshot.Sum(pair => pair.Value.Calls);
        var totalTicks = snapshot.Sum(pair => pair.Value.StopwatchTicks);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] ExecutorTickProfiler aggregate={0}, mode={1}, types={2}, calls={3}, totalMs={4:F2}, top={5}",
            aggregateId,
            mode,
            snapshot.Count,
            totalCalls,
            ToMilliseconds(totalTicks),
            top));
    }

    private static string CleanTypeName(string typeName)
    {
        const string prefix = "Timberborn.";
        return typeName.StartsWith(prefix, StringComparison.Ordinal) ? typeName.Substring(prefix.Length) : typeName;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static double ToMicroseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000000.0 / Stopwatch.Frequency;
    }

    private sealed class ModeCounters
    {
        public readonly object LockObject = new object();
        public readonly Dictionary<string, TypeCounters> ByType = new Dictionary<string, TypeCounters>(StringComparer.Ordinal);
    }

    private sealed class TypeCounters
    {
        public long Calls;
        public long Running;
        public long Success;
        public long Failure;
        public long StopwatchTicks;
        public long MaxStopwatchTicks;

        public TypeCounters Copy()
        {
            return new TypeCounters
            {
                Calls = Calls,
                Running = Running,
                Success = Success,
                Failure = Failure,
                StopwatchTicks = StopwatchTicks,
                MaxStopwatchTicks = MaxStopwatchTicks
            };
        }
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, BenchmarkMode.Vanilla, 0, string.Empty);

        public CallState(bool active, BenchmarkMode mode, long startTimestamp, string typeName)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            TypeName = typeName;
        }

        public bool Active { get; }

        public BenchmarkMode Mode { get; }

        public long StartTimestamp { get; }

        public string TypeName { get; }
    }
}
