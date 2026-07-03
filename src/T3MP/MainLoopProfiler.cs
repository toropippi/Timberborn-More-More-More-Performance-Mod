using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class MainLoopProfiler
{
    private static readonly object LockObject = new object();
    private static readonly object ProfileInfoCacheLock = new object();
    private static readonly Dictionary<StageStatsKey, TimedStats> StageStats = new Dictionary<StageStatsKey, TimedStats>();
    private static readonly Dictionary<TypeStatsKey, TimedStats> TypeStats = new Dictionary<TypeStatsKey, TimedStats>();
    private static readonly Dictionary<MethodBase, string> StageNameCache = new Dictionary<MethodBase, string>();
    private static readonly Dictionary<TypeMethodKey, TypeProfileInfo> TypeProfileInfoCache = new Dictionary<TypeMethodKey, TypeProfileInfo>();

    public static void BeginStage(MethodBase originalMethod, out StageCallState state)
    {
        if (!BenchmarkSettings.EnableMainLoopProfiler ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = StageCallState.Inactive;
            return;
        }

        state = new StageCallState(
            true,
            mode,
            Stopwatch.GetTimestamp(),
            GetCachedStageName(originalMethod),
            Time.frameCount,
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static void EndStage(StageCallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var stopwatchTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var elapsedMilliseconds = ToMilliseconds(stopwatchTicks);
        if (elapsedMilliseconds >= BenchmarkSettings.MainLoopSlowStageThresholdMilliseconds)
        {
            var managedMemory = GC.GetTotalMemory(false);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] MainLoopSlowStage mode={0}, frame={1}, stage={2}, ms={3:F3}, managedMemoryMb={4:F1}, managedMemoryDeltaMb={5:F1}, gc0Delta={6}, gc1Delta={7}, gc2Delta={8}",
                state.Mode,
                state.Frame,
                state.Name,
                elapsedMilliseconds,
                managedMemory / 1048576.0,
                (managedMemory - state.ManagedMemory) / 1048576.0,
                GC.CollectionCount(0) - state.Gc0,
                GC.CollectionCount(1) - state.Gc1,
                GC.CollectionCount(2) - state.Gc2));
        }

        RecordStage(state.Mode, state.Name, stopwatchTicks);
    }

    public static void BeginTypeCall(object instance, MethodBase originalMethod, out TypeCallState state)
    {
        if (!BenchmarkSettings.EnableMainLoopProfiler ||
            (!BenchmarkSettings.EnableMainLoopTypeProfiler && !BenchmarkSettings.EnableMainLoopUpdateTypeProfiler) ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = TypeCallState.Inactive;
            return;
        }

        var profileInfo = GetTypeProfileInfo(instance.GetType(), originalMethod.Name);
        if (!profileInfo.Active)
        {
            state = TypeCallState.Inactive;
            return;
        }

        state = new TypeCallState(
            true,
            mode,
            Stopwatch.GetTimestamp(),
            profileInfo.Category,
            profileInfo.TypeName,
            Time.frameCount);
    }

    public static void EndTypeCall(TypeCallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var stopwatchTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var elapsedMilliseconds = ToMilliseconds(stopwatchTicks);
        if (elapsedMilliseconds >= BenchmarkSettings.MainLoopSlowTypeThresholdMilliseconds)
        {
            var managedMemory = GC.GetTotalMemory(false);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] MainLoopSlowType mode={0}, frame={1}, category={2}, type={3}, ms={4:F3}, managedMemoryMb={5:F1}, gc0={6}, gc1={7}, gc2={8}",
                state.Mode,
                state.Frame,
                state.Category,
                state.TypeName,
                elapsedMilliseconds,
                managedMemory / 1048576.0,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)));
        }

        RecordType(state.Mode, state.Category, state.TypeName, stopwatchTicks);
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableMainLoopProfiler)
        {
            return;
        }

        Dictionary<string, TimedStatsSnapshot> stageSnapshots;
        Dictionary<string, TimedStatsSnapshot> typeSnapshots;
        lock (LockObject)
        {
            stageSnapshots = SnapshotAndReset(StageStats);
            typeSnapshots = SnapshotAndReset(TypeStats);
        }

        LogStageSnapshots(aggregateId, BenchmarkMode.Vanilla, stageSnapshots);
        LogStageSnapshots(aggregateId, BenchmarkMode.Optimized, stageSnapshots);
        LogTopTypeSnapshots(aggregateId, BenchmarkMode.Vanilla, typeSnapshots);
        LogTopTypeSnapshots(aggregateId, BenchmarkMode.Optimized, typeSnapshots);
    }

    public static void LogFrameCpuSummary(long aggregateId, BenchmarkMode mode, long sampleFrames, double sampleSeconds)
    {
        if (!BenchmarkSettings.EnableMainLoopProfiler || sampleFrames <= 0 || sampleSeconds <= 0.0)
        {
            return;
        }

        double tickerMilliseconds;
        double tickBucketsMilliseconds;
        double tickEntityBucketMilliseconds;
        double tickableSingletonMilliseconds;
        double tickSingletonsMilliseconds;
        double startParallelTickMilliseconds;
        double finishParallelTickMilliseconds;
        double lifecycleUpdateMilliseconds;
        double lifecycleUpdateSingletonsMilliseconds;
        double componentUpdateMilliseconds;
        double lifecycleLateUpdateMilliseconds;
        double lifecycleLateUpdateSingletonsMilliseconds;
        double componentLateUpdateMilliseconds;
        lock (LockObject)
        {
            tickerMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.Ticker.Update").StopwatchTicks);
            tickBucketsMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableBucketService.TickBuckets").StopwatchTicks);
            tickEntityBucketMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableEntityBucket.TickAll").StopwatchTicks);
            tickableSingletonMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableSingletonService.TickAll").StopwatchTicks);
            tickSingletonsMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableSingletonService.TickSingletons").StopwatchTicks);
            startParallelTickMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableSingletonService.StartParallelTick").StopwatchTicks);
            finishParallelTickMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableSingletonService.FinishParallelTick").StopwatchTicks);
            lifecycleUpdateMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.SingletonSystem.SingletonLifecycleService.UpdateAll").StopwatchTicks);
            lifecycleUpdateSingletonsMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.SingletonSystem.SingletonLifecycleService.UpdateSingletons").StopwatchTicks);
            componentUpdateMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.BaseComponentSystem.BaseComponentUpdateUnityAdapter.Update").StopwatchTicks);
            lifecycleLateUpdateMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.SingletonSystem.SingletonLifecycleService.LateUpdateAll").StopwatchTicks);
            lifecycleLateUpdateSingletonsMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.SingletonSystem.SingletonLifecycleService.LateUpdateSingletons").StopwatchTicks);
            componentLateUpdateMilliseconds = ToMilliseconds(GetStatsNoLock(StageStats, mode, "Timberborn.BaseComponentSystem.BaseComponentLateUpdateUnityAdapter.LateUpdate").StopwatchTicks);
        }

        var actualFramesPerSecond = sampleFrames / sampleSeconds;
        var actualFrameMilliseconds = 1000.0 / actualFramesPerSecond;
        var tickerMillisecondsPerFrame = tickerMilliseconds / sampleFrames;
        var tickBucketsMillisecondsPerFrame = tickBucketsMilliseconds / sampleFrames;
        var tickEntityBucketMillisecondsPerFrame = tickEntityBucketMilliseconds / sampleFrames;
        var tickableSingletonMillisecondsPerFrame = tickableSingletonMilliseconds / sampleFrames;
        var tickSingletonsMillisecondsPerFrame = tickSingletonsMilliseconds / sampleFrames;
        var startParallelTickMillisecondsPerFrame = startParallelTickMilliseconds / sampleFrames;
        var finishParallelTickMillisecondsPerFrame = finishParallelTickMilliseconds / sampleFrames;
        var lifecycleUpdateMillisecondsPerFrame = lifecycleUpdateMilliseconds / sampleFrames;
        var lifecycleUpdateSingletonsMillisecondsPerFrame = lifecycleUpdateSingletonsMilliseconds / sampleFrames;
        var componentUpdateMillisecondsPerFrame = componentUpdateMilliseconds / sampleFrames;
        var lifecycleLateUpdateMillisecondsPerFrame = lifecycleLateUpdateMilliseconds / sampleFrames;
        var lifecycleLateUpdateSingletonsMillisecondsPerFrame = lifecycleLateUpdateSingletonsMilliseconds / sampleFrames;
        var componentLateUpdateMillisecondsPerFrame = componentLateUpdateMilliseconds / sampleFrames;

        var knownCpuMillisecondsPerFrame =
            tickerMillisecondsPerFrame +
            lifecycleUpdateMillisecondsPerFrame +
            componentUpdateMillisecondsPerFrame +
            lifecycleLateUpdateMillisecondsPerFrame +
            componentLateUpdateMillisecondsPerFrame;
        var unknownMillisecondsPerFrame = actualFrameMilliseconds - knownCpuMillisecondsPerFrame;

        var tickerOtherMillisecondsPerFrame = NonNegative(tickerMillisecondsPerFrame - tickBucketsMillisecondsPerFrame);
        var tickBucketsOtherMillisecondsPerFrame = NonNegative(tickBucketsMillisecondsPerFrame - tickEntityBucketMillisecondsPerFrame - tickableSingletonMillisecondsPerFrame);
        var tickableSingletonOtherMillisecondsPerFrame = NonNegative(tickableSingletonMillisecondsPerFrame - tickSingletonsMillisecondsPerFrame - startParallelTickMillisecondsPerFrame - finishParallelTickMillisecondsPerFrame);
        var lifecycleUpdateOtherMillisecondsPerFrame = NonNegative(lifecycleUpdateMillisecondsPerFrame - lifecycleUpdateSingletonsMillisecondsPerFrame);
        var lifecycleLateUpdateOtherMillisecondsPerFrame = NonNegative(lifecycleLateUpdateMillisecondsPerFrame - lifecycleLateUpdateSingletonsMillisecondsPerFrame);

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] FrameCpu mode={0}, aggregate={1}, sampleFrames={2}, sampleSeconds={3:F2}, actualFps={4:F2}, actualFrameMs={5:F3}, knownTopLevelCpuMs={6:F3}, knownTopLevelCpuPercent={7:F1}, unknownOrUnmeasuredMs={8:F3}, tickerMs={9:F3}, updateAllMs={10:F3}, componentUpdateMs={11:F3}, lateUpdateAllMs={12:F3}, componentLateUpdateMs={13:F3}",
            mode,
            aggregateId,
            sampleFrames,
            sampleSeconds,
            actualFramesPerSecond,
            actualFrameMilliseconds,
            knownCpuMillisecondsPerFrame,
            actualFrameMilliseconds > 0.0 ? knownCpuMillisecondsPerFrame / actualFrameMilliseconds * 100.0 : 0.0,
            unknownMillisecondsPerFrame,
            tickerMillisecondsPerFrame,
            lifecycleUpdateMillisecondsPerFrame,
            componentUpdateMillisecondsPerFrame,
            lifecycleLateUpdateMillisecondsPerFrame,
            componentLateUpdateMillisecondsPerFrame));
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] FrameCpuNested mode={0}, aggregate={1}, tickerMs={2:F3}, tickerOtherMs={3:F3}, tickBucketsMs={4:F3}, tickBucketsOtherMs={5:F3}, tickEntityBucketMs={6:F3}, tickableSingletonMs={7:F3}, tickSingletonsMs={8:F3}, startParallelTickMs={9:F3}, finishParallelTickMs={10:F3}, tickableSingletonOtherMs={11:F3}, updateAllMs={12:F3}, updateSingletonsMs={13:F3}, updateAllOtherMs={14:F3}, lateUpdateAllMs={15:F3}, lateUpdateSingletonsMs={16:F3}, lateUpdateAllOtherMs={17:F3}",
            mode,
            aggregateId,
            tickerMillisecondsPerFrame,
            tickerOtherMillisecondsPerFrame,
            tickBucketsMillisecondsPerFrame,
            tickBucketsOtherMillisecondsPerFrame,
            tickEntityBucketMillisecondsPerFrame,
            tickableSingletonMillisecondsPerFrame,
            tickSingletonsMillisecondsPerFrame,
            startParallelTickMillisecondsPerFrame,
            finishParallelTickMillisecondsPerFrame,
            tickableSingletonOtherMillisecondsPerFrame,
            lifecycleUpdateMillisecondsPerFrame,
            lifecycleUpdateSingletonsMillisecondsPerFrame,
            lifecycleUpdateOtherMillisecondsPerFrame,
            lifecycleLateUpdateMillisecondsPerFrame,
            lifecycleLateUpdateSingletonsMillisecondsPerFrame,
            lifecycleLateUpdateOtherMillisecondsPerFrame));
    }

    public static string GetCurrentSummary(BenchmarkMode mode)
    {
        if (!BenchmarkSettings.EnableMainLoopProfiler)
        {
            return "mainLoopProfiler=disabled";
        }

        lock (LockObject)
        {
            var ticker = GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.Ticker.Update");
            var lifecycleUpdate = GetStatsNoLock(StageStats, mode, "Timberborn.SingletonSystem.SingletonLifecycleService.UpdateAll");
            var lifecycleLateUpdate = GetStatsNoLock(StageStats, mode, "Timberborn.SingletonSystem.SingletonLifecycleService.LateUpdateAll");
            var entityBucket = GetStatsNoLock(StageStats, mode, "Timberborn.TickSystem.TickableEntityBucket.TickAll");
            var componentUpdate = GetStatsNoLock(StageStats, mode, "Timberborn.BaseComponentSystem.BaseComponentUpdateUnityAdapter.Update");
            var componentLateUpdate = GetStatsNoLock(StageStats, mode, "Timberborn.BaseComponentSystem.BaseComponentLateUpdateUnityAdapter.LateUpdate");
            return string.Format(
                CultureInfo.InvariantCulture,
                "tickerMs={0:F2},updateAllMs={1:F2},lateUpdateAllMs={2:F2},entityBucketMs={3:F2},componentUpdateMs={4:F2},componentLateUpdateMs={5:F2}",
                ToMilliseconds(ticker.StopwatchTicks),
                ToMilliseconds(lifecycleUpdate.StopwatchTicks),
                ToMilliseconds(lifecycleLateUpdate.StopwatchTicks),
                ToMilliseconds(entityBucket.StopwatchTicks),
                ToMilliseconds(componentUpdate.StopwatchTicks),
                ToMilliseconds(componentLateUpdate.StopwatchTicks));
        }
    }

    private static void RecordStage(BenchmarkMode mode, string name, long stopwatchTicks)
    {
        if (stopwatchTicks <= 0)
        {
            return;
        }

        var key = new StageStatsKey(mode, name);
        lock (LockObject)
        {
            if (!StageStats.TryGetValue(key, out var stats))
            {
                stats = new TimedStats(mode, name);
                StageStats.Add(key, stats);
            }

            stats.Record(stopwatchTicks);
        }
    }

    private static void RecordType(BenchmarkMode mode, string category, string typeName, long stopwatchTicks)
    {
        if (stopwatchTicks <= 0)
        {
            return;
        }

        var key = new TypeStatsKey(mode, category, typeName);
        lock (LockObject)
        {
            if (!TypeStats.TryGetValue(key, out var stats))
            {
                stats = new TimedStats(mode, category + ":" + typeName);
                TypeStats.Add(key, stats);
            }

            stats.Record(stopwatchTicks);
        }
    }

    private static TimedStats GetStatsNoLock(Dictionary<StageStatsKey, TimedStats> statsByKey, BenchmarkMode mode, string name)
    {
        return statsByKey.TryGetValue(new StageStatsKey(mode, name), out var stats) ? stats : TimedStats.CreateEmpty(mode, name);
    }

    private static Dictionary<string, TimedStatsSnapshot> SnapshotAndReset(Dictionary<StageStatsKey, TimedStats> statsByKey)
    {
        var snapshots = new Dictionary<string, TimedStatsSnapshot>(StringComparer.Ordinal);
        foreach (var pair in statsByKey)
        {
            snapshots.Add(pair.Value.Mode + "|" + pair.Value.Name, pair.Value.Snapshot());
        }

        statsByKey.Clear();
        return snapshots;
    }

    private static Dictionary<string, TimedStatsSnapshot> SnapshotAndReset(Dictionary<TypeStatsKey, TimedStats> statsByKey)
    {
        var snapshots = new Dictionary<string, TimedStatsSnapshot>(StringComparer.Ordinal);
        foreach (var pair in statsByKey)
        {
            snapshots.Add(pair.Value.Mode + "|" + pair.Value.Name, pair.Value.Snapshot());
        }

        statsByKey.Clear();
        return snapshots;
    }

    private static void LogStageSnapshots(long aggregateId, BenchmarkMode mode, Dictionary<string, TimedStatsSnapshot> snapshots)
    {
        var modeSnapshots = snapshots.Values
            .Where(snapshot => snapshot.Mode == mode && snapshot.Calls > 0)
            .OrderByDescending(snapshot => snapshot.StopwatchTicks)
            .ToArray();
        if (modeSnapshots.Length == 0)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] MainLoop mode={0}, aggregate={1}, noSamples=true",
                mode,
                aggregateId));
            return;
        }

        var text = string.Join("; ", modeSnapshots.Select(FormatSnapshot));
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] MainLoop mode={0}, aggregate={1}, stages={2}",
            mode,
            aggregateId,
            text));
    }

    private static void LogTopTypeSnapshots(long aggregateId, BenchmarkMode mode, Dictionary<string, TimedStatsSnapshot> snapshots)
    {
        var modeSnapshots = snapshots.Values
            .Where(snapshot => snapshot.Mode == mode && snapshot.Calls > 0)
            .ToArray();
        if (modeSnapshots.Length == 0)
        {
            return;
        }

        foreach (var group in modeSnapshots.GroupBy(snapshot => GetCategory(snapshot.Name)).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var top = group
                .OrderByDescending(snapshot => snapshot.StopwatchTicks)
                .Take(BenchmarkSettings.MainLoopProfilerTopEntries)
                .Select(FormatTypeSnapshot);
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] MainLoopTop mode={0}, aggregate={1}, category={2}, top={3}",
                mode,
                aggregateId,
                group.Key,
                string.Join("; ", top)));
        }
    }

    private static string FormatSnapshot(TimedStatsSnapshot snapshot)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:calls={1},ms={2:F2},avgUs={3:F2},maxMs={4:F3}",
            snapshot.Name,
            snapshot.Calls,
            ToMilliseconds(snapshot.StopwatchTicks),
            snapshot.Calls > 0 ? ToMicroseconds(snapshot.StopwatchTicks) / snapshot.Calls : 0.0,
            ToMilliseconds(snapshot.MaxStopwatchTicks));
    }

    private static string FormatTypeSnapshot(TimedStatsSnapshot snapshot)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:calls={1},ms={2:F2},avgUs={3:F2},maxMs={4:F3}",
            GetTypeName(snapshot.Name),
            snapshot.Calls,
            ToMilliseconds(snapshot.StopwatchTicks),
            snapshot.Calls > 0 ? ToMicroseconds(snapshot.StopwatchTicks) / snapshot.Calls : 0.0,
            ToMilliseconds(snapshot.MaxStopwatchTicks));
    }

    private static string GetStageName(MethodBase method)
    {
        return (method.DeclaringType?.FullName ?? "<unknown>") + "." + method.Name;
    }

    private static string GetCachedStageName(MethodBase method)
    {
        lock (ProfileInfoCacheLock)
        {
            if (StageNameCache.TryGetValue(method, out var cached))
            {
                return cached;
            }

            var name = GetStageName(method);
            StageNameCache.Add(method, name);
            return name;
        }
    }

    private static TypeProfileInfo GetTypeProfileInfo(Type type, string methodName)
    {
        var key = new TypeMethodKey(type, methodName);
        lock (ProfileInfoCacheLock)
        {
            if (TypeProfileInfoCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var category = GetTypeCategory(type, methodName);
            var profileInfo = category.Length == 0
                ? TypeProfileInfo.Inactive
                : new TypeProfileInfo(true, category, GetShortTypeName(type));
            TypeProfileInfoCache.Add(key, profileInfo);
            return profileInfo;
        }
    }

    private static string GetTypeCategory(Type type, string methodName)
    {
        if (methodName == "Tick")
        {
            if (IsAssignableTo(type, "Timberborn.TickSystem.TickableComponent"))
            {
                return "TickableComponent";
            }

            if (Implements(type, "Timberborn.TickSystem.ITickableSingleton"))
            {
                return "TickableSingleton";
            }
        }

        if (methodName == "StartParallelTick" && Implements(type, "Timberborn.TickSystem.IParallelTickableSingleton"))
        {
            return "ParallelTickableSingleton";
        }

        if (methodName == "UpdateSingleton" && Implements(type, "Timberborn.SingletonSystem.IUpdatableSingleton"))
        {
            return "UpdatableSingleton";
        }

        if (methodName == "LateUpdateSingleton" && Implements(type, "Timberborn.SingletonSystem.ILateUpdatableSingleton"))
        {
            return "LateUpdatableSingleton";
        }

        if (methodName == "Update" && Implements(type, "Timberborn.BaseComponentSystem.IUpdatableComponent"))
        {
            return "UpdatableComponent";
        }

        if (methodName == "LateUpdate" && Implements(type, "Timberborn.BaseComponentSystem.ILateUpdatableComponent"))
        {
            return "LateUpdatableComponent";
        }

        if (methodName == "Decide" && IsAssignableTo(type, "Timberborn.BehaviorSystem.Behavior"))
        {
            return "BehaviorDecide";
        }

        if (methodName == "ActionPosition" && IsAssignableTo(type, "Timberborn.NeedBehaviorSystem.NeedBehavior"))
        {
            return "NeedActionPosition";
        }

        return string.Empty;
    }

    private static bool Implements(Type type, string interfaceFullName)
    {
        return type.GetInterfaces().Any(interfaceType => interfaceType.FullName == interfaceFullName);
    }

    private static bool IsAssignableTo(Type type, string baseTypeFullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName == baseTypeFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetShortTypeName(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        const string timberbornPrefix = "Timberborn.";
        return fullName.StartsWith(timberbornPrefix, StringComparison.Ordinal)
            ? fullName.Substring(timberbornPrefix.Length)
            : fullName;
    }

    private static string GetCategory(string name)
    {
        var separator = name.IndexOf(':');
        return separator >= 0 ? name.Substring(0, separator) : "<unknown>";
    }

    private static string GetTypeName(string name)
    {
        var separator = name.IndexOf(':');
        return separator >= 0 && separator + 1 < name.Length ? name.Substring(separator + 1) : name;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static double ToMicroseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000000.0 / Stopwatch.Frequency;
    }

    private static double NonNegative(double value)
    {
        return value > 0.0 ? value : 0.0;
    }

    public readonly struct StageCallState
    {
        public static readonly StageCallState Inactive = new StageCallState(false, BenchmarkMode.Vanilla, 0, string.Empty, 0, 0, 0, 0, 0);

        public StageCallState(bool active, BenchmarkMode mode, long startTimestamp, string name, int frame, long managedMemory, int gc0, int gc1, int gc2)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            Name = name;
            Frame = frame;
            ManagedMemory = managedMemory;
            Gc0 = gc0;
            Gc1 = gc1;
            Gc2 = gc2;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
        public string Name { get; }
        public int Frame { get; }
        public long ManagedMemory { get; }
        public int Gc0 { get; }
        public int Gc1 { get; }
        public int Gc2 { get; }
    }

    public readonly struct TypeCallState
    {
        public static readonly TypeCallState Inactive = new TypeCallState(false, BenchmarkMode.Vanilla, 0, string.Empty, string.Empty, 0);

        public TypeCallState(bool active, BenchmarkMode mode, long startTimestamp, string category, string typeName, int frame)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            Category = category;
            TypeName = typeName;
            Frame = frame;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
        public string Category { get; }
        public string TypeName { get; }
        public int Frame { get; }
    }

    private readonly struct TypeProfileInfo
    {
        public static readonly TypeProfileInfo Inactive = new TypeProfileInfo(false, string.Empty, string.Empty);

        public TypeProfileInfo(bool active, string category, string typeName)
        {
            Active = active;
            Category = category;
            TypeName = typeName;
        }

        public bool Active { get; }
        public string Category { get; }
        public string TypeName { get; }
    }

    private readonly struct StageStatsKey : IEquatable<StageStatsKey>
    {
        public StageStatsKey(BenchmarkMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        private BenchmarkMode Mode { get; }
        private string Name { get; }

        public bool Equals(StageStatsKey other)
        {
            return Mode == other.Mode &&
                   StringComparer.Ordinal.Equals(Name, other.Name);
        }

        public override bool Equals(object? obj)
        {
            return obj is StageStatsKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ StringComparer.Ordinal.GetHashCode(Name);
            }
        }
    }

    private readonly struct TypeMethodKey : IEquatable<TypeMethodKey>
    {
        public TypeMethodKey(Type type, string methodName)
        {
            Type = type;
            MethodName = methodName;
        }

        private Type Type { get; }
        private string MethodName { get; }

        public bool Equals(TypeMethodKey other)
        {
            return ReferenceEquals(Type, other.Type) &&
                   StringComparer.Ordinal.Equals(MethodName, other.MethodName);
        }

        public override bool Equals(object? obj)
        {
            return obj is TypeMethodKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Type?.GetHashCode() ?? 0) * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName);
            }
        }
    }

    private readonly struct TypeStatsKey : IEquatable<TypeStatsKey>
    {
        public TypeStatsKey(BenchmarkMode mode, string category, string typeName)
        {
            Mode = mode;
            Category = category;
            TypeName = typeName;
        }

        private BenchmarkMode Mode { get; }
        private string Category { get; }
        private string TypeName { get; }

        public bool Equals(TypeStatsKey other)
        {
            return Mode == other.Mode &&
                   StringComparer.Ordinal.Equals(Category, other.Category) &&
                   StringComparer.Ordinal.Equals(TypeName, other.TypeName);
        }

        public override bool Equals(object? obj)
        {
            return obj is TypeStatsKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Mode;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Category);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(TypeName);
                return hash;
            }
        }
    }

    private sealed class TimedStats
    {
        private TimedStats(BenchmarkMode mode, string name, bool empty)
        {
            Mode = mode;
            Name = name;
            IsEmpty = empty;
        }

        public TimedStats(BenchmarkMode mode, string name)
            : this(mode, name, false)
        {
        }

        public BenchmarkMode Mode { get; }
        public string Name { get; }
        public bool IsEmpty { get; }
        public long Calls { get; private set; }
        public long StopwatchTicks { get; private set; }
        public long MaxStopwatchTicks { get; private set; }

        public static TimedStats CreateEmpty(BenchmarkMode mode, string name)
        {
            return new TimedStats(mode, name, true);
        }

        public void Record(long stopwatchTicks)
        {
            if (IsEmpty)
            {
                return;
            }

            Calls++;
            StopwatchTicks += stopwatchTicks;
            if (stopwatchTicks > MaxStopwatchTicks)
            {
                MaxStopwatchTicks = stopwatchTicks;
            }
        }

        public TimedStatsSnapshot Snapshot()
        {
            return new TimedStatsSnapshot(Mode, Name, Calls, StopwatchTicks, MaxStopwatchTicks);
        }
    }

    private readonly struct TimedStatsSnapshot
    {
        public TimedStatsSnapshot(BenchmarkMode mode, string name, long calls, long stopwatchTicks, long maxStopwatchTicks)
        {
            Mode = mode;
            Name = name;
            Calls = calls;
            StopwatchTicks = stopwatchTicks;
            MaxStopwatchTicks = maxStopwatchTicks;
        }

        public BenchmarkMode Mode { get; }
        public string Name { get; }
        public long Calls { get; }
        public long StopwatchTicks { get; }
        public long MaxStopwatchTicks { get; }
    }
}
