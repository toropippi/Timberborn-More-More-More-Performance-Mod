using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Only initial append-only queues. Published source nodes and graph lists are
// ordinary independent native objects; later mutations use the original game.
internal static class InitialNavigationLoad
{
    internal static bool Installed { get; private set; }
    private const string Owner = "t3mp.load.initial-navigation";
    private const BindingFlags All = LoadPatchBridge.All;
    [ThreadStatic] private static int _depth;
    private static bool _compatible, _parallel;
    private static readonly List<Prepared> Plans = new();
    private static readonly Type[] SourceTypes = { typeof(TerrainNavMeshSource), typeof(InstantTerrainNavMeshSource), typeof(PreviewTerrainNavMeshSource),
        typeof(RoadNavMeshSource), typeof(InstantRoadNavMeshSource), typeof(PreviewRoadNavMeshSource) };
    private static readonly Type[] GraphTypes = { typeof(TerrainNavMeshGraph), typeof(InstantTerrainNavMeshGraph), typeof(PreviewTerrainNavMeshGraph),
        typeof(RoadNavMeshGraph), typeof(InstantRoadNavMeshGraph), typeof(PreviewRoadNavMeshGraph) };
    private static readonly MethodBase[] Protected = new[] { typeof(NavMeshSource), typeof(NavMeshSourceNode), typeof(NavMeshChange),
        typeof(NavMeshUpdate.Builder), typeof(NodeIdService) }.Concat(SourceTypes).Concat(GraphTypes)
        .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(All)))
        .Concat(new[] { typeof(NavMeshUpdater).GetMethod("ProcessInstantChanges", All)!, typeof(NavMeshUpdater).GetMethod("ProcessRegularChanges", All)! }).ToArray();

    internal sealed class Prepared
    {
        internal NodeIdService Ids = null!;
        internal bool Road;
        internal NavMeshChange[] Input = null!;
        internal InitialNavSource.Plan Source = null!;
        internal InitialNavGraph.Plan Graph = null!;
        internal int PairCount;
    }
    private sealed class PairComparer : IEqualityComparer<long>
    {
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long value)
        {
            unchecked { var x = (ulong)value; x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL;
                x ^= x >> 27; x *= 0x94d049bb133111ebUL; return (int)(x ^ (x >> 31)); }
        }
    }
    private static bool Reviewed() => LoadCompatibility.Reviewed(
        "Timberborn.Navigation|b848ebf8-28b6-4c17-ab4f-f91582085afa",
        "Timberborn.Common|88d60edf-d568-470b-af44-77abc48c8bd0",
        "UnityEngine.CoreModule|61dee272-fd45-47db-9c13-40fe43fbdc1a");
    private static bool CheckCompatibility() => Reviewed() && LoadCompatibility.Unmodified(Protected, Owner,
        (method, owner) => owner == "local.gpupathinginvestigation.benchmarkprobe" && method.DeclaringType == typeof(RoadNavMeshGraph) &&
            (method.Name == "ConnectNodes" || method.Name == "DisconnectNodes" || method.Name == "Load"));

    internal static void Install()
    {
        if (Installed || Environment.GetCommandLineArgs().Contains("-t3mpTestInitialNavBaseline")) return;
        if (!Reviewed()) { Debug.Log("[T3MPINITIALNAV] native fallback: unreviewed modules"); return; }
        // Measured parallel fill spent more process CPU without a repeatable
        // latency improvement. Keep it opt-in for controlled comparisons.
        _parallel = Environment.GetCommandLineArgs().Contains("-t3mpTestInitialNavParallel") &&
            !Environment.GetCommandLineArgs().Contains("-t3mpTestInitialNavSequential");
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony"); var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, Owner)!;
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(InitialNavigationLoad).GetMethod(name, All))!;
        try
        {
            patch.Invoke(harmony, new object?[] { typeof(NavigationSynchronizer).GetMethod("PostLoad", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
            patch.Invoke(harmony, new object?[] { typeof(NavMeshUpdater).GetMethod("ProcessInstantChanges", All), Hook(nameof(Instant)), null, null, null });
            patch.Invoke(harmony, new object?[] { typeof(NavMeshUpdater).GetMethod("ProcessRegularChanges", All), Hook(nameof(Regular)), null, null, null });
            Installed = true;
            Debug.Log("[T3MPINITIALNAV] installed parallel=" + _parallel + "; initial queues; independent native source and graph storage");
        }
        catch (Exception e)
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
            Debug.LogWarning("[T3MPINITIALNAV] disabled: " + e.GetBaseException().Message);
        }
    }
    private static void Begin(out bool __state)
    {
        __state = true;
        if (_depth++ != 0) return;
        Plans.Clear(); _compatible = CheckCompatibility();
    }
    private static void End(bool __state)
    {
        if (!__state || _depth <= 0 || --_depth != 0) return;
        Debug.Log($"[T3MPINITIALNAV] end plans={Plans.Count} pairEvaluations={Plans.Sum(p => (long)p.PairCount)} compatible={_compatible}");
        Plans.Clear();
    }
    private static bool Instant(NavMeshUpdater __instance, NavMeshUpdate.Builder navMeshUpdateBuilder) => Live(__instance, navMeshUpdateBuilder, true);
    private static bool Regular(NavMeshUpdater __instance, NavMeshUpdate.Builder navMeshUpdateBuilder) => Live(__instance, navMeshUpdateBuilder, false);

    private static bool Live(NavMeshUpdater u, NavMeshUpdate.Builder builder, bool instant)
    {
        if (_depth != 1 || !_compatible || u.GetType() != typeof(NavMeshUpdater)) return true;
        var terrain = instant ? u._enqueuedInstantTerrainChanges : u._enqueuedRegularTerrainChanges;
        var road = instant ? u._enqueuedInstantRoadChanges : u._enqueuedRegularRoadChanges;
        if (terrain.Count == 0) return true;
        var sources = instant ? new NavMeshSource[] { u._instantTerrainNavMeshSource, u._previewTerrainNavMeshSource, u._instantRoadNavMeshSource, u._previewRoadNavMeshSource }
            : new NavMeshSource[] { u._terrainNavMeshSource, u._roadNavMeshSource };
        if (sources.Any(s => !SourceTypes.Contains(s.GetType()) || s._nodeIdService.GetType() != typeof(NodeIdService) ||
            !GraphTypes.Contains(s._navMeshGraph.GetType()))) return true;
        var inputs = new[] { terrain.ToArray(), road.ToArray() };
        var prepared = new Prepared[sources.Length]; var builders = new NavMeshUpdate.Builder[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var isRoad = sources[i] is RoadNavMeshSource;
            var plan = Prepare(inputs[isRoad ? 1 : 0], sources[i]._nodeIdService, isRoad);
            if (plan == null || !Eligible(plan, sources[i])) return true;
            prepared[i] = plan; builders[i] = new NavMeshUpdate.Builder(plan.Ids);
        }
        // No source has been changed before every queue and target is checked.
        // Worker code only allocates/fills independent managed lists and value
        // structs. It invokes no Unity objects, game callbacks or native scratch.
        // v1.2: no runtime graph caches exist any more, so direct writes need no
        // invalidation step here.
        if (_parallel) Parallel.For(0, sources.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(sources.Length, Environment.ProcessorCount) },
            i => Apply(prepared[i], sources[i], builders[i]));
        else for (var i = 0; i < sources.Length; i++) Apply(prepared[i], sources[i], builders[i]);
        foreach (var id in builders[0]._terrainNodeIds) builder.AddTerrainNode(id);
        foreach (var id in builders[instant ? 2 : 1]._roadNodeIds) builder.AddRoadNode(id);
        terrain.Clear(); road.Clear();
        ReportApplied(sources, builders, prepared);
        return false;
    }
    // Coarse boundary also used by the separate test driver for native replays.
    private static void ReportApplied(NavMeshSource[] sources, NavMeshUpdate.Builder[] builders, object[] plans) =>
        Debug.Log($"[T3MPINITIALNAV] applied sources={sources.Length} inputChanges={plans.Cast<Prepared>().Sum(p => (long)p.Input.Length)}");

    private static bool Same(Prepared plan, NavMeshChange[] input, NodeIdService ids, bool road)
    {
        if (!ReferenceEquals(plan.Ids, ids) || plan.Road != road || plan.Input.Length != input.Length) return false;
        for (var i = 0; i < input.Length; i++)
        {
            var a = plan.Input[i]; var b = input[i];
            if (a._changeType != b._changeType || a._startNodeId != b._startNodeId || a._endNodeId != b._endNodeId ||
                a._groupId != b._groupId || BitConverter.SingleToInt32Bits(a._cost) != BitConverter.SingleToInt32Bits(b._cost)) return false;
        }
        return true;
    }
    internal static Prepared? Prepare(NavMeshChange[] input, NodeIdService ids, bool road)
    {
        var prior = Plans.FirstOrDefault(p => Same(p, input, ids, road));
        if (prior != null) return prior;
        var initial = InitialNavSource.Create(input, ids.NumberOfNodes);
        if (initial == null) return null;
        var last = new Dictionary<long, int>(new PairComparer());
        long Key(NavMeshChange c) => ((long)Math.Min(c._startNodeId, c._endNodeId) << 32) | (uint)Math.Max(c._startNodeId, c._endNodeId);
        for (var i = 0; i < input.Length; i++) if (input[i]._changeType != NavMeshChangeType.None) last[Key(input[i])] = i;
        var pairs = new long[last.Count]; var next = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c._changeType != NavMeshChangeType.None && last[Key(c)] == i) pairs[next++] = ((long)c._startNodeId << 32) | (uint)c._endNodeId;
        }
        var result = new Prepared { Ids = ids, Road = road, Input = input, Source = initial, PairCount = pairs.Length,
            Graph = InitialNavGraph.Create(InitialNavData.Create(initial, ids.NumberOfNodes), initial, pairs) };
        Plans.Add(result); return result;
    }
    internal static bool Eligible(Prepared plan, NavMeshSource source)
    {
        if (!ReferenceEquals(plan.Ids, source._nodeIdService) || plan.Road != (source is RoadNavMeshSource) || source._nodes.Length != plan.Ids.NumberOfNodes) return false;
        foreach (var n in plan.Source.Nodes)
        {
            if (source._nodes[n.Id] != null) return false;
            if (source._navMeshGraph is TerrainNavMeshGraph t)
            {
                if (!ReferenceEquals(t._allNeighbors[n.Id], TerrainNavMeshGraph.EmptyAllNeighbors) || !ReferenceEquals(t._cheapNeighbors[n.Id], TerrainNavMeshGraph.EmptyCheapNeighbors)) return false;
            }
            else if (source._navMeshGraph is RoadNavMeshGraph r)
            { if (!ReferenceEquals(r._neighbors[n.Id], RoadNavMeshGraph.EmptyNeighbors)) return false; }
            else return false;
        }
        return true;
    }
    internal static void Apply(Prepared plan, NavMeshSource source, NavMeshUpdate.Builder builder)
    {
        if (!InitialNavSource.TryApply(plan.Source, source, builder, plan.Road) || !InitialNavGraph.TryApply(plan.Graph, source._navMeshGraph))
            throw new InvalidOperationException("Initial navigation target changed after eligibility check");
    }
}
