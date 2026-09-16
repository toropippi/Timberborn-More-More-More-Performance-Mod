using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Test-only, coarse runtime attribution. No per-candidate or per-component hooks.
// Inclusive durations can overlap on reentry; never add them as exclusive shares.
internal static class SearchProfiler
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class Counter { internal long Calls, Elapsed, Exceptions; }
    private static readonly Dictionary<MethodBase, Counter> Counters = new Dictionary<MethodBase, Counter>();
    private static bool _installed, _active;
    private static bool HarvestRequested => Environment.GetCommandLineArgs().Contains("-t3mpTestHarvestProfile", StringComparer.OrdinalIgnoreCase);
    private static bool DecisionRequested => Environment.GetCommandLineArgs().Contains("-t3mpTestDecisionProfile", StringComparer.OrdinalIgnoreCase);
    private static bool WaterRequested => Environment.GetCommandLineArgs().Contains("-t3mpTestWaterObjectProfile", StringComparer.OrdinalIgnoreCase);
    internal static bool Requested => HarvestRequested || DecisionRequested || WaterRequested;

    internal static void Install()
    {
        if (!Requested || _installed) return;
        if ((HarvestRequested ? 1 : 0) + (DecisionRequested ? 1 : 0) + (WaterRequested ? 1 : 0) != 1)
            throw new InvalidOperationException("Choose one search profile group");
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var searchTargets = WaterRequested ? new[]
        {
            (Type: "Timberborn.WaterObjects.WaterObjectService", Name: "Tick", Parameters: 0)
        } : DecisionRequested ? new[]
        {
            (Type: "Timberborn.Hauling.DistrictHaulCandidates", Name: "GetWorkplaceBehaviorsOrdered", Parameters: 1),
            (Type: "Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService", Name: "PickBestAction", Parameters: 4),
            (Type: "Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService", Name: "AppraiseNeedBehaviors", Parameters: 2),
            (Type: "Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService", Name: "PickShortestAction", Parameters: 4)
        } : new[]
        {
            (Type: "Timberborn.YielderFinding.YielderFinder", Name: "FindLivingYielderWithoutAccessible", Parameters: 4),
            (Type: "Timberborn.YielderFinding.YielderFinder", Name: "FindYielderWithAccessible", Parameters: 4),
            (Type: "Timberborn.Planting.PlantingSpotFinder", Name: "FindClosest", Parameters: 1),
            (Type: "Timberborn.Yielding.InRangeYielders", Name: "GetYielders", Parameters: 2)
        };
        var targets = searchTargets.Append((Type: "Timberborn.TickSystem.TickableBucketService", Name: "TickBuckets", Parameters: 1))
            .Select(t => Find(t.Type).GetMethods(All | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == t.Name && m.GetParameters().Length == t.Parameters)).ToArray();
        var harmonyType = Find("HarmonyLib.Harmony");
        var methodType = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.searchprofile")!;
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(methodType, typeof(SearchProfiler).GetMethod(name, All))!;
        foreach (var target in targets)
        {
            Counters.Add(target, new Counter());
            patch.Invoke(harmony, new object?[] { target, Hook(nameof(Before)), null, null, Hook(nameof(After)) });
            Debug.Log("[T3MPSEARCH] target=" + target.DeclaringType!.FullName + "." + target + " module=" + target.Module.ModuleVersionId);
        }
        _installed = true;
        Debug.Log("[T3MPSEARCH] installed targets=" + targets.Length);
    }

    internal static void Begin()
    {
        if (!Requested) return;
        if (!_installed) throw new InvalidOperationException("Search profiler was not installed");
        Clear();
        _active = true;
    }

    internal static void End() => _active = false;
    private static void Before(out long __state) => __state = _active ? Stopwatch.GetTimestamp() : 0;

    // A void finalizer observes both exits without suppressing native exceptions.
    private static void After(MethodBase __originalMethod, long __state, Exception? __exception)
    {
        if (__state == 0) return;
        var elapsed = Stopwatch.GetTimestamp() - __state;
        var counter = Counters[__originalMethod];
        counter.Calls++;
        counter.Elapsed += elapsed;
        if (__exception != null) counter.Exceptions++;
    }

    internal static void Report(double windowSeconds)
    {
        if (!_active) return;
        Debug.Log(string.Format(CultureInfo.InvariantCulture, "[T3MPSEARCH] window seconds={0:F6}", windowSeconds));
        foreach (var pair in Counters)
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[T3MPSEARCH] method={0}.{1} calls={2} inclusiveMs={3:F6} exceptions={4}",
                pair.Key.DeclaringType!.FullName, pair.Key.Name, pair.Value.Calls,
                pair.Value.Elapsed * 1000.0 / Stopwatch.Frequency, pair.Value.Exceptions));
        Clear();
    }

    private static void Clear()
    {
        foreach (var counter in Counters.Values) counter.Calls = counter.Elapsed = counter.Exceptions = 0;
    }
}
