using System;
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

internal static class FastYielderFinder
{
    private static readonly FieldInfo? ClosestYielderFinderField =
        typeof(YielderFinder).GetField("_closestYielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? CarryAmountCalculatorField =
        typeof(ClosestYielderFinder).GetField("_carryAmountCalculator", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object CalculatorLock = new object();
    private static readonly object DistanceCacheLock = new object();
    private static readonly Dictionary<DistanceCacheKey, DistanceCacheEntry> DistanceCache =
        new Dictionary<DistanceCacheKey, DistanceCacheEntry>();

    private static YielderFinder? _cachedYielderFinder;
    private static CarryAmountCalculator? _cachedCarryAmountCalculator;
    private static int _warningLogged;

    [ThreadStatic]
    private static List<Yielder>? _candidateBuffer;

    [ThreadStatic]
    private static List<CachedReachableYielder>? _cacheBuildBuffer;

    [ThreadStatic]
    private static HashSet<string>? _goodIdBuffer;

    private static long _capacityPrecheckAttempts;
    private static long _capacityPrecheckSkips;
    private static long _capacityPrecheckCandidates;
    private static long _capacityPrecheckGoodIds;

    public static bool TryFindLivingYielderWithoutAccessible(
        YielderFinder yielderFinder,
        Inventory receivingInventory,
        Accessible start,
        int liftingCapacity,
        IEnumerable<Yielder> yielders,
        out YielderSearchResult result)
    {
        var candidateBuffer = GetCandidateBuffer();
        var candidates = 0;
        foreach (var yielder in yielders)
        {
            candidates++;
            if (yielder != null)
            {
                candidateBuffer.Add(yielder);
            }
        }

        try
        {
            return TryFindLivingYielderWithoutAccessibleFromCandidates(
                yielderFinder,
                receivingInventory,
                start,
                liftingCapacity,
                candidateBuffer,
                candidates,
                out result);
        }
        finally
        {
            _candidateBuffer?.Clear();
        }
    }

    public static bool TryFindLivingYielderWithoutAccessibleFromList(
        YielderFinder yielderFinder,
        Inventory receivingInventory,
        Accessible start,
        int liftingCapacity,
        List<Yielder> yielders,
        out YielderSearchResult result)
    {
        return TryFindLivingYielderWithoutAccessibleFromCandidates(
            yielderFinder,
            receivingInventory,
            start,
            liftingCapacity,
            yielders,
            yielders.Count,
            out result);
    }

    private static bool TryFindLivingYielderWithoutAccessibleFromCandidates(
        YielderFinder yielderFinder,
        Inventory receivingInventory,
        Accessible start,
        int liftingCapacity,
        List<Yielder> candidateBuffer,
        int candidates,
        out YielderSearchResult result)
    {
        result = default;

        var yieldingCandidates = 0;
        var pathCalls = 0;
        var handled = false;
        var cacheHit = false;
        var cacheMiss = false;
        var reachableEntries = 0;

        try
        {
            var carryAmountCalculator = GetCarryAmountCalculator(yielderFinder);
            if (carryAmountCalculator is null)
            {
                return false;
            }

            var startAccess = start.UnblockedSingleAccess;
            if (!startAccess.HasValue)
            {
                result = YielderSearchResult.CreateNoYielderInRange();
                handled = true;
                return true;
            }

            var startPosition = startAccess.GetValueOrDefault();
            if (BenchmarkSettings.EnableFastYielderNoCapacityPrecheck &&
                !HasCapacityForAnyCandidateGood(receivingInventory, candidateBuffer, out var checkedCandidates, out var checkedGoodIds))
            {
                Interlocked.Increment(ref _capacityPrecheckSkips);
                result = YielderSearchResult.CreateEmpty();
                handled = true;
                return true;
            }

            var cacheKey = DistanceCacheKey.Create(start, startPosition, candidateBuffer);
            var cachedReachableYielders = GetOrBuildDistanceCache(
                cacheKey,
                start,
                candidateBuffer,
                out pathCalls,
                out cacheHit,
                out cacheMiss);
            reachableEntries = cachedReachableYielders.Length;

            var hasReachableLivingOrYielding = false;
            for (var index = 0; index < cachedReachableYielders.Length; index++)
            {
                var yielder = cachedReachableYielders[index].Yielder;
                if (yielder == null)
                {
                    continue;
                }

                if (!yielder.IsYielding)
                {
                    if (IsAliveOrFallback(yielder))
                    {
                        hasReachableLivingOrYielding = true;
                    }

                    continue;
                }

                hasReachableLivingOrYielding = true;
                yieldingCandidates++;

                var yield = CarryAmountCalculatorOptimizer.AmountToCarry(carryAmountCalculator, liftingCapacity, yielder.Yield, receivingInventory);
                if (yield.Amount > 0)
                {
                    result = YielderSearchResult.CreateSearchResult(yielder, yield);
                    handled = true;
                    return true;
                }
            }

            result = hasReachableLivingOrYielding
                ? YielderSearchResult.CreateEmpty()
                : YielderSearchResult.CreateNoYielderInRange();
            handled = true;
            return true;
        }
        catch (Exception exception)
        {
            if (System.Threading.Interlocked.Exchange(ref _warningLogged, 1) == 0)
            {
                Debug.LogWarning($"[T3MP] FastYielderFinder failed once; falling back to vanilla. {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
        finally
        {
            _cacheBuildBuffer?.Clear();
            _goodIdBuffer?.Clear();
            if (BenchmarkModeController.TryGetSampleMode(out var mode))
            {
                BenchmarkMetrics.RecordFastYielderFinder(
                    mode,
                    candidates,
                    yieldingCandidates,
                    pathCalls,
                    handled,
                    cacheHit,
                    cacheMiss,
                    reachableEntries);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _capacityPrecheckAttempts, 0);
        var skips = Interlocked.Exchange(ref _capacityPrecheckSkips, 0);
        var candidates = Interlocked.Exchange(ref _capacityPrecheckCandidates, 0);
        var goodIds = Interlocked.Exchange(ref _capacityPrecheckGoodIds, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(
            $"[T3MP] FastYielderCapacityPrecheck aggregate={aggregateId}, enabled={BenchmarkSettings.EnableFastYielderNoCapacityPrecheck}, attempts={attempts}, skips={skips}, skipRate={(double)skips / attempts:F3}, candidates={candidates}, avgCandidates={(double)candidates / attempts:F2}, goodIds={goodIds}, avgGoodIds={(double)goodIds / attempts:F2}");
    }

    private static bool HasCapacityForAnyCandidateGood(Inventory receivingInventory, List<Yielder> yielders, out int checkedCandidates, out int checkedGoodIds)
    {
        checkedCandidates = 0;
        checkedGoodIds = 0;
        Interlocked.Increment(ref _capacityPrecheckAttempts);

        if (yielders.Count == 0)
        {
            return true;
        }

        var goodIds = _goodIdBuffer ??= new HashSet<string>(StringComparer.Ordinal);
        goodIds.Clear();
        for (var index = 0; index < yielders.Count; index++)
        {
            checkedCandidates++;
            var yielder = yielders[index];
            if (yielder == null)
            {
                continue;
            }

            var goodId = yielder.Yield.GoodId;
            if (string.IsNullOrEmpty(goodId) || !goodIds.Add(goodId))
            {
                continue;
            }

            checkedGoodIds++;
            if (receivingInventory.UnreservedCapacity(goodId) > 0)
            {
                Interlocked.Add(ref _capacityPrecheckCandidates, checkedCandidates);
                Interlocked.Add(ref _capacityPrecheckGoodIds, checkedGoodIds);
                return true;
            }
        }

        Interlocked.Add(ref _capacityPrecheckCandidates, checkedCandidates);
        Interlocked.Add(ref _capacityPrecheckGoodIds, checkedGoodIds);
        return goodIds.Count == 0;
    }

    private static CachedReachableYielder[] GetOrBuildDistanceCache(
        DistanceCacheKey cacheKey,
        Accessible start,
        List<Yielder> yielders,
        out int pathCalls,
        out bool cacheHit,
        out bool cacheMiss)
    {
        pathCalls = 0;
        cacheHit = false;
        cacheMiss = false;

        var frame = Time.frameCount;
        lock (DistanceCacheLock)
        {
            if (DistanceCache.TryGetValue(cacheKey, out var entry) &&
                frame - entry.CreatedFrame <= BenchmarkSettings.FastYielderDistanceCacheMaxAgeFrames)
            {
                cacheHit = true;
                return entry.ReachableYielders;
            }
        }

        cacheMiss = true;
        var buildBuffer = GetCacheBuildBuffer();
        for (var index = 0; index < yielders.Count; index++)
        {
            var yielder = yielders[index];
            pathCalls++;
            if (start.FindTerrainPath(yielder.CenterPosition, out var distance))
            {
                buildBuffer.Add(new CachedReachableYielder(yielder, distance, yielder.InstantiationOrder));
            }
        }

        buildBuffer.Sort(CompareReachableYielderDistance);
        var reachableYielders = buildBuffer.ToArray();

        lock (DistanceCacheLock)
        {
            if (DistanceCache.Count >= BenchmarkSettings.FastYielderDistanceCacheMaxEntries)
            {
                RemoveOldestDistanceCacheEntry();
            }

            DistanceCache[cacheKey] = new DistanceCacheEntry(reachableYielders, frame);
        }

        return reachableYielders;
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

    private static CarryAmountCalculator? GetCarryAmountCalculator(YielderFinder yielderFinder)
    {
        if (ReferenceEquals(_cachedYielderFinder, yielderFinder))
        {
            return _cachedCarryAmountCalculator;
        }

        lock (CalculatorLock)
        {
            if (ReferenceEquals(_cachedYielderFinder, yielderFinder))
            {
                return _cachedCarryAmountCalculator;
            }

            var calculator = FindCarryAmountCalculator(yielderFinder);
            _cachedYielderFinder = yielderFinder;
            _cachedCarryAmountCalculator = calculator;
            return calculator;
        }
    }

    private static CarryAmountCalculator? FindCarryAmountCalculator(YielderFinder yielderFinder)
    {
        if (ClosestYielderFinderField?.GetValue(yielderFinder) is not ClosestYielderFinder closestYielderFinder)
        {
            return null;
        }

        return CarryAmountCalculatorField?.GetValue(closestYielderFinder) as CarryAmountCalculator;
    }

    private static bool IsAliveOrFallback(Yielder yielder)
    {
        return yielder.IsAlive();
    }

    private static List<Yielder> GetCandidateBuffer()
    {
        return _candidateBuffer ??= new List<Yielder>(512);
    }

    private static List<CachedReachableYielder> GetCacheBuildBuffer()
    {
        return _cacheBuildBuffer ??= new List<CachedReachableYielder>(512);
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
        public DistanceCacheEntry(CachedReachableYielder[] reachableYielders, int createdFrame)
        {
            ReachableYielders = reachableYielders;
            CreatedFrame = createdFrame;
        }

        public CachedReachableYielder[] ReachableYielders { get; }
        public int CreatedFrame { get; }
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
        private DistanceCacheKey(
            int startIdentity,
            int startX,
            int startY,
            int startZ,
            int candidateCount,
            int candidateHash)
        {
            StartIdentity = startIdentity;
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            CandidateCount = candidateCount;
            CandidateHash = candidateHash;
        }

        public int StartIdentity { get; }
        public int StartX { get; }
        public int StartY { get; }
        public int StartZ { get; }
        public int CandidateCount { get; }
        public int CandidateHash { get; }

        public static DistanceCacheKey Create(Accessible start, Vector3 startPosition, List<Yielder> yielders)
        {
            unchecked
            {
                var hash = 17;
                for (var index = 0; index < yielders.Count; index++)
                {
                    var yielder = yielders[index];
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(yielder);
                    hash = hash * 31 + yielder.InstantiationOrder;
                    hash = hash * 31 + Quantize(yielder.CenterPosition.x);
                    hash = hash * 31 + Quantize(yielder.CenterPosition.y);
                    hash = hash * 31 + Quantize(yielder.CenterPosition.z);
                }

                return new DistanceCacheKey(
                    RuntimeHelpers.GetHashCode(start),
                    Quantize(startPosition.x),
                    Quantize(startPosition.y),
                    Quantize(startPosition.z),
                    yielders.Count,
                    hash);
            }
        }

        public bool Equals(DistanceCacheKey other)
        {
            return StartIdentity == other.StartIdentity &&
                StartX == other.StartX &&
                StartY == other.StartY &&
                StartZ == other.StartZ &&
                CandidateCount == other.CandidateCount &&
                CandidateHash == other.CandidateHash;
        }

        public override bool Equals(object? obj)
        {
            return obj is DistanceCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StartIdentity;
                hash = hash * 397 ^ StartX;
                hash = hash * 397 ^ StartY;
                hash = hash * 397 ^ StartZ;
                hash = hash * 397 ^ CandidateCount;
                hash = hash * 397 ^ CandidateHash;
                return hash;
            }
        }
    }

    private static int Quantize(float value)
    {
        return Mathf.RoundToInt(value * 100f);
    }
}
