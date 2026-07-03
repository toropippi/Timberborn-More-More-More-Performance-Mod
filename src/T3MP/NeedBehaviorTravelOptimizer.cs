using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NeedBehaviorTravelOptimizer
{
    [ThreadStatic]
    private static PickBestContext? _pickBestContext;

    [ThreadStatic]
    private static PickBestContext? _reusablePickBestContext;

    [ThreadStatic]
    private static DurationContext _durationContext;

    private static readonly object ActionPositionLock = new object();
    private static readonly Dictionary<PositionKey, int> ActionPositionCounts = new Dictionary<PositionKey, int>();
    private static readonly HashSet<PositionKey> AggregateActionPositions = new HashSet<PositionKey>();
    private static readonly HashSet<PositionKey> CumulativeActionPositions = new HashSet<PositionKey>();
    private static readonly HashSet<GridPositionKey> AggregateActionGridPositions = new HashSet<GridPositionKey>();
    private static readonly HashSet<GridPositionKey> CumulativeActionGridPositions = new HashSet<GridPositionKey>();
    private static readonly HashSet<PositionKey> AggregateReturnPositions = new HashSet<PositionKey>();
    private static readonly HashSet<PositionKey> CumulativeReturnPositions = new HashSet<PositionKey>();
    private static readonly HashSet<GridPositionKey> AggregateReturnGridPositions = new HashSet<GridPositionKey>();
    private static readonly HashSet<GridPositionKey> CumulativeReturnGridPositions = new HashSet<GridPositionKey>();
    private static readonly object GlobalShadowCacheLock = new object();
    private static readonly Dictionary<TravelKey, ShadowEntry> GlobalShadowCache = new Dictionary<TravelKey, ShadowEntry>();
    private static readonly List<TravelKey> GlobalShadowExpiredKeys = new List<TravelKey>();
    private static readonly ShadowStats[] GlobalShadowStats =
    {
        new ShadowStats("other"),
        new ShadowStats("currentToAction"),
        new ShadowStats("actionToReturn")
    };
    private static long _actionPositionSamples;
    private static long _actionXzIntegerSamples;
    private static long _actionXyzIntegerSamples;
    private static long _actionXzHalfOrIntegerSamples;
    private static long _actionXyzHalfOrIntegerSamples;
    private static int _globalShadowLastPruneFrame = -1;

    private static long _pickBestContexts;
    private static long _durationCalls;
    private static long _leg1Calls;
    private static long _leg2Calls;
    private static long _otherTravelCalls;
    private static long _leg1StopwatchTicks;
    private static long _leg2StopwatchTicks;
    private static long _otherTravelStopwatchTicks;
    private static long _cacheAttempts;
    private static long _cacheHits;
    private static long _cacheStores;
    private static long _cacheContextsWithHits;
    private static long _globalShadowPruneCalls;
    private static long _globalShadowPruneTicks;
    private static long _globalShadowPruneMaxTicks;
    private static int _globalShadowNavMeshVersion;
    private static long _navMeshInvalidations;

    public static int GlobalShadowCacheEntryCount
    {
        get
        {
            lock (GlobalShadowCacheLock)
            {
                return GlobalShadowCache.Count;
            }
        }
    }

    /// <summary>
    /// Called from the regular NavMeshUpdateNotifier.NotifyOfNavMeshUpdates
    /// postfix (batched, at most once per tick, main thread). Bumping the
    /// version makes every cached travel time stale in O(1); stale entries are
    /// reclaimed by the existing prune cycle. With this exact invalidation the
    /// cache no longer relies on the TTL for correctness, so the TTL is
    /// extended in event mode (see IsGlobalShadowFresh).
    /// </summary>
    public static void OnRegularNavMeshUpdate()
    {
        if (!BenchmarkSettings.EnableNavMeshEventTravelCacheInvalidation)
        {
            return;
        }

        Interlocked.Increment(ref _globalShadowNavMeshVersion);
        Interlocked.Increment(ref _navMeshInvalidations);
    }

    public static void BeginPickBest()
    {
        if (!BenchmarkSettings.EnablePickBestTravelCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            _pickBestContext = null;
            return;
        }

        var context = _reusablePickBestContext;
        if (context is null)
        {
            context = new PickBestContext();
            _reusablePickBestContext = context;
        }
        else
        {
            context.Reset();
        }

        _pickBestContext = context;
        Interlocked.Increment(ref _pickBestContexts);
    }

    public static void EndPickBest()
    {
        var context = _pickBestContext;
        if (context is { Hits: > 0 })
        {
            Interlocked.Increment(ref _cacheContextsWithHits);
        }

        context?.Reset();
        _pickBestContext = null;
    }

    public static void BeginDurationWithReturn(Vector3 actionPosition, Vector3 returnPosition)
    {
        if (!BenchmarkSettings.EnableNeedTravelCacheMetrics &&
            !BenchmarkSettings.EnableNeedActionPositionSampling)
        {
            _durationContext = DurationContext.Inactive;
            return;
        }

        _durationContext = new DurationContext(true, 0);
        if (BenchmarkSettings.EnableNeedTravelCacheMetrics)
        {
            Interlocked.Increment(ref _durationCalls);
        }
        RecordActionPosition(actionPosition, returnPosition);
    }

    public static void EndDurationWithReturn()
    {
        _durationContext = DurationContext.Inactive;
    }

    private static readonly Dictionary<TravelKey, DistanceEntry> DistanceCache = new Dictionary<TravelKey, DistanceEntry>();
    private static readonly ConditionalWeakTable<Timberborn.WalkingSystem.Walker, WalkerTravelContext?> WalkerContexts =
        new ConditionalWeakTable<Timberborn.WalkingSystem.Walker, WalkerTravelContext?>();
    private static FieldInfo? _walkerNavigationServiceField;
    private static FieldInfo? _walkerTravelDayNightCycleField;
    private static FieldInfo? _walkerTravelSpeedManagerField;
    private static bool _walkerReflectionInitialized;
    private static bool _distanceCacheDisabled;
    private static int _distanceCacheWarnCount;
    private static long _distanceCacheHits;
    private static long _distanceCacheStores;
    private static long _distanceCacheDeclined;

    /// <summary>
    /// Speed-normalized travel cache: reimplements the 4-line
    /// Walker.CalculateTravelTimeInHours body, caching the PATH DISTANCE
    /// (speed-independent, navmesh-version-invalidated) and converting it to
    /// hours with the querying walker's own current base speed. This is more
    /// vanilla-faithful than the hours-based shadow cache, which shared
    /// durations across walkers with different speeds.
    /// </summary>
    public static bool TryHandleTravelTimeDistanceBased(object? walkerInstance, Vector3 start, Vector3 destination, ref float result)
    {
        if (!BenchmarkSettings.EnableTravelDistanceCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var walker = ResolveWalker(walkerInstance);
        if (_distanceCacheDisabled || walker is null)
        {
            Interlocked.Increment(ref _distanceCacheDeclined);
            return false;
        }

        var context = GetWalkerTravelContext(walker);
        if (context is null)
        {
            Interlocked.Increment(ref _distanceCacheDeclined);
            return false;
        }

        try
        {
            var key = TravelKey.Create(start, destination);
            var version = Volatile.Read(ref _globalShadowNavMeshVersion);
            float distance;
            if (DistanceCache.TryGetValue(key, out var entry) && entry.NavMeshVersion == version)
            {
                distance = entry.Distance;
                Interlocked.Increment(ref _distanceCacheHits);
            }
            else
            {
                if (!context.NavigationService.FindPathUnlimitedRange(start, destination, null, out distance))
                {
                    distance = context.NavigationService.HeuristicDistance(start, destination);
                }

                if (DistanceCache.Count >= BenchmarkSettings.TravelDistanceCacheMaxEntries)
                {
                    DistanceCache.Clear();
                }

                DistanceCache[key] = new DistanceEntry(distance, version);
                Interlocked.Increment(ref _distanceCacheStores);
            }

            result = context.DayNightCycle.SecondsToHours(distance / context.SpeedManager.GetWalkerBaseSpeed());
            return true;
        }
        catch (Exception exception)
        {
            _distanceCacheDisabled = true;
            if (_distanceCacheWarnCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] Travel distance cache disabled: {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
    }

    public static string GetDistanceCacheSummary()
    {
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "distanceCacheHits={0}, distanceCacheStores={1}, declined={2}, entries={3}, disabled={4}",
            Interlocked.Read(ref _distanceCacheHits),
            Interlocked.Read(ref _distanceCacheStores),
            Interlocked.Read(ref _distanceCacheDeclined),
            DistanceCache.Count,
            _distanceCacheDisabled);
    }

    private static FieldInfo? _actionDurationCalculatorWalkerField;
    private static bool _calculatorWalkerFieldInitialized;

    private static Timberborn.WalkingSystem.Walker? ResolveWalker(object? instance)
    {
        if (instance is Timberborn.WalkingSystem.Walker walker)
        {
            return walker;
        }

        // The patched method is ActionDurationCalculator.TravelTimeBetween,
        // which forwards to its private _walker.CalculateTravelTimeInHours.
        if (instance is Timberborn.NeedBehaviorSystem.ActionDurationCalculator calculator)
        {
            if (!_calculatorWalkerFieldInitialized)
            {
                _actionDurationCalculatorWalkerField = typeof(Timberborn.NeedBehaviorSystem.ActionDurationCalculator)
                    .GetField("_walker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _calculatorWalkerFieldInitialized = true;
            }

            return _actionDurationCalculatorWalkerField?.GetValue(calculator) as Timberborn.WalkingSystem.Walker;
        }

        return null;
    }

    private static WalkerTravelContext? GetWalkerTravelContext(Timberborn.WalkingSystem.Walker walker)
    {
        if (WalkerContexts.TryGetValue(walker, out var context))
        {
            return context;
        }

        context = CreateWalkerTravelContext(walker);
        WalkerContexts.Add(walker, context);
        return context;
    }

    private static WalkerTravelContext? CreateWalkerTravelContext(Timberborn.WalkingSystem.Walker walker)
    {
        try
        {
            if (!_walkerReflectionInitialized)
            {
                const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _walkerNavigationServiceField = typeof(Timberborn.WalkingSystem.Walker).GetField("_navigationService", instanceFlags);
                _walkerTravelDayNightCycleField = typeof(Timberborn.WalkingSystem.Walker).GetField("_dayNightCycle", instanceFlags);
                _walkerTravelSpeedManagerField = typeof(Timberborn.WalkingSystem.Walker).GetField("_walkerSpeedManager", instanceFlags);
                _walkerReflectionInitialized = true;
            }

            if (_walkerNavigationServiceField?.GetValue(walker) is not Timberborn.Navigation.INavigationService navigationService ||
                _walkerTravelDayNightCycleField?.GetValue(walker) is not Timberborn.TimeSystem.IDayNightCycle dayNightCycle ||
                _walkerTravelSpeedManagerField?.GetValue(walker) is not Timberborn.WalkingSystem.WalkerSpeedManager speedManager)
            {
                return null;
            }

            return new WalkerTravelContext(navigationService, dayNightCycle, speedManager);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class WalkerTravelContext
    {
        public readonly Timberborn.Navigation.INavigationService NavigationService;
        public readonly Timberborn.TimeSystem.IDayNightCycle DayNightCycle;
        public readonly Timberborn.WalkingSystem.WalkerSpeedManager SpeedManager;

        public WalkerTravelContext(
            Timberborn.Navigation.INavigationService navigationService,
            Timberborn.TimeSystem.IDayNightCycle dayNightCycle,
            Timberborn.WalkingSystem.WalkerSpeedManager speedManager)
        {
            NavigationService = navigationService;
            DayNightCycle = dayNightCycle;
            SpeedManager = speedManager;
        }
    }

    private readonly struct DistanceEntry
    {
        public DistanceEntry(float distance, int navMeshVersion)
        {
            Distance = distance;
            NavMeshVersion = navMeshVersion;
        }

        public float Distance { get; }
        public int NavMeshVersion { get; }
    }

    public static bool TryHandleTravelTime(Vector3 start, Vector3 destination, ref float result, out TravelCallState state)
    {
        var recordMetrics = BenchmarkSettings.EnableNeedTravelCacheMetrics;
        var leg = recordMetrics ? NextLeg() : TravelLeg.Other;
        var key = TravelKey.Create(start, destination);
        var shadowHit = TryGetGlobalShadowTravelTime(leg, key, out var shadowTravelTime);
        state = new TravelCallState(true, recordMetrics ? Stopwatch.GetTimestamp() : 0, leg, start, destination, shouldStore: false, cacheHit: false, shadowHit, shadowTravelTime);

        if (BenchmarkSettings.EnableGlobalNeedTravelCache &&
            BenchmarkModeController.CurrentMode == BenchmarkMode.Optimized &&
            shadowHit)
        {
            result = shadowTravelTime;
            state = new TravelCallState(true, recordMetrics ? Stopwatch.GetTimestamp() : 0, leg, start, destination, shouldStore: false, cacheHit: true, shadowHit, shadowTravelTime, globalCacheHit: true);
            return false;
        }

        var context = _pickBestContext;
        if (context is null ||
            !BenchmarkSettings.EnablePickBestTravelCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        Interlocked.Increment(ref _cacheAttempts);
        if (context.Cache.TryGetValue(key, out var cachedTravelTime))
        {
            context.Hits++;
            result = cachedTravelTime;
            state = new TravelCallState(true, recordMetrics ? Stopwatch.GetTimestamp() : 0, leg, start, destination, shouldStore: false, cacheHit: true, shadowHit, shadowTravelTime, globalCacheHit: false);
            Interlocked.Increment(ref _cacheHits);
            return false;
        }

        state = new TravelCallState(true, Stopwatch.GetTimestamp(), leg, start, destination, shouldStore: true, cacheHit: false, shadowHit, shadowTravelTime, globalCacheHit: false);
        return true;
    }

    public static void RecordTravelTimeReturn(TravelCallState state, float result)
    {
        if (!state.Active)
        {
            return;
        }

        RecordGlobalShadowTravelTime(state, result);
        if (BenchmarkSettings.EnableNeedTravelCacheMetrics)
        {
            var elapsedTicks = state.CacheHit ? 0 : Stopwatch.GetTimestamp() - state.StartTimestamp;
            switch (state.Leg)
            {
                case TravelLeg.CurrentToAction:
                    Interlocked.Increment(ref _leg1Calls);
                    Interlocked.Add(ref _leg1StopwatchTicks, elapsedTicks);
                    break;
                case TravelLeg.ActionToReturn:
                    Interlocked.Increment(ref _leg2Calls);
                    Interlocked.Add(ref _leg2StopwatchTicks, elapsedTicks);
                    break;
                default:
                    Interlocked.Increment(ref _otherTravelCalls);
                    Interlocked.Add(ref _otherTravelStopwatchTicks, elapsedTicks);
                    break;
            }
        }

        if (state.ShouldStore && _pickBestContext is { } context)
        {
            context.Cache[TravelKey.Create(state.Start, state.Destination)] = result;
            Interlocked.Increment(ref _cacheStores);
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var pickBestContexts = Interlocked.Exchange(ref _pickBestContexts, 0);
        var durationCalls = Interlocked.Exchange(ref _durationCalls, 0);
        var leg1Calls = Interlocked.Exchange(ref _leg1Calls, 0);
        var leg2Calls = Interlocked.Exchange(ref _leg2Calls, 0);
        var otherCalls = Interlocked.Exchange(ref _otherTravelCalls, 0);
        var leg1Ticks = Interlocked.Exchange(ref _leg1StopwatchTicks, 0);
        var leg2Ticks = Interlocked.Exchange(ref _leg2StopwatchTicks, 0);
        var otherTicks = Interlocked.Exchange(ref _otherTravelStopwatchTicks, 0);
        var cacheAttempts = Interlocked.Exchange(ref _cacheAttempts, 0);
        var cacheHits = Interlocked.Exchange(ref _cacheHits, 0);
        var cacheStores = Interlocked.Exchange(ref _cacheStores, 0);
        var cacheContextsWithHits = Interlocked.Exchange(ref _cacheContextsWithHits, 0);
        if (durationCalls == 0 && cacheAttempts == 0)
        {
            return;
        }

        var cacheHitRate = cacheAttempts > 0 ? (double)cacheHits / cacheAttempts : 0;
        Debug.Log(
            $"[T3MP] NeedTravelOptimizer aggregate={aggregateId}, quantizeStep={BenchmarkSettings.PickBestTravelCacheQuantizeStep:F2}, pickBestContexts={pickBestContexts}, durationCalls={durationCalls}, leg1Calls={leg1Calls}, leg1Ms={ToMilliseconds(leg1Ticks):F2}, leg2Calls={leg2Calls}, leg2Ms={ToMilliseconds(leg2Ticks):F2}, otherCalls={otherCalls}, otherMs={ToMilliseconds(otherTicks):F2}, cacheAttempts={cacheAttempts}, cacheHits={cacheHits}, cacheHitRate={cacheHitRate:F3}, cacheStores={cacheStores}, cacheContextsWithHits={cacheContextsWithHits}, {GetDistanceCacheSummary()}");
        LogAndResetGlobalShadowCache(aggregateId);
        LogAndResetActionPositions(aggregateId);
    }

    private static bool TryGetGlobalShadowTravelTime(TravelLeg leg, TravelKey key, out float travelTime)
    {
        travelTime = default;
        if (!BenchmarkSettings.EnableGlobalNeedTravelShadowCache)
        {
            return false;
        }

        var frame = Time.frameCount;
        if (BenchmarkSettings.EnableGlobalNeedTravelShadowCacheLocking)
        {
            lock (GlobalShadowCacheLock)
            {
                return TryGetGlobalShadowTravelTimeNoLock(leg, key, frame, out travelTime);
            }
        }

        return TryGetGlobalShadowTravelTimeNoLock(leg, key, frame, out travelTime);
    }

    private static void RecordGlobalShadowTravelTime(TravelCallState state, float result)
    {
        if (!BenchmarkSettings.EnableGlobalNeedTravelShadowCache)
        {
            return;
        }

        var key = TravelKey.Create(state.Start, state.Destination);
        var frame = Time.frameCount;
        if (BenchmarkSettings.EnableGlobalNeedTravelShadowCacheLocking)
        {
            lock (GlobalShadowCacheLock)
            {
                RecordGlobalShadowTravelTimeNoLock(state, result, key, frame);
                return;
            }
        }

        RecordGlobalShadowTravelTimeNoLock(state, result, key, frame);
    }

    private static void LogAndResetGlobalShadowCache(long aggregateId)
    {
        if (!BenchmarkSettings.EnableGlobalNeedTravelShadowCache ||
            !BenchmarkSettings.EnableNeedTravelCacheMetrics)
        {
            return;
        }

        ShadowStats[] snapshots;
        int entries;
        var pruneCalls = Interlocked.Exchange(ref _globalShadowPruneCalls, 0);
        var pruneTicks = Interlocked.Exchange(ref _globalShadowPruneTicks, 0);
        var pruneMaxTicks = Interlocked.Exchange(ref _globalShadowPruneMaxTicks, 0);
        if (BenchmarkSettings.EnableGlobalNeedTravelShadowCacheLocking)
        {
            lock (GlobalShadowCacheLock)
            {
                SnapshotGlobalShadowStatsNoLock(out entries, out snapshots);
            }
        }
        else
        {
            SnapshotGlobalShadowStatsNoLock(out entries, out snapshots);
        }

        var totalAttempts = 0L;
        foreach (var stats in snapshots)
        {
            totalAttempts += stats.Attempts;
        }

        if (totalAttempts == 0)
        {
            return;
        }

        var pruneMs = ToMilliseconds(pruneTicks);
        var pruneMaxMs = ToMilliseconds(pruneMaxTicks);
        var navMeshInvalidations = Interlocked.Exchange(ref _navMeshInvalidations, 0);
        Debug.Log(
            $"[T3MP] NeedTravelGlobalShadowCache aggregate={aggregateId}, ttlFrames={BenchmarkSettings.GlobalNeedTravelShadowCacheTtlFrames}, eventInvalidation={BenchmarkSettings.EnableNavMeshEventTravelCacheInvalidation}, navMeshInvalidations={navMeshInvalidations}, quantizeStep={BenchmarkSettings.PickBestTravelCacheQuantizeStep:F2}, entries={entries}, pruneCalls={pruneCalls}, pruneMs={pruneMs:F3}, pruneMaxMs={pruneMaxMs:F3}, {snapshots[0]}, {snapshots[1]}, {snapshots[2]}");
    }

    private static bool TryGetGlobalShadowTravelTimeNoLock(TravelLeg leg, TravelKey key, int frame, out float travelTime)
    {
        PruneGlobalShadowExpiredIfNeeded(frame);
        var stats = GlobalShadowStats[(int)leg];
        var recordMetrics = BenchmarkSettings.EnableNeedTravelCacheMetrics;
        if (recordMetrics)
        {
            stats.Attempts++;
        }
        if (!GlobalShadowCache.TryGetValue(key, out var entry) || !IsGlobalShadowFresh(frame, entry))
        {
            if (GlobalShadowCache.Remove(key))
            {
                if (recordMetrics)
                {
                    stats.ExpiredRemovals++;
                }
            }

            travelTime = default;
            return false;
        }

        if (recordMetrics)
        {
            stats.Hits++;
        }
        travelTime = entry.TravelTime;
        return true;
    }

    private static void RecordGlobalShadowTravelTimeNoLock(TravelCallState state, float result, TravelKey key, int frame)
    {
        PruneGlobalShadowExpiredIfNeeded(frame);
        var stats = GlobalShadowStats[(int)state.Leg];
        var recordMetrics = BenchmarkSettings.EnableNeedTravelCacheMetrics;
        if (state.GlobalCacheHit)
        {
            if (recordMetrics)
            {
                stats.Returns++;
            }
            if (!BenchmarkSettings.EnableGlobalNeedTravelShadowCacheRefreshOnHit)
            {
                return;
            }
        }

        if (recordMetrics && state.ShadowHit)
        {
            var error = Math.Abs(result - state.ShadowTravelTime);
            stats.ErrorSum += error;
            if (error > stats.ErrorMax)
            {
                stats.ErrorMax = error;
            }
        }

        GlobalShadowCache[key] = new ShadowEntry(frame, result, Volatile.Read(ref _globalShadowNavMeshVersion));
        if (recordMetrics)
        {
            stats.Stores++;
        }
    }

    private static void SnapshotGlobalShadowStatsNoLock(out int entries, out ShadowStats[] snapshots)
    {
        entries = GlobalShadowCache.Count;
        snapshots = new ShadowStats[GlobalShadowStats.Length];
        for (var i = 0; i < GlobalShadowStats.Length; i++)
        {
            snapshots[i] = GlobalShadowStats[i].Clone();
            GlobalShadowStats[i].Reset();
        }
    }

    private static void PruneGlobalShadowExpiredIfNeeded(int frame)
    {
        if (_globalShadowLastPruneFrame >= 0 &&
            frame - _globalShadowLastPruneFrame < BenchmarkSettings.GlobalNeedTravelShadowCachePruneIntervalFrames)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        PruneGlobalShadowExpired(frame);
        var elapsed = Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref _globalShadowPruneCalls);
        Interlocked.Add(ref _globalShadowPruneTicks, elapsed);
        UpdateMax(ref _globalShadowPruneMaxTicks, elapsed);
        _globalShadowLastPruneFrame = frame;
    }

    private static void PruneGlobalShadowExpired(int frame)
    {
        GlobalShadowExpiredKeys.Clear();
        foreach (var pair in GlobalShadowCache)
        {
            if (!IsGlobalShadowFresh(frame, pair.Value))
            {
                GlobalShadowExpiredKeys.Add(pair.Key);
            }
        }

        for (var index = 0; index < GlobalShadowExpiredKeys.Count; index++)
        {
            GlobalShadowCache.Remove(GlobalShadowExpiredKeys[index]);
        }

        GlobalShadowExpiredKeys.Clear();
    }

    private static bool IsGlobalShadowFresh(int currentFrame, in ShadowEntry entry)
    {
        var age = currentFrame - entry.Frame;
        if (BenchmarkSettings.EnableNavMeshEventTravelCacheInvalidation)
        {
            // Exact invalidation: entries survive only while the navmesh is
            // unchanged. The extended TTL only bounds memory growth.
            return entry.NavMeshVersion == Volatile.Read(ref _globalShadowNavMeshVersion) &&
                age >= 0 &&
                age <= BenchmarkSettings.GlobalNeedTravelShadowCacheEventModeTtlFrames;
        }

        return age >= 0 && age <= BenchmarkSettings.GlobalNeedTravelShadowCacheTtlFrames;
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static void RecordActionPosition(Vector3 actionPosition, Vector3 returnPosition)
    {
        if (!BenchmarkSettings.EnableNeedActionPositionSampling)
        {
            return;
        }

        var actionKey = PositionKey.Create(actionPosition);
        var actionGridKey = GridPositionKey.Create(actionPosition);
        var returnKey = PositionKey.Create(returnPosition);
        var returnGridKey = GridPositionKey.Create(returnPosition);

        lock (ActionPositionLock)
        {
            _actionPositionSamples++;
            Increment(ActionPositionCounts, actionKey);
            AggregateActionPositions.Add(actionKey);
            CumulativeActionPositions.Add(actionKey);
            AggregateActionGridPositions.Add(actionGridKey);
            CumulativeActionGridPositions.Add(actionGridKey);
            AggregateReturnPositions.Add(returnKey);
            CumulativeReturnPositions.Add(returnKey);
            AggregateReturnGridPositions.Add(returnGridKey);
            CumulativeReturnGridPositions.Add(returnGridKey);

            if (IsNearInteger(actionPosition.x) && IsNearInteger(actionPosition.z))
            {
                _actionXzIntegerSamples++;
            }

            if (IsNearInteger(actionPosition.x) &&
                IsNearInteger(actionPosition.y) &&
                IsNearInteger(actionPosition.z))
            {
                _actionXyzIntegerSamples++;
            }

            if (IsNearHalfOrInteger(actionPosition.x) && IsNearHalfOrInteger(actionPosition.z))
            {
                _actionXzHalfOrIntegerSamples++;
            }

            if (IsNearHalfOrInteger(actionPosition.x) &&
                IsNearHalfOrInteger(actionPosition.y) &&
                IsNearHalfOrInteger(actionPosition.z))
            {
                _actionXyzHalfOrIntegerSamples++;
            }
        }
    }

    private static void LogAndResetActionPositions(long aggregateId)
    {
        if (!BenchmarkSettings.EnableNeedActionPositionSampling)
        {
            return;
        }

        ActionPositionSnapshot snapshot;
        lock (ActionPositionLock)
        {
            snapshot = new ActionPositionSnapshot(
                _actionPositionSamples,
                AggregateActionPositions.Count,
                AggregateActionGridPositions.Count,
                CumulativeActionPositions.Count,
                CumulativeActionGridPositions.Count,
                AggregateReturnPositions.Count,
                AggregateReturnGridPositions.Count,
                CumulativeReturnPositions.Count,
                CumulativeReturnGridPositions.Count,
                _actionXzIntegerSamples,
                _actionXyzIntegerSamples,
                _actionXzHalfOrIntegerSamples,
                _actionXyzHalfOrIntegerSamples,
                FormatTopActionPositions());

            _actionPositionSamples = 0;
            _actionXzIntegerSamples = 0;
            _actionXyzIntegerSamples = 0;
            _actionXzHalfOrIntegerSamples = 0;
            _actionXyzHalfOrIntegerSamples = 0;
            ActionPositionCounts.Clear();
            AggregateActionPositions.Clear();
            AggregateActionGridPositions.Clear();
            AggregateReturnPositions.Clear();
            AggregateReturnGridPositions.Clear();
        }

        if (snapshot.Samples == 0)
        {
            return;
        }

        var xzIntegerRate = (double)snapshot.XzIntegerSamples / snapshot.Samples;
        var xyzIntegerRate = (double)snapshot.XyzIntegerSamples / snapshot.Samples;
        var xzHalfOrIntegerRate = (double)snapshot.XzHalfOrIntegerSamples / snapshot.Samples;
        var xyzHalfOrIntegerRate = (double)snapshot.XyzHalfOrIntegerSamples / snapshot.Samples;
        Debug.Log(
            $"[T3MP] NeedActionPositions aggregate={aggregateId}, samples={snapshot.Samples}, uniqueActionExact={snapshot.UniqueActionExact}, uniqueActionQ1={snapshot.UniqueActionQ1}, cumulativeActionExact={snapshot.CumulativeActionExact}, cumulativeActionQ1={snapshot.CumulativeActionQ1}, uniqueReturnExact={snapshot.UniqueReturnExact}, uniqueReturnQ1={snapshot.UniqueReturnQ1}, cumulativeReturnExact={snapshot.CumulativeReturnExact}, cumulativeReturnQ1={snapshot.CumulativeReturnQ1}, xzIntegerRate={xzIntegerRate:F3}, xyzIntegerRate={xyzIntegerRate:F3}, xzHalfOrIntegerRate={xzHalfOrIntegerRate:F3}, xyzHalfOrIntegerRate={xyzHalfOrIntegerRate:F3}, topActions={snapshot.TopActions}");
    }

    private static string FormatTopActionPositions()
    {
        var top = new List<KeyValuePair<PositionKey, int>>(ActionPositionCounts);
        top.Sort((left, right) =>
        {
            var countComparison = right.Value.CompareTo(left.Value);
            return countComparison != 0 ? countComparison : left.Key.CompareTo(right.Key);
        });

        var limit = Math.Min(6, top.Count);
        if (limit == 0)
        {
            return "none";
        }

        var parts = new string[limit];
        for (var index = 0; index < limit; index++)
        {
            parts[index] = $"{top[index].Key}:{top[index].Value}";
        }

        return string.Join("|", parts);
    }

    private static void Increment(Dictionary<PositionKey, int> counts, PositionKey key)
    {
        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }

    private static TravelLeg NextLeg()
    {
        if (!_durationContext.Active)
        {
            return TravelLeg.Other;
        }

        var leg = _durationContext.TravelCallIndex == 0
            ? TravelLeg.CurrentToAction
            : _durationContext.TravelCallIndex == 1
                ? TravelLeg.ActionToReturn
                : TravelLeg.Other;
        _durationContext = _durationContext.Next();
        return leg;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    public readonly struct TravelCallState
    {
        public TravelCallState(bool active, long startTimestamp, TravelLeg leg, Vector3 start, Vector3 destination, bool shouldStore, bool cacheHit, bool shadowHit, float shadowTravelTime)
            : this(active, startTimestamp, leg, start, destination, shouldStore, cacheHit, shadowHit, shadowTravelTime, globalCacheHit: false)
        {
        }

        public TravelCallState(bool active, long startTimestamp, TravelLeg leg, Vector3 start, Vector3 destination, bool shouldStore, bool cacheHit, bool shadowHit, float shadowTravelTime, bool globalCacheHit)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Leg = leg;
            Start = start;
            Destination = destination;
            ShouldStore = shouldStore;
            CacheHit = cacheHit;
            ShadowHit = shadowHit;
            ShadowTravelTime = shadowTravelTime;
            GlobalCacheHit = globalCacheHit;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public TravelLeg Leg { get; }
        public Vector3 Start { get; }
        public Vector3 Destination { get; }
        public bool ShouldStore { get; }
        public bool CacheHit { get; }
        public bool ShadowHit { get; }
        public float ShadowTravelTime { get; }
        public bool GlobalCacheHit { get; }
    }

    public enum TravelLeg
    {
        Other,
        CurrentToAction,
        ActionToReturn
    }

    private sealed class PickBestContext
    {
        public Dictionary<TravelKey, float> Cache { get; } = new Dictionary<TravelKey, float>();
        public int Hits { get; set; }

        public void Reset()
        {
            Cache.Clear();
            Hits = 0;
        }
    }

    private sealed class ShadowStats
    {
        public ShadowStats(string name)
        {
            Name = name;
        }

        private string Name { get; }
        public long Attempts { get; set; }
        public long Hits { get; set; }
        public long Returns { get; set; }
        public long Stores { get; set; }
        public long ExpiredRemovals { get; set; }
        public double ErrorSum { get; set; }
        public double ErrorMax { get; set; }

        public ShadowStats Clone()
        {
            return new ShadowStats(Name)
            {
                Attempts = Attempts,
                Hits = Hits,
                Returns = Returns,
                Stores = Stores,
                ExpiredRemovals = ExpiredRemovals,
                ErrorSum = ErrorSum,
                ErrorMax = ErrorMax
            };
        }

        public void Reset()
        {
            Attempts = 0;
            Hits = 0;
            Returns = 0;
            Stores = 0;
            ExpiredRemovals = 0;
            ErrorSum = 0;
            ErrorMax = 0;
        }

        public override string ToString()
        {
            var hitRate = Attempts > 0 ? (double)Hits / Attempts : 0;
            var errorAvg = Hits > 0 ? ErrorSum / Hits : 0;
            return $"{Name}:attempts={Attempts},hits={Hits},hitRate={hitRate:F3},returns={Returns},stores={Stores},expired={ExpiredRemovals},errAvgHours={errorAvg:F4},errMaxHours={ErrorMax:F4}";
        }
    }

    private readonly struct ShadowEntry
    {
        public ShadowEntry(int frame, float travelTime, int navMeshVersion)
        {
            Frame = frame;
            TravelTime = travelTime;
            NavMeshVersion = navMeshVersion;
        }

        public int Frame { get; }
        public float TravelTime { get; }
        public int NavMeshVersion { get; }
    }

    private readonly struct DurationContext
    {
        public static readonly DurationContext Inactive = new DurationContext(false, 0);

        public DurationContext(bool active, int travelCallIndex)
        {
            Active = active;
            TravelCallIndex = travelCallIndex;
        }

        public bool Active { get; }
        public int TravelCallIndex { get; }

        public DurationContext Next()
        {
            return new DurationContext(Active, TravelCallIndex + 1);
        }
    }

    private readonly struct TravelKey : IEquatable<TravelKey>
    {
        private TravelKey(int startX, int startY, int startZ, int destinationX, int destinationY, int destinationZ)
        {
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            DestinationX = destinationX;
            DestinationY = destinationY;
            DestinationZ = destinationZ;
        }

        private int StartX { get; }
        private int StartY { get; }
        private int StartZ { get; }
        private int DestinationX { get; }
        private int DestinationY { get; }
        private int DestinationZ { get; }

        public static TravelKey Create(Vector3 start, Vector3 destination)
        {
            return new TravelKey(
                Quantize(start.x),
                Quantize(start.y),
                Quantize(start.z),
                Quantize(destination.x),
                Quantize(destination.y),
                Quantize(destination.z));
        }

        public bool Equals(TravelKey other)
        {
            return StartX == other.StartX &&
                StartY == other.StartY &&
                StartZ == other.StartZ &&
                DestinationX == other.DestinationX &&
                DestinationY == other.DestinationY &&
                DestinationZ == other.DestinationZ;
        }

        public override bool Equals(object? obj)
        {
            return obj is TravelKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StartX;
                hash = (hash * 397) ^ StartY;
                hash = (hash * 397) ^ StartZ;
                hash = (hash * 397) ^ DestinationX;
                hash = (hash * 397) ^ DestinationY;
                hash = (hash * 397) ^ DestinationZ;
                return hash;
            }
        }

        private static int Quantize(float value)
        {
            return RoundHalfAwayFromZero(value / BenchmarkSettings.PickBestTravelCacheQuantizeStep);
        }

        private static int RoundHalfAwayFromZero(float value)
        {
            return value >= 0f
                ? Mathf.FloorToInt(value + 0.5f)
                : Mathf.CeilToInt(value - 0.5f);
        }
    }

    private readonly struct PositionKey : IEquatable<PositionKey>, IComparable<PositionKey>
    {
        private const float Scale = 1000f;

        private PositionKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }
        private int Y { get; }
        private int Z { get; }

        public static PositionKey Create(Vector3 position)
        {
            return new PositionKey(
                RoundToInt(position.x * Scale),
                RoundToInt(position.y * Scale),
                RoundToInt(position.z * Scale));
        }

        public int CompareTo(PositionKey other)
        {
            var x = X.CompareTo(other.X);
            if (x != 0)
            {
                return x;
            }

            var y = Y.CompareTo(other.Y);
            if (y != 0)
            {
                return y;
            }

            return Z.CompareTo(other.Z);
        }

        public bool Equals(PositionKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is PositionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                return hash;
            }
        }

        public override string ToString()
        {
            return $"{X / Scale:F3},{Y / Scale:F3},{Z / Scale:F3}";
        }
    }

    private readonly struct GridPositionKey : IEquatable<GridPositionKey>
    {
        private GridPositionKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }
        private int Y { get; }
        private int Z { get; }

        public static GridPositionKey Create(Vector3 position)
        {
            return new GridPositionKey(
                RoundHalfAwayFromZero(position.x),
                RoundHalfAwayFromZero(position.y),
                RoundHalfAwayFromZero(position.z));
        }

        public bool Equals(GridPositionKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is GridPositionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                return hash;
            }
        }
    }

    private readonly struct ActionPositionSnapshot
    {
        public ActionPositionSnapshot(
            long samples,
            int uniqueActionExact,
            int uniqueActionQ1,
            int cumulativeActionExact,
            int cumulativeActionQ1,
            int uniqueReturnExact,
            int uniqueReturnQ1,
            int cumulativeReturnExact,
            int cumulativeReturnQ1,
            long xzIntegerSamples,
            long xyzIntegerSamples,
            long xzHalfOrIntegerSamples,
            long xyzHalfOrIntegerSamples,
            string topActions)
        {
            Samples = samples;
            UniqueActionExact = uniqueActionExact;
            UniqueActionQ1 = uniqueActionQ1;
            CumulativeActionExact = cumulativeActionExact;
            CumulativeActionQ1 = cumulativeActionQ1;
            UniqueReturnExact = uniqueReturnExact;
            UniqueReturnQ1 = uniqueReturnQ1;
            CumulativeReturnExact = cumulativeReturnExact;
            CumulativeReturnQ1 = cumulativeReturnQ1;
            XzIntegerSamples = xzIntegerSamples;
            XyzIntegerSamples = xyzIntegerSamples;
            XzHalfOrIntegerSamples = xzHalfOrIntegerSamples;
            XyzHalfOrIntegerSamples = xyzHalfOrIntegerSamples;
            TopActions = topActions;
        }

        public long Samples { get; }
        public int UniqueActionExact { get; }
        public int UniqueActionQ1 { get; }
        public int CumulativeActionExact { get; }
        public int CumulativeActionQ1 { get; }
        public int UniqueReturnExact { get; }
        public int UniqueReturnQ1 { get; }
        public int CumulativeReturnExact { get; }
        public int CumulativeReturnQ1 { get; }
        public long XzIntegerSamples { get; }
        public long XyzIntegerSamples { get; }
        public long XzHalfOrIntegerSamples { get; }
        public long XyzHalfOrIntegerSamples { get; }
        public string TopActions { get; }
    }

    private static bool IsNearInteger(float value)
    {
        return Mathf.Abs(value - Mathf.Round(value)) <= 0.01f;
    }

    private static bool IsNearHalfOrInteger(float value)
    {
        var nearestHalf = Mathf.Round(value * 2f) / 2f;
        return Mathf.Abs(value - nearestHalf) <= 0.01f;
    }

    private static int RoundToInt(float value)
    {
        return value >= 0f
            ? Mathf.FloorToInt(value + 0.5f)
            : Mathf.CeilToInt(value - 0.5f);
    }

    private static int RoundHalfAwayFromZero(float value)
    {
        return value >= 0f
            ? Mathf.FloorToInt(value + 0.5f)
            : Mathf.CeilToInt(value - 0.5f);
    }
}
