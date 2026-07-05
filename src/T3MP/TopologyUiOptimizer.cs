using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.MechanicalSystem;
using Timberborn.SelectionSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// UI-only optimizations for the topology hot paths measured by
// TopologyUiProbe on a large colony (baseline: testlogs/autoload-20260705-020708.log):
//
// 1. Mechanical network highlight (46ms/refresh on a 1643-node network,
//    re-fired by every construction-finished event at high speed): replace
//    the unhighlight-all + re-highlight-all refresh with a DIFF against the
//    currently highlighted set. The DFS still runs (6-7ms); the ~39ms of
//    redundant material churn is skipped when the set barely changes.
//
// 2. District path overlay rebuild (33ms per rebuild, re-fired by ANY
//    instant-navmesh change anywhere on the map while a path/building is
//    selected or previewed): rate-limit rebuilds per drawer instance. The
//    first rebuild after a selection is never deferred.
//
// 3. Preview placer churn (every frame while holding a placement tool the
//    previews are removed/re-added to the preview navmesh, invalidating the
//    preview district maps even when the cursor did not move): skip
//    ShowPreviews entirely when the placement list is unchanged and the last
//    full run is recent. Actual placement validation (GetBuildableCoordinates)
//    is untouched, so what gets BUILT is exactly vanilla.
//
// None of these touch the simulation: they only change when UI highlight /
// overlay / preview visuals are recomputed.
internal static class TopologyUiOptimizer
{
    // --- 1. Mechanical highlight diff -----------------------------------

    private static FieldInfo? _rootNodesField;
    private static FieldInfo? _highlighterField;
    private static FieldInfo? _highlightColorField;
    private static FieldInfo? _iteratorField;
    private static MethodInfo? _iterateMethod;
    private static object? _lastHighlightService;
    private static readonly HashSet<MechanicalNode> CurrentlyHighlighted = new HashSet<MechanicalNode>();
    private static readonly HashSet<MechanicalNode> FreshGraphNodes = new HashSet<MechanicalNode>();
    private static int _highlightErrorLogs;

