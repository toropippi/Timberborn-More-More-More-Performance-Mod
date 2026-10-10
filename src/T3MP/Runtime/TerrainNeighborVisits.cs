using System;
using System.Collections.Generic;
using System.Reflection;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// One set lookup for a newly discovered terrain node. No search results or
// navigation state survive a call. Native output order and float math remain.
internal static class TerrainNeighborVisits
{
    private const string Owner = "t3mp.runtime.terrain-visits";
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
            apply(RuntimePatches.Find(typeof(TerrainReachabilityService), "VisitNeighbors"), null, null, nameof(RewriteNeighbors), null),
            typeof(TerrainNeighborVisits));
        Installed = true;
        Debug.Log("[T3MP] Terrain neighbor single-lookup visits installed.");
    }

    // VisitNeighbors with Contains + VisitNode folded into one HashSet.Add.
    private static void VisitNeighbors(TerrainReachabilityService instance, TerrainReachabilityService.NodeToVisit current)
    {
        var neighbors = instance._terrainNavMeshGraph.GetNeighbors(current.Id);
        for (var i = 0; i < neighbors.Count; i++)
        {
            var neighbor = neighbors[i];
            if (instance._visitedNodes.Add(neighbor.Id))
                instance._nodesToVisit.Enqueue(new TerrainReachabilityService.NodeToVisit(neighbor.Id, current.Distance + neighbor.Cost));
        }
    }

    private static IEnumerable<T> RewriteNeighbors<T>(IEnumerable<T> instructions) =>
        RuntimePatches.CallAndReturn<T>(typeof(TerrainNeighborVisits).GetMethod(nameof(VisitNeighbors), RuntimePatches.All)!, 2);
}
