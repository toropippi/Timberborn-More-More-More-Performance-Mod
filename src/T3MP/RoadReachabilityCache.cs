using System;
using System.Collections.Generic;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// Exact result cache for RoadReachabilityService.GetReachableNeighborsInRange
/// (the radius-10 BFS behind every wander decide - ~17.5k calls / 20s on n10c,
/// mostly from the same starting nodes because wander destinations are picked
/// from the beaver's HOME access node). The BFS is a pure function of the road
/// navmesh graph: same start + range produce the same nodes in the same
/// append order, so replaying a cached snapshot is bit-identical, including
/// the RNG stream of the callers (they consume randomness based only on the
/// produced list). Invalidated whenever the regular navmesh changes, via the
/// same NavMeshUpdateNotifier hook the travel shadow cache uses.
/// </summary>
internal static class RoadReachabilityCache
{
    private static readonly Dictionary<long, int[]> Cache = new Dictionary<long, int[]>(512);
    private static long _hits;
    private static long _misses;
    private static long _invalidations;

    public static bool TryGet(int startingNodeId, int range, List<int> output)
    {
        if (Cache.TryGetValue(Key(startingNodeId, range), out var nodes))
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                output.Add(nodes[i]);
            }

            Interlocked.Increment(ref _hits);
            return true;
        }

        Interlocked.Increment(ref _misses);
        return false;
    }

    public static void Store(int startingNodeId, int range, List<int> produced, int startIndex)
    {
        var count = produced.Count - startIndex;
        if (count < 0)
        {
            return;
        }

        var nodes = new int[count];
        for (var i = 0; i < count; i++)
        {
            nodes[i] = produced[startIndex + i];
        }

        Cache[Key(startingNodeId, range)] = nodes;
    }

    public static void OnNavMeshUpdate()
    {
        if (Cache.Count > 0)
        {
            Cache.Clear();
        }

        Interlocked.Increment(ref _invalidations);
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var hits = Interlocked.Exchange(ref _hits, 0);
        var misses = Interlocked.Exchange(ref _misses, 0);
        var invalidations = Interlocked.Exchange(ref _invalidations, 0);
        if (hits == 0 && misses == 0)
        {
            return;
        }

        var hitRate = hits + misses > 0 ? (double)hits / (hits + misses) : 0.0;
        Debug.Log($"[T3MP] RoadReachabilityCache aggregate={aggregateId}, hits={hits}, misses={misses}, hitRate={hitRate:F3}, invalidations={invalidations}, entries={Cache.Count}");
    }

    private static long Key(int startingNodeId, int range)
    {
        return ((long)range << 32) | (uint)startingNodeId;
    }
}
