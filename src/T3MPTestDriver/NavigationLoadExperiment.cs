using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Explicit test switches enable disposable post-load replays or experimental
// live queue draining. Replay timing is not total load speedup.
internal static class NavigationLoadExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Job
    {
        internal string Name = "";
        internal bool Road;
        internal NavMeshChange[] Changes = null!;
        internal NodeIdService Ids = null!;
        internal ConnectionPlan? Plan;
    }
    private sealed class Result
    {
        internal NavMeshSource Source = null!;
        internal INavMeshGraph Graph = null!;
        internal NavMeshUpdate.Builder Builder = null!;
        internal long Touches, Unique;
        internal bool InitialBuilt;
        internal bool InitialGraphBuilt;
        internal NavigationPackedSource.State? Packed;
    }
    private sealed class Batch
    {
        internal NavMeshSource Source = null!;
    }
    private sealed class ConnectionPlan { internal long[] Pairs = null!; internal int Touches; internal NavigationInitialBuilder.Plan? Initial; internal NavigationPackedSource.Data? Packed; internal Lazy<NavigationInitialGraph.Plan>? Graph; }
    private static bool _initialBuild, _packedSource, _initialGraph;
    private static readonly List<Job> LivePlans = new List<Job>();
    private sealed class PairComparer : IEqualityComparer<long>
    {
        public bool Equals(long x, long y) => x == y;
        // long.GetHashCode XORs the two node ids: adjacent grid pairs collide
        // heavily. Mix the packed pair before reducing it to 32 bits.
        public int GetHashCode(long value)
        {
            unchecked
            {
                var x = (ulong)value;
                x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL;
                x ^= x >> 27; x *= 0x94d049bb133111ebUL;
                return (int)(x ^ (x >> 31));
            }
        }
    }
    [ThreadStatic] private static Batch? _batch;
    [ThreadStatic] private static bool _loading;
    private static Job[]? _jobs;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        var main = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Loading.InitialNavigationLoad")).FirstOrDefault(t => t != null);
        if (main != null && (bool)(main.GetProperty("Installed", All)?.GetValue(null) ?? false)) return;
        var args = Environment.GetCommandLineArgs();
        _initialGraph = args.Contains("-t3mpTestNavInitialGraph");
        _packedSource = args.Contains("-t3mpTestNavPackedSource") || _initialGraph;
        _initialBuild = args.Contains("-t3mpTestNavInitialBuild") || _packedSource;
        if (!args.Contains("-t3mpTestNavReplay") && !args.Contains("-t3mpTestNavLive")) return;
        if (_packedSource) NavigationPackedSource.Install();
        if (_initialGraph) Debug.Log("[T3MPNAVGRAPH] installed; shared immutable decisions, independent exact-capacity native lists");
        var ht = Find("HarmonyLib.Harmony");
        var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.nav-replay");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        void Prefix(Type type, string name, string hook) => patch.Invoke(harmony, new object?[] {
            type.GetMethod(name, All), Activator.CreateInstance(hm, typeof(NavigationLoadExperiment).GetMethod(hook, All)), null, null, null });
        if (args.Contains("-t3mpTestNavReplay")) Prefix(typeof(NavigationSynchronizer), "PostLoad", nameof(Capture));
        Prefix(typeof(NavMeshSource), "UpdateConnectionBetweenNodes", nameof(Defer));
        if (args.Contains("-t3mpTestNavLive"))
        {
            Prefix(typeof(NavigationSynchronizer), "PostLoad", nameof(BeginLoad));
            patch.Invoke(harmony, new object?[] { typeof(NavigationSynchronizer).GetMethod("PostLoad", All), null, null, null,
                Activator.CreateInstance(hm, typeof(NavigationLoadExperiment).GetMethod(nameof(EndLoad), All)) });
            Prefix(typeof(NavMeshUpdater), "ProcessInstantChanges", nameof(Instant));
            Prefix(typeof(NavMeshUpdater), "ProcessRegularChanges", nameof(Regular));
        }
    }

    private static void BeginLoad() { LivePlans.Clear(); _loading = true; }
    private static void EndLoad()
    {
        _loading = false;
        if (_initialGraph)
        {
            var plans = LivePlans.Select(j => j.Plan).Distinct().Where(p => p?.Graph?.IsValueCreated == true).ToArray();
            Debug.Log($"[T3MPNAVGRAPH] phase=end evaluatedPlans={plans.Length} pairEvaluations={plans.Sum(p => (long)p!.Pairs.Length)}");
        }
        LivePlans.Clear();
    }
    private static bool Instant(NavMeshUpdater __instance, NavMeshUpdate.Builder navMeshUpdateBuilder) =>
        Live(__instance, navMeshUpdateBuilder, true);
    private static bool Regular(NavMeshUpdater __instance, NavMeshUpdate.Builder navMeshUpdateBuilder) =>
        Live(__instance, navMeshUpdateBuilder, false);

    private static bool Live(NavMeshUpdater u, NavMeshUpdate.Builder builder, bool instant)
    {
        if (!_loading) return true;
        var terrain = instant ? u._enqueuedInstantTerrainChanges : u._enqueuedRegularTerrainChanges;
        var road = instant ? u._enqueuedInstantRoadChanges : u._enqueuedRegularRoadChanges;
        if (terrain.Count == 0) return true;
        var clock = Stopwatch.StartNew();
        var tc = terrain.ToArray(); var rc = road.ToArray();
        if (tc.Concat(rc).Any(c => c._startNodeId == c._endNodeId)) return true;
        var sources = instant ? new NavMeshSource[] { u._instantTerrainNavMeshSource, u._previewTerrainNavMeshSource, u._instantRoadNavMeshSource, u._previewRoadNavMeshSource }
            : new NavMeshSource[] { u._terrainNavMeshSource, u._roadNavMeshSource };
        var jobs = sources.Select(s => new Job { Name = s.GetType().Name, Ids = s._nodeIdService, Road = s is RoadNavMeshSource,
            Changes = s is RoadNavMeshSource ? rc : tc }).ToArray();
        var validate = Environment.GetCommandLineArgs().Contains("-t3mpTestNavLiveValidate");
        if (validate && sources.Any(s => s._nodes.Any(n => n != null))) throw new Exception("Live comparison requires initial empty sources");
        var results = new Result[sources.Length];
        foreach (var job in jobs)
        {
            var previous = LivePlans.FirstOrDefault(p => SameInput(p, job));
            job.Plan = previous?.Plan ?? BuildPlan(job.Changes, job.Ids.NumberOfNodes);
            LivePlans.Add(job);
        }
        Parallel.For(0, sources.Length, new ParallelOptions { MaxDegreeOfParallelism = sources.Length }, i =>
            results[i] = ReplayInto(jobs[i], new Result { Source = sources[i], Graph = sources[i]._navMeshGraph,
                Builder = new NavMeshUpdate.Builder(jobs[i].Ids) }, true));
        // The original instant route inserts each terrain/road id twice. The
        // builder removes duplicates, so merge one copy in the original order.
        foreach (var id in results[0].Builder._terrainNodeIds) builder.AddTerrainNode(id);
        foreach (var id in results[instant ? 2 : 1].Builder._roadNodeIds) builder.AddRoadNode(id);
        terrain.Clear(); road.Clear();
        clock.Stop();
        Debug.Log(FormattableString.Invariant($"[T3MPNAVLIVE] phase={(instant ? "instant" : "regular")} ms={clock.Elapsed.TotalMilliseconds:F3} changes={tc.Length + rc.Length} graphs={sources.Length} touches={results.Sum(r => r.Touches)} unique={results.Sum(r => r.Unique)} initialBuilt={results.Count(r => r.InitialBuilt)} packedSources={results.Count(r => r.Packed != null)}"));
        if (_initialGraph) Debug.Log($"[T3MPNAVGRAPH] phase={(instant ? "instant" : "regular")} built={results.Count(r => r.InitialGraphBuilt)} graphs={results.Length} plans={jobs.Select(j => j.Plan).Distinct().Count()} connections={jobs.Sum(j => j.Plan!.Graph?.IsValueCreated == true ? j.Plan.Graph.Value.ConnectionCount : 0)}");
        if (validate)
        {
            for (var i = 0; i < jobs.Length; i++)
            {
                var reference = Replay(jobs[i], false);
                if (Hash(reference) != Hash(results[i])) throw new Exception("Live graph differs: " + jobs[i].Name);
                Debug.Log("[T3MPNAVLIVE] VALIDATE PASS " + jobs[i].Name);
            }
        }
        return false;
    }

    private static void Capture(NavigationSynchronizer __instance)
    {
        if (_jobs != null) return;
        var u = __instance._navMeshUpdater;
        var it = u._enqueuedInstantTerrainChanges.ToArray();
        var ir = u._enqueuedInstantRoadChanges.ToArray();
        Job Make(string name, NavMeshSource source, NavMeshChange[] changes, bool road)
        {
            if (source._nodes.Any(n => n != null)) throw new Exception("Replay requires initially empty source: " + name);
            return new Job { Name = name, Ids = source._nodeIdService, Road = road, Changes = changes };
        }
        _jobs = new[] {
            Make("regular-terrain", u._terrainNavMeshSource, u._enqueuedRegularTerrainChanges.ToArray(), false),
            Make("instant-terrain", u._instantTerrainNavMeshSource, it, false),
            Make("preview-terrain", u._previewTerrainNavMeshSource, u._enqueuedPreviewTerrainChanges.Concat(it).ToArray(), false),
            Make("regular-road", u._roadNavMeshSource, u._enqueuedRegularRoadChanges.ToArray(), true),
            Make("instant-road", u._instantRoadNavMeshSource, ir, true),
            Make("preview-road", u._previewRoadNavMeshSource, u._enqueuedPreviewRoadChanges.Concat(ir).ToArray(), true)
        };
        foreach (var j in _jobs) Debug.Log($"[T3MPNAVREPLAY] input={j.Name} changes={j.Changes.Length} nodes={j.Ids.NumberOfNodes}");
    }

    private static bool Defer(NavMeshSource __instance, int aNodeId, int bNodeId)
    {
        var batch = _batch;
        return batch == null || !ReferenceEquals(batch.Source, __instance);
    }

    private static ConnectionPlan BuildPlan(NavMeshChange[] changes, int nodeCount)
    {
        var last = new Dictionary<long, int>(new PairComparer());
        var touches = 0;
        long Key(NavMeshChange c) => ((long)Math.Min(c._startNodeId, c._endNodeId) << 32) | (uint)Math.Max(c._startNodeId, c._endNodeId);
        for (var i = 0; i < changes.Length; i++)
            if (changes[i]._changeType != NavMeshChangeType.None) { last[Key(changes[i])] = i; touches++; }
        var pairs = new long[last.Count]; var index = 0;
        // A second linear pass preserves last-touch order without allocating
        // sort keys/items. The immutable plan is shared by identical inputs.
        for (var i = 0; i < changes.Length; i++)
        {
            var c = changes[i];
            if (c._changeType != NavMeshChangeType.None && last[Key(c)] == i)
                pairs[index++] = ((long)c._startNodeId << 32) | (uint)c._endNodeId;
        }
        var initial = _initialBuild ? NavigationInitialBuilder.Create(changes, nodeCount) : null;
        var result = new ConnectionPlan { Pairs = pairs, Touches = touches, Initial = initial,
            Packed = _packedSource && initial != null ? NavigationPackedSource.Create(initial, nodeCount) : null };
        if (_initialGraph && result.Packed != null)
            result.Graph = new Lazy<NavigationInitialGraph.Plan>(() => NavigationInitialGraph.Create(result.Packed, initial!, pairs), true);
        return result;
    }

    private static Result Create(Job job)
    {
        var r = new Result { Builder = new NavMeshUpdate.Builder(job.Ids) };
        if (job.Road)
        {
            var graph = new RoadNavMeshGraph(job.Ids); graph.Load();
            r.Graph = graph; r.Source = new RoadNavMeshSource(job.Ids, graph);
        }
        else
        {
            var graph = new TerrainNavMeshGraph(job.Ids, new NavMeshGroupService()); graph.Load();
            r.Graph = graph; r.Source = new TerrainNavMeshSource(job.Ids, graph);
        }
        r.Source.Load();
        return r;
    }

    private static Result Replay(Job job, bool bulk)
    {
        return ReplayInto(job, Create(job), bulk);
    }

    private static Result ReplayInto(Job job, Result r, bool bulk)
    {
        if (bulk && job.Plan == null) job.Plan = BuildPlan(job.Changes, job.Ids.NumberOfNodes);
        var batch = bulk ? new Batch { Source = r.Source } : null;
        _batch = batch;
        try
        {
            r.InitialBuilt = bulk && (NavigationPackedSource.TryAttach(job.Plan!.Packed, job.Plan.Initial, r.Source, r.Builder, job.Road) ||
                (NavigationPackedSource.Get(r.Source) == null && NavigationInitialBuilder.TryApply(job.Plan.Initial, r.Source, r.Builder, job.Road)));
            if (!r.InitialBuilt)
                foreach (var change in job.Changes)
                    if (job.Road) change.Apply((RoadNavMeshSource)r.Source, r.Builder);
                    else change.Apply((TerrainNavMeshSource)r.Source, r.Builder);
        }
        finally { _batch = null; }
        r.Packed = NavigationPackedSource.Get(r.Source);
        if (batch != null)
        {
            r.InitialGraphBuilt = r.InitialBuilt && job.Plan!.Graph != null &&
                NavigationInitialGraph.TryApply(job.Plan.Graph.Value, r.Graph);
            var scratch = new HashSet<int>();
            // Final adjacency order equals ascending order of the pair's last
            // Connect/Disconnect. Intermediate source edge/blockage operations
            // are retained in exactly the input order.
            if (!r.InitialGraphBuilt) foreach (var pair in job.Plan!.Pairs)
            {
                var a = (int)(pair >> 32); var b = (int)pair;
                if (Connected(r, a, b, scratch, out var ag, out var ac) &&
                    Connected(r, b, a, scratch, out var bg, out var bc) && ag == bg)
                    r.Graph.ConnectNodes(a, b, ag, Math.Max(ac, bc));
                else r.Graph.DisconnectNodes(a, b);
            }
            r.Touches = job.Plan!.Touches; r.Unique = job.Plan.Pairs.Length;
        }
        return r;
    }

    private static Result Clone(Job job, Result original)
    {
        var r = Create(job);
        for (var i = 0; i < r.Source._nodes.Length; i++)
        {
            var node = original.Source._nodes[i];
            if (node != null)
            {
                var copy = new NavMeshSourceNode();
                if (!ReferenceEquals(node._edges, NavMeshSourceNode.EmptyEdges)) copy._edges = new List<NavMeshNode>(node._edges);
                if (!ReferenceEquals(node._blockages, NavMeshSourceNode.EmptyBlockages)) copy._blockages = new List<int>(node._blockages);
                r.Source._nodes[i] = copy;
            }
            if (original.Graph is TerrainNavMeshGraph t)
            {
                var target = (TerrainNavMeshGraph)r.Graph;
                if (!ReferenceEquals(t._allNeighbors[i], TerrainNavMeshGraph.EmptyAllNeighbors)) target._allNeighbors[i] = new List<NavMeshNode>(t._allNeighbors[i]);
                if (!ReferenceEquals(t._cheapNeighbors[i], TerrainNavMeshGraph.EmptyCheapNeighbors)) target._cheapNeighbors[i] = new List<int>(t._cheapNeighbors[i]);
            }
            else
            {
                var g = (RoadNavMeshGraph)original.Graph;
                if (!ReferenceEquals(g._neighbors[i], RoadNavMeshGraph.EmptyNeighbors)) ((RoadNavMeshGraph)r.Graph)._neighbors[i] = new List<NavMeshNode>(g._neighbors[i]);
            }
        }
        foreach (var id in original.Builder._terrainNodeIds) r.Builder.AddTerrainNode(id);
        foreach (var id in original.Builder._roadNodeIds) r.Builder.AddRoadNode(id);
        return r;
    }

    private static bool SameInput(Job a, Job b)
    {
        if (a.Road != b.Road || a.Changes.Length != b.Changes.Length || !ReferenceEquals(a.Ids, b.Ids)) return false;
        for (var i = 0; i < a.Changes.Length; i++)
        {
            var x = a.Changes[i]; var y = b.Changes[i];
            if (x._changeType != y._changeType || x._startNodeId != y._startNodeId || x._endNodeId != y._endNodeId ||
                x._groupId != y._groupId || BitConverter.SingleToInt32Bits(x._cost) != BitConverter.SingleToInt32Bits(y._cost)) return false;
        }
        return true;
    }

    private static bool Connected(NavMeshSourceNode node, int id, HashSet<int> scratch, out int group, out float cost)
    {
        scratch.Clear(); foreach (var blocked in node._blockages) scratch.Add(blocked);
        cost = float.MaxValue; group = 0; var found = false;
        foreach (var edge in node._edges)
            if (edge.Id == id && edge.Cost < cost && !scratch.Remove(unchecked(edge.Id * 397) ^ edge.GroupId))
            { found = true; cost = edge.Cost; group = edge.GroupId; }
        return found;
    }

    private static bool Connected(Result result, int from, int to, HashSet<int> scratch, out int group, out float cost)
    {
        var packed = result.Packed;
        if (packed != null && packed.Remaining[from]) return packed.Connected(from, to, scratch, out group, out cost);
        var node = result.Source._nodes[from];
        if (node != null) return Connected(node, to, scratch, out group, out cost);
        group = 0; cost = float.MaxValue; return false;
    }

    private static string Hash(Result result)
    {
        var packedState = NavigationPackedSource.Get(result.Source);
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        void Edges(IEnumerable<NavMeshNode> values) { foreach (var n in values) { writer.Write(n.Id); writer.Write(n.GroupId); writer.Write(n.Cost); } }
        for (var i = 0; i < result.Source._nodes.Length; i++)
        {
            var node = result.Source._nodes[i];
            var packed = packedState != null && packedState.Remaining[i];
            writer.Write(packed || node != null);
            if (packed) packedState!.WriteNode(writer, i);
            else if (node != null)
            {
                writer.Write(node._edges.Count); Edges(node._edges);
                writer.Write(node._blockages.Count); foreach (var b in node._blockages) writer.Write(b);
            }
            if (result.Graph is TerrainNavMeshGraph t)
            {
                writer.Write(t._allNeighbors[i].Count); Edges(t._allNeighbors[i]);
                writer.Write(t._cheapNeighbors[i].Count); foreach (var n in t._cheapNeighbors[i]) writer.Write(n);
            }
            else { var g = (RoadNavMeshGraph)result.Graph; writer.Write(g._neighbors[i].Count); Edges(g._neighbors[i]); }
        }
        writer.Write(result.Builder._terrainNodeIds.Count);
        foreach (var id in result.Builder._terrainNodeIds) writer.Write(id);
        writer.Write(result.Builder._roadNodeIds.Count);
        foreach (var id in result.Builder._roadNodeIds) writer.Write(id);
        writer.Flush(); data.Position = 0;
        using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
    }

    internal static void Run()
    {
        if (Environment.GetCommandLineArgs().Contains("-t3mpTestNavLiveValidate")) ValidateTransitions();
        if (_jobs == null) return;
        string[]? expected = null;
        foreach (var workers in new[] { 0, 1, 2, 6, -1, -2, 0 })
        {
            GC.Collect();
            var gc = GC.CollectionCount(0);
            var results = new Result[_jobs.Length];
            var timer = Stopwatch.StartNew();
            if (workers > 0)
            {
                for (var i = 0; i < _jobs.Length; i++)
                {
                    var previous = Enumerable.Range(0, i).Where(j => SameInput(_jobs[i], _jobs[j])).DefaultIfEmpty(-1).First();
                    _jobs[i].Plan = previous >= 0 ? _jobs[previous].Plan : BuildPlan(_jobs[i].Changes, _jobs[i].Ids.NumberOfNodes);
                }
            }
            if (workers < 0)
            {
                var representatives = Enumerable.Range(0, _jobs.Length).Select(i => Enumerable.Range(0, i + 1).First(j => SameInput(_jobs[i], _jobs[j]))).ToArray();
                // Baseline replay uses a global scratch HashSet, so representative
                // builds remain serial. Only independently owned copies run in parallel.
                for (var i = 0; i < _jobs.Length; i++) if (representatives[i] == i) results[i] = Replay(_jobs[i], false);
                if (workers == -1) for (var i = 0; i < _jobs.Length; i++) { if (representatives[i] != i) results[i] = Clone(_jobs[i], results[representatives[i]]); }
                else Parallel.For(0, _jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => { if (representatives[i] != i) results[i] = Clone(_jobs[i], results[representatives[i]]); });
            }
            else if (workers <= 1) for (var i = 0; i < _jobs.Length; i++) results[i] = Replay(_jobs[i], workers != 0);
            else Parallel.For(0, _jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, i => results[i] = Replay(_jobs[i], true));
            timer.Stop();
            var collections = GC.CollectionCount(0) - gc;
            var hashes = results.Select(Hash).ToArray();
            if (expected == null) expected = hashes;
            if (!expected.SequenceEqual(hashes)) throw new Exception("Navigation replay differs for workers=" + workers);
            Debug.Log(FormattableString.Invariant($"[T3MPNAVREPLAY] workers={workers} bulk={workers > 0} copy={workers < 0} ms={timer.Elapsed.TotalMilliseconds:F3} gc={collections} touches={results.Sum(r => r.Touches)} unique={results.Sum(r => r.Unique)} PASS hashes={string.Join(",", hashes)}"));
        }
        _jobs = null;
    }

    private sealed class SmallMap : INavMeshSizeProvider { public UnityEngine.Vector3Int Size => new UnityEngine.Vector3Int(4, 4, 4); }

    private static void ValidateTransitions()
    {
        var ids = new NodeIdService(new SmallMap()); ids.Load();
        var random = new Random(9137);
        if (_initialBuild)
        {
            for (var test = 0; test < 32; test++)
            {
                var changes = new List<NavMeshChange>();
                for (var i = 0; i < 512; i++)
                {
                    var a = random.Next(1, 10); var b = random.Next(1, 10);
                    if (a == b) b = a % 9 + 1;
                    var op = i % 13 == 0 ? NavMeshChangeType.None :
                        (random.Next(2) == 0 ? NavMeshChangeType.AddEdge : NavMeshChangeType.BlockEdge);
                    changes.Add(new NavMeshChange(op, a, b, random.Next(3), random.Next(4) / 2f));
                }
                var job = new Job { Name = "initial", Road = test % 2 == 0, Ids = ids, Changes = changes.ToArray() };
                var reference = Replay(job, false); var optimized = Replay(job, true);
                if (!optimized.InitialBuilt || Hash(reference) != Hash(optimized))
                    throw new Exception("Initial navigation build differs: " + test);
                var copy = Replay(job, true);
                if (_initialGraph)
                {
                    if (!optimized.InitialGraphBuilt || !copy.InitialGraphBuilt)
                        throw new Exception("Initial graph experiment did not engage");
                    for (var i = 0; i < ids.NumberOfNodes; i++)
                    {
                        if (optimized.Graph is TerrainNavMeshGraph gt)
                        {
                            var ct = (TerrainNavMeshGraph)copy.Graph;
                            CheckLists(gt._allNeighbors[i], ct._allNeighbors[i]);
                            CheckLists(gt._cheapNeighbors[i], ct._cheapNeighbors[i]);
                        }
                        else CheckLists(((RoadNavMeshGraph)optimized.Graph)._neighbors[i], ((RoadNavMeshGraph)copy.Graph)._neighbors[i]);
                    }
                    // Source resets do not reset graphs. A non-empty graph must
                    // reject direct initial filling without changing any row.
                    var before = Hash(optimized);
                    if (job.Plan!.Graph!.Value.ConnectionCount > 0 &&
                        (NavigationInitialGraph.TryApply(job.Plan.Graph.Value, optimized.Graph) || Hash(optimized) != before))
                        throw new Exception("Populated graph guard changed state");
                }
                for (var i = 0; i < ids.NumberOfNodes; i++)
                {
                    var a = optimized.Source._nodes[i]; var b = copy.Source._nodes[i];
                    if (a == null) continue;
                    if (ReferenceEquals(a, b) || (a._edges.Count != 0 && ReferenceEquals(a._edges, b._edges)) ||
                        (a._blockages.Count != 0 && ReferenceEquals(a._blockages, b._blockages)))
                        throw new Exception("Initial sources share mutable storage");
                }
                if (_packedSource) ValidatePackedMutations(job, reference, optimized, copy, random, test);
                ReplayInto(job, reference, false); ReplayInto(job, optimized, true);
                if (optimized.InitialBuilt || Hash(reference) != Hash(optimized))
                    throw new Exception("Initial navigation non-empty fallback differs: " + test);
            }
            Debug.Log("[T3MPNAVLIVE] INITIAL TRANSITIONS PASS cases=64 (append, duplicate blocks, groups, None, independent storage, populated fallback)");
            if (_initialGraph) Debug.Log("[T3MPNAVGRAPH] VALIDATE PASS cases=32 (ordered rows, exact capacity, independent mutable lists, populated graph rejection, native runtime mutations)");
        }
        for (var test = 0; test < 32; test++)
        {
            var edges = new List<(int A, int B, int Group, float Cost)>();
            var blocks = new List<(int A, int B, int Group)>();
            var changes = new List<NavMeshChange>();
            for (var i = 0; i < 512; i++)
            {
                var op = random.Next(4);
                var a = random.Next(1, 10); var b = random.Next(1, 10); var group = random.Next(3);
                if (a == b) b = a % 9 + 1;
                var cost = random.Next(4) / 2f;
                if (op == 0 || (op == 1 && edges.Count == 0))
                { edges.Add((a, b, group, cost)); changes.Add(new NavMeshChange(NavMeshChangeType.AddEdge, a, b, group, cost)); }
                else if (op == 1)
                { var n = random.Next(edges.Count); var e = edges[n]; edges.RemoveAt(n); changes.Add(new NavMeshChange(NavMeshChangeType.RemoveEdge, e.A, e.B, e.Group, e.Cost)); }
                else if (op == 2 || blocks.Count == 0)
                { blocks.Add((a, b, group)); changes.Add(new NavMeshChange(NavMeshChangeType.BlockEdge, a, b, group, 0)); }
                else
                { var n = random.Next(blocks.Count); var e = blocks[n]; blocks.RemoveAt(n); changes.Add(new NavMeshChange(NavMeshChangeType.UnblockEdge, e.A, e.B, e.Group, 0)); }
            }
            var job = new Job { Name = "transitions", Road = test % 2 == 0, Ids = ids, Changes = changes.ToArray() };
            var reference = Replay(job, false); var optimized = Replay(job, true);
            if (Hash(reference) != Hash(optimized)) throw new Exception("Random navigation transitions differ: " + test);
            // Continue into non-empty graphs as happens with preview updates.
            ReplayInto(job, reference, false); ReplayInto(job, optimized, true);
            if (Hash(reference) != Hash(optimized)) throw new Exception("Non-empty navigation transitions differ: " + test);
        }
        Debug.Log("[T3MPNAVLIVE] TRANSITIONS PASS cases=64 (duplicates, groups, block/unblock, removal, non-empty graphs)");
        if (_packedSource) ValidatePackedEdgeCases(ids);

        void CheckLists<T>(List<T> a, List<T> b)
        {
            if (a.Count != 0 && (ReferenceEquals(a, b) || a.Capacity != a.Count || b.Capacity != b.Count))
                throw new Exception("Initial graph list ownership/capacity differs");
        }
    }

    private static void ValidatePackedMutations(Job job, Result reference, Result optimized, Result copy, Random random, int test)
    {
        if (optimized.Packed == null || copy.Packed == null ||
            !ReferenceEquals(optimized.Packed.Data, copy.Packed.Data) ||
            ReferenceEquals(optimized.Packed.Remaining, copy.Packed.Remaining) ||
            ReferenceEquals(optimized.Source._nodes, copy.Source._nodes))
            throw new Exception("Packed source ownership differs");
        var unchanged = Hash(copy);
        var initialEdges = job.Changes.Where(c => c._changeType == NavMeshChangeType.AddEdge).ToArray();
        var initialBlocks = job.Changes.Where(c => c._changeType == NavMeshChangeType.BlockEdge).ToArray();
        for (var step = 0; step < 96; step++)
        {
            var a = random.Next(1, 15); var b = random.Next(1, 15);
            var kind = (NavMeshChangeType)new[] { (int)NavMeshChangeType.AddEdge, (int)NavMeshChangeType.RemoveEdge,
                (int)NavMeshChangeType.BlockEdge, (int)NavMeshChangeType.UnblockEdge }[step % 4];
            var group = random.Next(4); var cost = random.Next(5) / 2f;
            if (kind == NavMeshChangeType.RemoveEdge && initialEdges.Length > 0)
            { var e = initialEdges[random.Next(initialEdges.Length)]; a = e._startNodeId; b = e._endNodeId; group = e._groupId; cost = e._cost; }
            if (kind == NavMeshChangeType.UnblockEdge && initialBlocks.Length > 0 && step % 8 != 7)
            { var e = initialBlocks[random.Next(initialBlocks.Length)]; a = e._startNodeId; b = e._endNodeId; group = e._groupId; }
            CompareRuntimeChange(job, reference, optimized, new NavMeshChange(kind, a, b, group, cost), "random " + test + "/" + step);
            if (Hash(copy) != unchanged) throw new Exception("Runtime mutation changed another packed source");
        }
        Debug.Log($"[T3MPNAVPACKED] MUTATIONS PASS case={test} steps=96 independentSource=True");
    }

    private static void CompareRuntimeChange(Job job, Result reference, Result optimized, NavMeshChange change, string label)
    {
        string Apply(Result r)
        {
            try
            {
                if (job.Road) change.Apply((RoadNavMeshSource)r.Source, r.Builder);
                else change.Apply((TerrainNavMeshSource)r.Source, r.Builder);
                return "ok";
            }
            catch (Exception e) { return e.GetType().FullName + "|" + e.Message; }
        }
        var expected = Apply(reference); var actual = Apply(optimized);
        if (expected != actual || Hash(reference) != Hash(optimized))
            throw new Exception("Packed runtime change differs: " + label + ": " + expected + " / " + actual);
    }

    private static void ValidatePackedEdgeCases(NodeIdService ids)
    {
        for (var road = 0; road < 2; road++)
        {
            var changes = new[] {
                new NavMeshChange(NavMeshChangeType.AddEdge, 1, 2, 0, 1f),
                new NavMeshChange(NavMeshChangeType.AddEdge, 1, 2, 0, 1f),
                new NavMeshChange(NavMeshChangeType.AddEdge, 2, 1, 0, 1f),
                new NavMeshChange(NavMeshChangeType.AddEdge, 1, 2, 1, float.NaN),
                new NavMeshChange(NavMeshChangeType.AddEdge, 1, 2, 1, float.PositiveInfinity),
                new NavMeshChange(NavMeshChangeType.AddEdge, 1, 2, 1, -0f),
                new NavMeshChange(NavMeshChangeType.BlockEdge, 1, 2, 0, 0),
                new NavMeshChange(NavMeshChangeType.BlockEdge, 1, 2, 0, 0),
                // Same packed blockage key as destination 2 / group 0.
                new NavMeshChange(NavMeshChangeType.BlockEdge, 1, 3, (3 * 397) ^ (2 * 397), 0),
                new NavMeshChange(NavMeshChangeType.AddEdge, 4, 5, 0, float.NegativeInfinity),
                new NavMeshChange(NavMeshChangeType.AddEdge, 5, 4, 0, float.NegativeInfinity)
            };
            var job = new Job { Name = "edge-cases", Road = road != 0, Ids = ids, Changes = changes };
            var reference = Replay(job, false); var optimized = Replay(job, true);
            if (!optimized.InitialBuilt || Hash(reference) != Hash(optimized)) throw new Exception("Packed special initial state differs");
            var runtime = new[] {
                new NavMeshChange(NavMeshChangeType.RemoveEdge, 1, 2, 0, 1f),
                new NavMeshChange(NavMeshChangeType.RemoveEdge, 1, 2, 1, float.NaN),
                new NavMeshChange(NavMeshChangeType.UnblockEdge, 1, 2, 0, 0),
                new NavMeshChange(NavMeshChangeType.UnblockEdge, 1, 2, 0, 0),
                new NavMeshChange(NavMeshChangeType.UnblockEdge, 1, 2, 0, 0),
                new NavMeshChange(NavMeshChangeType.UnblockEdge, 1, 2, 0, 0),
                new NavMeshChange(NavMeshChangeType.RemoveEdge, 4, 5, 0, float.NegativeInfinity),
                new NavMeshChange(NavMeshChangeType.RemoveEdge, 5, 4, 0, float.NegativeInfinity),
                new NavMeshChange(NavMeshChangeType.UnblockEdge, 10, 11, 17, 0),
                new NavMeshChange(NavMeshChangeType.AddEdge, 1, 1, 0, 0),
                new NavMeshChange(NavMeshChangeType.RemoveEdge, 1, 1, 0, 0),
                new NavMeshChange(NavMeshChangeType.AddEdge, -1, 2, 0, 0),
                new NavMeshChange(NavMeshChangeType.AddEdge, 7, ids.NumberOfNodes, 0, 0)
            };
            for (var step = 0; step < runtime.Length; step++) CompareRuntimeChange(job, reference, optimized, runtime[step], "special " + road + "/" + step);
            reference.Source.Load(); optimized.Source.Load();
            if (NavigationPackedSource.Get(optimized.Source) != null || Hash(reference) != Hash(optimized))
                throw new Exception("Packed source reset differs");
        }
        Debug.Log("[T3MPNAVPACKED] SPECIAL PASS cases=2 transitions=26 (duplicates, hash collisions, NaN/infinities, signed zero, self edges, exceptions, endpoint cleanup, reset)");
    }
}
