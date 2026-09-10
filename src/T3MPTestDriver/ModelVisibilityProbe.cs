using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Intrusive child attribution, kept separate from clean comparison runs.
internal static class ModelVisibilityProbe
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Total { internal long Calls, Inclusive, Own; internal double Heap; }
    private struct Scope { internal long Start, Children, Heap; }
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static long _accounted;
    private static readonly Dictionary<MethodBase, Total> Totals = new Dictionary<MethodBase, Total>();
    private static bool _work;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        _work = Environment.GetCommandLineArgs().Contains("-t3mpTestModelWorkProfile");
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestModelVisibilityProfile") && !_work) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.model-visibility-profile");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name)
        {
            var hook = Activator.CreateInstance(hm, typeof(ModelVisibilityProbe).GetMethod(name, All))!;
            if (name == nameof(Stop)) hm.GetField("priority")!.SetValue(hook, 900);
            return hook;
        }
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Start)), null, null, Hook(nameof(Stop)) });
        var targets = new HashSet<MethodInfo>();
        foreach (var entry in new[] {
            "Timberborn.BlockObjectModelSystem.BlockObjectModelController|UpdateAll,UpdateModel,SetModelState",
            "Timberborn.BlockObjectModelSystem.GameObjectExtensions|ToggleModelVisibility,ToggleRenderers,ToggleColliders",
            "Timberborn.BlockObjectModelSystem.BlockObjectModel|UpdateModelVisibility",
            "Timberborn.Buildings.BuildingModel|UpdateModelVisibility",
            "Timberborn.NaturalResourcesModelSystem.NaturalResourceModel|SubscribeToEvents,ShowCurrentModel,HideModels",
            "Timberborn.NaturalResourcesLifecycleModelSystem.NaturalResourceLifecycleModel|Show,Hide",
            "Timberborn.GoodStackSystem.GoodStackModelFactory|Create",
            "Timberborn.GoodStackSystem.GoodStackModel|Initialize",
            "Timberborn.Rendering.EntityMaterials|AddMaterials,GetChildMaterials"
        })
        {
            var parts = entry.Split('|');
            foreach (var method in Find(parts[0]).GetMethods(All | BindingFlags.DeclaredOnly).Where(m => parts[1].Split(',').Contains(m.Name))) targets.Add(method);
        }
        var contract = Find("Timberborn.BlockObjectModelSystem.IModelUpdater");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Timberborn.")))
        foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters && contract.IsAssignableFrom(t)))
        foreach (var method in type.GetInterfaceMap(contract).TargetMethods)
            if (!method.ContainsGenericParameters) targets.Add((MethodInfo)method.Module.ResolveMethod(method.MetadataToken)!);
        foreach (var method in targets) patch.Invoke(harmony, new object?[] { method, Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        if (_work)
        {
            var rewrite = typeof(ModelVisibilityProbe).GetMethod(nameof(RewriteWork), All)!.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
            foreach (var method in targets.Where(m => m.DeclaringType!.Name == "GameObjectExtensions" && (m.Name == "ToggleRenderers" || m.Name == "ToggleColliders") ||
                         m.DeclaringType!.Name == "NaturalResourceLifecycleModel" && (m.Name == "Show" || m.Name == "Hide")))
                patch.Invoke(harmony, new object?[] { method, null, null, Activator.CreateInstance(hm, rewrite), null });
        }
        Debug.Log("[T3MPMODELPROFILE] installed methods=" + targets.Count + "; intrusive attribution only");
        if (_work) Debug.Log("[T3MPMODELWORK] query/activation caller attribution; heap sampling disabled");
    }

    private static void Start() { if (_depth++ == 0) { Totals.Clear(); _accounted = 0; } }
    private static void Begin(out Scope __state) => __state = _depth == 0 ? default :
        new Scope { Heap = _work ? 0 : GC.GetTotalMemory(false), Start = Stopwatch.GetTimestamp(), Children = _accounted };
    private static void End(MethodBase __originalMethod, Scope __state)
    {
        if (__state.Start == 0) return;
        var elapsed = Stopwatch.GetTimestamp() - __state.Start;
        var own = elapsed - (_accounted - __state.Children);
        _accounted = __state.Children + elapsed;
        if (!Totals.TryGetValue(__originalMethod, out var total)) Totals.Add(__originalMethod, total = new Total());
        total.Calls++; total.Inclusive += elapsed; total.Own += own;
        if (!_work) total.Heap += (GC.GetTotalMemory(false) - __state.Heap) / 1048576.0;
    }
    private static void Stop()
    {
        if (--_depth != 0) return;
        foreach (var pair in Totals.OrderByDescending(p => p.Value.Own))
        {
            var t = pair.Value;
            Debug.Log(FormattableString.Invariant($"[T3MPMODELPROFILE] {pair.Key.DeclaringType!.FullName}.{pair.Key.Name} calls={t.Calls} ownMs={t.Own * 1000.0 / Stopwatch.Frequency:F3} inclusiveMs={t.Inclusive * 1000.0 / Stopwatch.Frequency:F3} inclusiveHeapMB={t.Heap:F3}"));
        }
        Totals.Clear();
    }

    private static readonly MethodInfo RendererQuery = typeof(ModelVisibilityProbe).GetMethod(nameof(QueryRenderers), All)!;
    private static readonly MethodInfo ColliderQuery = typeof(ModelVisibilityProbe).GetMethod(nameof(QueryColliders), All)!;
    private static readonly MethodInfo Activation = typeof(ModelVisibilityProbe).GetMethod(nameof(SetActive), All)!;
    private static Renderer[] QueryRenderers(GameObject model, bool inactive)
    { Begin(out var state); try { return model.GetComponentsInChildren<Renderer>(inactive); } finally { End(RendererQuery, state); } }
    private static Collider[] QueryColliders(GameObject model, bool inactive)
    { Begin(out var state); try { return model.GetComponentsInChildren<Collider>(inactive); } finally { End(ColliderQuery, state); } }
    private static void SetActive(GameObject model, bool active)
    { Begin(out var state); try { model.SetActive(active); } finally { End(Activation, state); } }
    private static IEnumerable<T> RewriteWork<T>(IEnumerable<T> source)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!;
        var count = 0;
        foreach (var instruction in source)
        {
            if (operand.GetValue(instruction) is MethodInfo method && method.DeclaringType == typeof(GameObject))
            {
                MethodInfo? target = null;
                if (method.Name == "GetComponentsInChildren" && method.IsGenericMethod && method.GetParameters().Length == 1)
                    target = method.GetGenericArguments()[0] == typeof(Renderer) ? RendererQuery : method.GetGenericArguments()[0] == typeof(Collider) ? ColliderQuery : null;
                if (method.Name == "SetActive") target = Activation;
                if (target != null) { operand.SetValue(instruction, target); opcode.SetValue(instruction, OpCodes.Call); count++; }
            }
            yield return instruction;
        }
        if (count == 0) throw new InvalidOperationException("No reviewed model operation found");
    }
}