    public static bool EnsureHighlightMembers(Type serviceType)
    {
        if (_rootNodesField is not null)
        {
            return true;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        _rootNodesField = serviceType.GetField("_rootNodes", flags);
        _highlighterField = serviceType.GetField("_highlighter", flags);
        _highlightColorField = serviceType.GetField("_highlightColor", flags);
        _iteratorField = serviceType.GetField("_mechanicalGraphIterator", flags);
        _iterateMethod = _iteratorField?.FieldType.GetMethod("Iterate", flags);
        return _rootNodesField is not null &&
            _highlighterField is not null &&
            _highlightColorField is not null &&
            _iteratorField is not null &&
            _iterateMethod is not null;
    }

    // Replaces MechanicalGraphHighlightService.RefreshHighlight. Returns true
    // when the original must still run (missing members -> fall back).
    public static bool RefreshHighlightDiff(object service)
    {
        if (!EnsureHighlightMembers(service.GetType()))
        {
            return true;
        }

        try
        {
            if (!ReferenceEquals(_lastHighlightService, service))
            {
                // New game session: never trust the cache from a previous one.
                _lastHighlightService = service;
                CurrentlyHighlighted.Clear();
                PendingUnhighlight.Clear();
                PendingHighlight.Clear();
                _finalClearPending = false;
            }

            var rootNodes = (IEnumerable<MechanicalNode>)_rootNodesField!.GetValue(service);
            var highlighter = (Highlighter)_highlighterField!.GetValue(service);
            var anyRoot = false;
            var includeUnfinished = false;
            foreach (var rootNode in rootNodes)
            {
                anyRoot = true;
                var blockObject = rootNode ? rootNode.GetComponent<BlockObject>() : null;
                if (blockObject is not null && (blockObject.IsUnfinished || blockObject.IsPreview))
                {
                    includeUnfinished = true;
                }
            }

            PendingUnhighlight.Clear();
            PendingHighlight.Clear();
            _finalClearPending = false;

            if (!anyRoot)
            {
                if (CurrentlyHighlighted.Count == 0)
                {
                    // Vanilla behavior on empty roots: clear all secondaries.
                    highlighter.UnhighlightAllSecondary();
                    return false;
                }

                // Amortized unpaint of our own set; one cheap global clear at
                // the end catches any foreign secondaries (vanilla parity).
                PendingUnhighlight.AddRange(CurrentlyHighlighted);
                _finalClearPending = true;
                ProcessHighlightQueues(highlighter);
                return false;
            }

            FreshGraphNodes.Clear();
            if (!includeUnfinished && TryCollectFromGraphs(rootNodes))
            {
                // Fast path: for finished roots the network to highlight IS
                // the root's MechanicalGraph (the game maintains connected
                // components on every join/split), so the 7ms block-lookup
                // DFS is unnecessary.
            }
            else
            {
                var iterator = _iteratorField!.GetValue(service);
                _iterateMethod!.Invoke(iterator, new object[] { rootNodes, FreshGraphNodes, includeUnfinished });
            }

            // Queue only actual changes; painting is budgeted per frame so a
            // 1600-node repaint becomes a few-ms sweep over several frames
            // instead of one 20ms+ hitch.
            foreach (var node in CurrentlyHighlighted)
            {
                if (!node || !FreshGraphNodes.Contains(node))
                {
                    PendingUnhighlight.Add(node);
                }
            }

            foreach (var node in FreshGraphNodes)
            {
                if (!CurrentlyHighlighted.Contains(node))
                {
                    PendingHighlight.Add(node);
                }
            }

            _pendingColor = (Color)_highlightColorField!.GetValue(service);
            FreshGraphNodes.Clear();
            ProcessHighlightQueues(highlighter);
            return false;
        }
        catch (Exception exception)
        {
            if (_highlightErrorLogs++ < 3)
            {
                Debug.LogWarning($"[T3MP] Mechanical highlight diff failed, falling back to vanilla: {exception.GetType().Name}: {exception.Message}");
            }

            CurrentlyHighlighted.Clear();
            return true;
        }
    }

    // Keeps the diff cache honest when anything else wipes all secondary
    // highlights behind our back.
    public static void OnUnhighlightAllSecondary()
    {
        CurrentlyHighlighted.Clear();
    }

    public static bool HighlightDrainPending =>
        PendingUnhighlight.Count > 0 || PendingHighlight.Count > 0 || _finalClearPending;

    private static readonly List<MechanicalNode> PendingUnhighlight = new List<MechanicalNode>();
    private static readonly List<MechanicalNode> PendingHighlight = new List<MechanicalNode>();
    private static Color _pendingColor;
    private static bool _finalClearPending;

    // Fast path for finished roots: union of their MechanicalGraph node sets.
    // Returns false (caller falls back to the vanilla DFS) if any root is
    // detached or has no graph yet.
    private static bool TryCollectFromGraphs(IEnumerable<MechanicalNode> rootNodes)
    {
        foreach (var rootNode in rootNodes)
        {
            if (!rootNode || rootNode.IsDetached)
            {
                // Vanilla skips detached roots entirely.
                continue;
            }

            var graph = rootNode.Graph;
            if (graph is null)
            {
                FreshGraphNodes.Clear();
                return false;
            }

            foreach (var node in graph.Nodes)
            {
                FreshGraphNodes.Add(node);
            }
        }

        return true;
    }

    private static void ProcessHighlightQueues(Highlighter highlighter)
    {
        var budget = BenchmarkSettings.TopoHighlightOpsPerFrame;
        while (budget > 0 && PendingUnhighlight.Count > 0)
        {
            var index = PendingUnhighlight.Count - 1;
            var node = PendingUnhighlight[index];
            PendingUnhighlight.RemoveAt(index);
            if (CurrentlyHighlighted.Remove(node))
            {
                highlighter.UnhighlightSecondary(node);
                budget--;
            }
        }

        while (budget > 0 && PendingHighlight.Count > 0)
        {
            var index = PendingHighlight.Count - 1;
            var node = PendingHighlight[index];
            PendingHighlight.RemoveAt(index);
            if (node && CurrentlyHighlighted.Add(node))
            {
                highlighter.HighlightSecondary(node, _pendingColor);
                budget--;
            }
        }

        if (_finalClearPending && PendingUnhighlight.Count == 0)
        {
            _finalClearPending = false;
            highlighter.UnhighlightAllSecondary();
            CurrentlyHighlighted.Clear();
        }
    }

    // --- 1b. Mechanical highlight refresh coalescing ----------------------
    // While dragging a gear preview along a network, the preview alternates
    // between connected and disconnected, so even the diff refresh repaints
    // the whole network on every flip (~25-30ms on 1643 nodes, up to ~14x/s
    // measured in manual play). Coalesce dirty refreshes to one per interval;
    // the deferred dirty is never lost, so the final state is always painted.

    private static FieldInfo? _highlightDirtyField;
    private static float _nextAllowedHighlightRefreshRealtime;
    private static bool _highlightDeferredDirty;

    public static void BeforeHighlightLateUpdate(object service)
    {
        _highlightDirtyField ??= service.GetType().GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_highlightDirtyField is null)
        {
            return;
        }

        _highlightDeferredDirty = false;
        if (!(bool)_highlightDirtyField.GetValue(service))
        {
            return;
        }

        // Drain frames (budgeted painting still pending) bypass the interval:
        // the refresh must keep running until the queues are empty.
        var now = Time.realtimeSinceStartup;
        if (now < _nextAllowedHighlightRefreshRealtime && !HighlightDrainPending)
        {
            _highlightDirtyField.SetValue(service, false);
            _highlightDeferredDirty = true;
        }
        else
        {
            _nextAllowedHighlightRefreshRealtime = now + BenchmarkSettings.TopoHighlightMinRefreshIntervalSeconds;
        }
    }

