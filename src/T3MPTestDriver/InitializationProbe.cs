using TransputRoutingExperiment = T3MP.Loading.TransputLoadRouting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Intrusive attribution, deliberately separate from timed A/B runs.
internal static class InitializationProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Stat { internal long Calls, Ticks, OwnTicks, Max, GcCalls, GcTicks, HeapBytes, HeapSamples; }
    private struct Scope { internal long Start, Heap, Children; internal int Gc; }
    [ThreadStatic] private static long _accounted;
    private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
    private static readonly Dictionary<MethodBase, Stat[]> PhaseStats = new Dictionary<MethodBase, Stat[]>();
    private static readonly string[] PhaseNames = { "PreInitialize", "Initialize", "PostInitialize" };
    private static readonly long[] PhaseTicks = new long[3];
    [ThreadStatic] private static int _phase;
    [ThreadStatic] private static long _phaseStart;
    private static bool _active, _memory;
    private static Action? _installDeferred, _removeDeferred;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        _memory = Environment.GetCommandLineArgs().Contains("-t3mpTestInitializationMemoryProfile");
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestInitializationProfile") && !_memory) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.initialization-profile");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var prefix = Activator.CreateInstance(hm, typeof(InitializationProbe).GetMethod(nameof(Begin), All));
        var finalizer = Activator.CreateInstance(hm, typeof(InitializationProbe).GetMethod(nameof(End), All));
        // Model preparation checks component patches while clones are created.
        // Install attribution only after that work, preserving the production
        // compatibility guard. Remove it after the measured phases. Phase hook
        // installation time is outside our own phase clock (but inside coarse
        // caller probes, which must not be used as clean timings for this run).
        var visual = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Loading.PreparedEntityVisuals"))
            .FirstOrDefault(t => t != null && t.Assembly != typeof(InitializationProbe).Assembly);
        var defer = visual != null && (bool)visual.GetProperty("Installed", All)!.GetValue(null)!;
        var interfaces = new[] { "Timberborn.EntitySystem.IPreInitializableEntity", "Timberborn.EntitySystem.IInitializableEntity", "Timberborn.EntitySystem.IPostInitializableEntity",
            "Timberborn.BlockSystem.IFinishedStateListener", "Timberborn.BlockSystem.IUnfinishedStateListener" }.Select(Find).ToArray();
        var targets = new HashSet<MethodInfo>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Timberborn.")))
        foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters))
        foreach (var contract in interfaces.Where(i => i.IsAssignableFrom(type)))
        foreach (var method in type.GetInterfaceMap(contract).TargetMethods)
        {
            var declared = (MethodInfo)method.Module.ResolveMethod(method.MetadataToken)!;
            if (!declared.ContainsGenericParameters) targets.Add(declared);
        }
        // Coarse children of the consistently hot mechanical initializer.
        // Avoid timing each broadcast recipient: that changes millions of calls.
        foreach (var entry in new[] {
            "Timberborn.MechanicalSystem.MechanicalNode|InitializeTransputs,InitializeActuals,AddToGraph",
            "Timberborn.MechanicalSystem.TransputMap|AddNode",
            "Timberborn.MechanicalSystem.MechanicalGraphManager|AddNode"
        })
        {
            var parts = entry.Split('|');
            foreach (var method in Find(parts[0]).GetMethods(All).Where(m => parts[1].Split(',').Contains(m.Name))) targets.Add(method);
        }
        if (Environment.GetCommandLineArgs().Contains("-t3mpTestInitializationDetailProfile"))
        {
            foreach (var entry in new[] {
                "Timberborn.GoodStackSystem.GoodStackModelFactory|Create",
                "Timberborn.GoodStackSystem.GoodStackModel|Initialize",
                "Timberborn.Rendering.EntityMaterials|AddMaterials",
                "Timberborn.Rendering.MaterialLightingRenderers|CollectRenderers",
                "Timberborn.Carrying.GoodCarrierModel|InitializeItems,UpdateItemsVisibility",
                "Timberborn.Carrying.CarriedItem|CreateLinkedToObject,ParseGoodContainerFromName,SetVisibility",
                "Timberborn.TemplateAttachmentSystem.TemplateAttachments|CreateAttachment,GetAttachmentDefinition",
                "Timberborn.BuildingsNavigation.BuildingNavMesh|RecalculateNavMeshObject",
                "Timberborn.BlockSystemNavigation.NavMeshObjectUpdater|Update,AddManuallySetEdges,AddEdgesAddedByStackable,AddUnblockedEdges,AddBlockedEdges,CanAddBlockingEdge",
                "Timberborn.BlockObstacles.LayeredBlockObstacle|CreateBlockOccupationLayers,TryUpdateBlockOccupationLayers,UpdateMaxOccupancyRange",
                "Timberborn.BlockObstacles.BlockOccupationLayerFactory|Create",
                "Timberborn.BlockObstacles.BlockOccupationLayer|CanBeAddedToServices,AddToServices",
                "Timberborn.BlockSystem.BlockObjectFactory|CreateAsPreview" })
            {
                var parts = entry.Split('|');
                foreach (var method in Find(parts[0]).GetMethods(All | BindingFlags.DeclaredOnly).Where(m => parts[1].Split(',').Contains(m.Name))) targets.Add(method);
            }
            var transput = T3MP.Loading.LoadCompatibility.MainInstalled(typeof(TransputRoutingExperiment))
                ? AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Code").GetType("T3MP.Loading.TransputLoadRouting")!
                : typeof(TransputRoutingExperiment);
            foreach (var name in new[] { "Build", "Dispatch" }) targets.Add(transput.GetMethod(name, All)!);
            Debug.Log("[T3MPINIT] detailed child attribution enabled");
        }
        foreach (var target in targets)
        {
            Stats.Add(target, new Stat());
            PhaseStats.Add(target, new[] { new Stat(), new Stat(), new Stat() });
        }
        void InstallMethods()
        {
            foreach (var target in targets)
                patch.Invoke(harmony, new object?[] { target, prefix, null, null, finalizer });
        }
        if (defer)
        {
            _installDeferred = InstallMethods;
            _removeDeferred = () => ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { "t3mp.test.initialization-profile" });
        }
        else InstallMethods();
        foreach (var name in PhaseNames)
            patch.Invoke(harmony, new object?[] { Find("Timberborn.WorldPersistence.EntitiesLoader").GetMethod(name, All),
                Activator.CreateInstance(hm, typeof(InitializationProbe).GetMethod(nameof(BeginPhase), All)), null, null,
                Activator.CreateInstance(hm, typeof(InitializationProbe).GetMethod(nameof(EndPhase), All)) });
        _active = true;
        Debug.Log("[T3MPINIT] installed=" + targets.Count + " inclusiveHeapProfile=" + _memory);
        if (defer) Debug.Log("[T3MPINIT] component hooks deferred until after creation; removed after PostInitialize");
    }
    private static void BeginPhase(MethodBase __originalMethod)
    {
        if (_installDeferred != null)
        {
            var install = _installDeferred; _installDeferred = null; install();
        }
        if (_phase != 0) throw new InvalidOperationException("Nested initialization phase");
        _phase = Array.IndexOf(PhaseNames, __originalMethod.Name) + 1;
        _accounted = 0;
        _phaseStart = Stopwatch.GetTimestamp();
    }
    private static void EndPhase()
    {
        PhaseTicks[_phase - 1] += Stopwatch.GetTimestamp() - _phaseStart;
        var finished = _phase == 3;
        _phase = 0;
        if (finished && _removeDeferred != null)
        {
            var remove = _removeDeferred; _removeDeferred = null; remove();
        }
    }
    private static void Begin(out Scope __state) => __state = _active && _phase != 0 ? new Scope { Gc = GC.CollectionCount(0), Heap = _memory ? GC.GetTotalMemory(false) : 0, Start = Stopwatch.GetTimestamp(), Children = _accounted } : default;
    private static void End(MethodBase __originalMethod, Scope __state)
    {
        if (!_active || __state.Start == 0) return;
        var elapsed = Stopwatch.GetTimestamp() - __state.Start;
        var own = elapsed - (_accounted - __state.Children);
        _accounted = __state.Children + elapsed;
        var stat = Stats[__originalMethod]; stat.Calls++; stat.Ticks += elapsed; stat.Max = Math.Max(stat.Max, elapsed);
        stat.OwnTicks += own;
        var phaseStat = PhaseStats[__originalMethod][_phase - 1];
        phaseStat.Calls++; phaseStat.Ticks += elapsed; phaseStat.OwnTicks += own;
        if (GC.CollectionCount(0) != __state.Gc) { stat.GcCalls++; stat.GcTicks += elapsed; }
        else if (_memory) { stat.HeapBytes += GC.GetTotalMemory(false) - __state.Heap; stat.HeapSamples++; }
    }
    internal static void Report()
    {
        for (var i = 0; i < PhaseNames.Length; i++)
        {
            if (PhaseTicks[i] == 0) continue;
            var measured = PhaseStats.Values.Sum(s => s[i].OwnTicks);
            Debug.Log(FormattableString.Invariant($"[T3MPINITPHASE] phase={PhaseNames[i]} totalMs={PhaseTicks[i] * 1000.0 / Stopwatch.Frequency:F3} measuredOwnMs={measured * 1000.0 / Stopwatch.Frequency:F3} remainderMs={(PhaseTicks[i] - measured) * 1000.0 / Stopwatch.Frequency:F3}"));
            foreach (var pair in PhaseStats.Where(p => p.Value[i].Calls > 0).OrderByDescending(p => p.Value[i].OwnTicks))
                Debug.Log(FormattableString.Invariant($"[T3MPINITPHASE] phase={PhaseNames[i]} method={pair.Key.DeclaringType!.FullName}.{pair.Key.Name} calls={pair.Value[i].Calls} ms={pair.Value[i].Ticks * 1000.0 / Stopwatch.Frequency:F3} ownMs={pair.Value[i].OwnTicks * 1000.0 / Stopwatch.Frequency:F3}"));
        }
        foreach (var pair in Stats.OrderByDescending(p => p.Value.Ticks).Take(35))
            Debug.Log(FormattableString.Invariant($"[T3MPINIT] {pair.Key.DeclaringType!.FullName}.{pair.Key.Name} calls={pair.Value.Calls} ms={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F3} ownMs={pair.Value.OwnTicks * 1000.0 / Stopwatch.Frequency:F3} maxMs={pair.Value.Max * 1000.0 / Stopwatch.Frequency:F3} gcCalls={pair.Value.GcCalls} gcCallMs={pair.Value.GcTicks * 1000.0 / Stopwatch.Frequency:F3}"));
        if (_memory) foreach (var pair in Stats.OrderByDescending(p => p.Value.HeapBytes).Take(40))
            Debug.Log(FormattableString.Invariant($"[T3MPINITMEMORY] {pair.Key.DeclaringType!.FullName}.{pair.Key.Name} calls={pair.Value.Calls} noGcSamples={pair.Value.HeapSamples} inclusiveHeapDeltaMiB={pair.Value.HeapBytes / 1048576.0:F3} gcCalls={pair.Value.GcCalls}"));
        _active = false; Stats.Clear();
    }
}
