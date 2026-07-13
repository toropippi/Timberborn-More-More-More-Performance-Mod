using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Carrying;
using Timberborn.Cutting;
using Timberborn.Goods;
using Timberborn.Growing;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Timberborn.TemplateSystem;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class GatherWorkplaceOptimizer
{
    private static readonly Type? GatherWorkplaceBehaviorType = FindType("Timberborn.Gathering.GatherWorkplaceBehavior");
    private static readonly FieldInfo? YielderFinderField = GatherWorkplaceBehaviorType?.GetField("_yielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? InventoryField = GatherWorkplaceBehaviorType?.GetField("_inventory", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? InRangeYieldersField = GatherWorkplaceBehaviorType?.GetField("_inRangeYielders", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? YieldersInRangeField = typeof(InRangeYielders).GetField("_yieldersInRange", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? ClosestYielderFinderField =
        typeof(YielderFinder).GetField("_closestYielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? CarryAmountCalculatorField =
        typeof(ClosestYielderFinder).GetField("_carryAmountCalculator", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object IndexLock = new object();
    private static readonly Dictionary<IndexKey, GatherIndex> Indexes = new Dictionary<IndexKey, GatherIndex>();

    private static int _warningLogged;
    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _candidateCount;
    private static long _reachableCount;
    private static long _pathCalls;
    private static long _indexBuilds;
    private static long _dirtyRebuilds;
    private static long _readyQueries;
    private static long _aliveQueries;
    private static long _presentQueries;
    private static long _reservedSkips;
    private static long _staleClears;
    private static long _capacityRejects;
    private static long _emptyResults;
    private static long _noYielderResults;

    public static bool TryFindYielder(
        object gatherWorkplaceBehavior,
        Accessible start,
        int liftingCapacity,
        string? templateName,
        out YielderSearchResult result)
    {
        result = default;
        if (!BenchmarkSettings.EnableGatherWorkplaceOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }

        try
        {
            if (YielderFinderField?.GetValue(gatherWorkplaceBehavior) is not YielderFinder yielderFinder ||
                InventoryField?.GetValue(gatherWorkplaceBehavior) is not Inventory inventory ||
                InRangeYieldersField?.GetValue(gatherWorkplaceBehavior) is not InRangeYielders inRangeYielders)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }

                return false;
            }

            var carryAmountCalculator = GetCarryAmountCalculator(yielderFinder);
            if (carryAmountCalculator is null)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }

                return false;
            }

            var index = GetIndex(gatherWorkplaceBehavior, inRangeYielders);
            var stats = index.Find(start, inventory, liftingCapacity, carryAmountCalculator, templateName, out result);
            if (recordMetrics)
            {
                RecordStats(stats);
                Interlocked.Increment(ref _handled);
            }

            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }

            if (Interlocked.Exchange(ref _warningLogged, 1) == 0)
            {
                Debug.LogWarning($"[T3MP] GatherWorkplaceOptimizer failed once; falling back to vanilla. {exception.GetType().Name}: {exception.Message}");
            }

            return false;
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
        var candidateCount = Interlocked.Exchange(ref _candidateCount, 0);
        var reachableCount = Interlocked.Exchange(ref _reachableCount, 0);
        var pathCalls = Interlocked.Exchange(ref _pathCalls, 0);
        var indexBuilds = Interlocked.Exchange(ref _indexBuilds, 0);
        var dirtyRebuilds = Interlocked.Exchange(ref _dirtyRebuilds, 0);
        var readyQueries = Interlocked.Exchange(ref _readyQueries, 0);
        var aliveQueries = Interlocked.Exchange(ref _aliveQueries, 0);
        var presentQueries = Interlocked.Exchange(ref _presentQueries, 0);
        var reservedSkips = Interlocked.Exchange(ref _reservedSkips, 0);
        var staleClears = Interlocked.Exchange(ref _staleClears, 0);
        var capacityRejects = Interlocked.Exchange(ref _capacityRejects, 0);
        var emptyResults = Interlocked.Exchange(ref _emptyResults, 0);
        var noYielderResults = Interlocked.Exchange(ref _noYielderResults, 0);
        if (attempts == 0)
        {
            return;
        }

        int entries;
        lock (IndexLock)
        {
            entries = Indexes.Count;
        }

        Debug.Log(
            $"[T3MP] GatherWorkplaceOptimizer aggregate={aggregateId}, enabled={BenchmarkSettings.EnableGatherWorkplaceOptimizer}, attempts={attempts}, handled={handled}, handledRate={(double)handled / attempts:F3}, fallbacks={fallbacks}, avgCandidates={(double)candidateCount / attempts:F2}, avgReachable={(double)reachableCount / attempts:F2}, pathCalls={pathCalls}, indexBuilds={indexBuilds}, dirtyRebuilds={dirtyRebuilds}, readyQueries={readyQueries}, aliveQueries={aliveQueries}, presentQueries={presentQueries}, reservedSkips={reservedSkips}, staleClears={staleClears}, capacityRejects={capacityRejects}, empty={emptyResults}, noYielderInRange={noYielderResults}, entries={entries}");
    }

    // Terrain-path reachability (start.FindTerrainPath) is cached per index and
    // otherwise only recomputed when the spatial in-range yielder set or the
    // start position changes - NOT when roads/paths change. A route change can
    // make a fruiting resource newly reachable (or unreachable) without touching
    // either, so without this the gatherer would keep ignoring it. Vanilla
    // recomputes the path every Decide(); this restores that by forcing a full
    // rebuild (which re-runs FindTerrainPath) on the next search after any
    // regular-navmesh update. Called from the NavMeshUpdateNotifier hook.
    public static void OnNavMeshUpdate()
    {
        lock (IndexLock)
        {
            foreach (var index in Indexes.Values)
            {
                index.MarkDistanceDirty();
            }
        }
    }

    private static GatherIndex GetIndex(object gatherWorkplaceBehavior, InRangeYielders inRangeYielders)
    {
        var key = new IndexKey(gatherWorkplaceBehavior, inRangeYielders);
        lock (IndexLock)
        {
            if (!Indexes.TryGetValue(key, out var index))
            {
                index = new GatherIndex(inRangeYielders);
                Indexes.Add(key, index);
            }

            return index;
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

    private static void RecordStats(GatherStats stats)
    {
        Interlocked.Add(ref _candidateCount, stats.Candidates);
        Interlocked.Add(ref _reachableCount, stats.Reachable);
        Interlocked.Add(ref _pathCalls, stats.PathCalls);
        if (stats.IndexBuilt)
        {
            Interlocked.Increment(ref _indexBuilds);
        }
        if (stats.DirtyRebuild)
        {
            Interlocked.Increment(ref _dirtyRebuilds);
        }

        Interlocked.Add(ref _readyQueries, stats.ReadyQueries);
        Interlocked.Add(ref _aliveQueries, stats.AliveQueries);
        Interlocked.Add(ref _presentQueries, stats.PresentQueries);
        Interlocked.Add(ref _reservedSkips, stats.ReservedSkips);
        Interlocked.Add(ref _staleClears, stats.StaleClears);
        Interlocked.Add(ref _capacityRejects, stats.CapacityRejects);
        if (stats.Empty)
        {
            Interlocked.Increment(ref _emptyResults);
        }
        if (stats.NoYielderInRange)
        {
            Interlocked.Increment(ref _noYielderResults);
        }
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

    private sealed class GatherIndex
    {
        private static readonly List<Yielder> ForceUpdateBuffer = new List<Yielder>(16);

        private readonly InRangeYielders _inRangeYielders;
        private readonly List<Slot> _slots = new List<Slot>(512);
        private readonly Dictionary<Yielder, int> _slotIndexByYielder = new Dictionary<Yielder, int>(ReferenceEqualityComparer<Yielder>.Instance);
        private readonly Dictionary<string, int> _inRangeCountByTemplate = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unreservedInRangeCountByTemplate = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, TreeSet> _treesByTemplate = new Dictionary<string, TreeSet>(StringComparer.Ordinal);
        private readonly Dictionary<string, TreeSet> _treesByGoodId = new Dictionary<string, TreeSet>(StringComparer.Ordinal);
        private readonly Dictionary<TemplateGoodKey, TreeSet> _treesByTemplateGoodId = new Dictionary<TemplateGoodKey, TreeSet>();
        private readonly List<string> _goodIds = new List<string>(32);
        private readonly HashSet<string> _goodIdSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _goodIdsByTemplate = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _goodIdSetsByTemplate = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        private TreeSet _allTrees = TreeSet.Empty;
        private bool _eventsSubscribed;
        private bool _distanceDirty = true;
        private int _generation;
        private int _lastStartIdentity;
        private int _lastStartX;
        private int _lastStartY;
        private int _lastStartZ;
        private int _inRangeCountAll;
        private int _unreservedInRangeCountAll;

        public GatherIndex(InRangeYielders inRangeYielders)
        {
            _inRangeYielders = inRangeYielders;
            SubscribeEvents();
        }

        // Forces the next EnsureBuilt to re-run FindTerrainPath for every
        // in-range yielder (reachability depends on the navmesh).
        public void MarkDistanceDirty()
        {
            _distanceDirty = true;
        }

        public GatherStats Find(
            Accessible start,
            Inventory inventory,
            int liftingCapacity,
            CarryAmountCalculator carryAmountCalculator,
            string? templateName,
            out YielderSearchResult result)
        {
            var stats = new GatherStats();
            result = default;

            EnsureBuilt(start, ref stats);
            var treeSet = string.IsNullOrWhiteSpace(templateName)
                ? _allTrees
                : (_treesByTemplate.TryGetValue(templateName, out var templateTrees) ? templateTrees : _allTrees);

            if (TryFindReadyByGood(
                    treeSet,
                    templateName,
                    inventory,
                    liftingCapacity,
                    carryAmountCalculator,
                    ref stats,
                    out result))
            {
                return stats;
            }

            result = AssessEmptyOrNoYielder(treeSet, templateName, ref stats);
            return stats;
        }

        private void EnsureBuilt(Accessible start, ref GatherStats stats)
        {
            SubscribeEvents();
            var startAccess = start.UnblockedSingleAccess;
            var startIdentity = RuntimeHelpers.GetHashCode(start);
            var startX = startAccess.HasValue ? Quantize(startAccess.Value.x) : int.MinValue;
            var startY = startAccess.HasValue ? Quantize(startAccess.Value.y) : int.MinValue;
            var startZ = startAccess.HasValue ? Quantize(startAccess.Value.z) : int.MinValue;
            if (!_distanceDirty &&
                startIdentity == _lastStartIdentity &&
                startX == _lastStartX &&
                startY == _lastStartY &&
                startZ == _lastStartZ)
            {
                stats.Candidates = _inRangeCountAll;
                stats.Reachable = _slots.Count;
                return;
            }

            stats.IndexBuilt = true;
            stats.DirtyRebuild = _distanceDirty;
            _distanceDirty = false;
            _lastStartIdentity = startIdentity;
            _lastStartX = startX;
            _lastStartY = startY;
            _lastStartZ = startZ;
            _generation++;

            UnsubscribeAllSlots();
            _slots.Clear();
            _slotIndexByYielder.Clear();
            _inRangeCountByTemplate.Clear();
            _unreservedInRangeCountByTemplate.Clear();
            _treesByTemplate.Clear();
            _treesByGoodId.Clear();
            _treesByTemplateGoodId.Clear();
            _goodIds.Clear();
            _goodIdSet.Clear();
            _goodIdsByTemplate.Clear();
            _goodIdSetsByTemplate.Clear();
            _allTrees = TreeSet.Empty;
            _inRangeCountAll = 0;
            _unreservedInRangeCountAll = 0;

            if (!startAccess.HasValue || YieldersInRangeField?.GetValue(_inRangeYielders) is not List<Yielder> yieldersInRange)
            {
                return;
            }

            ForceUpdateBuffer.Clear();
            _inRangeYielders.GetYielders(ForceUpdateBuffer, null);
            ForceUpdateBuffer.Clear();

            var buildSlots = new List<Slot>(yieldersInRange.Count);
            for (var index = 0; index < yieldersInRange.Count; index++)
            {
                var yielder = yieldersInRange[index];
                if (yielder == null)
                {
                    continue;
                }

                var template = GetTemplateNameSafe(yielder);
                _inRangeCountAll++;
                Increment(_inRangeCountByTemplate, template);
                if (!IsReservedSafe(yielder))
                {
                    _unreservedInRangeCountAll++;
                    Increment(_unreservedInRangeCountByTemplate, template);
                }

                stats.PathCalls++;
                var goodId = GetGoodIdSafe(yielder);
                AddGoodId(template, goodId);

                if (start.FindTerrainPath(yielder.CenterPosition, out var distance))
                {
                    buildSlots.Add(new Slot(yielder, template, goodId, distance, yielder.InstantiationOrder, _generation));
                }
            }

            buildSlots.Sort(CompareSlots);
            _slots.AddRange(buildSlots);
            _allTrees = new TreeSet(_slots.Count);
            for (var index = 0; index < _slots.Count; index++)
            {
                var slot = _slots[index];
                if (!_slotIndexByYielder.ContainsKey(slot.Yielder))
                {
                    _slotIndexByYielder.Add(slot.Yielder, index);
                }

                SubscribeSlot(index);
                ReconcileSlotBits(index);
            }

            stats.Candidates = _inRangeCountAll;
            stats.Reachable = _slots.Count;
        }

        private bool TryFindReadyByGood(
            TreeSet treeSet,
            string? templateName,
            Inventory inventory,
            int liftingCapacity,
            CarryAmountCalculator carryAmountCalculator,
            ref GatherStats stats,
            out YielderSearchResult result)
        {
            result = default;
            var bestSlotIndex = -1;
            GoodAmount bestYield = default;
            var bestDistance = float.MaxValue;
            var goodIds = GetGoodIdsForQuery(templateName);
            for (var goodIndex = 0; goodIndex < goodIds.Count; goodIndex++)
            {
                var goodId = goodIds[goodIndex];
                if (string.IsNullOrEmpty(goodId) || inventory.UnreservedCapacity(goodId) <= 0)
                {
                    stats.CapacityRejects++;
                    continue;
                }

                var goodTreeSet = GetGoodTreeSet(templateName, goodId);
                var searchIndex = 0;
                while (goodTreeSet.Ready.FindFirst(searchIndex) is var slotIndex && slotIndex >= 0)
                {
                    stats.ReadyQueries++;
                    var slot = _slots[slotIndex];
                    var validation = ValidateReady(slot, templateName, inventory, liftingCapacity, carryAmountCalculator, out var yield);
                    if (validation == ReadyValidation.Valid)
                    {
                        if (slot.Distance < bestDistance)
                        {
                            bestDistance = slot.Distance;
                            bestSlotIndex = slotIndex;
                            bestYield = yield;
                        }

                        break;
                    }

                    if (validation == ReadyValidation.Reserved)
                    {
                        stats.ReservedSkips++;
                        searchIndex = slotIndex + 1;
                        continue;
                    }

                    if (validation == ReadyValidation.NotCarryable)
                    {
                        stats.CapacityRejects++;
                        break;
                    }

                    ClearReady(slotIndex);
                    stats.StaleClears++;
                    searchIndex = slotIndex + 1;
                }
            }

            if (bestSlotIndex >= 0)
            {
                result = YielderSearchResult.CreateSearchResult(_slots[bestSlotIndex].Yielder, bestYield);
                return true;
            }

            return false;
        }

        private YielderSearchResult AssessEmptyOrNoYielder(TreeSet treeSet, string? templateName, ref GatherStats stats)
        {
            var hasUnreservedReachable = false;
            var searchIndex = 0;
            while (treeSet.Alive.FindFirst(searchIndex) is var slotIndex && slotIndex >= 0)
            {
                stats.AliveQueries++;
                var slot = _slots[slotIndex];
                var validation = ValidateAlive(slot, templateName);
                if (validation == AliveValidation.Valid)
                {
                    stats.Empty = true;
                    return YielderSearchResult.CreateEmpty();
                }

                if (validation == AliveValidation.Reserved)
                {
                    stats.ReservedSkips++;
                    searchIndex = slotIndex + 1;
                    continue;
                }

                ClearAlive(slotIndex);
                stats.StaleClears++;
                searchIndex = slotIndex + 1;
            }

            searchIndex = 0;
            while (treeSet.Present.FindFirst(searchIndex) is var slotIndex && slotIndex >= 0)
            {
                stats.PresentQueries++;
                var slot = _slots[slotIndex];
                if (!TemplateMatches(slot, templateName) || slot.Yielder == null)
                {
                    ClearPresent(slotIndex);
                    stats.StaleClears++;
                    searchIndex = slotIndex + 1;
                    continue;
                }

                if (IsReservedSafe(slot.Yielder))
                {
                    stats.ReservedSkips++;
                    searchIndex = slotIndex + 1;
                    continue;
                }

                hasUnreservedReachable = true;
                break;
            }

            if (hasUnreservedReachable || HasUnreservedInRange(templateName))
            {
                stats.NoYielderInRange = true;
                return YielderSearchResult.CreateNoYielderInRange();
            }

            if (HasAnyInRange(templateName))
            {
                stats.Empty = true;
                return YielderSearchResult.CreateEmpty();
            }

            stats.NoYielderInRange = true;
            return YielderSearchResult.CreateNoYielderInRange();
        }

        private ReadyValidation ValidateReady(
            Slot slot,
            string? templateName,
            Inventory inventory,
            int liftingCapacity,
            CarryAmountCalculator carryAmountCalculator,
            out GoodAmount yield)
        {
            yield = default;
            var yielder = slot.Yielder;
            if (yielder == null || !TemplateMatches(slot, templateName) || !IsYieldingSafe(yielder))
            {
                return ReadyValidation.Stale;
            }

            if (IsReservedSafe(yielder))
            {
                return ReadyValidation.Reserved;
            }

            yield = CarryAmountCalculatorOptimizer.AmountToCarry(carryAmountCalculator, liftingCapacity, yielder.Yield, inventory);
            if (yield.Amount > 0)
            {
                return ReadyValidation.Valid;
            }

            return ReadyValidation.NotCarryable;
        }

        private static AliveValidation ValidateAlive(Slot slot, string? templateName)
        {
            var yielder = slot.Yielder;
            if (yielder == null || !TemplateMatches(slot, templateName) || !IsAliveOrYieldingSafe(yielder))
            {
                return AliveValidation.Stale;
            }

            return IsReservedSafe(yielder) ? AliveValidation.Reserved : AliveValidation.Valid;
        }

        private void ReconcileSlotBits(int slotIndex)
        {
            var slot = _slots[slotIndex];
            var yielder = slot.Yielder;
            var present = yielder != null;
            var alive = yielder != null && IsAliveOrYieldingSafe(yielder);
            var ready = yielder != null && IsYieldingSafe(yielder);
            SetPresent(slotIndex, present);
            SetAlive(slotIndex, alive);
            SetReady(slotIndex, ready);
        }

        private void UpdateSlotFromEvent(int slotIndex, int generation)
        {
            if (slotIndex < 0 || slotIndex >= _slots.Count || _slots[slotIndex].Generation != generation)
            {
                return;
            }

            ReconcileSlotBits(slotIndex);
        }

        private void ClearPresent(int slotIndex)
        {
            SetPresent(slotIndex, enabled: false);
            SetAlive(slotIndex, enabled: false);
            SetReady(slotIndex, enabled: false);
        }

        private void ClearAlive(int slotIndex)
        {
            SetAlive(slotIndex, enabled: false);
            SetReady(slotIndex, enabled: false);
        }

        private void ClearReady(int slotIndex)
        {
            SetReady(slotIndex, enabled: false);
        }

        private void SetPresent(int slotIndex, bool enabled)
        {
            _allTrees.Present.Set(slotIndex, enabled);
            var treeSet = GetTemplateTreeForSet(slotIndex, enabled);
            treeSet?.Present.Set(slotIndex, enabled);
            var goodTreeSet = GetGoodTreeForSet(slotIndex, enabled);
            goodTreeSet?.Present.Set(slotIndex, enabled);
            var templateGoodTreeSet = GetTemplateGoodTreeForSet(slotIndex, enabled);
            templateGoodTreeSet?.Present.Set(slotIndex, enabled);
        }

        private void SetAlive(int slotIndex, bool enabled)
        {
            _allTrees.Alive.Set(slotIndex, enabled);
            var treeSet = GetTemplateTreeForSet(slotIndex, enabled);
            treeSet?.Alive.Set(slotIndex, enabled);
            var goodTreeSet = GetGoodTreeForSet(slotIndex, enabled);
            goodTreeSet?.Alive.Set(slotIndex, enabled);
            var templateGoodTreeSet = GetTemplateGoodTreeForSet(slotIndex, enabled);
            templateGoodTreeSet?.Alive.Set(slotIndex, enabled);
        }

        private void SetReady(int slotIndex, bool enabled)
        {
            _allTrees.Ready.Set(slotIndex, enabled);
            var treeSet = GetTemplateTreeForSet(slotIndex, enabled);
            treeSet?.Ready.Set(slotIndex, enabled);
            var goodTreeSet = GetGoodTreeForSet(slotIndex, enabled);
            goodTreeSet?.Ready.Set(slotIndex, enabled);
            var templateGoodTreeSet = GetTemplateGoodTreeForSet(slotIndex, enabled);
            templateGoodTreeSet?.Ready.Set(slotIndex, enabled);
        }

        private TreeSet? GetTemplateTreeForSet(int slotIndex, bool enabled)
        {
            var template = _slots[slotIndex].TemplateName;
            if (string.IsNullOrEmpty(template))
            {
                return null;
            }

            if (enabled)
            {
                if (!_treesByTemplate.TryGetValue(template, out var treeSet))
                {
                    treeSet = new TreeSet(_slots.Count);
                    _treesByTemplate.Add(template, treeSet);
                }

                return treeSet;
            }

            return _treesByTemplate.TryGetValue(template, out var existingTreeSet) ? existingTreeSet : null;
        }

        private TreeSet? GetGoodTreeForSet(int slotIndex, bool enabled)
        {
            var goodId = _slots[slotIndex].GoodId;
            if (string.IsNullOrEmpty(goodId))
            {
                return null;
            }

            if (enabled)
            {
                if (!_treesByGoodId.TryGetValue(goodId, out var treeSet))
                {
                    treeSet = new TreeSet(_slots.Count);
                    _treesByGoodId.Add(goodId, treeSet);
                }

                return treeSet;
            }

            return _treesByGoodId.TryGetValue(goodId, out var existingTreeSet) ? existingTreeSet : null;
        }

        private TreeSet? GetTemplateGoodTreeForSet(int slotIndex, bool enabled)
        {
            var slot = _slots[slotIndex];
            if (string.IsNullOrEmpty(slot.TemplateName) || string.IsNullOrEmpty(slot.GoodId))
            {
                return null;
            }

            var key = new TemplateGoodKey(slot.TemplateName, slot.GoodId);
            if (enabled)
            {
                if (!_treesByTemplateGoodId.TryGetValue(key, out var treeSet))
                {
                    treeSet = new TreeSet(_slots.Count);
                    _treesByTemplateGoodId.Add(key, treeSet);
                }

                return treeSet;
            }

            return _treesByTemplateGoodId.TryGetValue(key, out var existingTreeSet) ? existingTreeSet : null;
        }

        private TreeSet GetGoodTreeSet(string? templateName, string goodId)
        {
            if (!string.IsNullOrWhiteSpace(templateName) &&
                _treesByTemplateGoodId.TryGetValue(new TemplateGoodKey(templateName, goodId), out var templateGoodTreeSet))
            {
                return templateGoodTreeSet;
            }

            return _treesByGoodId.TryGetValue(goodId, out var goodTreeSet) ? goodTreeSet : TreeSet.Empty;
        }

        private List<string> GetGoodIdsForQuery(string? templateName)
        {
            if (!string.IsNullOrWhiteSpace(templateName) &&
                _goodIdsByTemplate.TryGetValue(templateName, out var templateGoodIds))
            {
                return templateGoodIds;
            }

            return _goodIds;
        }

        private void AddGoodId(string template, string goodId)
        {
            if (string.IsNullOrEmpty(goodId))
            {
                return;
            }

            if (_goodIdSet.Add(goodId))
            {
                _goodIds.Add(goodId);
            }

            if (string.IsNullOrEmpty(template))
            {
                return;
            }

            if (!_goodIdSetsByTemplate.TryGetValue(template, out var templateSet))
            {
                templateSet = new HashSet<string>(StringComparer.Ordinal);
                _goodIdSetsByTemplate.Add(template, templateSet);
                _goodIdsByTemplate.Add(template, new List<string>(4));
            }

            if (templateSet.Add(goodId))
            {
                _goodIdsByTemplate[template].Add(goodId);
            }
        }

        private bool HasAnyInRange(string? templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return _inRangeCountAll > 0;
            }

            return _inRangeCountByTemplate.TryGetValue(templateName, out var count) && count > 0;
        }

        private bool HasUnreservedInRange(string? templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return _unreservedInRangeCountAll > 0;
            }

            return _unreservedInRangeCountByTemplate.TryGetValue(templateName, out var count) && count > 0;
        }

        private void SubscribeEvents()
        {
            if (_eventsSubscribed)
            {
                return;
            }

            _inRangeYielders.YieldersChanged += OnYieldersChanged;
            _inRangeYielders.YielderAdded += OnYielderAdded;
            _eventsSubscribed = true;
        }

        private void OnYieldersChanged(object sender, EventArgs e)
        {
            _distanceDirty = true;
        }

        private void OnYielderAdded(object sender, Yielder yielder)
        {
            _distanceDirty = true;
        }

        private void SubscribeSlot(int slotIndex)
        {
            var slot = _slots[slotIndex];
            var yielder = slot.Yielder;
            var generation = slot.Generation;
            EventHandler yieldHandler = (_, _) => UpdateSlotFromEvent(slotIndex, generation);
            yielder.YieldAdded += yieldHandler;
            yielder.YieldDecreased += yieldHandler;
            slot.YieldAddedHandler = yieldHandler;
            slot.YieldDecreasedHandler = yieldHandler;

            if (TryGetComponentSafe(yielder, out Growable growable))
            {
                EventHandler growthHandler = (_, _) => UpdateSlotFromEvent(slotIndex, generation);
                growable.HasGrown += growthHandler;
                slot.Growable = growable;
                slot.GrowthHandler = growthHandler;
            }

            if (TryGetComponentSafe(yielder, out Cuttable cuttable))
            {
                EventHandler cutHandler = (_, _) => UpdateSlotFromEvent(slotIndex, generation);
                cuttable.WasCut += cutHandler;
                slot.Cuttable = cuttable;
                slot.CutHandler = cutHandler;
            }
        }

        private void UnsubscribeAllSlots()
        {
            for (var index = 0; index < _slots.Count; index++)
            {
                var slot = _slots[index];
                var yielder = slot.Yielder;
                if (slot.YieldAddedHandler is { } yieldAddedHandler)
                {
                    yielder.YieldAdded -= yieldAddedHandler;
                }
                if (slot.YieldDecreasedHandler is { } yieldDecreasedHandler)
                {
                    yielder.YieldDecreased -= yieldDecreasedHandler;
                }
                if (slot.Growable is { } growable && slot.GrowthHandler is { } growthHandler)
                {
                    growable.HasGrown -= growthHandler;
                }
                if (slot.Cuttable is { } cuttable && slot.CutHandler is { } cutHandler)
                {
                    cuttable.WasCut -= cutHandler;
                }
            }
        }

        private static bool TemplateMatches(Slot slot, string? templateName)
        {
            return string.IsNullOrWhiteSpace(templateName) ||
                string.Equals(slot.TemplateName, templateName, StringComparison.Ordinal) ||
                IsNamedSafe(slot.Yielder, templateName);
        }

        private static bool IsNamedSafe(Yielder yielder, string templateName)
        {
            try
            {
                return yielder.GetComponent<TemplateSpec>().IsNamed(templateName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetTemplateNameSafe(Yielder yielder)
        {
            try
            {
                var templateSpec = yielder.GetComponent<TemplateSpec>();
                return templateSpec?.TemplateName ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string GetGoodIdSafe(Yielder yielder)
        {
            try
            {
                return yielder.Yield.GoodId ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsReservedSafe(Yielder yielder)
        {
            try
            {
                return yielder.Reservable.Reserved;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool IsYieldingSafe(Yielder yielder)
        {
            try
            {
                return yielder.IsYielding;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAliveOrYieldingSafe(Yielder yielder)
        {
            try
            {
                return yielder.IsYielding || yielder.IsAlive();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryGetComponentSafe<T>(Yielder yielder, out T component)
            where T : class
        {
            try
            {
                return yielder.TryGetComponent(out component);
            }
            catch (Exception)
            {
                component = null!;
                return false;
            }
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        private static int CompareSlots(Slot left, Slot right)
        {
            var distanceComparison = left.Distance.CompareTo(right.Distance);
            if (distanceComparison != 0)
            {
                return distanceComparison;
            }

            return left.InstantiationOrder.CompareTo(right.InstantiationOrder);
        }

        private static int Quantize(float value)
        {
            return Mathf.RoundToInt(value * 100f);
        }
    }

    private sealed class Slot
    {
        public Slot(Yielder yielder, string templateName, string goodId, float distance, int instantiationOrder, int generation)
        {
            Yielder = yielder;
            TemplateName = templateName;
            GoodId = goodId;
            Distance = distance;
            InstantiationOrder = instantiationOrder;
            Generation = generation;
        }

        public Yielder Yielder { get; }
        public string TemplateName { get; }
        public string GoodId { get; }
        public float Distance { get; }
        public int InstantiationOrder { get; }
        public int Generation { get; }
        public Growable? Growable { get; set; }
        public Cuttable? Cuttable { get; set; }
        public EventHandler? YieldAddedHandler { get; set; }
        public EventHandler? YieldDecreasedHandler { get; set; }
        public EventHandler? GrowthHandler { get; set; }
        public EventHandler? CutHandler { get; set; }
    }

    private sealed class TreeSet
    {
        public static readonly TreeSet Empty = new TreeSet(0);

        public TreeSet(int count)
        {
            Present = new SegmentTree(count);
            Alive = new SegmentTree(count);
            Ready = new SegmentTree(count);
        }

        public SegmentTree Present { get; }
        public SegmentTree Alive { get; }
        public SegmentTree Ready { get; }
    }

    private sealed class SegmentTree
    {
        private readonly int _count;
        private readonly int _size;
        private readonly int[] _tree;

        public SegmentTree(int count)
        {
            _count = count;
            _size = 1;
            while (_size < count)
            {
                _size <<= 1;
            }

            _tree = new int[_size << 1];
        }

        public void Set(int index, bool enabled)
        {
            if (index < 0 || index >= _count)
            {
                return;
            }

            var node = index + _size;
            _tree[node] = enabled ? 1 : 0;
            node >>= 1;
            while (node > 0)
            {
                _tree[node] = _tree[node << 1] + _tree[(node << 1) + 1];
                node >>= 1;
            }
        }

        public int FindFirst(int startIndex)
        {
            if (_count == 0 || startIndex >= _count)
            {
                return -1;
            }

            return FindFirst(1, 0, _size, Math.Max(0, startIndex));
        }

        private int FindFirst(int node, int left, int right, int startIndex)
        {
            if (_tree[node] == 0 || right <= startIndex)
            {
                return -1;
            }

            if (right - left == 1)
            {
                return left < _count ? left : -1;
            }

            var middle = (left + right) >> 1;
            var result = FindFirst(node << 1, left, middle, startIndex);
            return result >= 0 ? result : FindFirst((node << 1) + 1, middle, right, startIndex);
        }
    }

    private enum ReadyValidation
    {
        Valid,
        Reserved,
        Stale,
        NotCarryable
    }

    private enum AliveValidation
    {
        Valid,
        Reserved,
        Stale
    }

    private struct GatherStats
    {
        public int Candidates;
        public int Reachable;
        public int PathCalls;
        public int ReadyQueries;
        public int AliveQueries;
        public int PresentQueries;
        public int ReservedSkips;
        public int StaleClears;
        public int CapacityRejects;
        public bool IndexBuilt;
        public bool DirtyRebuild;
        public bool Empty;
        public bool NoYielderInRange;
    }

    private readonly struct IndexKey : IEquatable<IndexKey>
    {
        private readonly object _behavior;
        private readonly InRangeYielders _inRangeYielders;

        public IndexKey(object behavior, InRangeYielders inRangeYielders)
        {
            _behavior = behavior;
            _inRangeYielders = inRangeYielders;
        }

        public bool Equals(IndexKey other)
        {
            return ReferenceEquals(_behavior, other._behavior) &&
                ReferenceEquals(_inRangeYielders, other._inRangeYielders);
        }

        public override bool Equals(object? obj)
        {
            return obj is IndexKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(_behavior) * 397) ^ RuntimeHelpers.GetHashCode(_inRangeYielders);
            }
        }
    }

    private readonly struct TemplateGoodKey : IEquatable<TemplateGoodKey>
    {
        private readonly string _templateName;
        private readonly string _goodId;

        public TemplateGoodKey(string templateName, string goodId)
        {
            _templateName = templateName;
            _goodId = goodId;
        }

        public bool Equals(TemplateGoodKey other)
        {
            return string.Equals(_templateName, other._templateName, StringComparison.Ordinal) &&
                string.Equals(_goodId, other._goodId, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is TemplateGoodKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((_templateName != null ? StringComparer.Ordinal.GetHashCode(_templateName) : 0) * 397) ^
                    (_goodId != null ? StringComparer.Ordinal.GetHashCode(_goodId) : 0);
            }
        }
    }

    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

        public bool Equals(T? x, T? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(T obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
