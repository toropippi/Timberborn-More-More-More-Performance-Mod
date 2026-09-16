using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only inclusive timing of three subsystems the user asked about:
// ranged (area) effects, the mechanical power graph and pathfinding. Whole
// classes are hooked (every declared non-accessor method), so nested calls
// count in every enclosing method; never add the rows. Enabled by
// -t3mpTestSubsystemProfile. Not a benchmark figure.
internal static class SubsystemProfiler
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Counter { internal long Calls, Elapsed, Exceptions; internal string Name = ""; }
    private static readonly Dictionary<MethodBase, Counter> Counters = new Dictionary<MethodBase, Counter>();
    private static bool _installed;
    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestSubsystemProfile", StringComparison.OrdinalIgnoreCase));

    // Whole classes; accessors excluded. InStoppingProximity and the terrain
    // reachability helpers are guarded by shipped features and stay unhooked.
    private static readonly string[] Classes =
    {
        "Timberborn.TickSystem.TickableBucketService",
        "Timberborn.RangedEffectSystem.RangedEffectSubject", "Timberborn.RangedEffectSystem.RangedEffect", "Timberborn.RangedEffectSystem.RangedEffects",
        "Timberborn.RangedEffectSystem.RangedEffectService", "Timberborn.RangedEffectSystem.RangedEffectBuilding", "Timberborn.RangedEffectSystem.RangedEffectApplier",
        "Timberborn.RangedEffectSystem.RangedEffectsAffectingEnterable",
        "Timberborn.MechanicalSystem.BatteryService", "Timberborn.MechanicalSystem.MechanicalGraph", "Timberborn.MechanicalSystem.MechanicalGraphManager",
        "Timberborn.MechanicalSystem.MechanicalGraphReorganizer", "Timberborn.MechanicalSystem.MechanicalGraphFactory", "Timberborn.MechanicalSystem.MechanicalNode",
        "Timberborn.MechanicalSystem.MechanicalNodeParticlesController", "Timberborn.MechanicalSystem.MechanicalBuilding", "Timberborn.MechanicalSystem.BatteryCharger",
        "Timberborn.MechanicalSystem.BatteryDischarger",
        "Timberborn.Navigation.NavigationService", "Timberborn.Navigation.PathfindingService", "Timberborn.Navigation.FlowFieldPathFinder",
        "Timberborn.Navigation.FlowFieldPathBuilder", "Timberborn.Navigation.TerrainAStarPathfinder", "Timberborn.Navigation.RoadAStarPathfinder",
        "Timberborn.Navigation.RoadFlowFieldGenerator", "Timberborn.Navigation.TerrainFlowFieldGenerator", "Timberborn.Navigation.DistrictRoadFlowFieldGenerator",
        "Timberborn.Navigation.RoadSpillFlowFieldGenerator", "Timberborn.Navigation.RoadFlowFieldCache", "Timberborn.Navigation.TerrainFlowFieldCache",
        "Timberborn.Navigation.FlowFieldCache", "Timberborn.Navigation.FlowFieldPathTransformer",
        "Timberborn.NeedSystem.NeedManager",
    };
    private static readonly HashSet<string> Excluded = new HashSet<string> { "InStoppingProximity", "ToString", "GetHashCode", "Equals", "Finalize", "MemberwiseClone" };

    internal static void Install()
    {
        if (_installed || !Requested) return;
        _installed = true;
        try
        {
            Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            var harmonyType = Find("HarmonyLib.Harmony")!;
            var methodType = Find("HarmonyLib.HarmonyMethod")!;
            var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.subsystemprofile")!;
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            var before = Activator.CreateInstance(methodType, typeof(SubsystemProfiler).GetMethod(nameof(Before), All))!;
            var after = Activator.CreateInstance(methodType, typeof(SubsystemProfiler).GetMethod(nameof(After), All))!;
            var missing = new List<string>();
            foreach (var className in Classes)
            {
                var type = Find(className);
                if (type == null) { missing.Add(className); continue; }
                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || method.IsSpecialName || Excluded.Contains(method.Name)) continue;
                    if (method.GetMethodBody() == null) continue;
                    try
                    {
                        patch.Invoke(harmony, new object?[] { method, before, null, null, after });
                        Counters[method] = new Counter { Name = type.Name + "." + method.Name + "/" + method.GetParameters().Length };
                    }
                    catch (Exception e) { Debug.Log("[T3MPSUB] could not time " + type.Name + "." + method.Name + ": " + e.GetBaseException().Message); }
                }
            }
            Debug.Log("[T3MPSUB] installed methods=" + Counters.Count + (missing.Count > 0 ? " missing=" + string.Join(",", missing) : ""));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPSUB] unavailable: " + exception.GetBaseException().Message);
        }
    }

    private static void Before(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void After(MethodBase __originalMethod, long __state, Exception? __exception)
    {
        if (!Counters.TryGetValue(__originalMethod, out var counter)) return;
        counter.Calls++;
        counter.Elapsed += Stopwatch.GetTimestamp() - __state;
        if (__exception != null) counter.Exceptions++;
    }

    internal static void Report(double windowSeconds)
    {
        if (!_installed) return;
        var culture = CultureInfo.InvariantCulture;
        Debug.Log(string.Format(culture, "[T3MPSUB] window={0:F1}s", windowSeconds));
        foreach (var pair in Counters.Where(p => p.Value.Calls > 0).OrderByDescending(p => p.Value.Elapsed).Take(60))
            Debug.Log(string.Format(culture, "[T3MPSUB] {0} inclusiveMs={1:F1} calls={2} avgUs={3:F2} exceptions={4}",
                pair.Value.Name, pair.Value.Elapsed * 1000.0 / Stopwatch.Frequency, pair.Value.Calls,
                pair.Value.Elapsed * 1e6 / Stopwatch.Frequency / Math.Max(1, pair.Value.Calls), pair.Value.Exceptions));
        foreach (var counter in Counters.Values) { counter.Calls = 0; counter.Elapsed = 0; counter.Exceptions = 0; }
    }
}
