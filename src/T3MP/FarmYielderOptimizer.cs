using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.BuildingsNavigation;
using Timberborn.Carrying;
using Timberborn.Cutting;
using Timberborn.Fields;
using Timberborn.Goods;
using Timberborn.Growing;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Timberborn.ReservableSystem;
using Timberborn.TemplateSystem;
using Timberborn.WorkSystem;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class FarmYielderOptimizer
{
    private static readonly FieldInfo? WorkerField =
        typeof(HarvestStarter).GetField("_worker", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? GoodCarrierField =
        typeof(HarvestStarter).GetField("_goodCarrier", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? YielderFinderField =
        typeof(HarvestStarter).GetField("_yielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ClosestYielderFinderField =
        typeof(YielderFinder).GetField("_closestYielderFinder", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? CarryAmountCalculatorField =
        typeof(ClosestYielderFinder).GetField("_carryAmountCalculator", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object IndexLock = new object();
    private static readonly object SlotEventLock = new object();
    private static readonly Dictionary<InRangeYielders, FarmIndex> Indexes = new Dictionary<InRangeYielders, FarmIndex>();
    private static readonly Dictionary<Reservable, List<SlotRegistration>> SlotRegistrationsByReservable =
        new Dictionary<Reservable, List<SlotRegistration>>(ReferenceEqualityComparer<Reservable>.Instance);

    [ThreadStatic]
    private static List<SlotRegistration>? _slotRegistrationEventBuffer;

    [ThreadStatic]
    private static bool _slotRegistrationEventBufferInUse;

    private static int _warningLogged;
    private static long _eventSlotUpdates;
    private static long _reservableEventUpdates;
    private static long _yieldEventUpdates;
    private static long _growthEventUpdates;
    private static long _cutEventUpdates;
    private static long _yielderAddedEventUpdates;
    private static long _yieldersChangedIgnored;
    private static long _yieldersChangedDirty;
    private static long _rangeDirtyEvents;
    private static long _slotSubscriptions;
    private static long _slotUnsubscriptions;
    private static long _safetyRefreshes;
    private static long _safetySlotsChecked;
    private static long _safetyYielderAdds;
    private static long _safetyYielderRemoves;
    private static long _safetyYielderReplaces;
    private static long _safetyTemplateChanges;
    private static long _safetyStaleAliveBits;
    private static long _safetyStaleReadyBits;
    private static long _safetyAliveBitCorrections;
    private static long _safetyReadyBitCorrections;

    public static bool TryFindYielder(
        HarvestStarter harvestStarter,
        Inventory receivingInventory,
        InRangeYielders inRangeYielders,
        string prioritizedName,
        out YielderSearchResult result,
        out FarmOptimizerStats stats)
    {
        result = default;
        stats = default;

        try
        {
            if (!TryGetDependencies(harvestStarter, out var accessible, out var liftingCapacity, out var carryAmountCalculator))
            {
                stats = FarmOptimizerStats.CreateFallback();
                return false;
            }

            var index = GetIndex(inRangeYielders, accessible);
            result = index.Find(receivingInventory, liftingCapacity, carryAmountCalculator, prioritizedName, out stats);
            return true;
        }
        catch (Exception exception)
        {
            if (System.Threading.Interlocked.Exchange(ref _warningLogged, 1) == 0)
            {
                Debug.LogWarning($"[T3MP] FarmYielderOptimizer failed once; falling back to vanilla. {exception.GetType().Name}: {exception.Message}");
            }

            stats = FarmOptimizerStats.CreateFallback();
            return false;
        }
    }

    private static bool TryGetDependencies(
        HarvestStarter harvestStarter,
        out Accessible accessible,
        out int liftingCapacity,
        out CarryAmountCalculator carryAmountCalculator)
    {
        accessible = default!;
        liftingCapacity = 0;
        carryAmountCalculator = default!;

        if (WorkerField?.GetValue(harvestStarter) is not Worker worker ||
            GoodCarrierField?.GetValue(harvestStarter) is not GoodCarrier goodCarrier ||
            YielderFinderField?.GetValue(harvestStarter) is not YielderFinder yielderFinder)
        {
            return false;
        }

        if (!worker.Workplace)
        {
            return false;
        }

        accessible = worker.Workplace.GetEnabledComponent<Accessible>();
        if (accessible == null)
        {
            return false;
        }

        if (ClosestYielderFinderField?.GetValue(yielderFinder) is not ClosestYielderFinder closestYielderFinder ||
            CarryAmountCalculatorField?.GetValue(closestYielderFinder) is not CarryAmountCalculator calculator)
        {
            return false;
        }

        liftingCapacity = goodCarrier.LiftingCapacity;
        carryAmountCalculator = calculator;
        return true;
    }

    // See GatherWorkplaceOptimizer.OnNavMeshUpdate: the per-slot terrain-path
    // reachability is cached and otherwise only refreshed on spatial/range
    // changes, so a road/path change would leave a newly-reachable (or newly
    // unreachable) crop stale. Force a distance rebuild on the next search after
    // any regular-navmesh update. Called from the NavMeshUpdateNotifier hook.
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

    private static FarmIndex GetIndex(InRangeYielders inRangeYielders, Accessible accessible)
    {
        lock (IndexLock)
        {
            if (!Indexes.TryGetValue(inRangeYielders, out var index))
            {
                index = new FarmIndex(inRangeYielders, accessible);
                Indexes.Add(inRangeYielders, index);
            }

            return index;
        }
    }

    public static void OnReservableChanged(Reservable reservable)
    {
        if (!BenchmarkSettings.EnableFarmEventDrivenUpdates)
        {
            return;
        }

        var registrations = GetSlotRegistrationEventBuffer(out var pooled);
        try
        {
            lock (SlotEventLock)
            {
                if (!SlotRegistrationsByReservable.TryGetValue(reservable, out var list) || list.Count == 0)
                {
                    return;
                }

                registrations.AddRange(list);
            }

            for (var index = 0; index < registrations.Count; index++)
            {
                registrations[index].Index.UpdateSlotFromEvent(
                    registrations[index].SlotIndex,
                    registrations[index].Generation,
                    SlotEventKind.Reservation);
            }
        }
        finally
        {
            registrations.Clear();
            if (pooled)
            {
                _slotRegistrationEventBufferInUse = false;
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var eventSlotUpdates = System.Threading.Interlocked.Exchange(ref _eventSlotUpdates, 0);
        var reservableEventUpdates = System.Threading.Interlocked.Exchange(ref _reservableEventUpdates, 0);
        var yieldEventUpdates = System.Threading.Interlocked.Exchange(ref _yieldEventUpdates, 0);
        var growthEventUpdates = System.Threading.Interlocked.Exchange(ref _growthEventUpdates, 0);
        var cutEventUpdates = System.Threading.Interlocked.Exchange(ref _cutEventUpdates, 0);
        var yielderAddedEventUpdates = System.Threading.Interlocked.Exchange(ref _yielderAddedEventUpdates, 0);
        var yieldersChangedIgnored = System.Threading.Interlocked.Exchange(ref _yieldersChangedIgnored, 0);
        var yieldersChangedDirty = System.Threading.Interlocked.Exchange(ref _yieldersChangedDirty, 0);
        var rangeDirtyEvents = System.Threading.Interlocked.Exchange(ref _rangeDirtyEvents, 0);
        var slotSubscriptions = System.Threading.Interlocked.Exchange(ref _slotSubscriptions, 0);
        var slotUnsubscriptions = System.Threading.Interlocked.Exchange(ref _slotUnsubscriptions, 0);
        var safetyRefreshes = System.Threading.Interlocked.Exchange(ref _safetyRefreshes, 0);
        var safetySlotsChecked = System.Threading.Interlocked.Exchange(ref _safetySlotsChecked, 0);
        var safetyYielderAdds = System.Threading.Interlocked.Exchange(ref _safetyYielderAdds, 0);
        var safetyYielderRemoves = System.Threading.Interlocked.Exchange(ref _safetyYielderRemoves, 0);
        var safetyYielderReplaces = System.Threading.Interlocked.Exchange(ref _safetyYielderReplaces, 0);
        var safetyTemplateChanges = System.Threading.Interlocked.Exchange(ref _safetyTemplateChanges, 0);
        var safetyStaleAliveBits = System.Threading.Interlocked.Exchange(ref _safetyStaleAliveBits, 0);
        var safetyStaleReadyBits = System.Threading.Interlocked.Exchange(ref _safetyStaleReadyBits, 0);
        var safetyAliveBitCorrections = System.Threading.Interlocked.Exchange(ref _safetyAliveBitCorrections, 0);
        var safetyReadyBitCorrections = System.Threading.Interlocked.Exchange(ref _safetyReadyBitCorrections, 0);
        if (eventSlotUpdates == 0 &&
            slotSubscriptions == 0 &&
            slotUnsubscriptions == 0 &&
            yieldersChangedIgnored == 0 &&
            yieldersChangedDirty == 0 &&
            rangeDirtyEvents == 0 &&
            safetyRefreshes == 0)
        {
            return;
        }

        int trackedReservables;
        lock (SlotEventLock)
        {
            trackedReservables = SlotRegistrationsByReservable.Count;
        }

        Debug.Log(
            $"[T3MP] FarmEventUpdates aggregate={aggregateId}, enabled={BenchmarkSettings.EnableFarmEventDrivenUpdates}, safetyRefreshFrames={BenchmarkSettings.FarmEventDrivenSafetyRefreshFrames}, slotUpdates={eventSlotUpdates}, reservationUpdates={reservableEventUpdates}, yieldUpdates={yieldEventUpdates}, growthUpdates={growthEventUpdates}, cutUpdates={cutEventUpdates}, yielderAddedUpdates={yielderAddedEventUpdates}, yieldersChangedIgnored={yieldersChangedIgnored}, yieldersChangedDirty={yieldersChangedDirty}, rangeDirtyEvents={rangeDirtyEvents}, slotSubscriptions={slotSubscriptions}, slotUnsubscriptions={slotUnsubscriptions}, trackedReservables={trackedReservables}, safetyRefreshes={safetyRefreshes}, safetySlotsChecked={safetySlotsChecked}, safetyYielderAdds={safetyYielderAdds}, safetyYielderRemoves={safetyYielderRemoves}, safetyYielderReplaces={safetyYielderReplaces}, safetyTemplateChanges={safetyTemplateChanges}, safetyStaleAliveBits={safetyStaleAliveBits}, safetyStaleReadyBits={safetyStaleReadyBits}, safetyAliveBitCorrections={safetyAliveBitCorrections}, safetyReadyBitCorrections={safetyReadyBitCorrections}");
    }

    private static void RegisterReservable(Reservable reservable, FarmIndex index, int slotIndex, int generation)
    {
        if (!BenchmarkSettings.EnableFarmEventDrivenUpdates)
        {
            return;
        }

        lock (SlotEventLock)
        {
            if (!SlotRegistrationsByReservable.TryGetValue(reservable, out var list))
            {
                list = new List<SlotRegistration>(1);
                SlotRegistrationsByReservable.Add(reservable, list);
            }

            list.Add(new SlotRegistration(index, slotIndex, generation));
        }
    }

    private static void UnregisterReservable(Reservable reservable, FarmIndex index, int slotIndex, int generation)
    {
        if (!BenchmarkSettings.EnableFarmEventDrivenUpdates)
        {
            return;
        }

        lock (SlotEventLock)
        {
            if (!SlotRegistrationsByReservable.TryGetValue(reservable, out var list))
            {
                return;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var registration = list[i];
                if (ReferenceEquals(registration.Index, index) &&
                    registration.SlotIndex == slotIndex &&
                    registration.Generation == generation)
                {
                    list.RemoveAt(i);
                }
            }

            if (list.Count == 0)
            {
                SlotRegistrationsByReservable.Remove(reservable);
            }
        }
    }

    internal readonly struct FarmOptimizerStats
    {
        public FarmOptimizerStats(
            bool handled,
            bool fallback,
            int indexBuilds,
            int indexCells,
            int pathCalls,
            int dynamicRefreshes,
            long indexBuildStopwatchTicks,
            long dynamicRefreshStopwatchTicks,
            int readyQueries,
            int liveRejects,
            int bitClears)
        {
            Handled = handled;
            Fallback = fallback;
            IndexBuilds = indexBuilds;
            IndexCells = indexCells;
            PathCalls = pathCalls;
            DynamicRefreshes = dynamicRefreshes;
            IndexBuildStopwatchTicks = indexBuildStopwatchTicks;
            DynamicRefreshStopwatchTicks = dynamicRefreshStopwatchTicks;
            ReadyQueries = readyQueries;
            LiveRejects = liveRejects;
            BitClears = bitClears;
        }

        public bool Handled { get; }
        public bool Fallback { get; }
        public int IndexBuilds { get; }
        public int IndexCells { get; }
        public int PathCalls { get; }
        public int DynamicRefreshes { get; }
        public long IndexBuildStopwatchTicks { get; }
        public long DynamicRefreshStopwatchTicks { get; }
        public int ReadyQueries { get; }
        public int LiveRejects { get; }
        public int BitClears { get; }

        public static FarmOptimizerStats CreateFallback()
        {
            return new FarmOptimizerStats(false, true, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private sealed class FarmIndex
    {
        private const int LegacyDynamicRefreshIntervalFrames = 60;

        private readonly InRangeYielders _inRangeYielders;
        private readonly Accessible _accessible;
        private readonly BuildingTerrainRange _buildingTerrainRange;
        private readonly List<CellSlot> _slots = new List<CellSlot>();
        private readonly Dictionary<Vector3Int, int> _slotIndexByCoordinates = new Dictionary<Vector3Int, int>();
        private readonly Dictionary<string, TreeSet> _treesByTemplate = new Dictionary<string, TreeSet>(StringComparer.Ordinal);
        private readonly List<Yielder> _yielderBuffer = new List<Yielder>(512);
        private readonly Dictionary<Vector3Int, Yielder> _yielderByCoordinatesBuffer = new Dictionary<Vector3Int, Yielder>();
        private TreeSet _allTrees = TreeSet.Empty;
        private bool _distanceDirty = true;
        private bool _stateDirty = true;
        private bool _eventsSubscribed;
        private int _lastDynamicRefreshFrame = -LegacyDynamicRefreshIntervalFrames;
        private int _generation;

        public FarmIndex(InRangeYielders inRangeYielders, Accessible accessible)
        {
            _inRangeYielders = inRangeYielders;
            _accessible = accessible;
            _buildingTerrainRange = inRangeYielders.GetComponent<BuildingTerrainRange>();
            SubscribeEvents();
        }

        // Forces the next search to rebuild the distance index (re-running
        // FindTerrainPath), because reachability depends on the navmesh.
        public void MarkDistanceDirty()
        {
            _distanceDirty = true;
        }

        public YielderSearchResult Find(
            Inventory receivingInventory,
            int liftingCapacity,
            CarryAmountCalculator carryAmountCalculator,
            string prioritizedName,
            out FarmOptimizerStats stats)
        {
            var indexBuilds = 0;
            var pathCalls = 0;
            var dynamicRefreshes = 0;
            var indexBuildStopwatchTicks = 0L;
            var dynamicRefreshStopwatchTicks = 0L;
            var readyQueries = 0;
            var liveRejects = 0;
            var bitClears = 0;

            EnsureBuilt(ref indexBuilds, ref pathCalls, ref dynamicRefreshes, ref indexBuildStopwatchTicks, ref dynamicRefreshStopwatchTicks);
            if (Time.frameCount - _lastDynamicRefreshFrame >= DynamicRefreshIntervalFrames)
            {
                var refreshStart = Stopwatch.GetTimestamp();
                RefreshDynamicState(auditSafetyRefresh: BenchmarkSettings.EnableFarmSafetyRefreshAudit);
                dynamicRefreshStopwatchTicks += Stopwatch.GetTimestamp() - refreshStart;
                dynamicRefreshes++;
            }

            var found = false;
            var bestSlotIndex = -1;
            var bestYield = default(GoodAmount);
            var bestDistance = float.MaxValue;
            var hasAliveCandidate = false;

            if (!string.IsNullOrWhiteSpace(prioritizedName))
            {
                if (_treesByTemplate.TryGetValue(prioritizedName, out var prioritizedTreeSet))
                {
                    hasAliveCandidate = prioritizedTreeSet.Alive.Any;
                    found = TryFindNearestInTree(
                        prioritizedTreeSet,
                        prioritizedName,
                        receivingInventory,
                        liftingCapacity,
                        carryAmountCalculator,
                        ref readyQueries,
                        ref liveRejects,
                        ref bitClears,
                        out bestSlotIndex,
                        out bestYield);
                }
            }
            else
            {
                hasAliveCandidate = _allTrees.Alive.Any;
                foreach (var treeSet in _treesByTemplate.Values)
                {
                    if (!TryFindNearestInTree(
                        treeSet,
                        prioritizedName,
                        receivingInventory,
                        liftingCapacity,
                        carryAmountCalculator,
                        ref readyQueries,
                        ref liveRejects,
                        ref bitClears,
                        out var slotIndex,
                        out var yield))
                    {
                        continue;
                    }

                    var distance = _slots[slotIndex].Distance;
                    if (distance >= bestDistance)
                    {
                        continue;
                    }

                    found = true;
                    bestSlotIndex = slotIndex;
                    bestYield = yield;
                    bestDistance = distance;
                }
            }

            stats = new FarmOptimizerStats(
                true,
                false,
                indexBuilds,
                _slots.Count,
                pathCalls,
                dynamicRefreshes,
                indexBuildStopwatchTicks,
                dynamicRefreshStopwatchTicks,
                readyQueries,
                liveRejects,
                bitClears);
            if (found)
            {
                return YielderSearchResult.CreateSearchResult(_slots[bestSlotIndex].Yielder!, bestYield);
            }

            return hasAliveCandidate ? YielderSearchResult.CreateEmpty() : YielderSearchResult.CreateNoYielderInRange();
        }

        private void EnsureBuilt(
            ref int indexBuilds,
            ref int pathCalls,
            ref int dynamicRefreshes,
            ref long indexBuildStopwatchTicks,
            ref long dynamicRefreshStopwatchTicks)
        {
            if (_distanceDirty)
            {
                var buildStart = Stopwatch.GetTimestamp();
                BuildDistanceIndex(ref pathCalls);
                indexBuildStopwatchTicks += Stopwatch.GetTimestamp() - buildStart;
                indexBuilds++;
                _distanceDirty = false;
                _stateDirty = true;
            }

            if (_stateDirty)
            {
                var refreshStart = Stopwatch.GetTimestamp();
                RefreshDynamicState(auditSafetyRefresh: false);
                dynamicRefreshStopwatchTicks += Stopwatch.GetTimestamp() - refreshStart;
                dynamicRefreshes++;
            }
        }

        public void UpdateSlotFromEvent(int slotIndex, int generation, SlotEventKind eventKind)
        {
            if (!BenchmarkSettings.EnableFarmEventDrivenUpdates ||
                _distanceDirty ||
                generation != _generation ||
                slotIndex < 0 ||
                slotIndex >= _slots.Count)
            {
                return;
            }

            UpdateSlotBits(slotIndex);
            System.Threading.Interlocked.Increment(ref _eventSlotUpdates);
            switch (eventKind)
            {
                case SlotEventKind.Reservation:
                    System.Threading.Interlocked.Increment(ref _reservableEventUpdates);
                    break;
                case SlotEventKind.Yield:
                    System.Threading.Interlocked.Increment(ref _yieldEventUpdates);
                    break;
                case SlotEventKind.Growth:
                    System.Threading.Interlocked.Increment(ref _growthEventUpdates);
                    break;
                case SlotEventKind.Cut:
                    System.Threading.Interlocked.Increment(ref _cutEventUpdates);
                    break;
                case SlotEventKind.YielderAdded:
                    System.Threading.Interlocked.Increment(ref _yielderAddedEventUpdates);
                    break;
            }
        }

        private void BuildDistanceIndex(ref int pathCalls)
        {
            UnsubscribeAllSlots();
            _slots.Clear();
            _slotIndexByCoordinates.Clear();
            ClearTrees();
            _generation++;

            if (_buildingTerrainRange == null)
            {
                return;
            }

            foreach (var coordinates in _buildingTerrainRange.GetRange())
            {
                pathCalls++;
                var position = NavigationCoordinateSystem.GridToWorld(coordinates);
                if (_accessible.FindTerrainPath(position, out var distance))
                {
                    _slots.Add(new CellSlot(coordinates, distance));
                }
            }

            _slots.Sort(CompareSlots);
            for (var index = 0; index < _slots.Count; index++)
            {
                _slotIndexByCoordinates[_slots[index].Coordinates] = index;
            }

            _allTrees = new TreeSet(_slots.Count);
        }

        private void RefreshDynamicState(bool auditSafetyRefresh)
        {
            var before = auditSafetyRefresh ? CaptureSafetySnapshot() : null;

            if (BenchmarkSettings.EnableFarmEventDrivenUpdates)
            {
                RefreshDynamicStateDifferential();
            }
            else
            {
                RebuildDynamicState();
            }

            _stateDirty = false;
            _lastDynamicRefreshFrame = Time.frameCount;

            if (before is not null)
            {
                RecordSafetyRefreshDiff(before);
            }
        }

        private void RebuildDynamicState()
        {
            ClearTrees();
            _allTrees = new TreeSet(_slots.Count);
            UnsubscribeAllSlots();

            _yielderBuffer.Clear();
            _inRangeYielders.GetYielders(_yielderBuffer, null);
            for (var index = 0; index < _yielderBuffer.Count; index++)
            {
                AssignYielder(_yielderBuffer[index]);
            }

            _yielderBuffer.Clear();
        }

        private void RefreshDynamicStateDifferential()
        {
            _yielderBuffer.Clear();
            _yielderByCoordinatesBuffer.Clear();
            _inRangeYielders.GetYielders(_yielderBuffer, null);
            for (var index = 0; index < _yielderBuffer.Count; index++)
            {
                var yielder = _yielderBuffer[index];
                if (yielder != null && _slotIndexByCoordinates.ContainsKey(yielder.Coordinates))
                {
                    _yielderByCoordinatesBuffer[yielder.Coordinates] = yielder;
                }
            }

            for (var slotIndex = 0; slotIndex < _slots.Count; slotIndex++)
            {
                var slot = _slots[slotIndex];
                if (!_yielderByCoordinatesBuffer.TryGetValue(slot.Coordinates, out var yielder))
                {
                    if (slot.Yielder != null || slot.Subscribed)
                    {
                        ClearSlotState(slotIndex);
                    }

                    continue;
                }

                if (!ReferenceEquals(slot.Yielder, yielder))
                {
                    AssignYielder(yielder);
                    continue;
                }

                var templateName = GetTemplateName(yielder);
                if (!string.Equals(slot.TemplateName, templateName, StringComparison.Ordinal))
                {
                    ClearSlotBits(slotIndex);
                    slot.SetYielder(yielder, templateName, _generation);
                }

                SubscribeSlot(slot, slotIndex);
                ReconcileSlotBits(slotIndex);
            }

            _yielderByCoordinatesBuffer.Clear();
            _yielderBuffer.Clear();
        }

        private SafetySlotSnapshot[] CaptureSafetySnapshot()
        {
            var snapshots = new SafetySlotSnapshot[_slots.Count];
            for (var index = 0; index < _slots.Count; index++)
            {
                var slot = _slots[index];
                snapshots[index] = new SafetySlotSnapshot(
                    slot.Yielder,
                    slot.TemplateName,
                    _allTrees.Alive.IsSet(index),
                    _allTrees.Ready.IsSet(index),
                    SafeIsLiveAlive(slot),
                    SafeIsLiveReady(slot));
            }

            return snapshots;
        }

        private void RecordSafetyRefreshDiff(SafetySlotSnapshot[] before)
        {
            var checkedSlots = Math.Min(before.Length, _slots.Count);
            var yielderAdds = 0;
            var yielderRemoves = 0;
            var yielderReplaces = 0;
            var templateChanges = 0;
            var staleAliveBits = 0;
            var staleReadyBits = 0;
            var aliveBitCorrections = 0;
            var readyBitCorrections = 0;

            for (var index = 0; index < checkedSlots; index++)
            {
                var old = before[index];
                var slot = _slots[index];
                var newAliveBit = _allTrees.Alive.IsSet(index);
                var newReadyBit = _allTrees.Ready.IsSet(index);

                if (old.AliveBit != old.LiveAlive)
                {
                    staleAliveBits++;
                }

                if (old.ReadyBit != old.LiveReady)
                {
                    staleReadyBits++;
                }

                if (old.AliveBit != newAliveBit)
                {
                    aliveBitCorrections++;
                }

                if (old.ReadyBit != newReadyBit)
                {
                    readyBitCorrections++;
                }

                if (old.Yielder == null && slot.Yielder != null)
                {
                    yielderAdds++;
                    continue;
                }

                if (old.Yielder != null && slot.Yielder == null)
                {
                    yielderRemoves++;
                    continue;
                }

                if (old.Yielder != null &&
                    slot.Yielder != null &&
                    !ReferenceEquals(old.Yielder, slot.Yielder))
                {
                    yielderReplaces++;
                    continue;
                }

                if (!string.Equals(old.TemplateName, slot.TemplateName, StringComparison.Ordinal))
                {
                    templateChanges++;
                }
            }

            System.Threading.Interlocked.Increment(ref _safetyRefreshes);
            System.Threading.Interlocked.Add(ref _safetySlotsChecked, checkedSlots);
            System.Threading.Interlocked.Add(ref _safetyYielderAdds, yielderAdds);
            System.Threading.Interlocked.Add(ref _safetyYielderRemoves, yielderRemoves);
            System.Threading.Interlocked.Add(ref _safetyYielderReplaces, yielderReplaces);
            System.Threading.Interlocked.Add(ref _safetyTemplateChanges, templateChanges);
            System.Threading.Interlocked.Add(ref _safetyStaleAliveBits, staleAliveBits);
            System.Threading.Interlocked.Add(ref _safetyStaleReadyBits, staleReadyBits);
            System.Threading.Interlocked.Add(ref _safetyAliveBitCorrections, aliveBitCorrections);
            System.Threading.Interlocked.Add(ref _safetyReadyBitCorrections, readyBitCorrections);
        }

        private void AssignYielder(Yielder yielder)
        {
            if (yielder == null || !_slotIndexByCoordinates.TryGetValue(yielder.Coordinates, out var slotIndex))
            {
                return;
            }

            var slot = _slots[slotIndex];
            var templateName = GetTemplateName(yielder);
            var yielderChanged = !ReferenceEquals(slot.Yielder, yielder);
            var templateChanged = !string.Equals(slot.TemplateName, templateName, StringComparison.Ordinal);
            if (yielderChanged || templateChanged)
            {
                ClearSlotBits(slotIndex);
            }

            if (!ReferenceEquals(slot.Yielder, yielder))
            {
                UnsubscribeSlot(slot, slotIndex);
            }

            slot.SetYielder(yielder, templateName, _generation);
            SubscribeSlot(slot, slotIndex);
            UpdateSlotBits(slotIndex);
        }

        private void UpdateSlotBits(int slotIndex)
        {
            var slot = _slots[slotIndex];
            var alive = IsLiveAlive(slot);
            var ready = alive && IsLiveReady(slot);
            SetAlive(slotIndex, alive);
            SetReady(slotIndex, ready);
        }

        private void ReconcileSlotBits(int slotIndex)
        {
            var slot = _slots[slotIndex];
            var alive = SafeIsLiveAlive(slot);
            var ready = alive && SafeIsLiveReady(slot);

            if (_allTrees.Alive.IsSet(slotIndex) != alive)
            {
                SetAlive(slotIndex, alive);
            }

            if (_allTrees.Ready.IsSet(slotIndex) != ready)
            {
                SetReady(slotIndex, ready);
            }
        }

        private void ClearSlotState(int slotIndex)
        {
            var slot = _slots[slotIndex];
            ClearSlotBits(slotIndex);
            UnsubscribeSlot(slot, slotIndex);
        }

        private void ClearSlotBits(int slotIndex)
        {
            SetReady(slotIndex, enabled: false);
            SetAlive(slotIndex, enabled: false);
        }

        private void SetAlive(int slotIndex, bool enabled)
        {
            _allTrees.Alive.Set(slotIndex, enabled);
            var templateName = _slots[slotIndex].TemplateName;
            if (!string.IsNullOrEmpty(templateName))
            {
                var treeSet = enabled
                    ? GetOrCreateTemplateTrees(templateName)
                    : (_treesByTemplate.TryGetValue(templateName, out var existingTreeSet) ? existingTreeSet : null);
                treeSet?.Alive.Set(slotIndex, enabled);
            }
        }

        private void SetReady(int slotIndex, bool enabled)
        {
            _allTrees.Ready.Set(slotIndex, enabled);
            var templateName = _slots[slotIndex].TemplateName;
            if (!string.IsNullOrEmpty(templateName))
            {
                var treeSet = enabled
                    ? GetOrCreateTemplateTrees(templateName)
                    : (_treesByTemplate.TryGetValue(templateName, out var existingTreeSet) ? existingTreeSet : null);
                treeSet?.Ready.Set(slotIndex, enabled);
            }
        }

        private TreeSet GetOrCreateTemplateTrees(string templateName)
        {
            if (!_treesByTemplate.TryGetValue(templateName, out var treeSet))
            {
                treeSet = new TreeSet(_slots.Count);
                _treesByTemplate.Add(templateName, treeSet);
            }

            return treeSet;
        }

        private void ClearTrees()
        {
            _allTrees = TreeSet.Empty;
            _treesByTemplate.Clear();
        }

        private static int CompareSlots(CellSlot left, CellSlot right)
        {
            var distanceComparison = left.Distance.CompareTo(right.Distance);
            if (distanceComparison != 0)
            {
                return distanceComparison;
            }

            var xComparison = left.Coordinates.x.CompareTo(right.Coordinates.x);
            if (xComparison != 0)
            {
                return xComparison;
            }

            var yComparison = left.Coordinates.y.CompareTo(right.Coordinates.y);
            if (yComparison != 0)
            {
                return yComparison;
            }

            return left.Coordinates.z.CompareTo(right.Coordinates.z);
        }

        private bool TryFindNearestInTree(
            TreeSet treeSet,
            string prioritizedName,
            Inventory receivingInventory,
            int liftingCapacity,
            CarryAmountCalculator carryAmountCalculator,
            ref int readyQueries,
            ref int liveRejects,
            ref int bitClears,
            out int slotIndex,
            out GoodAmount yield)
        {
            slotIndex = -1;
            yield = default;

            var searchIndex = 0;
            while (treeSet.Ready.FindFirst(searchIndex) is var candidateSlotIndex && candidateSlotIndex >= 0)
            {
                readyQueries++;
                var slot = _slots[candidateSlotIndex];
                var validationResult = ValidateReady(
                    slot,
                    prioritizedName,
                    receivingInventory,
                    liftingCapacity,
                    carryAmountCalculator,
                    out yield);

                if (validationResult == ReadyValidation.Valid)
                {
                    slotIndex = candidateSlotIndex;
                    return true;
                }

                liveRejects++;
                if (validationResult == ReadyValidation.NotCarryable)
                {
                    return false;
                }

                SetReady(candidateSlotIndex, enabled: false);
                treeSet.Ready.Set(candidateSlotIndex, enabled: false);
                bitClears++;
                searchIndex = candidateSlotIndex;
            }

            return false;
        }

        private ReadyValidation ValidateReady(
            CellSlot slot,
            string prioritizedName,
            Inventory receivingInventory,
            int liftingCapacity,
            CarryAmountCalculator carryAmountCalculator,
            out GoodAmount yield)
        {
            yield = default;
            var yielder = slot.Yielder;
            if (yielder == null || !TemplateMatches(slot, prioritizedName) || !IsLiveReady(slot))
            {
                return ReadyValidation.Stale;
            }

            yield = CarryAmountCalculatorOptimizer.AmountToCarry(carryAmountCalculator, liftingCapacity, yielder.Yield, receivingInventory);
            return yield.Amount > 0 ? ReadyValidation.Valid : ReadyValidation.NotCarryable;
        }

        private static bool IsLiveAlive(CellSlot slot)
        {
            var yielder = slot.Yielder;
            return yielder != null && !yielder.Reservable.Reserved && yielder.IsAlive();
        }

        private static bool IsLiveReady(CellSlot slot)
        {
            var yielder = slot.Yielder;
            return yielder != null && !yielder.Reservable.Reserved && yielder.IsYielding;
        }

        private static bool SafeIsLiveAlive(CellSlot slot)
        {
            try
            {
                return IsLiveAlive(slot);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeIsLiveReady(CellSlot slot)
        {
            try
            {
                return IsLiveReady(slot);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TemplateMatches(CellSlot slot, string prioritizedName)
        {
            if (string.IsNullOrWhiteSpace(prioritizedName))
            {
                return true;
            }

            var yielder = slot.Yielder;
            return yielder != null && yielder.GetComponent<TemplateSpec>().IsNamed(prioritizedName);
        }

        private static string GetTemplateName(Yielder yielder)
        {
            var templateSpec = yielder.GetComponent<TemplateSpec>();
            return templateSpec?.TemplateName ?? string.Empty;
        }

        private void SubscribeEvents()
        {
            if (_eventsSubscribed)
            {
                return;
            }

            _inRangeYielders.YieldersChanged += OnYieldersChanged;
            _inRangeYielders.YielderAdded += OnYielderAdded;
            if (_buildingTerrainRange != null)
            {
                _buildingTerrainRange.RangeChanged += OnRangeChanged;
            }

            _eventsSubscribed = true;
        }

        private void OnRangeChanged(object sender, RangeChangedEventArgs e)
        {
            _distanceDirty = true;
            System.Threading.Interlocked.Increment(ref _rangeDirtyEvents);
        }

        private void OnYieldersChanged(object sender, EventArgs e)
        {
            if (BenchmarkSettings.EnableFarmEventDrivenUpdates)
            {
                System.Threading.Interlocked.Increment(ref _yieldersChangedIgnored);
                return;
            }

            _stateDirty = true;
            System.Threading.Interlocked.Increment(ref _yieldersChangedDirty);
        }

        private void OnYielderAdded(object sender, Yielder yielder)
        {
            if (_distanceDirty || _slots.Count == 0)
            {
                _stateDirty = true;
                return;
            }

            AssignYielder(yielder);
            if (BenchmarkSettings.EnableFarmEventDrivenUpdates &&
                _slotIndexByCoordinates.TryGetValue(yielder.Coordinates, out var slotIndex))
            {
                UpdateSlotFromEvent(slotIndex, _generation, SlotEventKind.YielderAdded);
            }
        }

        private int DynamicRefreshIntervalFrames => BenchmarkSettings.EnableFarmEventDrivenUpdates
            ? BenchmarkSettings.FarmEventDrivenSafetyRefreshFrames
            : LegacyDynamicRefreshIntervalFrames;

        private void SubscribeSlot(CellSlot slot, int slotIndex)
        {
            if (!BenchmarkSettings.EnableFarmEventDrivenUpdates ||
                slot.Subscribed ||
                slot.Yielder is not { } yielder)
            {
                return;
            }

            var generation = slot.Generation;
            EventHandler yieldHandler = (_, _) => UpdateSlotFromEvent(slotIndex, generation, SlotEventKind.Yield);
            yielder.YieldAdded += yieldHandler;
            yielder.YieldDecreased += yieldHandler;
            slot.YieldAddedHandler = yieldHandler;
            slot.YieldDecreasedHandler = yieldHandler;

            if (TryGetComponentSafe(yielder, out Growable growable))
            {
                EventHandler growthHandler = (_, _) => UpdateSlotFromEvent(slotIndex, generation, SlotEventKind.Growth);
                growable.HasGrown += growthHandler;
                slot.Growable = growable;
                slot.GrowthHandler = growthHandler;
            }

            if (TryGetComponentSafe(yielder, out Cuttable cuttable))
            {
                EventHandler cutHandler = (_, _) => UpdateSlotFromEvent(slotIndex, generation, SlotEventKind.Cut);
                cuttable.WasCut += cutHandler;
                slot.Cuttable = cuttable;
                slot.CutHandler = cutHandler;
            }

            var reservable = yielder.Reservable;
            slot.Reservable = reservable;
            RegisterReservable(reservable, this, slotIndex, generation);
            slot.Subscribed = true;
            System.Threading.Interlocked.Increment(ref _slotSubscriptions);
        }

        private void UnsubscribeAllSlots()
        {
            for (var index = 0; index < _slots.Count; index++)
            {
                UnsubscribeSlot(_slots[index], index);
            }
        }

        private void UnsubscribeSlot(CellSlot slot, int slotIndex)
        {
            if (slot.Yielder is { } yielder)
            {
                if (slot.YieldAddedHandler is { } yieldAddedHandler)
                {
                    yielder.YieldAdded -= yieldAddedHandler;
                }

                if (slot.YieldDecreasedHandler is { } yieldDecreasedHandler)
                {
                    yielder.YieldDecreased -= yieldDecreasedHandler;
                }
            }

            if (slot.Growable is { } growable && slot.GrowthHandler is { } growthHandler)
            {
                growable.HasGrown -= growthHandler;
            }

            if (slot.Cuttable is { } cuttable && slot.CutHandler is { } cutHandler)
            {
                cuttable.WasCut -= cutHandler;
            }

            if (slot.Reservable is { } reservable)
            {
                UnregisterReservable(reservable, this, slotIndex, slot.Generation);
            }

            if (slot.Subscribed)
            {
                System.Threading.Interlocked.Increment(ref _slotUnsubscriptions);
            }

            slot.ClearYielder();
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
    }

    private sealed class CellSlot
    {
        public CellSlot(Vector3Int coordinates, float distance)
        {
            Coordinates = coordinates;
            Distance = distance;
        }

        public Vector3Int Coordinates { get; }
        public float Distance { get; }
        public Yielder? Yielder { get; private set; }
        public string TemplateName { get; private set; } = string.Empty;
        public int Generation { get; private set; }
        public bool Subscribed { get; set; }
        public Reservable? Reservable { get; set; }
        public Growable? Growable { get; set; }
        public Cuttable? Cuttable { get; set; }
        public EventHandler? YieldAddedHandler { get; set; }
        public EventHandler? YieldDecreasedHandler { get; set; }
        public EventHandler? GrowthHandler { get; set; }
        public EventHandler? CutHandler { get; set; }

        public void SetYielder(Yielder yielder, string templateName, int generation)
        {
            Yielder = yielder;
            TemplateName = templateName;
            Generation = generation;
        }

        public void ClearYielder()
        {
            Yielder = null;
            TemplateName = string.Empty;
            Reservable = null;
            Growable = null;
            Cuttable = null;
            YieldAddedHandler = null;
            YieldDecreasedHandler = null;
            GrowthHandler = null;
            CutHandler = null;
            Subscribed = false;
        }
    }

    private enum SlotEventKind
    {
        Reservation,
        Yield,
        Growth,
        Cut,
        YielderAdded
    }

    private readonly struct SlotRegistration
    {
        public SlotRegistration(FarmIndex index, int slotIndex, int generation)
        {
            Index = index;
            SlotIndex = slotIndex;
            Generation = generation;
        }

        public FarmIndex Index { get; }
        public int SlotIndex { get; }
        public int Generation { get; }
    }

    private static List<SlotRegistration> GetSlotRegistrationEventBuffer(out bool pooled)
    {
        if (_slotRegistrationEventBufferInUse)
        {
            pooled = false;
            return new List<SlotRegistration>(8);
        }

        var buffer = _slotRegistrationEventBuffer;
        if (buffer is null)
        {
            buffer = new List<SlotRegistration>(8);
            _slotRegistrationEventBuffer = buffer;
        }

        buffer.Clear();
        _slotRegistrationEventBufferInUse = true;
        pooled = true;
        return buffer;
    }

    private readonly struct SafetySlotSnapshot
    {
        public SafetySlotSnapshot(
            Yielder? yielder,
            string templateName,
            bool aliveBit,
            bool readyBit,
            bool liveAlive,
            bool liveReady)
        {
            Yielder = yielder;
            TemplateName = templateName;
            AliveBit = aliveBit;
            ReadyBit = readyBit;
            LiveAlive = liveAlive;
            LiveReady = liveReady;
        }

        public Yielder? Yielder { get; }
        public string TemplateName { get; }
        public bool AliveBit { get; }
        public bool ReadyBit { get; }
        public bool LiveAlive { get; }
        public bool LiveReady { get; }
    }

    private enum ReadyValidation
    {
        Valid,
        Stale,
        NotCarryable
    }

    private sealed class TreeSet
    {
        public static readonly TreeSet Empty = new TreeSet(0);

        public TreeSet(int count)
        {
            Ready = new SegmentTree(count);
            Alive = new SegmentTree(count);
        }

        public SegmentTree Ready { get; }
        public SegmentTree Alive { get; }
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

        public bool Any => _tree.Length > 1 && _tree[1] > 0;

        public bool IsSet(int index)
        {
            if (index < 0 || index >= _count)
            {
                return false;
            }

            return _tree[index + _size] > 0;
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