    public static void AfterHighlightLateUpdate(object service)
    {
        if (_highlightDirtyField is null)
        {
            return;
        }

        if (_highlightDeferredDirty)
        {
            _highlightDeferredDirty = false;
            _highlightDirtyField.SetValue(service, true);
        }
        else if (HighlightDrainPending)
        {
            // Keep the service dirty so the next LateUpdate continues the
            // budgeted paint sweep.
            _highlightDirtyField.SetValue(service, true);
        }
    }

    // --- 2. Path overlay rebuild throttle --------------------------------

    private sealed class DrawerThrottleState
    {
        public float NextAllowedRebuildRealtime;
        public bool DeferredDirty;
    }

    private static readonly ConditionalWeakTable<object, DrawerThrottleState> DrawerStates =
        new ConditionalWeakTable<object, DrawerThrottleState>();
    private static FieldInfo? _drawerDirtyField;

    public static void BeforeDrawerLateUpdate(object drawer)
    {
        _drawerDirtyField ??= drawer.GetType().GetField("_dirty", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_drawerDirtyField is null)
        {
            return;
        }

        var state = DrawerStates.GetOrCreateValue(drawer);
        state.DeferredDirty = false;
        if (!(bool)_drawerDirtyField.GetValue(drawer))
        {
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now < state.NextAllowedRebuildRealtime)
        {
            // Too soon: draw the existing mesh this frame, re-arm dirty after.
            _drawerDirtyField.SetValue(drawer, false);
            state.DeferredDirty = true;
        }
        else
        {
            state.NextAllowedRebuildRealtime = now + BenchmarkSettings.TopoPathOverlayMinRebuildIntervalSeconds;
        }
    }

