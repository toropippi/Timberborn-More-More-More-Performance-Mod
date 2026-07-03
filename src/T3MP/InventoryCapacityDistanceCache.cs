using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BlockingSystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class InventoryCapacityDistanceCache
{
    private static readonly object LockObject = new object();
    private static readonly Dictionary<CacheKey, CacheEntry> Entries =
        new Dictionary<CacheKey, CacheEntry>(CacheKeyComparer.Instance);
    private static readonly Dictionary<Inventory, List<CapacityIndexReference>> CapacityIndexReferences =
        new Dictionary<Inventory, List<CapacityIndexReference>>(InventoryReferenceComparer.Instance);
    private static readonly Dictionary<Inventory, byte> SubscribedInventories =
        new Dictionary<Inventory, byte>(InventoryReferenceComparer.Instance);

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _builds;
    private static long _hits;
    private static long _emptyResults;
    private static long _candidateChecks;
    private static long _capacityRejects;
    private static long _filterRejects;
    private static long _buildCandidates;
    private static long _buildReachable;
    private static long _capacityIndexChecks;
    private static long _capacityIndexFullScans;
    private static long _capacityIndexLiveUpdates;
    private static long _buildStopwatchTicks;
    private static long _totalStopwatchTicks;
    private static int _warningCount;

    public static bool TryFind(
        DistrictInventoryPicker picker,
        Accessible start,
        GoodAmount goodAmount,
        Predicate<Inventory> inventoryFilter,
        ref float closestDistance,
        out Inventory? result)
    {
        result = null;
        var goodId = goodAmount.GoodId;
        if (!BenchmarkSettings.EnableInventoryCapacityDistanceCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            picker is null ||
            start is null ||
            string.IsNullOrEmpty(goodId) ||
            goodAmount.Amount <= 0 ||
            inventoryFilter is null)
        {
            return false;
        }

        var startTimestamp = BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        var localHandled = 0L;
        var localFallbacks = 0L;
        var localHits = 0L;
        var localEmptyResults = 0L;
        var localCandidateChecks = 0L;
        var localCapacityRejects = 0L;
        var localFilterRejects = 0L;
        var localCapacityIndexChecks = 0L;
        var localCapacityIndexFullScans = 0L;
        try
        {
            var key = new CacheKey(picker, start, goodId, goodAmount.Amount);
            CacheEntry entry;
            var frame = Time.frameCount;
            lock (LockObject)
            {
                if (!Entries.TryGetValue(key, out entry!) || entry.ExpiresAtFrame < frame)
                {
                    if (entry is not null)
                    {
                        UnregisterCapacityIndex(entry);
                    }

                    entry = BuildEntry(picker, start, goodAmount, frame);
                    Entries[key] = entry;
                }
            }

            List<int>? staleCapacityIndices = null;
            foreach (var index in entry.CapacityfulIndices)
            {
                var candidate = entry.Candidates[index];
                var inventory = candidate.Inventory;
                localCapacityIndexChecks++;
                localCandidateChecks++;
                if (!IsTaking(inventory, goodAmount))
                {
                    staleCapacityIndices ??= new List<int>(4);
                    staleCapacityIndices.Add(index);
                    localCapacityRejects++;
                    continue;
                }

                if (!inventoryFilter(inventory))
                {
                    localFilterRejects++;
                    continue;
                }

                localHandled++;
                localHits++;
                closestDistance = candidate.Distance;
                result = inventory;
                return true;
            }

            if (staleCapacityIndices is not null)
            {
                lock (LockObject)
                {
                    foreach (var index in staleCapacityIndices)
                    {
                        entry.SetCapacityful(index, false);
                    }
                }
            }

            localHandled++;
            localEmptyResults++;
            closestDistance = float.MaxValue;
            return true;
        }
        catch (Exception exception)
        {
            localFallbacks++;
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] Inventory capacity distance cache fallback: {exception}");
            }

            return false;
        }
        finally
        {
            Interlocked.Increment(ref _attempts);
            Interlocked.Add(ref _handled, localHandled);
            Interlocked.Add(ref _fallbacks, localFallbacks);
            Interlocked.Add(ref _hits, localHits);
            Interlocked.Add(ref _emptyResults, localEmptyResults);
            Interlocked.Add(ref _candidateChecks, localCandidateChecks);
            Interlocked.Add(ref _capacityRejects, localCapacityRejects);
            Interlocked.Add(ref _filterRejects, localFilterRejects);
            Interlocked.Add(ref _capacityIndexChecks, localCapacityIndexChecks);
            Interlocked.Add(ref _capacityIndexFullScans, localCapacityIndexFullScans);
            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                Interlocked.Add(ref _totalStopwatchTicks, Stopwatch.GetTimestamp() - startTimestamp);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var builds = Interlocked.Exchange(ref _builds, 0);
        var hits = Interlocked.Exchange(ref _hits, 0);
        var emptyResults = Interlocked.Exchange(ref _emptyResults, 0);
        var candidateChecks = Interlocked.Exchange(ref _candidateChecks, 0);
        var capacityRejects = Interlocked.Exchange(ref _capacityRejects, 0);
        var filterRejects = Interlocked.Exchange(ref _filterRejects, 0);
        var buildCandidates = Interlocked.Exchange(ref _buildCandidates, 0);
        var buildReachable = Interlocked.Exchange(ref _buildReachable, 0);
        var capacityIndexChecks = Interlocked.Exchange(ref _capacityIndexChecks, 0);
        var capacityIndexFullScans = Interlocked.Exchange(ref _capacityIndexFullScans, 0);
        var capacityIndexLiveUpdates = Interlocked.Exchange(ref _capacityIndexLiveUpdates, 0);
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
            $"[T3MP] InventoryCapacityDistanceCache aggregate={aggregateId}, enabled={BenchmarkSettings.EnableInventoryCapacityDistanceCache}, ttlFrames={BenchmarkSettings.InventoryCapacityDistanceCacheTtlFrames}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, hits={hits}, hitRate={hitRate:F3}, empty={emptyResults}, builds={builds}, buildCandidates={buildCandidates}, buildReachable={buildReachable}, candidateChecks={candidateChecks}, capacityRejects={capacityRejects}, filterRejects={filterRejects}, capacityIndexChecks={capacityIndexChecks}, capacityIndexFullScans={capacityIndexFullScans}, capacityIndexLiveUpdates={capacityIndexLiveUpdates}, buildMs={ToMilliseconds(buildTicks):F2}, totalMs={ToMilliseconds(totalTicks):F2}, entries={entries}");
    }

    private static CacheEntry BuildEntry(DistrictInventoryPicker picker, Accessible start, GoodAmount goodAmount, int frame)
    {
        var stopwatch = BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        var localBuildCandidates = 0L;
        var localBuildReachable = 0L;
        var entry = new CacheEntry(goodAmount, frame + BenchmarkSettings.InventoryCapacityDistanceCacheTtlFrames);
        var registry = picker.GetComponent<DistrictInventoryRegistry>();
        var goodId = goodAmount.GoodId;
        foreach (var inventory in registry.Inventories)
        {
            localBuildCandidates++;
            if (!inventory || !inventory.Takes(goodId))
            {
                continue;
            }

            var accessible = inventory.GetEnabledComponent<Accessible>();
            if (accessible && start.FindRoadPath(accessible, out var distance))
            {
                entry.Candidates.Add(new Candidate(inventory, distance));
                localBuildReachable++;
            }
        }

        entry.Candidates.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        RegisterCapacityIndex(entry);
        Interlocked.Increment(ref _builds);
        Interlocked.Add(ref _buildCandidates, localBuildCandidates);
        Interlocked.Add(ref _buildReachable, localBuildReachable);
        if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
        {
            Interlocked.Add(ref _buildStopwatchTicks, Stopwatch.GetTimestamp() - stopwatch);
        }

        return entry;
    }

    private static bool TryFindByFullScanAndRefresh(
        CacheEntry entry,
        GoodAmount goodAmount,
        Predicate<Inventory> inventoryFilter,
        ref float closestDistance,
        out Inventory? result,
        ref long localCandidateChecks,
        ref long localCapacityRejects,
        ref long localFilterRejects)
    {
        result = null;
        for (var index = 0; index < entry.Candidates.Count; index++)
        {
            var candidate = entry.Candidates[index];
            var inventory = candidate.Inventory;
            localCandidateChecks++;
            if (!IsTaking(inventory, goodAmount))
            {
                localCapacityRejects++;
                continue;
            }

            lock (LockObject)
            {
                entry.SetCapacityful(index, true);
            }

            if (!inventoryFilter(inventory))
            {
                localFilterRejects++;
                continue;
            }

            closestDistance = candidate.Distance;
            result = inventory;
            return true;
        }

        return false;
    }

    private static void RegisterCapacityIndex(CacheEntry entry)
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

            if (!CapacityIndexReferences.TryGetValue(inventory, out var references))
            {
                references = new List<CapacityIndexReference>(4);
                CapacityIndexReferences.Add(inventory, references);
            }

            references.Add(new CapacityIndexReference(entry, index));
            if (IsTaking(inventory, entry.GoodAmount))
            {
                entry.SetCapacityful(index, true);
            }
        }
    }

    private static void UnregisterCapacityIndex(CacheEntry entry)
    {
        for (var index = 0; index < entry.Candidates.Count; index++)
        {
            var inventory = entry.Candidates[index].Inventory;
            if (!inventory || !CapacityIndexReferences.TryGetValue(inventory, out var references))
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
                CapacityIndexReferences.Remove(inventory);
            }
        }

        entry.CapacityfulIndices.Clear();
    }

    private static void OnInventoryChanged(object sender, InventoryChangedEventArgs e)
    {
        if (sender is not Inventory inventory)
        {
            return;
        }

        lock (LockObject)
        {
            if (!CapacityIndexReferences.TryGetValue(inventory, out var references))
            {
                return;
            }

            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                reference.Entry.SetCapacityful(reference.Index, IsTaking(inventory, reference.Entry.GoodAmount));
            }
        }

        Interlocked.Increment(ref _capacityIndexLiveUpdates);
    }

    private static bool IsTaking(Inventory inventory, GoodAmount goodAmount)
    {
        if (!inventory || !inventory.HasUnreservedCapacity(goodAmount))
        {
            return false;
        }

        var validator = inventory.GetComponent<IInventoryValidator>();
        if (!validator.ValidInventory)
        {
            return false;
        }

        return inventory.GetComponent<BlockableObject>().IsUnblocked;
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
        public CacheEntry(GoodAmount goodAmount, int expiresAtFrame)
        {
            GoodAmount = goodAmount;
            ExpiresAtFrame = expiresAtFrame;
        }

        public GoodAmount GoodAmount { get; }

        public int ExpiresAtFrame { get; }

        public List<Candidate> Candidates { get; } = new List<Candidate>(64);

        public SortedSet<int> CapacityfulIndices { get; } = new SortedSet<int>();

        public void SetCapacityful(int index, bool capacityful)
        {
            if (capacityful)
            {
                CapacityfulIndices.Add(index);
            }
            else
            {
                CapacityfulIndices.Remove(index);
            }
        }
    }

    private readonly struct CapacityIndexReference
    {
        public CapacityIndexReference(CacheEntry entry, int index)
        {
            Entry = entry;
            Index = index;
        }

        public CacheEntry Entry { get; }

        public int Index { get; }
    }

    private readonly struct CacheKey
    {
        public CacheKey(DistrictInventoryPicker picker, Accessible start, string goodId, int amount)
        {
            Picker = picker;
            Start = start;
            GoodId = goodId;
            Amount = amount;
        }

        public DistrictInventoryPicker Picker { get; }

        public Accessible Start { get; }

        public string GoodId { get; }

        public int Amount { get; }
    }

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        public static readonly CacheKeyComparer Instance = new CacheKeyComparer();

        public bool Equals(CacheKey x, CacheKey y)
        {
            return ReferenceEquals(x.Picker, y.Picker) &&
                ReferenceEquals(x.Start, y.Start) &&
                string.Equals(x.GoodId, y.GoodId, StringComparison.Ordinal) &&
                x.Amount == y.Amount;
        }

        public int GetHashCode(CacheKey obj)
        {
            unchecked
            {
                var hash = RuntimeHelpers.GetHashCode(obj.Picker);
                hash = (hash * 397) ^ RuntimeHelpers.GetHashCode(obj.Start);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(obj.GoodId);
                hash = (hash * 397) ^ obj.Amount;
                return hash;
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
