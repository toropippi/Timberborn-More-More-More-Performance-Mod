using System;
using System.Collections.Generic;
using Timberborn.Navigation;

namespace T3MP.Loading;

// Initial graph construction. Immutable connection decisions
// may be reused for identical inputs; all published native lists remain owned
// by their individual graph. No runtime graph methods are patched.
internal static class InitialNavGraph
{
    internal readonly struct Connection
    {
        internal readonly int A, B, Group;
        internal readonly float Cost;
        internal Connection(int a, int b, int group, float cost)
        { A = a; B = b; Group = group; Cost = cost; }
    }

    internal sealed class Plan
    {
        internal Connection[] Connections = null!;
        internal int ConnectionCount;
        internal Row[] Nodes = null!;
    }

    internal readonly struct Row
    {
        internal readonly int Id, Degree, CheapDegree;
        internal Row(int id, int degree, int cheapDegree)
        { Id = id; Degree = degree; CheapDegree = cheapDegree; }
    }

    internal static Plan Create(InitialNavData.Data source, InitialNavSource.Plan initial, long[] pairs)
    {
        var degrees = new int[source.Present.Length];
        var cheapDegrees = new int[degrees.Length];
        var connections = new Connection[pairs.Length];
        var count = 0;
        var scratch = new HashSet<int>();
        foreach (var pair in pairs)
        {
            var a = (int)(pair >> 32); var b = (int)pair;
            if (!InitialNavData.Connected(source, a, b, scratch, out var ag, out var ac) ||
                !InitialNavData.Connected(source, b, a, scratch, out var bg, out var bc) || ag != bg) continue;
            var cost = Math.Max(ac, bc);
            connections[count++] = new Connection(a, b, ag, cost);
            degrees[a]++; degrees[b]++;
            if (cost <= 1f) { cheapDegrees[a]++; cheapDegrees[b]++; }
        }
        var nodes = new Row[initial.Nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var id = initial.Nodes[i].Id;
            nodes[i] = new Row(id, degrees[id], cheapDegrees[id]);
        }
        return new Plan { Connections = connections, ConnectionCount = count, Nodes = nodes };
    }

    internal static bool TryApply(Plan plan, INavMeshGraph graph)
    {
        if (graph is TerrainNavMeshGraph terrain)
        {
            // Guard before any mutation, including rows with only blocked edges.
            // Existing graphs use ordinary ordered updates instead.
            foreach (var n in plan.Nodes)
                if (!ReferenceEquals(terrain._allNeighbors[n.Id], TerrainNavMeshGraph.EmptyAllNeighbors) ||
                    !ReferenceEquals(terrain._cheapNeighbors[n.Id], TerrainNavMeshGraph.EmptyCheapNeighbors)) return false;
            foreach (var n in plan.Nodes)
            {
                if (n.Degree != 0) terrain._allNeighbors[n.Id] = new List<NavMeshNode>(n.Degree);
                if (n.CheapDegree != 0) terrain._cheapNeighbors[n.Id] = new List<int>(n.CheapDegree);
            }
            for (var i = 0; i < plan.ConnectionCount; i++)
            {
                var c = plan.Connections[i];
                terrain._allNeighbors[c.A].Add(new NavMeshNode(c.B, c.Group, c.Cost));
                terrain._allNeighbors[c.B].Add(new NavMeshNode(c.A, c.Group, c.Cost));
                if (c.Cost <= 1f)
                { terrain._cheapNeighbors[c.A].Add(c.B); terrain._cheapNeighbors[c.B].Add(c.A); }
            }
            return true;
        }
        if (graph is RoadNavMeshGraph road)
        {
            foreach (var n in plan.Nodes)
                if (!ReferenceEquals(road._neighbors[n.Id], RoadNavMeshGraph.EmptyNeighbors)) return false;
            foreach (var n in plan.Nodes)
                if (n.Degree != 0) road._neighbors[n.Id] = new List<NavMeshNode>(n.Degree);
            for (var i = 0; i < plan.ConnectionCount; i++)
            {
                var c = plan.Connections[i];
                road._neighbors[c.A].Add(new NavMeshNode(c.B, c.Group, c.Cost));
                road._neighbors[c.B].Add(new NavMeshNode(c.A, c.Group, c.Cost));
            }
            return true;
        }
        return false;
    }
}
