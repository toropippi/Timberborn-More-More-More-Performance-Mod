using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class LoadProfiler
{
    private static readonly object LockObject = new object();
    private static readonly Dictionary<string, TimedStats> ComponentStats = new Dictionary<string, TimedStats>(StringComparer.Ordinal);
    private static int _activeDepth;
    private static long _sessionId;

    [ThreadStatic]
    private static string? _currentStage;

    public static bool IsActive => Volatile.Read(ref _activeDepth) > 0;

    public static LoadStageState BeginStage(MethodBase originalMethod)
    {
        var name = $"{originalMethod.DeclaringType?.FullName}.{originalMethod.Name}";
        Interlocked.Increment(ref _activeDepth);
        _currentStage = name;
        return new LoadStageState(true, name, Stopwatch.GetTimestamp());
    }

    public static void EndStage(LoadStageState state)
    {
        if (!state.Active || state.StartTimestamp <= 0)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        if (elapsedMilliseconds < BenchmarkSettings.LoadSlowCallThresholdMilliseconds)
        {
            _currentStage = null;
            Interlocked.Decrement(ref _activeDepth);
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] LoadStage {0} ms={1:F2}, frame={2}, memMb={3:F1}, depth={4}",
            ShortStageName(state.Name),
            elapsedMilliseconds,
            Time.frameCount,
            GC.GetTotalMemory(false) / 1048576.0,
            Volatile.Read(ref _activeDepth)));

        if (state.Name.EndsWith("WorldEntitiesLoader.PostLoadNonSingletons", StringComparison.Ordinal) ||
            state.Name.EndsWith("SingletonLifecycleService.LoadAll", StringComparison.Ordinal))
        {
            LogComponentStatsAndReset(ShortStageName(state.Name));
        }

        _currentStage = null;
        Interlocked.Decrement(ref _activeDepth);
    }

    public static void BeginComponentCall(object instance, MethodBase originalMethod, out LoadComponentCallState state)
    {
        if ((!BenchmarkSettings.EnableLoadComponentProfiler && !BenchmarkSettings.EnableLoadSingletonProfiler) || !IsActive)
        {
            state = LoadComponentCallState.Inactive;
            return;
        }

        state = new LoadComponentCallState(
            true,
            Stopwatch.GetTimestamp(),
            _currentStage ?? "<unknown>",
            GetCategory(originalMethod),
            GetShortTypeName(instance.GetType()),
            originalMethod.Name,
            true);
    }

    public static void BeginEventBusCall(MethodBase originalMethod, object? eventObject, out LoadComponentCallState state)
    {
        if (!BenchmarkSettings.EnableLoadEventProfiler)
        {
            state = LoadComponentCallState.Inactive;
            return;
        }

        var recordAggregate = IsActive;
        state = new LoadComponentCallState(
            true,
            Stopwatch.GetTimestamp(),
            recordAggregate ? _currentStage ?? "<unknown>" : "<runtime>",
            "EventBus." + originalMethod.Name,
            eventObject is null ? "<null>" : GetShortTypeName(eventObject.GetType()),
            originalMethod.Name,
            recordAggregate);
    }

    public static void BeginEventHandlerCall(object instance, MethodBase originalMethod, object? eventObject, out LoadComponentCallState state)
    {
        if (!BenchmarkSettings.EnableLoadEventProfiler)
        {
            state = LoadComponentCallState.Inactive;
            return;
        }

        var recordAggregate = IsActive;
        var eventName = eventObject is null ? "<null>" : GetShortTypeName(eventObject.GetType());
        state = new LoadComponentCallState(
            true,
            Stopwatch.GetTimestamp(),
            recordAggregate ? _currentStage ?? "<unknown>" : "<runtime>",
            "EventHandler:" + eventName,
            GetShortTypeName(instance.GetType()),
            originalMethod.Name,
            recordAggregate);
    }

    public static void EndComponentCall(LoadComponentCallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var stopwatchTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        if (stopwatchTicks <= 0)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(stopwatchTicks);
        if (elapsedMilliseconds >= BenchmarkSettings.LoadSlowCallThresholdMilliseconds)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] LoadSlowCall stage={0}, category={1}, type={2}, method={3}, ms={4:F3}, frame={5}, managedMemoryMb={6:F1}",
                ShortStageName(state.Stage),
                state.Category,
                state.TypeName,
                state.MethodName,
                elapsedMilliseconds,
                Time.frameCount,
                GC.GetTotalMemory(false) / 1048576.0));
        }

        if (!state.RecordAggregate)
        {
            return;
        }

        var key = state.Stage + "|" + state.Category + "|" + state.TypeName;
        lock (LockObject)
        {
            if (!ComponentStats.TryGetValue(key, out var stats))
            {
                stats = new TimedStats(state.Stage, state.Category, state.TypeName);
                ComponentStats.Add(key, stats);
            }

            stats.Record(stopwatchTicks);
        }
    }

    private static void LogComponentStatsAndReset(string endStage)
    {
        TimedStatsSnapshot[] snapshots;
        lock (LockObject)
        {
            snapshots = ComponentStats.Values.Select(stats => stats.Snapshot()).ToArray();
            ComponentStats.Clear();
        }

        var sessionId = Interlocked.Increment(ref _sessionId);
        if (snapshots.Length == 0)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] LoadComponentTop session={0}, endStage={1}, noSamples=true",
                sessionId,
                endStage));
            return;
        }

        foreach (var group in snapshots.GroupBy(snapshot => snapshot.Category).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var top = group
                .OrderByDescending(snapshot => snapshot.StopwatchTicks)
                .Take(BenchmarkSettings.LoadProfilerTopEntries)
                .Select(FormatSnapshot);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] LoadComponentTop session={0}, category={1}, top={2}",
                sessionId,
                group.Key,
                string.Join("; ", top)));
        }

        foreach (var group in snapshots.GroupBy(snapshot => snapshot.Stage).OrderByDescending(group => group.Sum(snapshot => snapshot.StopwatchTicks)).Take(12))
        {
            var totalMs = ToMilliseconds(group.Sum(snapshot => snapshot.StopwatchTicks));
            var calls = group.Sum(snapshot => snapshot.Calls);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] LoadStageComponentSummary session={0}, stage={1}, calls={2}, componentMs={3:F2}",
                sessionId,
                group.Key,
                calls,
                totalMs));
        }
    }

    private static string FormatSnapshot(TimedStatsSnapshot snapshot)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:stage={1},calls={2},ms={3:F2},avgUs={4:F2},maxMs={5:F3}",
            snapshot.TypeName,
            ShortStageName(snapshot.Stage),
            snapshot.Calls,
            ToMilliseconds(snapshot.StopwatchTicks),
            snapshot.Calls > 0 ? ToMicroseconds(snapshot.StopwatchTicks) / snapshot.Calls : 0.0,
            ToMilliseconds(snapshot.MaxStopwatchTicks));
    }

    private static string GetCategory(MethodBase originalMethod)
    {
        return originalMethod.Name switch
        {
            "Load" => "PersistentLoad",
            "PreInitializeEntity" => "PreInitializeEntity",
            "InitializeEntity" => "InitializeEntity",
            "PostInitializeEntity" => "PostInitializeEntity",
            "PostLoadEntity" => "PostLoadEntity",
            "BatchLoadEntities" => "BatchLoadEntities",
            "OnEnterFinishedState" => "EnterFinishedStateListener",
            "OnEnterUnfinishedState" => "EnterUnfinishedStateListener",
            "OnEnterPreviewState" => "EnterPreviewStateListener",
            _ => originalMethod.Name
        };
    }

    private static string ShortStageName(string stage)
    {
        const string prefix = "Timberborn.";
        return stage.StartsWith(prefix, StringComparison.Ordinal) ? stage.Substring(prefix.Length) : stage;
    }

    private static string GetShortTypeName(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        const string prefix = "Timberborn.";
        return fullName.StartsWith(prefix, StringComparison.Ordinal) ? fullName.Substring(prefix.Length) : fullName;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static double ToMicroseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000000.0 / Stopwatch.Frequency;
    }

    public readonly struct LoadStageState
    {
        public LoadStageState(bool active, string name, long startTimestamp)
        {
            Active = active;
            Name = name;
            StartTimestamp = startTimestamp;
        }

        public bool Active { get; }
        public string Name { get; }
        public long StartTimestamp { get; }
    }

    public readonly struct LoadComponentCallState
    {
        public static readonly LoadComponentCallState Inactive = new LoadComponentCallState(false, 0, string.Empty, string.Empty, string.Empty, string.Empty, false);

        public LoadComponentCallState(bool active, long startTimestamp, string stage, string category, string typeName, string methodName, bool recordAggregate)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Stage = stage;
            Category = category;
            TypeName = typeName;
            MethodName = methodName;
            RecordAggregate = recordAggregate;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public string Stage { get; }
        public string Category { get; }
        public string TypeName { get; }
        public string MethodName { get; }
        public bool RecordAggregate { get; }
    }

    private sealed class TimedStats
    {
        public TimedStats(string stage, string category, string typeName)
        {
            Stage = stage;
            Category = category;
            TypeName = typeName;
        }

        public string Stage { get; }
        public string Category { get; }
        public string TypeName { get; }
        public long Calls { get; private set; }
        public long StopwatchTicks { get; private set; }
        public long MaxStopwatchTicks { get; private set; }

        public void Record(long stopwatchTicks)
        {
            Calls++;
            StopwatchTicks += stopwatchTicks;
            if (stopwatchTicks > MaxStopwatchTicks)
            {
                MaxStopwatchTicks = stopwatchTicks;
            }
        }

        public TimedStatsSnapshot Snapshot()
        {
            return new TimedStatsSnapshot(Stage, Category, TypeName, Calls, StopwatchTicks, MaxStopwatchTicks);
        }
    }

    private readonly struct TimedStatsSnapshot
    {
        public TimedStatsSnapshot(string stage, string category, string typeName, long calls, long stopwatchTicks, long maxStopwatchTicks)
        {
            Stage = stage;
            Category = category;
            TypeName = typeName;
            Calls = calls;
            StopwatchTicks = stopwatchTicks;
            MaxStopwatchTicks = maxStopwatchTicks;
        }

        public string Stage { get; }
        public string Category { get; }
        public string TypeName { get; }
        public long Calls { get; }
        public long StopwatchTicks { get; }
        public long MaxStopwatchTicks { get; }
    }
}
