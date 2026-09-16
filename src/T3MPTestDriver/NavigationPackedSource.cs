using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Initial sources use immutable contiguous storage. Before an ordinary source
// mutation, only its endpoints materialize into the game's original node/list
// representation. The original mutation, graph update and exception paths run.
internal static class NavigationPackedSource
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly ConditionalWeakTable<NavMeshSource, State> States = new ConditionalWeakTable<NavMeshSource, State>();
    private static long _sources, _packedNodes, _materialized;

    internal sealed class Data
    {
        internal readonly int[] EdgeOffsets, BlockOffsets;
        internal readonly NavMeshNode[] Edges;
        internal readonly int[] Blocks;
        internal readonly bool[] Present;
        internal Data(int[] edgeOffsets, int[] blockOffsets, NavMeshNode[] edges, int[] blocks, bool[] present)
        { EdgeOffsets = edgeOffsets; BlockOffsets = blockOffsets; Edges = edges; Blocks = blocks; Present = present; }
    }

    internal sealed class State
    {
        internal readonly Data Data;
        // Per-source state; shared Data arrays are never modified after creation.
        internal readonly bool[] Remaining;
        internal State(Data data) { Data = data; Remaining = (bool[])data.Present.Clone(); }

        internal bool Connected(int from, int to, HashSet<int> scratch, out int group, out float cost)
            => Connected(Data, from, to, scratch, out group, out cost);

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

        internal NavMeshSourceNode CopyNode(int id)
        {
            var node = new NavMeshSourceNode();
            var edgeStart = Data.EdgeOffsets[id]; var edgeEnd = Data.EdgeOffsets[id + 1];
            if (edgeEnd != edgeStart)
            {
                node._edges = new List<NavMeshNode>(edgeEnd - edgeStart);
                for (var i = edgeStart; i < edgeEnd; i++) node._edges.Add(Data.Edges[i]);
            }
            var blockStart = Data.BlockOffsets[id]; var blockEnd = Data.BlockOffsets[id + 1];
            if (blockEnd != blockStart)
            {
                node._blockages = new List<int>(blockEnd - blockStart);
                for (var i = blockStart; i < blockEnd; i++) node._blockages.Add(Data.Blocks[i]);
            }
            return node;
        }

        internal void WriteNode(BinaryWriter writer, int id)
        {
            var start = Data.EdgeOffsets[id]; var end = Data.EdgeOffsets[id + 1];
            writer.Write(end - start);
            for (var i = start; i < end; i++)
            { var edge = Data.Edges[i]; writer.Write(edge.Id); writer.Write(edge.GroupId); writer.Write(edge.Cost); }
            start = Data.BlockOffsets[id]; end = Data.BlockOffsets[id + 1];
            writer.Write(end - start);
            for (var i = start; i < end; i++) writer.Write(Data.Blocks[i]);
        }
    }

    internal static void Install()
    {
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.nav-packed-source");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        void Prefix(MethodInfo target, string hook) => patch.Invoke(harmony, new object?[] { target,
            Activator.CreateInstance(hm, typeof(NavigationPackedSource).GetMethod(hook, All)), null, null, null });
        Prefix(typeof(NavMeshSource).GetMethod("VerifyBeforeChange", All, null, new[] { typeof(int) }, null)!, nameof(Materialize));
        // Clear only after the original array replacement succeeds; an allocation
        // failure must not discard the previously valid packed representation.
        patch.Invoke(harmony, new object?[] { typeof(NavMeshSource).GetMethod("Load", All)!, null,
            Activator.CreateInstance(hm, typeof(NavigationPackedSource).GetMethod(nameof(Reset), All)), null, null });
        Debug.Log("[T3MPNAVPACKED] installed; immutable source arrays, original runtime mutations after endpoint materialization");
    }

    internal static Data Create(NavigationInitialBuilder.Plan plan, int nodeCount)
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

    internal static bool TryAttach(Data? data, NavigationInitialBuilder.Plan? plan, NavMeshSource source, NavMeshUpdate.Builder builder, bool road)
    {
        if (data == null || plan == null || States.TryGetValue(source, out _)) return false;
        foreach (var n in plan.Nodes) if (source._nodes[n.Id] != null) return false;
        var state = new State(data);
        States.Add(source, state);
        foreach (var n in plan.Nodes) if (road) builder.AddRoadNode(n.Id); else builder.AddTerrainNode(n.Id);
        Interlocked.Increment(ref _sources); Interlocked.Add(ref _packedNodes, plan.Nodes.Length);
        return true;
    }

    internal static State? Get(NavMeshSource source) => States.TryGetValue(source, out var value) ? value : null;

    private static void Materialize(NavMeshSource __instance, int nodeId)
    {
        if (!States.TryGetValue(__instance, out var state) || (uint)nodeId >= (uint)state.Remaining.Length || !state.Remaining[nodeId]) return;
        // Complete the copy before changing its visibility; failed allocation
        // leaves the immutable representation authoritative.
        var node = state.CopyNode(nodeId);
        __instance._nodes[nodeId] = node;
        state.Remaining[nodeId] = false;
        Interlocked.Increment(ref _materialized);
    }

    private static void Reset(NavMeshSource __instance) => States.Remove(__instance);

    internal static void Report()
    {
        if (_sources != 0) Debug.Log($"[T3MPNAVPACKED] sources={_sources} packedNodes={_packedNodes} materialized={_materialized}");
    }
}
