using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using T3MP.Loading;
using Timberborn.Navigation;
using UnityEngine;

namespace T3MPTestDriver;

// Opt-in experiment, never compiled into Code.dll. Memoizes the native FIFO
// result at the wander caller; terrain checks and RNG remain in native code.
// C/R benchmarks install identical observer hooks and differ only in Enabled.
internal static class RoadReachabilityExperiment
{
    private const string Owner = "t3mp.test.road-cache";
    private const BindingFlags All = LoadPatchBridge.All;
    private const int MaxEntries = 4096, MaxNodes = 262144;
    private sealed class State
    {
        internal readonly Dictionary<(int Start, int Range), int[]> Results = new();
        internal int Nodes;
        internal void Clear() { Results.Clear(); Nodes = 0; }
    }
    private static ConditionalWeakTable<RoadNavMeshGraph, State> Graphs = new();
    private static bool _running, _syntheticActive;
    internal static bool Installed { get; private set; }
    internal static bool Enabled { get; private set; }
    private static bool Has(string flag) => Environment.GetCommandLineArgs().Contains(flag, StringComparer.OrdinalIgnoreCase);
    internal static readonly bool Validate = Has("-t3mpTestRoadCacheValidate");
    internal static readonly bool Synthetic = Has("-t3mpTestRoadCacheSynthetic");
    internal static readonly bool Requested = Has("-t3mpTestRoadCache") || Validate || Synthetic;
    private static long _calls, _hits, _misses, _invalidations, _compared, _evictions;