    public static void AfterDrawerLateUpdate(object drawer)
    {
        if (_drawerDirtyField is null)
        {
            return;
        }

        if (DrawerStates.TryGetValue(drawer, out var state) && state.DeferredDirty)
        {
            state.DeferredDirty = false;
            _drawerDirtyField.SetValue(drawer, true);
        }
    }

    // --- 2b. Path overlay invalidation filter -----------------------------
    // Vanilla PathNavRangeDrawerInvalidator marks EVERY drawer dirty on ANY
    // instant-navmesh change anywhere on the map. Filter: only mark a drawer
    // whose drawn road area contains (or neighbors, incl. above/below - the
    // connection keys look one step out) a changed coordinate. Falls back to
    // vanilla marking on huge updates or missing reflection members.

    private static FieldInfo? _invalidatorDrawersField;
    private static FieldInfo? _drawerRoadNodesField;
    private static MethodInfo? _drawerMarkDirtyMethod;
    private static int _invalidationFilterErrorLogs;

    private static readonly Vector3Int[] NeighborOffsets =
    {
        new Vector3Int(0, 0, 0),
        new Vector3Int(1, 0, 0),
        new Vector3Int(-1, 0, 0),
        new Vector3Int(0, 1, 0),
        new Vector3Int(0, -1, 0),
        new Vector3Int(0, 0, 1),
        new Vector3Int(0, 0, -1)
    };

