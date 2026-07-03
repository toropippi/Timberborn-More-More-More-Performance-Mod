using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class InventoryStockDistanceCache
{
    private static readonly object LockObject = new object();
    private static readonly Dictionary<CacheKey, CacheEntry> Entries =
        new Dictionary<CacheKey, CacheEntry>(CacheKeyComparer.Instance);
    private static readonly Dictionary<StockIndexKey, List<StockIndexReference>> StockIndexReferences =
        new Dictionary<StockIndexKey, List<StockIndexReference>>(StockIndexKeyComparer.Instance);
    private static readonly Dictionary<Inventory, byte> SubscribedInventories =
        new Dictionary<Inventory, byte>(InventoryReferenceComparer.Instance);

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _builds;
    private static long _hits;
    private static long _emptyResults;
    private static long _candidateChecks;
    private static long _stockRejects;
    private static long _filterRejects;
    private static long _buildCandidates;
    private static long _buildReachable;
    private static long _stockIndexChecks;
    private static long _stockIndexFullScans;
    private static long _stockIndexLiveUpdates;
    private static long _buildStopwatchTicks;
    private static long _totalStopwatchTicks;
    private static int _warningCount;

    public static bool TryFind(
        DistrictInventoryPicker picker,
        Accessible start,
        string goodId,
        Predicate<Inventory> inventoryFilter,
        out Inventory? result)
    {
        result = null;
        if (!BenchmarkSettings.EnableInventoryStockDistanceCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            picker is null ||
            start is null ||
            string.IsNullOrEmpty(goodId) ||
            inventoryFilter is null)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        var startTimestamp = recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        var localHandled = 0L;
        var localFallbacks = 0L;
        var localHits = 0L;
        var localEmptyResults = 0L;
        var localCandidateChecks = 0L;
        var localStockRejects = 0L;
        var localFilterRejects = 0L;
        var localStockIndexChecks = 0L;
        var localStockIndexFullScans = 0L;
        try
        {
            var key = new CacheKey(picker, start, goodId);
            CacheEntry entry;
            var frame = Time.frameCount;
            lock (LockObject)
            {
                if (!Entries.TryGetValue(key, out entry!) || entry.ExpiresAtFrame < frame)
                {
                    if (entry is not null)
                    {
                        UnregisterStockIndex(entry);
                    }

                    entry = BuildEntry(picker, start, goodId, frame);
                    Entries[key] = entry;
                }
            }

            List<int>? staleStockIndices = null;
            foreach (var index in entry.StockfulIndices)
            {
                var inventory = entry.Candidates[index].Inventory;
                if (recordMetrics)
                {
                    localStockIndexChecks++;
                    localCandidateChecks++;
                }
                if (!inventory || !inventory.HasUnreservedStock(goodId))
                {
                    staleStockIndices ??= new List<int>(4);
                    staleStockIndices.Add(index);
                    if (recordMetrics)
                    {
                        localStockRejects++;
                    }
                    continue;
                }

                if (!inventoryFilter(inventory))
                {
                    if (recordMetrics)
                    {
                        localFilterRejects++;
                    }
                    continue;
                }

                if (recordMetrics)
                {
                    localHandled++;
                    localHits++;
                }
                result = inventory;
                return true;
            }

            if (staleStockIndices is not null)
            {
                lock (LockObject)
                {
                    foreach (var index in staleStockIndices)
                    {
                        entry.SetStockful(index, false);
                    }
                }
            }

            if (entry.StockfulIndices.Count == 0)
            {
                if (recordMetrics)
                {
                    localStockIndexFullScans++;
                }
                if (TryFindByFullScanAndRefresh(entry, goodId, inventoryFilter, out result, recordMetrics, ref localCandidateChecks, ref localStockRejects, ref localFilterRejects))
                {
                    if (recordMetrics)
                    {
                        localHandled++;
                        localHits++;
                    }
                    return true;
                }
            }

            if (recordMetrics)
            {
                localHandled++;
                localEmptyResults++;
            }
            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                localFallbacks++;
            }
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] Inventory stock distance cache fallback: {exception}");
            }

            return false;
        }
        finally
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _attempts);
                Interlocked.Add(ref _handled, localHandled);
                Interlocked.Add(ref _fallbacks, localFallbacks);
                Interlocked.Add(ref _hits, localHits);
                Interlocked.Add(ref _emptyResults, localEmptyResults);
                Interlocked.Add(ref _candidateChecks, localCandidateChecks);
                Interlocked.Add(ref _stockRejects, localStockRejects);
                Interlocked.Add(ref _filterRejects, localFilterRejects);
                Interlocked.Add(ref _stockIndexChecks, localStockIndexChecks);
                Interlocked.Add(ref _stockIndexFullScans, localStockIndexFullScans);
            }
            if (recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                Interlocked.Add(ref _totalStopwatchTicks, Stopwatch.GetTimestamp() - startTimestamp);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var builds = Interlocked.Exchange(ref _builds, 0);
        var hits = Interlocked.Exchange(ref _hits, 0);
        var emptyResults = Interlocked.Exchange(ref _emptyResults, 0);
        var candidateChecks = Interlocked.Exchange(ref _candidateChecks, 0);
        var stockRejects = Interlocked.Exchange(ref _stockRejects, 0);
        var filterRejects = Interlocked.Exchange(ref _filterRejects, 0);
        var buildCandidates = Interlocked.Exchange(ref _buildCandidates, 0);
        var buildReachable = Interlocked.Exchange(ref _buildReachable, 0);
        var stockIndexChecks = Interlocked.Exchange(ref _stockIndexChecks, 0);
        var stockIndexFullScans = Interlocked.Exchange(ref _stockIndexFullScans, 0);
        var stockIndexLiveUpdates = Interlocked.Exchange(ref _stockIndexLiveUpdates, 0);
        var buildTicks = Interlocked.Exchange(ref _buildStopwatchTicks, 0);
        var totalTicks = Interlocked.Exchange(ref _totalStopwatchTicks, 0);
        if (attempts == 0 && builds == 0)
        {
            return;
        }

        int entries;
        lock (LockObject)
        {
            entries = Entries.Count;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var hitRate = attempts > 0 ? (double)hits / attempts : 0.0;
        Debug.Log(
            $"[T3MP] InventoryStockDistanceCache aggregate={aggregateId}, enabled={BenchmarkSettings.EnableInventoryStockDistanceCache}, ttlFrames={BenchmarkSettings.InventoryStockDistanceCacheTtlFrames}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, hits={hits}, hitRate={hitRate:F3}, empty={emptyResults}, builds={builds}, buildCandidates={buildCandidates}, buildReachable={buildReachable}, candidateChecks={candidateChecks}, stockRejects={stockRejects}, filterRejects={filterRejects}, stockIndexChecks={stockIndexChecks}, stockIndexFullScans={stockIndexFullScans}, stockIndexLiveUpdates={stockIndexLiveUpdates}, buildMs={ToMilliseconds(buildTicks):F2}, totalMs={ToMilliseconds(totalTicks):F2}, entries={entries}");
    }

    private static CacheEntry BuildEntry(DistrictInventoryPicker picker, Accessible start, string goodId, int frame)
    {
        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        var stopwatch = recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        var localBuildCandidates = 0L;
        var localBuildReachable = 0L;
        var entry = new CacheEntry(goodId, frame + BenchmarkSettings.InventoryStockDistanceCacheTtlFrames);
        var registry = picker.GetComponent<DistrictInventoryRegistry>();
        foreach (var inventory in registry.Inventories)
        {
            if (recordMetrics)
            {
                localBuildCandidates++;
            }
            if (!inventory || !inventory.Gives(goodId))
            {
                continue;
            }

            var accessible = inventory.GetEnabledComponent<Accessible>();
            if (accessible && start.FindRoadPath(accessible, out var distance))
            {
                entry.Candidates.Add(new Candidate(inventory, distance));
                if (recordMetrics)
                {
                    localBuildReachable++;
                }
            }
        }

        entry.Candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        RegisterStockIndex(entry);
        if (recordMetrics)
        {
            Interlocked.Increment(ref _builds);
            Interlocked.Add(ref _buildCandidates, localBuildCandidates);
            Interlocked.Add(ref _buildReachable, localBuildReachable);
        }
        if (recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming)
        {
            Interlocked.Add(ref _buildStopwatchTicks, Stopwatch.GetTimestamp() - stopwatch);
        }
        return entry;
    }

    private static bool TryFindByFullScanAndRefresh(
        CacheEntry entry,
        string goodId,
        Predicate<Inventory> inventoryFilter,
        out Inventory? result,
        bool recordMetrics,
        ref long localCandidateChecks,
        ref long localStockRejects,
        ref long localFilterRejects)
    {
        result = null;
        for (var index = 0; index < entry.Candidates.Count; index++)
        {
            var inventory = entry.Candidates[index].Inventory;
            if (recordMetrics)
            {
                localCandidateChecks++;
            }
            if (!inventory || !inventory.HasUnreservedStock(goodId))
            {
                if (recordMetrics)
                {
                    localStockRejects++;
                }
                continue;
            }

            lock (LockObject)
            {
                entry.SetStockful(index, true);
            }

            if (!inventoryFilter(inventory))
            {
                if (recordMetrics)
                {
                    localFilterRejects++;
                }
                continue;
            }

            result = inventory;
            return true;
        }

        return false;
    }

    private static void RegisterStockIndex(CacheEntry entry)
    {
        for (var index = 0; index < entry.Candidates.Count; index++)
        {
            var inventory = entry.Candidates[index].Inventory;
            if (!inventory)
            {
                continue;
            }

            if (!SubscribedInventories.ContainsKey(inventory))
            {
                inventory.InventoryChanged += OnInventoryChanged;
                SubscribedInventories.Add(inventory, 0);
            }

            var key = new StockIndexKey(inventory, entry.GoodId);
            if (!StockIndexReferences.TryGetValue(key, out var references))
            {
                references = new List<StockIndexReference>(4);
                StockIndexReferences.Add(key, references);
            }

            references.Add(new StockIndexReference(entry, index));
            if (inventory.HasUnreservedStock(entry.GoodId))
            {
                entry.SetStockful(index, true);
            }
        }
    }

    private static void UnregisterStockIndex(CacheEntry entry)
    {
        for (var index = 0; index < entry.Candidates.Count; index++)
        {
            var inventory = entry.Candidates[index].Inventory;
            if (!inventory)
            {
                continue;
            }

            var key = new StockIndexKey(inventory, entry.GoodId);
            if (!StockIndexReferences.TryGetValue(key, out var references))
            {
                continue;
            }

            for (var referenceIndex = references.Count - 1; referenceIndex >= 0; referenceIndex--)
            {
                var reference = references[referenceIndex];
                if (ReferenceEquals(reference.Entry, entry) && reference.Index == index)
                {
                    references.RemoveAt(referenceIndex);
                    break;
                }
            }

            if (references.Count == 0)
            {
                StockIndexReferences.Remove(key);
            }
        }

        entry.StockfulIndices.Clear();
    }

    private static void OnInventoryChanged(object sender, InventoryChangedEventArgs e)
    {
        if (sender is not Inventory inventory || string.IsNullOrEmpty(e.GoodId))
        {
            return;
        }

        lock (LockObject)
        {
            var key = new StockIndexKey(inventory, e.GoodId);
            if (!StockIndexReferences.TryGetValue(key, out var references))
            {
                return;
            }

            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                reference.Entry.SetStockful(reference.Index, inventory && inventory.HasUnreservedStock(e.GoodId));
            }
        }

        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            Interlocked.Increment(ref _stockIndexLiveUpdates);
        }
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private readonly struct Candidate
    {
        public Candidate(Inventory inventory, float distance)
        {
            Inventory = inventory;
            Distance = distance;
        }

        public Inventory Inventory { get; }

        public float Distance { get; }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string goodId, int expiresAtFrame)
        {
            GoodId = goodId;
            ExpiresAtFrame = expiresAtFrame;
        }

        public string GoodId { get; }

        public int ExpiresAtFrame { get; }

        public List<Candidate> Candidates { get; } = new List<Candidate>(64);

        public SortedSet<int> StockfulIndices { get; } = new SortedSet<int>();

        public void SetStockful(int index, bool stockful)
        {
            if (stockful)
            {
                StockfulIndices.Add(index);
            }
            else
            {
                StockfulIndices.Remove(index);
            }
        }
    }

    private readonly struct StockIndexReference
    {
        public StockIndexReference(CacheEntry entry, int index)
        {
            Entry = entry;
            Index = index;
        }

        public CacheEntry Entry { get; }

        public int Index { get; }
    }

    private readonly struct StockIndexKey
    {
        public StockIndexKey(Inventory inventory, string goodId)
        {
            Inventory = inventory;
            GoodId = goodId;
        }

        public Inventory Inventory { get; }

        public string GoodId { get; }
    }

    private readonly struct CacheKey
    {
        public CacheKey(DistrictInventoryPicker picker, Accessible start, string goodId)
        {
            Picker = picker;
            Start = start;
            GoodId = goodId;
        }

        public DistrictInventoryPicker Picker { get; }

        public Accessible Start { get; }

        public string GoodId { get; }
    }

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static readonly CacheKeyComparer Instance = new CacheKeyComparer();

        public bool Equals(CacheKey x, CacheKey y)
        {
            return ReferenceEquals(x.Picker, y.Picker) &&
                ReferenceEquals(x.Start, y.Start) &&
                string.Equals(x.GoodId, y.GoodId, StringComparison.Ordinal);
        }

        public int GetHashCode(CacheKey obj)
        {
            unchecked
            {
                var hash = RuntimeHelpers.GetHashCode(obj.Picker);
                hash = (hash * 397) ^ RuntimeHelpers.GetHashCode(obj.Start);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(obj.GoodId);
                return hash;
            }
        }
    }

    private sealed class StockIndexKeyComparer : IEqualityComparer<StockIndexKey>
    {
        public static readonly StockIndexKeyComparer Instance = new StockIndexKeyComparer();

        public bool Equals(StockIndexKey x, StockIndexKey y)
        {
            return ReferenceEquals(x.Inventory, y.Inventory) &&
                string.Equals(x.GoodId, y.GoodId, StringComparison.Ordinal);
        }

        public int GetHashCode(StockIndexKey obj)
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(obj.Inventory) * 397) ^
                    StringComparer.Ordinal.GetHashCode(obj.GoodId);
            }
        }
    }

    private sealed class InventoryReferenceComparer : IEqualityComparer<Inventory>
    {
        public static readonly InventoryReferenceComparer Instance = new InventoryReferenceComparer();

        public bool Equals(Inventory? x, Inventory? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(Inventory obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