    internal static void Install()
    {
        if (!Requested || Installed) return;
        var mvid = typeof(RoadNavMeshGraph).Module.ModuleVersionId.ToString();
        if (mvid != "bbe52087-7a1c-4d96-b0d1-4bb2d536d63e" && mvid != "b848ebf8-28b6-4c17-ab4f-f91582085afa" &&
            mvid != "7ebefe1b-edc2-46a8-817c-3ab83e48b58f")
            throw new InvalidOperationException("Road experiment requires a reviewed Navigation module");
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony");
        var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, Owner)!;
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var caller = typeof(DistrictRandomDestinationPicker).GetMethod("GetRandomDestination", All, null, new[] { typeof(int) }, null)!;
        var protectedMethods = typeof(RoadReachabilityService).GetMethods(All | BindingFlags.DeclaredOnly)
            .Concat(typeof(RoadNavMeshGraph).GetMethods(All | BindingFlags.DeclaredOnly)).Append(caller).ToArray();
        if (!LoadCompatibility.Unmodified(protectedMethods, Owner))
            throw new InvalidOperationException("Road experiment cannot run with foreign road hooks");
        object Hook(string name, bool transpiler = false)
        {
            var method = typeof(RoadReachabilityExperiment).GetMethod(name, All)!;
            if (transpiler) method = LoadPatchBridge.Create(Owner, method);
            return Activator.CreateInstance(hm, method)!;
        }
        try
        {
            foreach (var name in new[] { "Load", "ConnectNodes", "DisconnectNodes" })
                patch.Invoke(harmony, new object?[] { typeof(RoadNavMeshGraph).GetMethod(name, All)!, Hook(nameof(Changing)), null, null, null });
            patch.Invoke(harmony, new object?[] { caller, null, null, Hook(nameof(Rewrite), true), null });
            var loadAll = LoadPatchBridge.Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All)!;
            patch.Invoke(harmony, new object?[] { loadAll, Hook(nameof(BeginWorld)), null, null, null });
            Enabled = !Has("-t3mpTestRoadCacheBaseline");
            Installed = true;
            Debug.Log("[T3MPTEST] Road cache experiment installed enabled=" + Enabled + " validate=" + Validate);
            if (Synthetic) SyntheticValidation();
        }
        catch (Exception exception)
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
            Installed = Enabled = false;
            if (Synthetic) Debug.LogError("[T3MPTEST] Road cache synthetic validation FAIL: " + exception);
            throw;
        }
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = instructions.ToList();
        var operand = typeof(T).GetField("operand", All)!;
        var opcode = typeof(T).GetField("opcode", All)!;
        var native = typeof(RoadReachabilityService).GetMethod("GetReachableNeighborsInRange", All)!;
        var calls = list.Where(i => Equals(operand.GetValue(i), native) &&
            ((OpCode)opcode.GetValue(i)! == OpCodes.Callvirt || (OpCode)opcode.GetValue(i)! == OpCodes.Call)).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Expected one native road query call");
        // Retain labels, exception blocks, and every other native instruction.
        operand.SetValue(calls[0], typeof(RoadReachabilityExperiment).GetMethod(nameof(Query), All)!);
        opcode.SetValue(calls[0], OpCodes.Call);
        return list;
    }

    private static void BeginWorld()
    {
        Graphs = new();
        _running = false;
        _calls = _hits = _misses = _invalidations = _compared = _evictions = 0;
    }
    internal static void SimulationStarted() { if (Installed) _running = true; }
    private static void Changing(RoadNavMeshGraph __instance)
    {
        if (Graphs.TryGetValue(__instance, out var state)) { state.Clear(); _invalidations++; }
    }

    private static void Query(RoadReachabilityService service, int start, int range, List<int> destination)
    {
        _calls++;
        if (!Enabled || !_running || service == null || destination == null || service._roadNavMeshGraph == null ||
            service._nodesToVisit.Count != 0 || service._visitedNodes.Count != 0)
        {
            service!.GetReachableNeighborsInRange(start, range, destination!);
            return;
        }
        var state = Graphs.GetOrCreateValue(service._roadNavMeshGraph);
        var key = (start, range);
        if (state.Results.TryGetValue(key, out var result))
        {
            _hits++;
            if (Validate || _syntheticActive)
            {
                var expected = new List<int>();
                service.GetReachableNeighborsInRange(start, range, expected);
                if (!expected.SequenceEqual(result))
                {
                    Debug.LogError("[T3MPTEST] Road cache runtime validation FAIL: native sequence mismatch");
                    throw new InvalidOperationException("Road cache/native sequence mismatch");
                }
                _compared++;
            }
            destination.AddRange(result);
            return;
        }
        _misses++;
        var before = destination.Count;
        service.GetReachableNeighborsInRange(start, range, destination);
        var length = destination.Count - before;
        if (length > MaxNodes) return;
        if (state.Results.Count >= MaxEntries || state.Nodes + length > MaxNodes) { state.Clear(); _evictions++; }
        result = new int[length];
        destination.CopyTo(before, result, 0, length);
        state.Results.Add(key, result);
        state.Nodes += length;
    }

    internal static void Report()
    {
        if (Installed) Debug.Log($"[T3MPTEST] Road cache calls={_calls} hits={_hits} misses={_misses} invalidations={_invalidations} compared={_compared} evictions={_evictions}");
        if (Validate && _running && _compared > 0)
            Debug.Log("[T3MPTEST] Road cache runtime validation PASS: compared=" + _compared);
    }

    private sealed class SmallMap : INavMeshSizeProvider { public Vector3Int Size => new(4, 4, 4); }
    private static void SyntheticValidation()
    {
        var ids = new NodeIdService(new SmallMap()); ids.Load();
        var graph = new RoadNavMeshGraph(ids); graph.Load();
        var other = new RoadNavMeshGraph(ids); other.Load();
        var service = new RoadReachabilityService(graph);
        var second = new RoadReachabilityService(other);
        var random = new System.Random(93817);
        _running = true;
        _syntheticActive = true;
        var checks = 0;
        void Compare(RoadReachabilityService current, int start, int range)
        {
            var expected = new List<int> { -123 };
            var actual = new List<int> { -123 };
            current.GetReachableNeighborsInRange(start, range, expected);
            Query(current, start, range, actual);
            if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Road append/order differs at case " + checks);
            if (current._nodesToVisit.Count != 0 || current._visitedNodes.Count != 0) throw new InvalidOperationException("Road scratch state differs");
            checks++;
        }
        for (var i = 0; i < 512; i++)
        {
            var a = random.Next(32); var b = (a + random.Next(1, 31)) % 32;
            if (i % 4 == 0) graph.DisconnectNodes(a, b);
            else graph.ConnectNodes(a, b, random.Next(3), random.Next(7) * 0.5f);
            for (var j = 0; j < 6; j++)
            {
                var start = random.Next(32); var range = random.Next(-1, 12);
                Compare(service, start, range);
                Compare(service, start, range);
                Compare(second, start, range);
            }
            if (i % 47 == 0) { graph.Load(); Compare(service, a, 10); }
        }
        if (_hits == 0 || _compared == 0 || _invalidations == 0) throw new InvalidOperationException("Road validation did not exercise caching/invalidation");
        for (var range = 20; range < MaxEntries + 24; range++) Compare(second, 0, range);
        if (_evictions == 0 || Graphs.GetOrCreateValue(other).Results.Count > MaxEntries)
            throw new InvalidOperationException("Road entry budget was not enforced");
        Debug.Log("[T3MPTEST] Road cache synthetic validation PASS: native weighted FIFO order/append/range, connect/disconnect/load, independent graphs; checks=" + checks);
        _syntheticActive = false;
        BeginWorld();
    }
}
