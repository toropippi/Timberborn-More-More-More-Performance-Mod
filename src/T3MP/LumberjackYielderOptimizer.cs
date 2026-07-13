using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Carrying;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class LumberjackYielderOptimizer
{
    private static readonly Type? LumberjackBehaviorType = FindType("Timberborn.Forestry.LumberjackFlagWorkplaceBehavior");
    private static readonly FieldInfo? YielderFinderField = LumberjackBehaviorType?.GetField("_yielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TreeCuttingAreaField = LumberjackBehaviorType?.GetField("_treeCuttingArea", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? InventoryField = LumberjackBehaviorType?.GetField("_inventory", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? AccessibleField = LumberjackBehaviorType?.GetField("_accessible", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? YieldersInAreaField = FindType("Timberborn.Forestry.TreeCuttingArea")?.GetField("_yieldersInArea", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ClosestYielderFinderField =
        typeof(YielderFinder).GetField("_closestYielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CarryAmountCalculatorField =
        typeof(ClosestYielderFinder).GetField("_carryAmountCalculator", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object CacheLock = new object();
    private static readonly Dictionary<object, int> AreaVersions = new Dictionary<object, int>(ReferenceComparer.Instance);
    private static readonly Dictionary<object, AreaGoodIdCacheEntry> AreaGoodIdCache =
        new Dictionary<object, AreaGoodIdCacheEntry>(ReferenceComparer.Instance);
    private static readonly Dictionary<DistanceCacheKey, DistanceCacheEntry> DistanceCache = new Dictionary<DistanceCacheKey, DistanceCacheEntry>();

    [ThreadStatic]
    private static List<CachedReachableYielder>? _buildBuffer;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _candidateCount;
    private static long _cacheHits;
    private static long _cacheMisses;
    private static long _pathCalls;
    private static long _reachableCount;
    private static long _reservedRejects;
    private static long _yieldingCandidates;
    private static long _staleSkips;
    private static long _scanChecks;
    private static long _successfulScans;
    private static long _emptyScans;
    private static long _successIndexSum;
    private static long _successIndexMax;
    private static long _capacitySkips;
    private static long _preCapacitySkips;
    private static long _areaGoodIdCacheHits;
    private static long _areaGoodIdCacheMisses;
    private static long _areaGoodIdCandidates;
    private static int _warningLogged;

    public static void InvalidateArea(object? treeCuttingArea)
    {
        if (treeCuttingArea is null)
        {
            return;
        }

        lock (CacheLock)
        {
            AreaVersions.TryGetValue(treeCuttingArea, out var version);
            AreaVersions[treeCuttingArea] = version + 1;
        }
    }

    // The distance cache stores start.FindTerrainPath reachability keyed by area
    // + accessible + start + area-version. None of those change when a road/path
    // is added or removed, so a route change could otherwise leave a tree stale
    // (unreachable-cached, or reachable-cached at a wrong distance). Drop the
    // reachability cache on any regular-navmesh update so it is rebuilt against
    // the current navmesh. Called from the NavMeshUpdateNotifier hook.
    public static void OnNavMeshUpdate()
    {
        lock (CacheLock)
        {
            if (DistanceCache.Count > 0)
            {
                DistanceCache.Clear();
            }
        }
    }

    public static bool TryFindCuttable(object lumberjackBehavior, int liftingCapacity, out YielderSearchResult result)
    {
        result = default;
        if (!BenchmarkSettings.EnableLumberjackYielderOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _attempts);
        try
        {
            if (YielderFinderField?.GetValue(lumberjackBehavior) is not YielderFinder yielderFinder ||
                TreeCuttingAreaField?.GetValue(lumberjackBehavior) is not { } treeCuttingArea ||
                InventoryField?.GetValue(lumberjackBehavior) is not Inventory inventory ||
                AccessibleField?.GetValue(lumberjackBehavior) is not Accessible accessible ||
                YieldersInAreaField?.GetValue(treeCuttingArea) is not IDictionary yieldersInArea)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            var carryAmountCalculator = GetCarryAmountCalculator(yielderFinder);
            if (carryAmountCalculator is null)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            var startAccess = accessible.UnblockedSingleAccess;
            if (!startAccess.HasValue)
            {
                result = YielderSearchResult.CreateNoYielderInRange();
                Interlocked.Increment(ref _handled);
                return true;
            }

            var areaVersion = GetAreaVersion(treeCuttingArea);
            var areaGoodIdCandidates = 0;
            var areaGoodIdCacheHit = false;
            var areaGoodIdCacheMiss = false;
            var areaYieldGoodIds = BenchmarkSettings.EnableLumberjackNoCapacityFastEmpty
                ? GetAreaYieldGoodIds(
                    treeCuttingArea,
                    areaVersion,
                    yieldersInArea,
                    out areaGoodIdCandidates,
                    out areaGoodIdCacheHit,
                    out areaGoodIdCacheMiss)
                : Array.Empty<string>();
            Interlocked.Add(ref _areaGoodIdCandidates, areaGoodIdCandidates);
            if (areaGoodIdCacheHit)
            {
                Interlocked.Increment(ref _areaGoodIdCacheHits);
            }
            if (areaGoodIdCacheMiss)
            {
                Interlocked.Increment(ref _areaGoodIdCacheMisses);
            }

            if (BenchmarkSettings.EnableLumberjackNoCapacityFastEmpty &&
                areaYieldGoodIds.Length > 0 &&
                !HasAnyUnreservedCapacity(inventory, areaYieldGoodIds))
            {
                Interlocked.Increment(ref _preCapacitySkips);
                result = YielderSearchResult.CreateEmpty();
                Interlocked.Increment(ref _handled);
                return true;
            }

            var cacheKey = DistanceCacheKey.Create(treeCuttingArea, accessible, startAccess.GetValueOrDefault(), areaVersion);
            var cacheEntry = GetOrBuildDistanceCache(
                cacheKey,
                treeCuttingArea,
                yieldersInArea,
                accessible,
                areaYieldGoodIds,
                out var candidates,
                out var pathCalls,
                out var cacheHit,
                out var cacheMiss);

            Interlocked.Add(ref _candidateCount, candidates);
            Interlocked.Add(ref _pathCalls, pathCalls);
            Interlocked.Add(ref _reachableCount, cacheEntry.ReachableYielders.Length);
            if (cacheHit)
            {
                Interlocked.Increment(ref _cacheHits);
            }
            if (cacheMiss)
            {
                Interlocked.Increment(ref _cacheMisses);
            }

            if (TryFindFromReachable(
                    cacheEntry.ReachableYielders,
                    cacheEntry.YieldGoodIds,
                    inventory,
                    carryAmountCalculator,
                    liftingCapacity,
                    out result,
                    out var reservedRejects,
                    out var yieldingCandidates,
                    out var staleSkips))
            {
                Interlocked.Add(ref _reservedRejects, reservedRejects);
                Interlocked.Add(ref _yieldingCandidates, yieldingCandidates);
                Interlocked.Add(ref _staleSkips, staleSkips);
                Interlocked.Increment(ref _handled);
                return true;
            }

            Interlocked.Increment(ref _fallbacks);
            return false;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Exchange(ref _warningLogged, 1) == 0)
            {
                Debug.LogWarning($"[T3MP] LumberjackYielderOptimizer failed once; falling back to vanilla. {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
        finally
        {
            _buildBuffer?.Clear();
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var candidateCount = Interlocked.Exchange(ref _candidateCount, 0);
        var cacheHits = Interlocked.Exchange(ref _cacheHits, 0);
        var cacheMisses = Interlocked.Exchange(ref _cacheMisses, 0);
        var pathCalls = Interlocked.Exchange(ref _pathCalls, 0);
        var reachableCount = Interlocked.Exchange(ref _reachableCount, 0);
        var reservedRejects = Interlocked.Exchange(ref _reservedRejects, 0);
        var yieldingCandidates = Interlocked.Exchange(ref _yieldingCandidates, 0);
        var staleSkips = Interlocked.Exchange(ref _staleSkips, 0);
        var scanChecks = Interlocked.Exchange(ref _scanChecks, 0);
        var successfulScans = Interlocked.Exchange(ref _successfulScans, 0);
        var emptyScans = Interlocked.Exchange(ref _emptyScans, 0);
        var successIndexSum = Interlocked.Exchange(ref _successIndexSum, 0);
        var successIndexMax = Interlocked.Exchange(ref _successIndexMax, 0);
        var capacitySkips = Interlocked.Exchange(ref _capacitySkips, 0);
        var preCapacitySkips = Interlocked.Exchange(ref _preCapacitySkips, 0);
        var areaGoodIdCacheHits = Interlocked.Exchange(ref _areaGoodIdCacheHits, 0);
        var areaGoodIdCacheMisses = Interlocked.Exchange(ref _areaGoodIdCacheMisses, 0);
        var areaGoodIdCandidates = Interlocked.Exchange(ref _areaGoodIdCandidates, 0);
        if (attempts == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var avgCandidates = attempts > 0 ? (double)candidateCount / attempts : 0.0;
        var cacheHitRate = cacheHits + cacheMisses > 0 ? (double)cacheHits / (cacheHits + cacheMisses) : 0.0;
        var areaGoodIdCacheHitRate = areaGoodIdCacheHits + areaGoodIdCacheMisses > 0
            ? (double)areaGoodIdCacheHits / (areaGoodIdCacheHits + areaGoodIdCacheMisses)
            : 0.0;
        var avgReachable = attempts > 0 ? (double)reachableCount / attempts : 0.0;
        var scanChecksPerAttempt = attempts > 0 ? (double)scanChecks / attempts : 0.0;
        var successIndexAvg = successfulScans > 0 ? (double)successIndexSum / successfulScans : 0.0;
        int entries;
        lock (CacheLock)
        {
            entries = DistanceCache.Count;
        }

        Debug.Log(
            $"[T3MP] LumberjackYielderOptimizer aggregate={aggregateId}, enabled={BenchmarkSettings.EnableLumberjackYielderOptimizer}, noCapacityFastEmpty={BenchmarkSettings.EnableLumberjackNoCapacityFastEmpty}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, avgCandidates={avgCandidates:F2}, candidates={candidateCount}, cacheHits={cacheHits}, cacheMisses={cacheMisses}, cacheHitRate={cacheHitRate:F3}, pathCalls={pathCalls}, avgReachable={avgReachable:F2}, reservedRejects={reservedRejects}, yieldingCandidates={yieldingCandidates}, staleSkips={staleSkips}, preCapacitySkips={preCapacitySkips}, capacitySkips={capacitySkips}, areaGoodIdCacheHits={areaGoodIdCacheHits}, areaGoodIdCacheMisses={areaGoodIdCacheMisses}, areaGoodIdCacheHitRate={areaGoodIdCacheHitRate:F3}, areaGoodIdCandidates={areaGoodIdCandidates}, scanChecks={scanChecks}, scanChecksPerAttempt={scanChecksPerAttempt:F2}, successfulScans={successfulScans}, successIndexAvg={successIndexAvg:F2}, successIndexMax={successIndexMax}, emptyScans={emptyScans}, entries={entries}");
    }

    private static int GetAreaVersion(object treeCuttingArea)
    {
        lock (CacheLock)
        {
            AreaVersions.TryGetValue(treeCuttingArea, out var version);
            return version;
        }
    }

    private static DistanceCacheEntry GetOrBuildDistanceCache(
        DistanceCacheKey cacheKey,
        object treeCuttingArea,
        IDictionary yieldersInArea,
        Accessible start,
        string[] yieldGoodIds,
        out int candidates,
        out int pathCalls,
        out bool cacheHit,
        out bool cacheMiss)
    {
        candidates = 0;
        pathCalls = 0;
        cacheHit = false;
        cacheMiss = false;

        var frame = Time.frameCount;
        lock (CacheLock)
        {
            if (DistanceCache.TryGetValue(cacheKey, out var entry) &&
                frame - entry.CreatedFrame <= BenchmarkSettings.FastYielderDistanceCacheMaxAgeFrames)
            {
                candidates = entry.CandidateCount;
                cacheHit = true;
                return entry;
            }
        }

        cacheMiss = true;
        var buildBuffer = _buildBuffer ??= new List<CachedReachableYielder>(2048);
        buildBuffer.Clear();
        foreach (var value in yieldersInArea.Values)
        {
            candidates++;
            if (value is not Yielder yielder || yielder == null)
            {
                continue;
            }

            pathCalls++;
            if (start.FindTerrainPath(yielder.CenterPosition, out var distance))
            {
                buildBuffer.Add(new CachedReachableYielder(yielder, distance, yielder.InstantiationOrder));
            }
        }

        buildBuffer.Sort(CompareReachableYielderDistance);
        var reachableYielders = buildBuffer.ToArray();
        var newEntry = new DistanceCacheEntry(treeCuttingArea, candidates, reachableYielders, yieldGoodIds, frame);

        lock (CacheLock)
        {
            if (DistanceCache.Count >= BenchmarkSettings.FastYielderDistanceCacheMaxEntries)
            {
                RemoveOldestDistanceCacheEntry();
            }

            DistanceCache[cacheKey] = newEntry;
        }

        return newEntry;
    }

    private static string[] GetAreaYieldGoodIds(
        object treeCuttingArea,
        int areaVersion,
        IDictionary yieldersInArea,
        out int candidates,
        out bool cacheHit,
        out bool cacheMiss)
    {
        candidates = 0;
        cacheHit = false;
        cacheMiss = false;

        lock (CacheLock)
        {
            if (AreaGoodIdCache.TryGetValue(treeCuttingArea, out var entry) && entry.AreaVersion == areaVersion)
            {
                cacheHit = true;
                return entry.GoodIds;
            }
        }

        cacheMiss = true;
        var goodIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in yieldersInArea.Values)
        {
            candidates++;
            if (value is not Yielder yielder || yielder == null)
            {
                continue;
            }

            var goodId = yielder.Yield.GoodId;
            if (!string.IsNullOrEmpty(goodId))
            {
                goodIds.Add(goodId);
            }
        }

        var result = new string[goodIds.Count];
        goodIds.CopyTo(result);
        lock (CacheLock)
        {
            AreaGoodIdCache[treeCuttingArea] = new AreaGoodIdCacheEntry(areaVersion, result);
        }

        return result;
    }

    private static bool TryFindFromReachable(
        CachedReachableYielder[] reachableYielders,
        string[] yieldGoodIds,
        Inventory inventory,
        CarryAmountCalculator carryAmountCalculator,
        int liftingCapacity,
        out YielderSearchResult result,
        out long reservedRejects,
        out long yieldingCandidates,
        out long staleSkips)
    {
        result = default;
        reservedRejects = 0;
        yieldingCandidates = 0;
        staleSkips = 0;

        if (BenchmarkSettings.EnableLumberjackNoCapacityFastEmpty &&
            yieldGoodIds.Length > 0 &&
            !HasAnyUnreservedCapacity(inventory, yieldGoodIds))
        {
            Interlocked.Increment(ref _capacitySkips);
            RecordScanStats(0, -1, false);
            result = YielderSearchResult.CreateEmpty();
            return true;
        }

        var hasReachableLivingOrYielding = false;
        var scanChecks = 0;
        for (var index = 0; index < reachableYielders.Length; index++)
        {
            scanChecks++;
            var yielder = reachableYielders[index].Yielder;
            if (yielder == null)
            {
                staleSkips++;
                continue;
            }

            if (yielder.Reservable.Reserved)
            {
                reservedRejects++;
                continue;
            }

            if (!yielder.IsYielding)
            {
                if (yielder.IsAlive())
                {
                    hasReachableLivingOrYielding = true;
                }

                continue;
            }

            hasReachableLivingOrYielding = true;
            yieldingCandidates++;

            var yield = CarryAmountCalculatorOptimizer.AmountToCarry(carryAmountCalculator, liftingCapacity, yielder.Yield, inventory);
            if (yield.Amount > 0)
            {
                RecordScanStats(scanChecks, index, true);
                result = YielderSearchResult.CreateSearchResult(yielder, yield);
                return true;
            }
        }

        RecordScanStats(scanChecks, -1, false);
        result = hasReachableLivingOrYielding
            ? YielderSearchResult.CreateEmpty()
            : YielderSearchResult.CreateNoYielderInRange();
        return true;
    }

    private static bool HasAnyUnreservedCapacity(Inventory inventory, string[] yieldGoodIds)
    {
        for (var index = 0; index < yieldGoodIds.Length; index++)
        {
            if (inventory.UnreservedCapacity(yieldGoodIds[index]) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void RecordScanStats(int scanChecks, int successIndex, bool success)
    {
        if (!BenchmarkSettings.EnableLumberjackScanStats)
        {
            return;
        }

        Interlocked.Add(ref _scanChecks, scanChecks);
        if (!success)
        {
            Interlocked.Increment(ref _emptyScans);
            return;
        }

        Interlocked.Increment(ref _successfulScans);
        Interlocked.Add(ref _successIndexSum, successIndex);
        UpdateMax(ref _successIndexMax, successIndex);
    }

    private static void UpdateMax(ref long target, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
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

    private static CarryAmountCalculator? GetCarryAmountCalculator(YielderFinder yielderFinder)
    {
        if (ClosestYielderFinderField?.GetValue(yielderFinder) is not ClosestYielderFinder closestYielderFinder)
        {
            return null;
        }

        return CarryAmountCalculatorField?.GetValue(closestYielderFinder) as CarryAmountCalculator;
    }

    private static void RemoveOldestDistanceCacheEntry()
    {
        var hasOldest = false;
        var oldestKey = default(DistanceCacheKey);
        var oldestFrame = int.MaxValue;
        foreach (var pair in DistanceCache)
        {
            if (!hasOldest || pair.Value.CreatedFrame < oldestFrame)
            {
                hasOldest = true;
                oldestKey = pair.Key;
                oldestFrame = pair.Value.CreatedFrame;
            }
        }

        if (hasOldest)
        {
            DistanceCache.Remove(oldestKey);
        }
    }

    private static int CompareReachableYielderDistance(CachedReachableYielder left, CachedReachableYielder right)
    {
        var distanceComparison = left.Distance.CompareTo(right.Distance);
        if (distanceComparison != 0)
        {
            return distanceComparison;
        }

        return left.InstantiationOrder.CompareTo(right.InstantiationOrder);
    }

    private readonly struct DistanceCacheEntry
    {
        public DistanceCacheEntry(
            object treeCuttingArea,
            int candidateCount,
            CachedReachableYielder[] reachableYielders,
            string[] yieldGoodIds,
            int createdFrame)
        {
            TreeCuttingArea = treeCuttingArea;
            CandidateCount = candidateCount;
            ReachableYielders = reachableYielders;
            YieldGoodIds = yieldGoodIds;
            CreatedFrame = createdFrame;
        }

        public object TreeCuttingArea { get; }
        public int CandidateCount { get; }
        public CachedReachableYielder[] ReachableYielders { get; }
        public string[] YieldGoodIds { get; }
        public int CreatedFrame { get; }
    }

    private readonly struct AreaGoodIdCacheEntry
    {
        public AreaGoodIdCacheEntry(int areaVersion, string[] goodIds)
        {
            AreaVersion = areaVersion;
            GoodIds = goodIds;
        }

        public int AreaVersion { get; }
        public string[] GoodIds { get; }
    }

    private readonly struct CachedReachableYielder
    {
        public CachedReachableYielder(Yielder yielder, float distance, int instantiationOrder)
        {
            Yielder = yielder;
            Distance = distance;
            InstantiationOrder = instantiationOrder;
        }

        public Yielder Yielder { get; }
        public float Distance { get; }
        public int InstantiationOrder { get; }
    }

    private readonly struct DistanceCacheKey : IEquatable<DistanceCacheKey>
    {
        private DistanceCacheKey(int areaIdentity, int startIdentity, int startX, int startY, int startZ, int areaVersion)
        {
            AreaIdentity = areaIdentity;
            StartIdentity = startIdentity;
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            AreaVersion = areaVersion;
        }

        public int AreaIdentity { get; }
        public int StartIdentity { get; }
        public int StartX { get; }
        public int StartY { get; }
        public int StartZ { get; }
        public int AreaVersion { get; }

        public static DistanceCacheKey Create(object treeCuttingArea, Accessible start, Vector3 startPosition, int areaVersion)
        {
            return new DistanceCacheKey(
                RuntimeHelpers.GetHashCode(treeCuttingArea),
                RuntimeHelpers.GetHashCode(start),
                Quantize(startPosition.x),
                Quantize(startPosition.y),
                Quantize(startPosition.z),
                areaVersion);
        }

        public bool Equals(DistanceCacheKey other)
        {
            return AreaIdentity == other.AreaIdentity &&
                StartIdentity == other.StartIdentity &&
                StartX == other.StartX &&
                StartY == other.StartY &&
                StartZ == other.StartZ &&
                AreaVersion == other.AreaVersion;
        }

        public override bool Equals(object? obj)
        {
            return obj is DistanceCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = AreaIdentity;
                hash = (hash * 397) ^ StartIdentity;
                hash = (hash * 397) ^ StartX;
                hash = (hash * 397) ^ StartY;
                hash = (hash * 397) ^ StartZ;
                hash = (hash * 397) ^ AreaVersion;
                return hash;
            }
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static int Quantize(float value)
    {
        return Mathf.RoundToInt(value * 100f);
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
