using System.Reflection;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    internal static class Debug
    {
        internal static readonly List<string> Messages = new();
        public static void Log(object value) => Messages.Add(value.ToString()!);
        public static void LogWarning(object value) => Log(value);
        public static void LogError(object value) => Log(value);
    }
}
namespace T3MP.Runtime
{
    internal static class RequestedSpeedPolicy { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class EventBusFastDelegates { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class WaterTextureUpload { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class TickFrontier { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class WalkerSpeedDelegates { internal static void Install(Type a, Type b, MethodInfo c) { } }
    internal static class TubeVisitFix { internal static void Install(Type a, Type b, MethodInfo c) { } }
}
namespace Timberborn.Navigation
{
    internal readonly struct NavMeshNode(int id, float cost)
    {
        public int Id { [MethodImpl(MethodImplOptions.NoInlining)] get; } = id;
        public float Cost { [MethodImpl(MethodImplOptions.NoInlining)] get; } = cost;
    }
    internal sealed class TerrainNavMeshGraph
    {
        internal List<NavMeshNode>[] Nodes = Array.Empty<List<NavMeshNode>>();
        internal Action? OnRead;
        internal List<NavMeshNode> GetNeighbors(int id) { OnRead?.Invoke(); return Nodes[id]; }
    }
    // Native control flow from the supported TerrainReachabilityService.
    // Graph storage is a fixture; production patch and Harmony helpers are linked.
    internal sealed class TerrainReachabilityService
    {
        internal readonly struct NodeToVisit(int id, float distance)
        {
            public int Id { get; } = id;
            public float Distance { [MethodImpl(MethodImplOptions.NoInlining)] get; } = distance;
        }
        internal TerrainNavMeshGraph _terrainNavMeshGraph = new();
        internal Queue<NodeToVisit> _nodesToVisit = new();
        internal HashSet<int> _visitedNodes = new();
        internal bool[] Restricted = Array.Empty<bool>();
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void VisitNode(int id, float distance)
        {
            _visitedNodes.Add(id);
            _nodesToVisit.Enqueue(new NodeToVisit(id, distance));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void VisitNeighbors(NodeToVisit node)
        {
            var neighbors = _terrainNavMeshGraph.GetNeighbors(node.Id);
            for (var i = 0; i < neighbors.Count; i++)
            {
                var neighbor = neighbors[i];
                var id = neighbor.Id;
                if (!_visitedNodes.Contains(id)) VisitNode(id, node.Distance + neighbor.Cost);
            }
        }
        internal void Search(int start, int range, List<int> result)
        {
            VisitNode(start, 0);
            while (_nodesToVisit.Count != 0)
            {
                var node = _nodesToVisit.Dequeue();
                if (node.Distance < range) VisitNeighbors(node);
            }
            foreach (var id in _visitedNodes) if (!Restricted[id]) result.Add(id);
            _visitedNodes.Clear();
        }
    }
}
