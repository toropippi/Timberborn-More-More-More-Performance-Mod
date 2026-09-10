using System;
using System.Collections.Generic;
using Timberborn.Navigation;

namespace T3MP.Loading;

// Initial-load construction. Runtime updates and queues with
// removals retain the ordered game implementation. Plans contain no live lists.
internal static class InitialNavSource
{
    internal sealed class Plan
    {
        internal Layout[] Nodes = null!;
        internal NavMeshChange[] Changes = null!;
    }

    internal readonly struct Layout
    {
        internal readonly int Id, Edges, Blocks;
        internal Layout(int id, int edges, int blocks) { Id = id; Edges = edges; Blocks = blocks; }
    }

    internal static Plan? Create(NavMeshChange[] changes, int nodeCount)
    {
        // Complete eligibility check before allocating counts or mutating a source.
        foreach (var c in changes)
        {
            if (c._changeType == NavMeshChangeType.None) continue;
            if ((c._changeType != NavMeshChangeType.AddEdge && c._changeType != NavMeshChangeType.BlockEdge) ||
                (uint)c._startNodeId >= (uint)nodeCount || (uint)c._endNodeId >= (uint)nodeCount ||
                c._startNodeId == c._endNodeId) return null;
            // A native blockage stores only (end * 397) XOR group. Cross-end
            // collisions can affect a different pair without re-evaluating its
            // graph connection. Final-state evaluation is equivalent only when
            // keys identify their endpoint: groups < 256 preserve the high bits,
            // and non-overflowing products are spaced by 397 (> 256).
            if ((uint)c._groupId >= 256u || c._endNodeId > int.MaxValue / 397) return null;
        }
        var edgeCounts = new int[nodeCount];
        var blockCounts = new int[nodeCount];
        var seen = new bool[nodeCount];
        var order = new List<int>();
        foreach (var c in changes)
        {
            if (c._changeType == NavMeshChangeType.None) continue;
            Touch(c._startNodeId); Touch(c._endNodeId);
            if (c._changeType == NavMeshChangeType.AddEdge) edgeCounts[c._startNodeId]++;
            else blockCounts[c._startNodeId]++;
        }
        var nodes = new Layout[order.Count];
        for (var i = 0; i < nodes.Length; i++)
        {
            var id = order[i]; nodes[i] = new Layout(id, edgeCounts[id], blockCounts[id]);
        }
        return new Plan { Nodes = nodes, Changes = changes };

        void Touch(int id) { if (!seen[id]) { seen[id] = true; order.Add(id); } }
    }

    internal static bool TryApply(Plan? plan, NavMeshSource source, NavMeshUpdate.Builder builder, bool road)
    {
        if (plan == null) return false;
        var nodes = source._nodes;
        foreach (var layout in plan.Nodes) if (nodes[layout.Id] != null) return false;
        foreach (var layout in plan.Nodes)
        {
            // End-only nodes exist in the original source too, even when empty.
            var node = new NavMeshSourceNode();
            if (layout.Edges != 0) node._edges = new List<NavMeshNode>(layout.Edges);
            if (layout.Blocks != 0) node._blockages = new List<int>(layout.Blocks);
            nodes[layout.Id] = node;
            // First-touch order equals Builder's original ordered deduplication.
            if (road) builder.AddRoadNode(layout.Id); else builder.AddTerrainNode(layout.Id);
        }
        foreach (var c in plan.Changes)
        {
            if (c._changeType == NavMeshChangeType.AddEdge)
                nodes[c._startNodeId]._edges.Add(new NavMeshNode(c._endNodeId, c._groupId, c._cost));
            else if (c._changeType == NavMeshChangeType.BlockEdge)
                nodes[c._startNodeId]._blockages.Add(unchecked(c._endNodeId * 397) ^ c._groupId);
        }
        return true;
    }
}