    // Returns true when the original (mark everything) must still run.
    public static bool FilterOverlayInvalidation(object invalidator, Timberborn.Navigation.NavMeshUpdate navMeshUpdate)
    {
        try
        {
            var changedCoordinates = navMeshUpdate.TerrainCoordinates;
            if (changedCoordinates.Count == 0 || changedCoordinates.Count > 256)
            {
                // No coordinate info (be safe) or a mass update (filtering
                // would cost more than it saves): vanilla behavior.
                return true;
            }

            _invalidatorDrawersField ??= invalidator.GetType().GetField("_districtPathNavRangeDrawers", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_invalidatorDrawersField?.GetValue(invalidator) is not System.Collections.IEnumerable drawers)
            {
                return true;
            }

            foreach (var drawer in drawers)
            {
                _drawerRoadNodesField ??= drawer.GetType().GetField("_roadNodes", BindingFlags.Instance | BindingFlags.NonPublic);
                _drawerMarkDirtyMethod ??= drawer.GetType().GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_drawerRoadNodesField is null || _drawerMarkDirtyMethod is null)
                {
                    return true;
                }

                var roadNodes = _drawerRoadNodesField.GetValue(drawer) as HashSet<Timberborn.Navigation.WeightedCoordinates>;
                if (roadNodes is null || roadNodes.Count == 0)
                {
                    // Nothing drawn yet: marking is free and always safe.
                    _drawerMarkDirtyMethod.Invoke(drawer, null);
                    continue;
                }

                var affected = false;
                for (var i = 0; i < changedCoordinates.Count && !affected; i++)
                {
                    var coordinate = changedCoordinates[i];
                    foreach (var offset in NeighborOffsets)
                    {
                        if (roadNodes.Contains(new Timberborn.Navigation.WeightedCoordinates(coordinate + offset, 0f)))
                        {
                            affected = true;
                            break;
                        }
                    }
                }

                if (affected)
                {
                    _drawerMarkDirtyMethod.Invoke(drawer, null);
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            if (_invalidationFilterErrorLogs++ < 3)
            {
                Debug.LogWarning($"[T3MP] Overlay invalidation filter failed, falling back to vanilla: {exception.GetType().Name}: {exception.Message}");
            }

            return true;
        }
    }

    // --- 2c. Frame-batched block object model updates ----------------------
    // BuildingModelUpdater re-runs BlockObjectModelController.UpdateModel for
    // every block object on every changed navmesh coordinate (and below), so
    // one water/navmesh tick can trigger 100k+ calls, with multi-tile objects
    // updated once PER TILE (measured bursts: 276k calls / 366ms per 5s).
    // Defer all UpdateModel calls into a per-frame set and flush each unique
    // controller ONCE at frame end. Rendering only ever sees the end-of-frame
    // state, so this is visually identical - just without the duplicate work.

    private static readonly HashSet<Timberborn.BlockObjectModelSystem.BlockObjectModelController> DeferredModelUpdates =
        new HashSet<Timberborn.BlockObjectModelSystem.BlockObjectModelController>();
    private static bool _flushingModelUpdates;

    // Prefix on BlockObjectModelController.UpdateModel. Returns true (run
    // vanilla) during the flush; otherwise defers.
    public static bool DeferModelUpdate(object controller)
    {
        if (_flushingModelUpdates)
        {
            return true;
        }

        if (controller is Timberborn.BlockObjectModelSystem.BlockObjectModelController typedController)
        {
            DeferredModelUpdates.Add(typedController);
            return false;
        }

        return true;
    }

    // Called from the mod controller's LateUpdate (after sim and game logic,
    // before rendering).
    public static void FlushDeferredModelUpdates()
    {
        if (DeferredModelUpdates.Count == 0)
        {
            return;
        }

        _flushingModelUpdates = true;
        try
        {
            foreach (var controller in DeferredModelUpdates)
            {
                try
                {
                    if (controller && controller.GameObject)
                    {
                        controller.UpdateModel();
                    }
                }
                catch (Exception)
                {
                    // A single destroyed/broken entity must not stop the flush.
                }
            }
        }
        finally
        {
            _flushingModelUpdates = false;
            DeferredModelUpdates.Clear();
        }
    }

    // --- 3. Preview placer same-placement skip ---------------------------

    private sealed class PreviewPlacerState
    {
        public readonly List<Placement> LastPlacements = new List<Placement>();
        public float LastFullRunRealtime = float.NegativeInfinity;
        public readonly List<Placement> ScratchPlacements = new List<Placement>();
    }

    private static readonly ConditionalWeakTable<object, PreviewPlacerState> PreviewPlacerStates =
        new ConditionalWeakTable<object, PreviewPlacerState>();

    // Returns true when the original ShowPreviews must run.
    public static bool ShouldRunShowPreviews(object previewPlacer, IEnumerable<Placement> placements)
    {
        var state = PreviewPlacerStates.GetOrCreateValue(previewPlacer);
        var scratch = state.ScratchPlacements;
        scratch.Clear();
        foreach (var placement in placements)
        {
            scratch.Add(placement);
        }

        var now = Time.realtimeSinceStartup;
        var unchanged = scratch.Count == state.LastPlacements.Count;
        if (unchanged)
        {
            for (var i = 0; i < scratch.Count; i++)
            {
                if (!scratch[i].Equals(state.LastPlacements[i]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        var sinceFullRun = now - state.LastFullRunRealtime;
        if (unchanged && sinceFullRun < BenchmarkSettings.TopoPreviewRefreshIntervalSeconds)
        {
            return false;
        }

        // Changed placements (dragging): cap the full-run rate too. Do NOT
        // update LastPlacements here, so the pending change keeps requesting
        // a run until the interval opens up.
        if (!unchanged && sinceFullRun < BenchmarkSettings.TopoPreviewDragMinIntervalSeconds)
        {
            return false;
        }

        state.LastPlacements.Clear();
        state.LastPlacements.AddRange(scratch);
        state.LastFullRunRealtime = now;
        return true;
    }

    // HideAllPreviews (tool exit) and GetBuildableCoordinates (actual
    // placement) invalidate the cache, so the next ShowPreviews after either
    // always does a full pass - a just-placed object is re-validated
    // immediately instead of after the refresh interval.
    public static void OnHideAllPreviews(object previewPlacer)
    {
        if (PreviewPlacerStates.TryGetValue(previewPlacer, out var state))
        {
            state.LastPlacements.Clear();
            state.LastFullRunRealtime = float.NegativeInfinity;
        }
    }
}
