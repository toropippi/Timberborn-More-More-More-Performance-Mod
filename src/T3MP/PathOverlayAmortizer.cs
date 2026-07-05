using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Timberborn.BuildingsNavigation;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Replaces DistrictPathNavRangeDrawer's dirty processing: instead of one
// synchronous rebuild (measured 32-46ms: ~4-6ms flow-field node refresh,
// ~16-22ms per-tile connection keys, ~17ms mesh Build), the rebuild is
// spread over frames - node refresh on the start frame, the tile loop in
// budgeted chunks, then the two mesh Builds on separate frames. The
// previously built mesh keeps drawing during the sweep, so the overlay is
// simply a few frames stale while updating, never a single big hitch.
// Rate limiting (one rebuild start per interval) is folded in here.
internal static class PathOverlayAmortizer
{
    private sealed class State
    {
        public readonly List<WeightedCoordinates> Queue = new List<WeightedCoordinates>(4096);
        public int Index;
        public bool Sweeping;
        public bool BuiltRegular;
        public float NextAllowedRebuildRealtime;
    }

    private static readonly ConditionalWeakTable<DistrictPathNavRangeDrawer, State> States =
        new ConditionalWeakTable<DistrictPathNavRangeDrawer, State>();
    private static int _errorLogs;

    // Harmony prefix on DistrictPathNavRangeDrawer.LateUpdate; returns false
    // (vanilla skipped) on success.
    internal static bool AmortizedLateUpdate(DistrictPathNavRangeDrawer __instance)
    {
        try
        {
            var state = States.GetOrCreateValue(__instance);
            var now = Time.realtimeSinceStartup;
            if (__instance._dirty && now >= state.NextAllowedRebuildRealtime)
            {
                state.NextAllowedRebuildRealtime = now + BenchmarkSettings.TopoPathOverlayMinRebuildIntervalSeconds;
                __instance._dirty = false;
                // Start frame: refresh the node set (flow-field query) and
                // reset the staging mesh builders. The displayed mesh is
                // untouched until Build.
                __instance.UpdateAllNodes();
                state.Queue.Clear();
                foreach (var node in __instance._roadNodes)
                {
                    state.Queue.Add(node);
                }

                __instance._regularMeshDrawer.Reset();
                __instance._stairsMeshDrawer.Reset();
                state.Index = 0;
                state.Sweeping = true;
                state.BuiltRegular = false;
            }

            if (state.Sweeping)
            {
                var budget = BenchmarkSettings.TopoOverlayTilesPerFrame;
                while (budget-- > 0 && state.Index < state.Queue.Count)
                {
                    __instance.AddTile(state.Queue[state.Index++]);
                }

                if (state.Index >= state.Queue.Count)
                {
                    // Split the two mesh uploads across separate frames.
                    if (!state.BuiltRegular)
                    {
                        __instance._regularMeshDrawer.Build();
                        state.BuiltRegular = true;
                    }
                    else
                    {
                        __instance._stairsMeshDrawer.Build();
                        state.Sweeping = false;
                        state.Queue.Clear();
                    }
                }
            }

            // Vanilla tail: draw the current meshes, then disable until the
            // next DrawRange re-enables the component.
            __instance.Draw();
            __instance.DisableComponent();
            return false;
        }
        catch (Exception exception)
        {
            if (_errorLogs++ < 3)
            {
                Debug.LogWarning($"[T3MP] Path overlay amortizer failed, falling back to vanilla: {exception.GetType().Name}: {exception.Message}");
            }

            return true;
        }
    }
}
