using System;
using System.Collections.Generic;
using Timberborn.Navigation;

namespace T3MP.Loading;

// Temporary ordered input for evaluating shared connection decisions.
// This representation is never installed as a live source.
internal static class InitialNavData
{
    internal sealed class Data
    {
        internal readonly int[] EdgeOffsets, BlockOffsets;
        internal readonly NavMeshNode[] Edges;
        internal readonly int[] Blocks;
        internal readonly bool[] Present;
        internal Data(int[] edgeOffsets, int[] blockOffsets, NavMeshNode[] edges, int[] blocks, bool[] present)
        { EdgeOffsets = edgeOffsets; BlockOffsets = blockOffsets; Edges = edges; Blocks = blocks; Present = present; }
    }

        internal static bool Connected(Data data, int from, int to, HashSet<int> scratch, out int group, out float cost)
        {
            scratch.Clear();
            for (var i = data.BlockOffsets[from]; i < data.BlockOffsets[from + 1]; i++) scratch.Add(data.Blocks[i]);
            cost = float.MaxValue; group = 0; var found = false;
            for (var i = data.EdgeOffsets[from]; i < data.EdgeOffsets[from + 1]; i++)
            {
                var edge = data.Edges[i];
                // Preserve short-circuit order and consumption of distinct block
                // keys; neither a boolean block flag nor multiplicity subtraction
                // implements the native source's behavior.
                if (edge.Id == to && edge.Cost < cost && !scratch.Remove(unchecked(edge.Id * 397) ^ edge.GroupId))
                { found = true; cost = edge.Cost; group = edge.GroupId; }
            }
            return found;
        }

    internal static Data Create(InitialNavSource.Plan plan, int nodeCount)
    {
        var edgeOffsets = new int[nodeCount + 1]; var blockOffsets = new int[nodeCount + 1];
        var present = new bool[nodeCount];
        foreach (var n in plan.Nodes)
        { edgeOffsets[n.Id + 1] = n.Edges; blockOffsets[n.Id + 1] = n.Blocks; present[n.Id] = true; }
        for (var i = 1; i <= nodeCount; i++)
        { edgeOffsets[i] += edgeOffsets[i - 1]; blockOffsets[i] += blockOffsets[i - 1]; }
        var edges = new NavMeshNode[edgeOffsets[nodeCount]]; var blocks = new int[blockOffsets[nodeCount]];
        // Before publication, offsets double as write cursors to avoid two more
        // dense cursor arrays. Restore their start positions after filling.
        foreach (var c in plan.Changes)
        {
            if (c._changeType == NavMeshChangeType.AddEdge)
                edges[edgeOffsets[c._startNodeId]++] = new NavMeshNode(c._endNodeId, c._groupId, c._cost);
            else if (c._changeType == NavMeshChangeType.BlockEdge)
                blocks[blockOffsets[c._startNodeId]++] = unchecked(c._endNodeId * 397) ^ c._groupId;
        }
        for (var i = nodeCount; i > 0; i--)
        { edgeOffsets[i] = edgeOffsets[i - 1]; blockOffsets[i] = blockOffsets[i - 1]; }
        edgeOffsets[0] = blockOffsets[0] = 0;
        return new Data(edgeOffsets, blockOffsets, edges, blocks, present);
    }

}
