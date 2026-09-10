using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static class PrefabConstructionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Total { internal long Calls, Inclusive, Own; internal double Heap; }
    internal sealed class Frame { internal Frame? Parent; internal long Start, Children, Heap; }
    [ThreadStatic] private static Frame? _current;
    [ThreadStatic] private static int _depth;
    private static readonly Dictionary<MethodBase, Total> Totals = new Dictionary<MethodBase, Total>();
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestPrefabProfile")) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.prefab-profile");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(PrefabConstructionProbe).GetMethod(name, All))!;
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Start)), null, null, Hook(nameof(Stop)) });
        var targets = new HashSet<MethodInfo>();
        foreach (var entry in new[] {
            "Timberborn.TemplateInstantiation.TemplateInstantiator|GetInstanceComponents,GetCachedTemplate",
            "Timberborn.PrefabOptimization.PrefabOptimizationChain|ProcessPrefab",
            "Timberborn.Timbermesh.TimbermeshReader|ReadFromStream",
            "Timberborn.Timbermesh.TimbermeshImporter|CreateMeshes,CreateRelations,PostprocessModel",
            "Timberborn.Timbermesh.StaticMeshBuilder|BuildMesh",
            "Timberborn.PrefabOptimization.MeshBuilder|Build,BuildIntermediateMesh"
        })
        {
            var parts = entry.Split('|');
            foreach (var m in Find(parts[0]).GetMethods(All).Where(m => parts[1].Split(',').Contains(m.Name) && !m.ContainsGenericParameters)) targets.Add(m);
        }
        foreach (var contractName in new[] { "Timberborn.PrefabOptimization.IPrefabOptimizer", "Timberborn.Timbermesh.IModelPostprocessor" })
        {
            var contract = Find(contractName);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Timberborn.")))
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters && contract.IsAssignableFrom(t)))
                foreach (var method in type.GetInterfaceMap(contract).TargetMethods) targets.Add(method);
        }
        foreach (var method in targets) patch.Invoke(harmony, new object?[] { method, Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        Debug.Log("[T3MPPREFAB] installed methods=" + targets.Count + "; intrusive attribution only");
    }
    private static void Start() { if (_depth++ == 0) Totals.Clear(); }
    private static void Stop()
    {
        if (--_depth != 0) return;
        foreach (var item in Totals.OrderByDescending(x => x.Value.Own))
        {
            var t = item.Value;
            Debug.Log(FormattableString.Invariant($"[T3MPPREFAB] {item.Key.DeclaringType!.Name}.{item.Key.Name} calls={t.Calls} ownMs={t.Own * 1000.0 / Stopwatch.Frequency:F3} inclusiveMs={t.Inclusive * 1000.0 / Stopwatch.Frequency:F3} inclusiveHeapMB={t.Heap:F3}"));
        }
        Totals.Clear();
    }
    private static void Begin(out Frame? __state)
    {
        __state = null;
        if (_depth == 0) return;
        __state = new Frame { Parent = _current, Heap = GC.GetTotalMemory(false), Start = Stopwatch.GetTimestamp() };
        _current = __state;
    }
    private static void End(MethodBase __originalMethod, Frame? __state)
    {
        if (__state == null) return;
        var elapsed = Stopwatch.GetTimestamp() - __state.Start;
        _current = __state.Parent;
        if (_current != null) _current.Children += elapsed;
        if (!Totals.TryGetValue(__originalMethod, out var total)) Totals.Add(__originalMethod, total = new Total());
        total.Calls++; total.Inclusive += elapsed; total.Own += elapsed - __state.Children;
        total.Heap += (GC.GetTotalMemory(false) - __state.Heap) / 1048576.0;
    }
}
