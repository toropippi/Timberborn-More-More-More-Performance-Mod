using System;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Faster RoadFlowFieldGenerator.FillFlowField with IDENTICAL results.
//
// Vanilla marks a node visited at PUSH time (AddNode immediately on first
// encounter), so the output - which nodes, their parents and distances, and
// the dictionary insertion order - is fully determined by the heap pop/push
// sequence. This reimplementation REUSES the generator's own BinaryHeap
// instance and performs pushes/pops and AddNode calls in exactly the vanilla
// order; only two pure overheads change:
//   1. the visited check: a generation-stamped int[] indexed by node id
//      instead of a Dictionary.ContainsKey per edge,
//   2. the output dictionary is pre-sized (EnsureCapacity) to the limiting
//      field's node count, eliminating rehash cascades during the fill.
// Any exception falls back to the vanilla method, which safely restarts the
// fill from scratch (the field is not yet marked filled).
internal static class FlowFieldFastPath
{
    private static int[] _visitedStamps = new int[65536];
    private static int _stamp;
    private static int _errorLogs;

    // Harmony prefix on RoadFlowFieldGenerator.FillFlowField; returns false
    // (skip vanilla) on success.
    internal static bool FastFillFlowField(
        RoadFlowFieldGenerator __instance,
        RoadNavMeshGraph roadNavMeshGraph,
        AccessFlowField flowField,
        AccessFlowField limitingFlowField,
        int startNodeId)
    {
        try
        {
            // Keep the instance fields consistent with vanilla in case any
            // other code inspects them.
            __instance._flowField = flowField;
            __instance._limitingFlowField = limitingFlowField;
            if (flowField.IsFilled)
            {
                return false;
            }

            flowField.Clear();
            if (!roadNavMeshGraph.IsOnNavMesh(startNodeId) || !limitingFlowField.HasNode(startNodeId))
            {
                return false;
            }

            var limitingNodes = limitingFlowField._nodes;
            flowField._nodes.EnsureCapacity(limitingNodes.Count);

            var heap = __instance._openSet;
            heap.Clear();
            _stamp++;
            EnsureVisitedCapacity(startNodeId);
            _visitedStamps[startNodeId] = _stamp;
            heap.Push(new RoadFlowFieldGenerator.Node(startNodeId, 0f));
            flowField.AddNode(startNodeId, -1, 0f);

            while (!heap.IsEmpty())
            {
                var parent = heap.Pop();
                var neighbors = roadNavMeshGraph.GetNeighbors(parent.NodeId);
                for (var i = 0; i < neighbors.Count; i++)
                {
                    var neighbor = neighbors[i];
                    var id = neighbor.Id;
                    if (id < 0)
                    {
                        continue;
                    }

                    EnsureVisitedCapacity(id);
                    if (_visitedStamps[id] == _stamp || !limitingNodes.ContainsKey(id))
                    {
                        continue;
                    }

                    _visitedStamps[id] = _stamp;
                    var distance = parent.Distance + neighbor.Cost;
                    heap.Push(new RoadFlowFieldGenerator.Node(id, distance));
                    flowField.AddNode(id, parent.NodeId, distance);
                }
            }

            flowField.MarkAsFilled();
            return false;
        }
        catch (Exception exception)
        {
            if (_errorLogs++ < 3)
            {
                Debug.LogWarning($"[T3MP] Flow field fast path failed, falling back to vanilla: {exception.GetType().Name}: {exception.Message}");
            }

            return true;
        }
    }

    private static void EnsureVisitedCapacity(int nodeId)
    {
        if (nodeId < _visitedStamps.Length)
        {
            return;
        }

        var newSize = _visitedStamps.Length * 2;
        while (newSize <= nodeId)
        {
            newSize *= 2;
        }

        Array.Resize(ref _visitedStamps, newSize);
    }
}
