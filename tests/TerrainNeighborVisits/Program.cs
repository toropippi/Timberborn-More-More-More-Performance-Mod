using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using T3MP.Runtime;
using Timberborn.Navigation;

internal static class Program
{
    private const string Owner = "t3mp.runtime.terrain-visits";
    private static readonly Harmony Foreign = new("terrain.test.foreign");
    private static int _calls, _checks;
    private static bool _sawNative;
    private static HashSet<int>? _watchedSet;
    private static MethodInfo Method(Type t, string name) => t.GetMethod(name, RuntimePatches.All)!;
    private static MethodInfo Visit => Method(typeof(TerrainReachabilityService), "VisitNode");
    private static MethodInfo Neighbors => Method(typeof(TerrainReachabilityService), "VisitNeighbors");
    private static void Check(bool ok, string text) { _checks++; if (!ok) throw new Exception(text); }
    private static void Install() => TerrainNeighborVisits.Install(typeof(Harmony), typeof(HarmonyMethod),
        typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5));
    private static bool Fingerprint(ref bool __result) { __result = true; return false; }
    private static void Prefix() => _calls++;
    private static void ChangeAddResult(HashSet<int> __instance, ref bool __result)
    {
        if (ReferenceEquals(__instance, _watchedSet)) __result = false;
    }
    private static void ThrowContains(HashSet<int> __instance)
    {
        if (ReferenceEquals(__instance, _watchedSet)) throw new ApplicationException("foreign Contains failure");
    }
    private static void AddPrefix(MethodInfo m) => Foreign.Patch(m, prefix: new HarmonyMethod(Method(typeof(Program), nameof(Prefix))));
    private static void FailFinalPatch(MethodBase original) { if (Equals(original, Neighbors)) throw new Exception("Injected install failure"); }
    private static IEnumerable<CodeInstruction> Downstream(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        _sawNative = list.Any(i => Equals(i.operand, Visit));
        return new[] { new CodeInstruction(OpCodes.Nop) }.Concat(list);
    }
    private static string Snapshot(TerrainReachabilityService service, int start, int range)
    {
        var result = new List<int> { 88888 }; string? error = null;
        try { service.Search(start, range, result); } catch (Exception e) { error = e.GetType().FullName + ":" + e.Message; }
        return JsonSerializer.Serialize(new {
            result, visited = service._visitedNodes?.ToArray(),
            queue = service._nodesToVisit?.Select(n => (n.Id, BitConverter.SingleToInt32Bits(n.Distance))).Select(n => $"{n.Item1}:{n.Item2}").ToArray(), error
        });
    }
    private static List<string> Cases()
    {
        var output = new List<string>(); var random = new Random(14771);
        var service = new TerrainReachabilityService();
        var costs = new[] { 0f, -1f, 0.5f, 1f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
        for (var run = 0; run < 500; run++)
        {
            var count = random.Next(1, 70);
            service._terrainNavMeshGraph.Nodes = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, random.Next(8))
                .Select(_ => new NavMeshNode(random.Next(count), costs[random.Next(costs.Length)])).ToList()).ToArray();
            service.Restricted = Enumerable.Range(0, count).Select(_ => random.Next(5) == 0).ToArray();
            for (var j = 0; j < 10; j++) output.Add(Snapshot(service, random.Next(count), random.Next(-1, 8)));
        }
        foreach (var bad in new[] { -1, 5 })
        {
            service = Small(); service._terrainNavMeshGraph.Nodes[0].Add(new NavMeshNode(bad, 1));
            output.Add(Snapshot(service, 0, 5)); output.Add(Snapshot(service, 0, 0));
        }
        foreach (var throwAt in new[] { 0, 1, 4, 10 })
        {
            service = Small(); var comparer = new Tracer(throwAt); service._visitedNodes = new HashSet<int>(comparer);
            output.Add(Snapshot(service, 0, 5)); output.Add(string.Join(",", comparer.Trace));
        }
        // A graph callback changes the live set before the first neighbor.
        service = Small(); var replacement = new Tracer(0);
        service._terrainNavMeshGraph.OnRead = () => { service._terrainNavMeshGraph.OnRead = null; service._visitedNodes = new HashSet<int>(replacement); };
        output.Add(Snapshot(service, 0, 5)); output.Add(string.Join(",", replacement.Trace));
        return output;
    }
    private static TerrainReachabilityService Small() => new() {
        Restricted = new bool[2],
        _terrainNavMeshGraph = new TerrainNavMeshGraph { Nodes = new[] {
            new List<NavMeshNode> { new(1, 1), new(1, 1), new(0, 1) }, new List<NavMeshNode> { new(0, 1) }
        } }
    };
    private static void Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "plain";
        if (mode == "native-il") { NativeIl.Verify(args[1], args[2]); return; }
        if (mode == "native-execution") { NativeExecution.Verify(args[1], args[2]); return; }
        if (mode != "unknown-version") new Harmony("terrain.test.fingerprint").Patch(
            Method(typeof(RuntimePatches), nameof(RuntimePatches.ReviewedBody)), prefix: new HarmonyMethod(Method(typeof(Program), nameof(Fingerprint))));
        if (mode.StartsWith("semantic-", StringComparison.Ordinal))
        {
            void PatchCollection() {
                if (mode.Contains("-add-", StringComparison.Ordinal)) Foreign.Patch(Method(typeof(HashSet<int>), "Add"),
                    postfix: new HarmonyMethod(Method(typeof(Program), nameof(ChangeAddResult))));
                else Foreign.Patch(Method(typeof(HashSet<int>), "Contains"),
                    prefix: new HarmonyMethod(Method(typeof(Program), nameof(ThrowContains))));
            }
            PatchCollection();
            var service = Small(); _watchedSet = service._visitedNodes;
            var expected = Snapshot(service, 0, 5);
            if (mode.EndsWith("-after", StringComparison.Ordinal))
            {
                Foreign.UnpatchAll(Foreign.Id);
                Install(); Check(TerrainNeighborVisits.Active, "initial install rejected");
                PatchCollection();
            }
            else Install();
            service = Small(); _watchedSet = service._visitedNodes;
            Check(!TerrainNeighborVisits.Active, "collection patch not detected");
            Check(Snapshot(service, 0, 5) == expected, "foreign return/exception semantics differ");
            TerrainNeighborVisits.Revalidate();
            Check(!TerrainNeighborVisits.Installed, "collection conflict persisted after reload");
            Console.WriteLine($"PASS {mode}: {_checks} checks"); return;
        }
        if (mode == "feature-disabled" || mode == "runtime-baseline")
        {
            RuntimePatches.Install(); Check(!TerrainNeighborVisits.Installed, "test flag ignored");
        }
        else
        {
            var expected = Cases();
            if (mode == "foreign-before") AddPrefix(Visit);
            var collectionMethod = mode.Replace("before-", "").Replace("after-", "") switch {
                "contains" => Method(typeof(HashSet<int>), "Contains"),
                "add" => Method(typeof(HashSet<int>), "Add"),
                "comparer" => Method(typeof(HashSet<int>), "get_Comparer"),
                "default" => Method(typeof(EqualityComparer<int>), "get_Default"),
                _ => null
            };
            if (mode.StartsWith("before-", StringComparison.Ordinal)) AddPrefix(collectionMethod!);
            if (mode == "install-failure") new Harmony("terrain.test.failure").Patch(
                typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5),
                prefix: new HarmonyMethod(Method(typeof(Program), nameof(FailFinalPatch))));
            Install();
            if (mode is "foreign-before" or "unknown-version" or "install-failure" || mode.StartsWith("before-", StringComparison.Ordinal))
            {
                Check(!TerrainNeighborVisits.Installed, "unsafe install accepted");
                Check(Harmony.GetAllPatchedMethods().All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(Owner) != true), "partial hooks remained");
            }
            else
            {
                Check(TerrainNeighborVisits.Active, "inactive: " + string.Join(";", UnityEngine.Debug.Messages));
                if (mode.StartsWith("after-", StringComparison.Ordinal)) AddPrefix(collectionMethod!);
                if (mode == "late-visit") AddPrefix(Visit);
                if (mode == "late-distance") AddPrefix(Method(typeof(TerrainReachabilityService.NodeToVisit), "get_Distance"));
                if (mode == "late-cost") AddPrefix(Method(typeof(NavMeshNode), "get_Cost"));
                if (mode == "late-neighbors") AddPrefix(Neighbors);
                if (mode == "downstream")
                {
                    var hook = new HarmonyMethod(Method(typeof(Program), nameof(Downstream))) { after = new[] { Owner } };
                    Foreign.Patch(Neighbors, transpiler: hook);
                    Check(_sawNative, "downstream patch did not receive native body");
                }
                if (mode == "mid-call")
                {
                    var service = Small();
                    service._terrainNavMeshGraph.OnRead = () => { service._terrainNavMeshGraph.OnRead = null; AddPrefix(Visit); };
                    Snapshot(service, 0, 5); Check(_calls > 0, "helper patch during graph read was bypassed");
                }
                Check(Cases().SequenceEqual(expected), "ordered results, exception or remaining state differs");
                if (mode.StartsWith("late-", StringComparison.Ordinal)) Check(_calls > 0, "foreign hook was bypassed");
                if (mode != "plain") Check(!TerrainNeighborVisits.Active, "late patch did not invalidate optimization");
                TerrainNeighborVisits.Revalidate();
                Check(Cases().SequenceEqual(expected), "reload changed results");
                if (mode != "plain") Check(!TerrainNeighborVisits.Installed, "invalid hooks remained after reload");
            }
        }
        Console.WriteLine($"PASS {mode}: {_checks} checks; comparison suite covers 5014 snapshots/traces per run.");
    }
    private sealed class Tracer(int throwAt) : IEqualityComparer<int>
    {
        internal readonly List<string> Trace = new();
        private void Record(string s) { Trace.Add(s); if (throwAt > 0 && Trace.Count == throwAt) throw new ApplicationException("comparer"); }
        public bool Equals(int x, int y) { Record($"eq:{x}:{y}"); return x == y; }
        public int GetHashCode(int x) { Record($"hash:{x}"); return x % 2; }
    }
}
