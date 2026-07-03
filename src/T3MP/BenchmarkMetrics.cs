using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class BenchmarkMetrics
{
    private static readonly ModeCounters[] Counters =
    {
        new ModeCounters(BenchmarkMode.Vanilla),
        new ModeCounters(BenchmarkMode.Optimized)
    };

    private static long _aggregateId;

    public static void RecordFrame(BenchmarkMode mode, float deltaSeconds)
    {
        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.SampleFrames);
        Interlocked.Add(ref counters.SampleMicroseconds, Math.Max(0, (long)(deltaSeconds * 1000000f)));
    }

    public static void RecordYielderCall(BenchmarkMode mode, long stopwatchTicks, int candidateCount, object? result)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.YielderCalls);
        Interlocked.Add(ref counters.YielderCandidates, candidateCount);
        Interlocked.Add(ref counters.YielderStopwatchTicks, stopwatchTicks);
        UpdateMax(ref counters.YielderMaxStopwatchTicks, stopwatchTicks);
        counters.RecordYielderFrame(Time.frameCount, stopwatchTicks);

        var kind = ClassifyYielderResult(result);
        switch (kind)
        {
            case YielderResultKind.Found:
                Interlocked.Increment(ref counters.YielderFound);
                break;
            case YielderResultKind.NoYielderInRange:
                Interlocked.Increment(ref counters.YielderNoYielderInRange);
                break;
            case YielderResultKind.Empty:
                Interlocked.Increment(ref counters.YielderEmpty);
                break;
            default:
                Interlocked.Increment(ref counters.YielderUnknownResult);
                break;
        }
    }

    public static void RecordFastYielderFinder(
        BenchmarkMode mode,
        int candidates,
        int yieldingCandidates,
        int pathCalls,
        bool handled,
        bool cacheHit,
        bool cacheMiss,
        int reachableEntries)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.FastAttempts);
        Interlocked.Add(ref counters.FastCandidates, candidates);
        Interlocked.Add(ref counters.FastYieldingCandidates, yieldingCandidates);
        Interlocked.Add(ref counters.FastPathCalls, pathCalls);
        Interlocked.Add(ref counters.FastCacheReachableEntries, reachableEntries);
        if (handled)
        {
            Interlocked.Increment(ref counters.FastHandled);
        }
        else
        {
            Interlocked.Increment(ref counters.FastFallbacks);
        }

        if (cacheHit)
        {
            Interlocked.Increment(ref counters.FastCacheHits);
        }

        if (cacheMiss)
        {
            Interlocked.Increment(ref counters.FastCacheMisses);
        }
    }

    public static void RecordFarmCall(BenchmarkMode mode, long stopwatchTicks, object? result)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.FarmCalls);
        Interlocked.Add(ref counters.FarmStopwatchTicks, stopwatchTicks);
        UpdateMax(ref counters.FarmMaxStopwatchTicks, stopwatchTicks);

        var kind = ClassifyYielderResult(result);
        switch (kind)
        {
            case YielderResultKind.Found:
                Interlocked.Increment(ref counters.FarmFound);
                break;
            case YielderResultKind.NoYielderInRange:
                Interlocked.Increment(ref counters.FarmNoYielderInRange);
                break;
            case YielderResultKind.Empty:
                Interlocked.Increment(ref counters.FarmEmpty);
                break;
            default:
                Interlocked.Increment(ref counters.FarmUnknownResult);
                break;
        }
    }

    public static void RecordFarmYielderOptimizer(BenchmarkMode mode, FarmYielderOptimizer.FarmOptimizerStats stats)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var counters = Counters[(int)mode];
        if (stats.Handled)
        {
            Interlocked.Increment(ref counters.FarmHandled);
        }

        if (stats.Fallback)
        {
            Interlocked.Increment(ref counters.FarmFallbacks);
        }

        Interlocked.Add(ref counters.FarmIndexBuilds, stats.IndexBuilds);
        Interlocked.Add(ref counters.FarmIndexCells, stats.IndexCells);
        Interlocked.Add(ref counters.FarmPathCalls, stats.PathCalls);
        Interlocked.Add(ref counters.FarmDynamicRefreshes, stats.DynamicRefreshes);
        Interlocked.Add(ref counters.FarmIndexBuildStopwatchTicks, stats.IndexBuildStopwatchTicks);
        Interlocked.Add(ref counters.FarmDynamicRefreshStopwatchTicks, stats.DynamicRefreshStopwatchTicks);
        Interlocked.Add(ref counters.FarmReadyQueries, stats.ReadyQueries);
        Interlocked.Add(ref counters.FarmLiveRejects, stats.LiveRejects);
        Interlocked.Add(ref counters.FarmBitClears, stats.BitClears);
    }

    public static void RecordInRangeYieldersCall(BenchmarkMode mode, long stopwatchTicks, int returnedCount, bool result)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.InRangeCalls);
        Interlocked.Add(ref counters.InRangeReturned, returnedCount);
        Interlocked.Add(ref counters.InRangeStopwatchTicks, stopwatchTicks);
        UpdateMax(ref counters.InRangeMaxStopwatchTicks, stopwatchTicks);
        if (result)
        {
            Interlocked.Increment(ref counters.InRangeTrueResults);
        }
    }

    public static void RecordNavigationCall(BenchmarkMode mode, string methodName, bool inYielderFinder, long stopwatchTicks)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.NavigationCalls);
        Interlocked.Add(ref counters.NavigationStopwatchTicks, stopwatchTicks);
        UpdateMax(ref counters.NavigationMaxStopwatchTicks, stopwatchTicks);
        switch (methodName)
        {
            case "FindTerrainPath":
                Interlocked.Increment(ref counters.NavigationFindTerrainPath);
                Interlocked.Add(ref counters.NavigationFindTerrainPathStopwatchTicks, stopwatchTicks);
                if (inYielderFinder)
                {
                    Interlocked.Increment(ref counters.YielderTerrainPathCalls);
                    Interlocked.Add(ref counters.YielderTerrainPathStopwatchTicks, stopwatchTicks);
                }

                break;
            case "FindRoadPath":
                Interlocked.Increment(ref counters.NavigationFindRoadPath);
                Interlocked.Add(ref counters.NavigationFindRoadPathStopwatchTicks, stopwatchTicks);
                if (inYielderFinder)
                {
                    Interlocked.Increment(ref counters.YielderRoadPathCalls);
                    Interlocked.Add(ref counters.YielderRoadPathStopwatchTicks, stopwatchTicks);
                }

                break;
            case "DestinationIsReachableUnlimitedRange":
                Interlocked.Increment(ref counters.NavigationReachableUnlimited);
                Interlocked.Add(ref counters.NavigationReachableUnlimitedStopwatchTicks, stopwatchTicks);
                break;
            case "FindPathUnlimitedRange":
                Interlocked.Increment(ref counters.NavigationFindPathUnlimited);
                Interlocked.Add(ref counters.NavigationFindPathUnlimitedStopwatchTicks, stopwatchTicks);
                break;
            default:
                Interlocked.Increment(ref counters.NavigationOther);
                Interlocked.Add(ref counters.NavigationOtherStopwatchTicks, stopwatchTicks);
                break;
        }
    }

    public static void LogAndReset(float elapsedSeconds)
    {
        var aggregateId = Interlocked.Increment(ref _aggregateId);
        var vanilla = Counters[(int)BenchmarkMode.Vanilla].SnapshotAndReset();
        var optimized = Counters[(int)BenchmarkMode.Optimized].SnapshotAndReset();

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] Bench aggregate={0}, elapsedSeconds={1:F2}, segmentSeconds={2:F2}, warmupFrames={3}, optimizedImplementation={4}",
            aggregateId,
            elapsedSeconds,
            BenchmarkSettings.ModeSegmentSeconds,
            BenchmarkSettings.WarmupFramesAfterSwitch,
            BenchmarkSettings.OptimizedImplementationName));
        LogModeSnapshot(aggregateId, vanilla);
        LogModeSnapshot(aggregateId, optimized);
        WalkerDistanceCache.LogAndReset(aggregateId);
        NeedBehaviorDecisionSampler.LogAndReset(aggregateId);
        NeedBehaviorTravelOptimizer.LogAndReset(aggregateId);
        NeedActionFlowFieldProbe.LogAndReset(aggregateId);
        WalkerMoverDelegateCacheOptimizer.LogAndReset(aggregateId);
        PathFollowerNoAnimationFastMove.LogAndReset(aggregateId);
        PathFollowerProfiler.LogAndReset(aggregateId);
        AnimatedPathFollowerHorizontalOptimizer.LogAndReset(aggregateId);
        CarryAmountCalculatorOptimizer.LogAndReset(aggregateId);
        GoodCarrierLiftingCapacityCache.LogAndReset(aggregateId);
        NeedManagerDirectCriticalState.LogAndReset(aggregateId);
        NeedManagerFastTick.LogAndReset(aggregateId);
        DistrictNeedBehaviorDirectOptimizer.LogAndReset(aggregateId);
        BeaverNeedDecisionFrequencySampler.LogAndReset(aggregateId, elapsedSeconds);
        NoActionCooldown.LogAndReset(aggregateId);
        FastYielderFinder.LogAndReset(aggregateId);
        FarmYielderOptimizer.LogAndReset(aggregateId);
        FarmHouseBehaviorDirectOptimizer.LogAndReset(aggregateId);
        PlantingSpotFinderOptimizer.LogAndReset(aggregateId);
        LumberjackYielderOptimizer.LogAndReset(aggregateId);
        GatherWorkplaceOptimizer.LogAndReset(aggregateId);
        HaulCandidateOrderCache.LogAndReset(aggregateId);
        HaulNoActionFrameCache.LogAndReset(aggregateId);
        WorkplaceNoActionFrameCache.LogAndReset(aggregateId);
        InventoryStockDistanceCache.LogAndReset(aggregateId);
        InventoryNeedGoodOptimizer.LogAndReset(aggregateId);
        InventoryCapacityDistanceCache.LogAndReset(aggregateId);
        InventoryCapacityVectorProfiler.LogAndReset(aggregateId);
        FillInputWorkplaceOptimizer.LogAndReset(aggregateId);
        WaitInsideIdlyOptimizer.LogAndReset(aggregateId);
        WorkerRootMetricsBypass.LogAndReset(aggregateId);
        WorkerWorkingSpeedOptimizer.LogAndReset(aggregateId);
        BehaviorManagerProcessOptimizer.LogAndReset(aggregateId);
        ExecutorTickProfiler.LogAndReset(aggregateId);
        DistrictResourceCounterThrottle.LogAndReset(aggregateId);
        WaterObjectServiceThrottle.LogAndReset(aggregateId);
        WaterObjectServiceFastSkip.LogAndReset(aggregateId);
        ThreadSafeWaterMapTickThrottle.LogAndReset(aggregateId);
        RangedEffectSubjectThrottle.LogAndReset(aggregateId);
        ContaminationApplierThrottle.LogAndReset(aggregateId);
        NavigationCallerSampler.LogAndReset(aggregateId);
        MainLoopProfiler.LogAndReset(aggregateId);
        UnityMarkerProfiler.LogAndReset(aggregateId);
        AnimatorRegistryProfiler.LogAndReset(aggregateId);
        MechanicalAnimationBatchProbe.LogAndReset(aggregateId);
        MechanicalDirectRotationOptimizer.LogAndReset(aggregateId);
        DefaultMechanicalAnimatorOptimizer.LogAndReset(aggregateId);
        RangedEffectSubjectProfiler.LogAndReset(aggregateId);
        StatusAggregatorThrottle.LogAndReset(aggregateId);
        SoundListenerStaticCameraOptimizer.LogAndReset(aggregateId);
    }

    public static void Reset()
    {
        Counters[(int)BenchmarkMode.Vanilla].SnapshotAndReset();
        Counters[(int)BenchmarkMode.Optimized].SnapshotAndReset();
        UnityMarkerProfiler.Reset();
        AnimatorRegistryProfiler.Reset();
        MechanicalAnimationBatchProbe.Reset();
        MechanicalDirectRotationOptimizer.Reset();
        DefaultMechanicalAnimatorOptimizer.Reset();
        RangedEffectSubjectProfiler.Reset();
        RangedEffectSubjectThrottle.Reset();
        WaterObjectServiceFastSkip.Reset();
        ThreadSafeWaterMapTickThrottle.Reset();
        ContaminationApplierThrottle.Reset();
        StatusAggregatorThrottle.Reset();
        SoundListenerStaticCameraOptimizer.Reset();
        FarmHouseBehaviorDirectOptimizer.Reset();
        WalkerMoverDelegateCacheOptimizer.Reset();
        PathFollowerNoAnimationFastMove.Reset();
        PathFollowerProfiler.Reset();
        AnimatedPathFollowerHorizontalOptimizer.Reset();
        CarryAmountCalculatorOptimizer.Reset();
        InventoryNeedGoodOptimizer.Reset();
        InventoryCapacityVectorProfiler.Reset();
        NeedManagerDirectCriticalState.Reset();
        NeedManagerFastTick.Reset();
        DistrictNeedBehaviorDirectOptimizer.Reset();
        ExecutorTickProfiler.Reset();
        WaitInsideIdlyOptimizer.Reset();
        WorkerWorkingSpeedOptimizer.Reset();
    }

    public static string GetCurrentSummary(BenchmarkMode mode)
    {
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return "detailedMetrics=disabled";
        }

        var counters = Counters[(int)mode];
        return string.Format(
            CultureInfo.InvariantCulture,
            "sampleFrames={0},yielderCalls={1},yielderMs={2:F2},yielderMaxMs={3:F3},fastPathCalls={4},inRangeCalls={5},inRangeMs={6:F2},navCalls={7},navMs={8:F2},navMaxMs={9:F3},navRoadMs={10:F2},navPathUnlimitedMs={11:F2},farmCalls={12},farmMs={13:F2},farmMaxMs={14:F3},farmDynamicMs={15:F2}",
            Volatile.Read(ref counters.SampleFrames),
            Volatile.Read(ref counters.YielderCalls),
            ToMilliseconds(Volatile.Read(ref counters.YielderStopwatchTicks)),
            ToMilliseconds(Volatile.Read(ref counters.YielderMaxStopwatchTicks)),
            Volatile.Read(ref counters.FastPathCalls),
            Volatile.Read(ref counters.InRangeCalls),
            ToMilliseconds(Volatile.Read(ref counters.InRangeStopwatchTicks)),
            Volatile.Read(ref counters.NavigationCalls),
            ToMilliseconds(Volatile.Read(ref counters.NavigationStopwatchTicks)),
            ToMilliseconds(Volatile.Read(ref counters.NavigationMaxStopwatchTicks)),
            ToMilliseconds(Volatile.Read(ref counters.NavigationFindRoadPathStopwatchTicks)),
            ToMilliseconds(Volatile.Read(ref counters.NavigationFindPathUnlimitedStopwatchTicks)),
            Volatile.Read(ref counters.FarmCalls),
            ToMilliseconds(Volatile.Read(ref counters.FarmStopwatchTicks)),
            ToMilliseconds(Volatile.Read(ref counters.FarmMaxStopwatchTicks)),
            ToMilliseconds(Volatile.Read(ref counters.FarmDynamicRefreshStopwatchTicks)));
    }

    private static void LogModeSnapshot(long aggregateId, ModeSnapshot snapshot)
    {
        var yielderMs = ToMilliseconds(snapshot.YielderStopwatchTicks);
        var inRangeMs = ToMilliseconds(snapshot.InRangeStopwatchTicks);
        var navMs = ToMilliseconds(snapshot.NavigationStopwatchTicks);
        var sampleSeconds = snapshot.SampleMicroseconds / 1000000.0;
        MainLoopProfiler.LogFrameCpuSummary(aggregateId, snapshot.Mode, snapshot.SampleFrames, sampleSeconds);
        SimulationProgressMetrics.LogAndResetMode(aggregateId, snapshot.Mode, sampleSeconds);
        if (!BenchmarkSettings.EnableBenchmarkDetailedMetrics)
        {
            return;
        }

        var yielderAvgUs = snapshot.YielderCalls > 0 ? ToMicroseconds(snapshot.YielderStopwatchTicks) / snapshot.YielderCalls : 0;
        var farmMs = ToMilliseconds(snapshot.FarmStopwatchTicks);
        var farmAvgUs = snapshot.FarmCalls > 0 ? ToMicroseconds(snapshot.FarmStopwatchTicks) / snapshot.FarmCalls : 0;
        var inRangeAvgUs = snapshot.InRangeCalls > 0 ? ToMicroseconds(snapshot.InRangeStopwatchTicks) / snapshot.InRangeCalls : 0;
        var navAvgUs = snapshot.NavigationCalls > 0 ? ToMicroseconds(snapshot.NavigationStopwatchTicks) / snapshot.NavigationCalls : 0;
        var yielderAvgCandidates = snapshot.YielderCalls > 0 ? (double)snapshot.YielderCandidates / snapshot.YielderCalls : 0;
        var inRangeAvgReturned = snapshot.InRangeCalls > 0 ? (double)snapshot.InRangeReturned / snapshot.InRangeCalls : 0;
        var yielderFrameAvgMs = snapshot.YielderActiveFrames > 0 ? yielderMs / snapshot.YielderActiveFrames : 0;

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] Bench mode={0}, aggregate={1}, sampleFrames={2}, sampleSeconds={3:F2}, yielderCalls={4}, yielderCandidates={5}, yielderAvgCandidates={6:F2}, yielderMs={7:F2}, yielderAvgUs={8:F2}, yielderMaxMs={9:F3}, yielderActiveFrames={10}, yielderFrameAvgMs={11:F3}, yielderFrameP95Ms={12:F3}, yielderFrameMaxMs={13:F3}, fastAttempts={14}, fastHandled={15}, fastFallbacks={16}, fastCandidates={17}, fastYieldingCandidates={18}, fastPathCalls={19}, found={20}, empty={21}, noYielderInRange={22}, inRangeCalls={23}, inRangeReturned={24}, inRangeAvgReturned={25:F2}, inRangeMs={26:F2}, inRangeAvgUs={27:F2}, inRangeMaxMs={28:F3}, navCalls={29}, navTerrain={30}, navRoad={31}, navReachable={32}, navPathUnlimited={33}, yielderTerrainPath={34}, yielderRoadPath={35}, fastCacheHits={36}, fastCacheMisses={37}, fastCacheReachable={38}, farmCalls={39}, farmMs={40:F2}, farmAvgUs={41:F2}, farmMaxMs={42:F3}, farmHandled={43}, farmFallbacks={44}, farmIndexBuilds={45}, farmIndexCells={46}, farmPathCalls={47}, farmDynamicRefreshes={48}, farmIndexBuildMs={49:F2}, farmDynamicRefreshMs={50:F2}, farmReadyQueries={51}, farmLiveRejects={52}, farmBitClears={53}, farmFound={54}, farmEmpty={55}, farmNoYielderInRange={56}, navMs={57:F2}, navAvgUs={58:F2}, navMaxMs={59:F3}, navTerrainMs={60:F2}, navRoadMs={61:F2}, navReachableMs={62:F2}, navPathUnlimitedMs={63:F2}, navOtherMs={64:F2}, yielderTerrainPathMs={65:F2}, yielderRoadPathMs={66:F2}",
            snapshot.Mode,
            aggregateId,
            snapshot.SampleFrames,
            sampleSeconds,
            snapshot.YielderCalls,
            snapshot.YielderCandidates,
            yielderAvgCandidates,
            yielderMs,
            yielderAvgUs,
            ToMilliseconds(snapshot.YielderMaxStopwatchTicks),
            snapshot.YielderActiveFrames,
            yielderFrameAvgMs,
            ToMilliseconds(snapshot.YielderFrameP95StopwatchTicks),
            ToMilliseconds(snapshot.YielderFrameMaxStopwatchTicks),
            snapshot.FastAttempts,
            snapshot.FastHandled,
            snapshot.FastFallbacks,
            snapshot.FastCandidates,
            snapshot.FastYieldingCandidates,
            snapshot.FastPathCalls,
            snapshot.YielderFound,
            snapshot.YielderEmpty,
            snapshot.YielderNoYielderInRange,
            snapshot.InRangeCalls,
            snapshot.InRangeReturned,
            inRangeAvgReturned,
            inRangeMs,
            inRangeAvgUs,
            ToMilliseconds(snapshot.InRangeMaxStopwatchTicks),
            snapshot.NavigationCalls,
            snapshot.NavigationFindTerrainPath,
            snapshot.NavigationFindRoadPath,
            snapshot.NavigationReachableUnlimited,
            snapshot.NavigationFindPathUnlimited,
            snapshot.YielderTerrainPathCalls,
            snapshot.YielderRoadPathCalls,
            snapshot.FastCacheHits,
            snapshot.FastCacheMisses,
            snapshot.FastCacheReachableEntries,
            snapshot.FarmCalls,
            farmMs,
            farmAvgUs,
            ToMilliseconds(snapshot.FarmMaxStopwatchTicks),
            snapshot.FarmHandled,
            snapshot.FarmFallbacks,
            snapshot.FarmIndexBuilds,
            snapshot.FarmIndexCells,
            snapshot.FarmPathCalls,
            snapshot.FarmDynamicRefreshes,
            ToMilliseconds(snapshot.FarmIndexBuildStopwatchTicks),
            ToMilliseconds(snapshot.FarmDynamicRefreshStopwatchTicks),
            snapshot.FarmReadyQueries,
            snapshot.FarmLiveRejects,
            snapshot.FarmBitClears,
            snapshot.FarmFound,
            snapshot.FarmEmpty,
            snapshot.FarmNoYielderInRange,
            navMs,
            navAvgUs,
            ToMilliseconds(snapshot.NavigationMaxStopwatchTicks),
            ToMilliseconds(snapshot.NavigationFindTerrainPathStopwatchTicks),
            ToMilliseconds(snapshot.NavigationFindRoadPathStopwatchTicks),
            ToMilliseconds(snapshot.NavigationReachableUnlimitedStopwatchTicks),
            ToMilliseconds(snapshot.NavigationFindPathUnlimitedStopwatchTicks),
            ToMilliseconds(snapshot.NavigationOtherStopwatchTicks),
            ToMilliseconds(snapshot.YielderTerrainPathStopwatchTicks),
            ToMilliseconds(snapshot.YielderRoadPathStopwatchTicks)));
    }

    private static YielderResultKind ClassifyYielderResult(object? result)
    {
        if (result is null)
        {
            return YielderResultKind.Unknown;
        }

        try
        {
            var type = result.GetType();
            if (type.GetProperty("HasYielder")?.GetValue(result) is bool hasYielder && hasYielder)
            {
                return YielderResultKind.Found;
            }

            if (type.GetProperty("NoYielderInRange")?.GetValue(result) is bool noYielderInRange && noYielderInRange)
            {
                return YielderResultKind.NoYielderInRange;
            }

            return YielderResultKind.Empty;
        }
        catch (Exception)
        {
            return YielderResultKind.Unknown;
        }
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

            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }
        }
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static double ToMicroseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000000.0 / Stopwatch.Frequency;
    }

    private enum YielderResultKind
    {
        Unknown,
        Found,
        Empty,
        NoYielderInRange
    }

    private sealed class ModeCounters
    {
        public ModeCounters(BenchmarkMode mode)
        {
            Mode = mode;
        }

        public BenchmarkMode Mode { get; }
        public long SampleFrames;
        public long SampleMicroseconds;
        public long YielderCalls;
        public long YielderCandidates;
        public long YielderStopwatchTicks;
        public long YielderMaxStopwatchTicks;
        public long YielderFound;
        public long YielderEmpty;
        public long YielderNoYielderInRange;
        public long YielderUnknownResult;
        public long InRangeCalls;
        public long InRangeReturned;
        public long InRangeTrueResults;
        public long InRangeStopwatchTicks;
        public long InRangeMaxStopwatchTicks;
        public long NavigationCalls;
        public long NavigationFindTerrainPath;
        public long NavigationFindRoadPath;
        public long NavigationReachableUnlimited;
        public long NavigationFindPathUnlimited;
        public long NavigationOther;
        public long NavigationStopwatchTicks;
        public long NavigationMaxStopwatchTicks;
        public long NavigationFindTerrainPathStopwatchTicks;
        public long NavigationFindRoadPathStopwatchTicks;
        public long NavigationReachableUnlimitedStopwatchTicks;
        public long NavigationFindPathUnlimitedStopwatchTicks;
        public long NavigationOtherStopwatchTicks;
        public long YielderTerrainPathCalls;
        public long YielderRoadPathCalls;
        public long YielderTerrainPathStopwatchTicks;
        public long YielderRoadPathStopwatchTicks;
        public long FastAttempts;
        public long FastHandled;
        public long FastFallbacks;
        public long FastCandidates;
        public long FastYieldingCandidates;
        public long FastPathCalls;
        public long FastCacheHits;
        public long FastCacheMisses;
        public long FastCacheReachableEntries;
        public long FarmCalls;
        public long FarmStopwatchTicks;
        public long FarmMaxStopwatchTicks;
        public long FarmFound;
        public long FarmEmpty;
        public long FarmNoYielderInRange;
        public long FarmUnknownResult;
        public long FarmHandled;
        public long FarmFallbacks;
        public long FarmIndexBuilds;
        public long FarmIndexCells;
        public long FarmPathCalls;
        public long FarmDynamicRefreshes;
        public long FarmIndexBuildStopwatchTicks;
        public long FarmDynamicRefreshStopwatchTicks;
        public long FarmReadyQueries;
        public long FarmLiveRejects;
        public long FarmBitClears;
        private readonly object _frameLock = new object();
        private readonly List<long> _yielderFrameTicks = new List<long>();
        private int _currentYielderFrame = -1;
        private long _currentYielderFrameTicks;

        public void RecordYielderFrame(int frame, long stopwatchTicks)
        {
            if (stopwatchTicks <= 0)
            {
                return;
            }

            lock (_frameLock)
            {
                if (_currentYielderFrame != frame)
                {
                    FlushYielderFrameNoLock();
                    _currentYielderFrame = frame;
                }

                _currentYielderFrameTicks += stopwatchTicks;
            }
        }

        public ModeSnapshot SnapshotAndReset()
        {
            var yielderFrameStats = SnapshotYielderFrames();
            return new ModeSnapshot(
                Mode,
                Interlocked.Exchange(ref SampleFrames, 0),
                Interlocked.Exchange(ref SampleMicroseconds, 0),
                Interlocked.Exchange(ref YielderCalls, 0),
                Interlocked.Exchange(ref YielderCandidates, 0),
                Interlocked.Exchange(ref YielderStopwatchTicks, 0),
                Interlocked.Exchange(ref YielderMaxStopwatchTicks, 0),
                yielderFrameStats.ActiveFrames,
                yielderFrameStats.P95StopwatchTicks,
                yielderFrameStats.MaxStopwatchTicks,
                Interlocked.Exchange(ref YielderFound, 0),
                Interlocked.Exchange(ref YielderEmpty, 0),
                Interlocked.Exchange(ref YielderNoYielderInRange, 0),
                Interlocked.Exchange(ref YielderUnknownResult, 0),
                Interlocked.Exchange(ref InRangeCalls, 0),
                Interlocked.Exchange(ref InRangeReturned, 0),
                Interlocked.Exchange(ref InRangeTrueResults, 0),
                Interlocked.Exchange(ref InRangeStopwatchTicks, 0),
                Interlocked.Exchange(ref InRangeMaxStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationCalls, 0),
                Interlocked.Exchange(ref NavigationFindTerrainPath, 0),
                Interlocked.Exchange(ref NavigationFindRoadPath, 0),
                Interlocked.Exchange(ref NavigationReachableUnlimited, 0),
                Interlocked.Exchange(ref NavigationFindPathUnlimited, 0),
                Interlocked.Exchange(ref NavigationOther, 0),
                Interlocked.Exchange(ref NavigationStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationMaxStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationFindTerrainPathStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationFindRoadPathStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationReachableUnlimitedStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationFindPathUnlimitedStopwatchTicks, 0),
                Interlocked.Exchange(ref NavigationOtherStopwatchTicks, 0),
                Interlocked.Exchange(ref YielderTerrainPathCalls, 0),
                Interlocked.Exchange(ref YielderRoadPathCalls, 0),
                Interlocked.Exchange(ref YielderTerrainPathStopwatchTicks, 0),
                Interlocked.Exchange(ref YielderRoadPathStopwatchTicks, 0),
                Interlocked.Exchange(ref FastAttempts, 0),
                Interlocked.Exchange(ref FastHandled, 0),
                Interlocked.Exchange(ref FastFallbacks, 0),
                Interlocked.Exchange(ref FastCandidates, 0),
                Interlocked.Exchange(ref FastYieldingCandidates, 0),
                Interlocked.Exchange(ref FastPathCalls, 0),
                Interlocked.Exchange(ref FastCacheHits, 0),
                Interlocked.Exchange(ref FastCacheMisses, 0),
                Interlocked.Exchange(ref FastCacheReachableEntries, 0),
                Interlocked.Exchange(ref FarmCalls, 0),
                Interlocked.Exchange(ref FarmStopwatchTicks, 0),
                Interlocked.Exchange(ref FarmMaxStopwatchTicks, 0),
                Interlocked.Exchange(ref FarmFound, 0),
                Interlocked.Exchange(ref FarmEmpty, 0),
                Interlocked.Exchange(ref FarmNoYielderInRange, 0),
                Interlocked.Exchange(ref FarmUnknownResult, 0),
                Interlocked.Exchange(ref FarmHandled, 0),
                Interlocked.Exchange(ref FarmFallbacks, 0),
                Interlocked.Exchange(ref FarmIndexBuilds, 0),
                Interlocked.Exchange(ref FarmIndexCells, 0),
                Interlocked.Exchange(ref FarmPathCalls, 0),
                Interlocked.Exchange(ref FarmDynamicRefreshes, 0),
                Interlocked.Exchange(ref FarmIndexBuildStopwatchTicks, 0),
                Interlocked.Exchange(ref FarmDynamicRefreshStopwatchTicks, 0),
                Interlocked.Exchange(ref FarmReadyQueries, 0),
                Interlocked.Exchange(ref FarmLiveRejects, 0),
                Interlocked.Exchange(ref FarmBitClears, 0));
        }

        private YielderFrameStats SnapshotYielderFrames()
        {
            lock (_frameLock)
            {
                FlushYielderFrameNoLock();
                if (_yielderFrameTicks.Count == 0)
                {
                    return default;
                }

                var ticks = _yielderFrameTicks.ToArray();
                Array.Sort(ticks);
                var p95Index = Math.Max(0, (int)Math.Ceiling(ticks.Length * 0.95) - 1);
                var stats = new YielderFrameStats(ticks.Length, ticks[p95Index], ticks[ticks.Length - 1]);
                _yielderFrameTicks.Clear();
                _currentYielderFrame = -1;
                _currentYielderFrameTicks = 0;
                return stats;
            }
        }

        private void FlushYielderFrameNoLock()
        {
            if (_currentYielderFrameTicks > 0)
            {
                _yielderFrameTicks.Add(_currentYielderFrameTicks);
                _currentYielderFrameTicks = 0;
            }
        }
    }

    private readonly struct YielderFrameStats
    {
        public YielderFrameStats(long activeFrames, long p95StopwatchTicks, long maxStopwatchTicks)
        {
            ActiveFrames = activeFrames;
            P95StopwatchTicks = p95StopwatchTicks;
            MaxStopwatchTicks = maxStopwatchTicks;
        }

        public long ActiveFrames { get; }
        public long P95StopwatchTicks { get; }
        public long MaxStopwatchTicks { get; }
    }

    private readonly struct ModeSnapshot
    {
        public ModeSnapshot(
            BenchmarkMode mode,
            long sampleFrames,
            long sampleMicroseconds,
            long yielderCalls,
            long yielderCandidates,
            long yielderStopwatchTicks,
            long yielderMaxStopwatchTicks,
            long yielderActiveFrames,
            long yielderFrameP95StopwatchTicks,
            long yielderFrameMaxStopwatchTicks,
            long yielderFound,
            long yielderEmpty,
            long yielderNoYielderInRange,
            long yielderUnknownResult,
            long inRangeCalls,
            long inRangeReturned,
            long inRangeTrueResults,
            long inRangeStopwatchTicks,
            long inRangeMaxStopwatchTicks,
            long navigationCalls,
            long navigationFindTerrainPath,
            long navigationFindRoadPath,
            long navigationReachableUnlimited,
            long navigationFindPathUnlimited,
            long navigationOther,
            long navigationStopwatchTicks,
            long navigationMaxStopwatchTicks,
            long navigationFindTerrainPathStopwatchTicks,
            long navigationFindRoadPathStopwatchTicks,
            long navigationReachableUnlimitedStopwatchTicks,
            long navigationFindPathUnlimitedStopwatchTicks,
            long navigationOtherStopwatchTicks,
            long yielderTerrainPathCalls,
            long yielderRoadPathCalls,
            long yielderTerrainPathStopwatchTicks,
            long yielderRoadPathStopwatchTicks,
            long fastAttempts,
            long fastHandled,
            long fastFallbacks,
            long fastCandidates,
            long fastYieldingCandidates,
            long fastPathCalls,
            long fastCacheHits,
            long fastCacheMisses,
            long fastCacheReachableEntries,
            long farmCalls,
            long farmStopwatchTicks,
            long farmMaxStopwatchTicks,
            long farmFound,
            long farmEmpty,
            long farmNoYielderInRange,
            long farmUnknownResult,
            long farmHandled,
            long farmFallbacks,
            long farmIndexBuilds,
            long farmIndexCells,
            long farmPathCalls,
            long farmDynamicRefreshes,
            long farmIndexBuildStopwatchTicks,
            long farmDynamicRefreshStopwatchTicks,
            long farmReadyQueries,
            long farmLiveRejects,
            long farmBitClears)
        {
            Mode = mode;
            SampleFrames = sampleFrames;
            SampleMicroseconds = sampleMicroseconds;
            YielderCalls = yielderCalls;
            YielderCandidates = yielderCandidates;
            YielderStopwatchTicks = yielderStopwatchTicks;
            YielderMaxStopwatchTicks = yielderMaxStopwatchTicks;
            YielderActiveFrames = yielderActiveFrames;
            YielderFrameP95StopwatchTicks = yielderFrameP95StopwatchTicks;
            YielderFrameMaxStopwatchTicks = yielderFrameMaxStopwatchTicks;
            YielderFound = yielderFound;
            YielderEmpty = yielderEmpty;
            YielderNoYielderInRange = yielderNoYielderInRange;
            YielderUnknownResult = yielderUnknownResult;
            InRangeCalls = inRangeCalls;
            InRangeReturned = inRangeReturned;
            InRangeTrueResults = inRangeTrueResults;
            InRangeStopwatchTicks = inRangeStopwatchTicks;
            InRangeMaxStopwatchTicks = inRangeMaxStopwatchTicks;
            NavigationCalls = navigationCalls;
            NavigationFindTerrainPath = navigationFindTerrainPath;
            NavigationFindRoadPath = navigationFindRoadPath;
            NavigationReachableUnlimited = navigationReachableUnlimited;
            NavigationFindPathUnlimited = navigationFindPathUnlimited;
            NavigationOther = navigationOther;
            NavigationStopwatchTicks = navigationStopwatchTicks;
            NavigationMaxStopwatchTicks = navigationMaxStopwatchTicks;
            NavigationFindTerrainPathStopwatchTicks = navigationFindTerrainPathStopwatchTicks;
            NavigationFindRoadPathStopwatchTicks = navigationFindRoadPathStopwatchTicks;
            NavigationReachableUnlimitedStopwatchTicks = navigationReachableUnlimitedStopwatchTicks;
            NavigationFindPathUnlimitedStopwatchTicks = navigationFindPathUnlimitedStopwatchTicks;
            NavigationOtherStopwatchTicks = navigationOtherStopwatchTicks;
            YielderTerrainPathCalls = yielderTerrainPathCalls;
            YielderRoadPathCalls = yielderRoadPathCalls;
            YielderTerrainPathStopwatchTicks = yielderTerrainPathStopwatchTicks;
            YielderRoadPathStopwatchTicks = yielderRoadPathStopwatchTicks;
            FastAttempts = fastAttempts;
            FastHandled = fastHandled;
            FastFallbacks = fastFallbacks;
            FastCandidates = fastCandidates;
            FastYieldingCandidates = fastYieldingCandidates;
            FastPathCalls = fastPathCalls;
            FastCacheHits = fastCacheHits;
            FastCacheMisses = fastCacheMisses;
            FastCacheReachableEntries = fastCacheReachableEntries;
            FarmCalls = farmCalls;
            FarmStopwatchTicks = farmStopwatchTicks;
            FarmMaxStopwatchTicks = farmMaxStopwatchTicks;
            FarmFound = farmFound;
            FarmEmpty = farmEmpty;
            FarmNoYielderInRange = farmNoYielderInRange;
            FarmUnknownResult = farmUnknownResult;
            FarmHandled = farmHandled;
            FarmFallbacks = farmFallbacks;
            FarmIndexBuilds = farmIndexBuilds;
            FarmIndexCells = farmIndexCells;
            FarmPathCalls = farmPathCalls;
            FarmDynamicRefreshes = farmDynamicRefreshes;
            FarmIndexBuildStopwatchTicks = farmIndexBuildStopwatchTicks;
            FarmDynamicRefreshStopwatchTicks = farmDynamicRefreshStopwatchTicks;
            FarmReadyQueries = farmReadyQueries;
            FarmLiveRejects = farmLiveRejects;
            FarmBitClears = farmBitClears;
        }

        public BenchmarkMode Mode { get; }
        public long SampleFrames { get; }
        public long SampleMicroseconds { get; }
        public long YielderCalls { get; }
        public long YielderCandidates { get; }
        public long YielderStopwatchTicks { get; }
        public long YielderMaxStopwatchTicks { get; }
        public long YielderActiveFrames { get; }
        public long YielderFrameP95StopwatchTicks { get; }
        public long YielderFrameMaxStopwatchTicks { get; }
        public long YielderFound { get; }
        public long YielderEmpty { get; }
        public long YielderNoYielderInRange { get; }
        public long YielderUnknownResult { get; }
        public long InRangeCalls { get; }
        public long InRangeReturned { get; }
        public long InRangeTrueResults { get; }
        public long InRangeStopwatchTicks { get; }
        public long InRangeMaxStopwatchTicks { get; }
        public long NavigationCalls { get; }
        public long NavigationFindTerrainPath { get; }
        public long NavigationFindRoadPath { get; }
        public long NavigationReachableUnlimited { get; }
        public long NavigationFindPathUnlimited { get; }
        public long NavigationOther { get; }
        public long NavigationStopwatchTicks { get; }
        public long NavigationMaxStopwatchTicks { get; }
        public long NavigationFindTerrainPathStopwatchTicks { get; }
        public long NavigationFindRoadPathStopwatchTicks { get; }
        public long NavigationReachableUnlimitedStopwatchTicks { get; }
        public long NavigationFindPathUnlimitedStopwatchTicks { get; }
        public long NavigationOtherStopwatchTicks { get; }
        public long YielderTerrainPathCalls { get; }
        public long YielderRoadPathCalls { get; }
        public long YielderTerrainPathStopwatchTicks { get; }
        public long YielderRoadPathStopwatchTicks { get; }
        public long FastAttempts { get; }
        public long FastHandled { get; }
        public long FastFallbacks { get; }
        public long FastCandidates { get; }
        public long FastYieldingCandidates { get; }
        public long FastPathCalls { get; }
        public long FastCacheHits { get; }
        public long FastCacheMisses { get; }
        public long FastCacheReachableEntries { get; }
        public long FarmCalls { get; }
        public long FarmStopwatchTicks { get; }
        public long FarmMaxStopwatchTicks { get; }
        public long FarmFound { get; }
        public long FarmEmpty { get; }
        public long FarmNoYielderInRange { get; }
        public long FarmUnknownResult { get; }
        public long FarmHandled { get; }
        public long FarmFallbacks { get; }
        public long FarmIndexBuilds { get; }
        public long FarmIndexCells { get; }
        public long FarmPathCalls { get; }
        public long FarmDynamicRefreshes { get; }
        public long FarmIndexBuildStopwatchTicks { get; }
        public long FarmDynamicRefreshStopwatchTicks { get; }
        public long FarmReadyQueries { get; }
        public long FarmLiveRejects { get; }
        public long FarmBitClears { get; }
    }
}
