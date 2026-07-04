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

            if (!anyRoot)
            {
                // Vanilla behavior on empty roots: clear all secondaries.
                highlighter.UnhighlightAllSecondary();
                CurrentlyHighlighted.Clear();
                return false;
            }

            FreshGraphNodes.Clear();
            var iterator = _iteratorField!.GetValue(service);
            _iterateMethod!.Invoke(iterator, new object[] { rootNodes, FreshGraphNodes, includeUnfinished });

            // Unhighlight only nodes that left the set, highlight only nodes
            // that entered it. Same end state as unhighlight-all + re-add.
            CurrentlyHighlighted.RemoveWhere(node =>
            {
                if (node && FreshGraphNodes.Contains(node))
                {
                    return false;
                }

                highlighter.UnhighlightSecondary(node);
                return true;
            });

            var highlightColor = (Color)_highlightColorField!.GetValue(service);
            foreach (var node in FreshGraphNodes)
            {
                if (CurrentlyHighlighted.Add(node))
                {
                    highlighter.HighlightSecondary(node, highlightColor);
                }
            }

            FreshGraphNodes.Clear();
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

        if (unchanged && now - state.LastFullRunRealtime < BenchmarkSettings.TopoPreviewRefreshIntervalSeconds)
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
