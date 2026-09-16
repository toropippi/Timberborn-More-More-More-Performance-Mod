using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// FlowFieldPathBuilder.AddEdgeNode asks the terrain navmesh graph for the
// connection cost and then for the group id of the same edge; each call scans
// the node's neighbor list for the first entry with the previous node's id.
// One scan finds that entry and both values are taken from it, with the
// native defaults when there is no such neighbor. Call order (cost, distance,
// group), the node appended and the float values are unchanged.
internal static class PathEdgeSingleScan
{
    private const string Owner = "t3mp.runtime.path-edge";
    private static Type? _harmonyType;
    private static MethodInfo? _addEdge;
    private static RuntimePatches.Shape? _shape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _generations;
    internal static long SingleScans, NativeScans;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;

    private sealed class Guard
    {
        internal readonly MethodInfo Method;
        internal readonly RuntimePatches.Shape Shape;
        internal int Generations;
        internal Guard(MethodInfo method, Type harmony)
        {
            Method = method;
            Shape = RuntimePatches.OriginalShape(harmony, method);
        }
    }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (_attempted) return;
        _attempted = true;
        _harmonyType = harmonyType;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            MethodInfo Find(Type type, string name, Type[]? parameters = null) =>
                (parameters == null ? type.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly)
                    : type.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly, null, parameters, null))
                ?? throw new MissingMethodException(type.FullName, name);
            _addEdge = Find(typeof(FlowFieldPathBuilder), "AddEdgeNode");
            var cost = Find(typeof(TerrainNavMeshGraph), "GetConnectionCost");
            var group = Find(typeof(TerrainNavMeshGraph), "GetGroupId");
            var checks = new (MethodInfo Method, string[] Hashes)[]
            {
                (_addEdge, new[] { "CEF3C9D74F9CA8356E3AE19EB7F020F9669348073208305D98DD7F31DA2177A6", "C2DE93E072EB2B6EB8390D8905E464D6CB8812880E0D1FE26BCE8CBD2DA75AE2" }),
                (cost, new[] { "0A74B9C3D6164A0F14E386356567656240EE6EC82061F78ED938EBEF42BDDE7A", "B451519E05B707EF18108A0AE220BBEC244E5A052458162AC4F6AF0AE9175BB7" }),
                (group, new[] { "4FB20734C041ED9C18FA0CDF838BD57494A9D07BBEAA3A9922268D350E58484D", "7761670D4A7D89D18FECF863458827C3F3F068334CE0397BEA3D26896B703BF2" }),
                (Find(typeof(NodeIdService), "Distance", new[] { typeof(int), typeof(int) }), new[] { "716A7EAB4FA4401D816B21975BA33839E3A65B1117D254E5E02B10BFBE6C3251" }),
                (Find(typeof(NodeIdService), "IdToWorld"), new[] { "76B6963B69FD2103AAB1609056B69C51A2EFC345F1CC6EACB0C33968109F572E", "7F16ABF0D0ECD7CEE0D201E00FFD61D502BB1D31F1BD4D4770BE191FBC5CD34A" }),
            };
            // Raw-IL fingerprints from the reviewed 1.1.2.4 and 1.0.13.1 APIs.
            foreach (var (method, hashes) in checks)
                if (!RuntimePatches.ReviewedBody(method, hashes)) throw new InvalidOperationException(method.Name + " is not a reviewed build");
            if (typeof(TerrainNavMeshGraph).GetField("_allNeighbors", RuntimePatches.All)?.FieldType != typeof(List<NavMeshNode>[]) ||
                typeof(TerrainNavMeshGraph).GetField("_defaultGroupId", RuntimePatches.All)?.FieldType != typeof(int) ||
                typeof(TerrainNavMeshGraph).GetField("DefaultConnectionCost", RuntimePatches.All)?.FieldType != typeof(float))
                throw new MissingFieldException(typeof(TerrainNavMeshGraph).FullName, "_allNeighbors");
            // The two graph searches are bypassed on the single-scan path; the
            // distance call between them is retained but observed, because the
            // matched neighbor is reused across it.
            var methods = new[] { cost, group, checks[3].Method };
            foreach (var method in methods.Append(_addEdge))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _shape = RuntimePatches.OriginalShape(harmonyType, _addEdge);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            apply(cost, null, null, nameof(ObserveCost), null);
            apply(group, null, null, nameof(ObserveGroup), null);
            apply(checks[3].Method, null, null, nameof(ObserveDistance), null);
            apply(_addEdge, null, null, nameof(RewriteAddEdge), null);
        }, typeof(PathEdgeSingleScan));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[T3MP] Path edge single scan installed.");
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _addEdge == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_addEdge).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[T3MP] Path edge revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static void AddEdgeNode(FlowFieldPathBuilder self, int nodeId, int previousNodeId, List<FlowFieldPathNode> flowFieldPath)
    {
        var graph = self._terrainNavMeshGraph;
        var ids = self._nodeIdService;
        if (!Active)
        {
            NativeScans++;
            float connectionCost = graph.GetConnectionCost(nodeId, previousNodeId);
            float distanceToNext = ids.Distance(nodeId, previousNodeId);
            int groupId = graph.GetGroupId(nodeId, previousNodeId);
            flowFieldPath.Add(new FlowFieldPathNode(ids.IdToWorld(nodeId), connectionCost, distanceToNext, groupId));
            return;
        }
        SingleScans++;
        // GetConnectionCost: first neighbor with the previous id, else the default.
        List<NavMeshNode> list = graph._allNeighbors[nodeId];
        int found = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == previousNodeId) { found = i; break; }
        }
        float cost = found >= 0 ? list[found].Cost : TerrainNavMeshGraph.DefaultConnectionCost;
        float distance = ids.Distance(nodeId, previousNodeId);
        // GetGroupId would rescan the same list for the same first match. If a
        // guard was invalidated during the distance call, rescan natively.
        int group = Active ? (found >= 0 ? list[found].GroupId : graph._defaultGroupId) : graph.GetGroupId(nodeId, previousNodeId);
        flowFieldPath.Add(new FlowFieldPathNode(ids.IdToWorld(nodeId), cost, distance, group));
    }

    private static IEnumerable<T> ObserveCost<T>(IEnumerable<T> i) => Observe(i, 0);
    private static IEnumerable<T> ObserveGroup<T>(IEnumerable<T> i) => Observe(i, 1);
    private static IEnumerable<T> ObserveDistance<T>(IEnumerable<T> i) => Observe(i, 2);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteAddEdge<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        if (Interlocked.Increment(ref _generations) != 1 || _shape == null ||
            !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(PathEdgeSingleScan).GetMethod(nameof(AddEdgeNode), RuntimePatches.All)!, 4);
    }
}
