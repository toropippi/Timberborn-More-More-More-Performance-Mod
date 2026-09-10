using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using T3MP.Loading;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Diagnostic caller-site scopes. Does not patch component Awake/Initialize or
// change their compatibility classification. Values stay on the evaluation
// stack across the observer calls; the original call and dispatch are retained.
internal static class EntityCreationProbe
{
    private const BindingFlags All = LoadPatchBridge.All;
    private sealed class Stat { internal string Name = ""; internal long Calls, Ticks, Own, GcTicks, GcCalls, Max; }
    private struct Scope { internal int Id, Gc; internal long Start, Children; }
    private static readonly List<Stat> Stats = new List<Stat>();
    private static readonly Dictionary<string, int> Ids = new Dictionary<string, int>();
    [ThreadStatic] private static Scope[]? _stack;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static bool _active;
    private static long _start;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestEntityCreationProfile")) return;
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony"); var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.entity-creation-profile");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(EntityCreationProbe).GetMethod(name, All))!;
        var rewrite = Activator.CreateInstance(hm, LoadPatchBridge.Create("T3MP.EntityCreationProbe", typeof(EntityCreationProbe).GetMethod(nameof(Rewrite), All)!))!;
        // Observe the installed visual Activate replacement after its rewrite.
        hm.GetField("priority")!.SetValue(rewrite, 0);
        foreach (var entry in new[] {
            "Timberborn.TemplateInstantiation.TemplateInstantiator|Instantiate",
            "Timberborn.BaseComponentSystem.BaseInstantiator|InstantiateInactive",
            "Bindito.Unity.Instantiator|InstantiateInactive",
            "T3MP.Loading.PreparedEntityVisuals|Activate" })
        {
            var parts = entry.Split('|');
            var type = parts[0] == "T3MP.Loading.PreparedEntityVisuals"
                ? AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Code").GetType(parts[0])!
                : LoadPatchBridge.Find(parts[0]);
            patch.Invoke(harmony, new object?[] { type.GetMethod(parts[1], All), null, null, rewrite, null });
        }
        patch.Invoke(harmony, new object?[] {
            LoadPatchBridge.Find("Timberborn.WorldPersistence.WorldEntitiesLoader").GetMethod("InstantiateEntities", All),
            Hook(nameof(BeginPhase)), null, null, Hook(nameof(EndPhase)) });
        Debug.Log("[T3MPCREATION] installed callerSites=" + Stats.Count + " intrusive=True");
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> source)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!;
        var labels = typeof(T).GetField("labels")!; var blocks = typeof(T).GetField("blocks")!;
        T New(OpCode code, object arg) => (T)Activator.CreateInstance(typeof(T), code, arg)!;
        foreach (var instruction in source)
        {
            var code = (OpCode)opcode.GetValue(instruction)!;
            if ((code != OpCodes.Call && code != OpCodes.Callvirt) || !(operand.GetValue(instruction) is MethodInfo method))
            { yield return instruction; continue; }
            var owner = method.DeclaringType!.FullName!;
            var selected = method.Name == "GetCachedTemplate" || method.Name == "InstantiateInactive" ||
                method.Name == "InstantiatePrefabUnderInactiveParent" || method.Name == "InjectIntoObjectAndChildren" ||
                (owner == "T3MP.Loading.PreparedEntityVisuals" && method.Name == "Prepare") ||
                (owner == "UnityEngine.Transform" && method.Name == "SetParent") ||
                method.Name == "InstantiateComponents" || method.Name == "AddComponent" ||
                method.Name == "TryGetComponentsHash" || method.Name == "GetOrAdd" ||
                (owner == "Timberborn.BaseComponentSystem.ComponentCache" && method.Name == "Initialize") ||
                (owner.StartsWith("System.Action", StringComparison.Ordinal) && method.Name == "Invoke") ||
                (owner == "UnityEngine.GameObject" && method.Name == "SetActive") ||
                (owner == "T3MP.Loading.PreparedEntityVisuals" && method.Name == "Activate");
            if (!selected) { yield return instruction; continue; }
            if (((IList)blocks.GetValue(instruction)!).Count != 0)
                throw new InvalidOperationException("Unreviewed exception boundary at " + method);
            var name = owner + "." + method.Name + (method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(t => t.Name)) + ">" : "");
            if (!Ids.TryGetValue(name, out var id))
            { id = Stats.Count; Ids.Add(name, id); Stats.Add(new Stat { Name = name }); }
            var before = New(OpCodes.Ldc_I4, id);
            var oldLabels = (IList)labels.GetValue(instruction)!;
            foreach (var label in oldLabels) ((IList)labels.GetValue(before)!).Add(label);
            oldLabels.Clear();
            yield return before;
            yield return New(OpCodes.Call, typeof(EntityCreationProbe).GetMethod(nameof(BeginCall), All)!);
            yield return instruction;
            yield return New(OpCodes.Call, typeof(EntityCreationProbe).GetMethod(nameof(EndCall), All)!);
        }
    }

    private static void BeginPhase()
    {
        if (_active) throw new InvalidOperationException("Nested entity creation profile");
        foreach (var stat in Stats) stat.Calls = stat.Ticks = stat.Own = stat.GcTicks = stat.GcCalls = stat.Max = 0;
        _stack = new Scope[64]; _depth = 0; _active = true; _start = Stopwatch.GetTimestamp();
    }
    private static void BeginCall(int id)
    {
        if (!_active) return;
        if (_depth == _stack!.Length) Array.Resize(ref _stack, _depth * 2);
        _stack[_depth++] = new Scope { Id = id, Gc = GC.CollectionCount(0), Start = Stopwatch.GetTimestamp() };
    }
    private static void EndCall()
    {
        if (!_active) return;
        var scope = _stack![--_depth];
        var ticks = Stopwatch.GetTimestamp() - scope.Start;
        var stat = Stats[scope.Id]; stat.Calls++; stat.Ticks += ticks; stat.Own += ticks - scope.Children;
        stat.Max = Math.Max(stat.Max, ticks);
        if (scope.Gc != GC.CollectionCount(0)) { stat.GcCalls++; stat.GcTicks += ticks; }
        if (_depth > 0) _stack[_depth - 1].Children += ticks;
    }
    private static void EndPhase(Exception? __exception)
    {
        var total = Stopwatch.GetTimestamp() - _start; _active = false;
        var own = Stats.Sum(s => s.Own);
        Debug.Log(FormattableString.Invariant($"[T3MPCREATION] totalMs={Ms(total):F3} measuredOwnMs={Ms(own):F3} remainderMs={Ms(total - own):F3} openScopes={_depth} failed={__exception != null}"));
        foreach (var stat in Stats.OrderByDescending(s => s.Own))
            Debug.Log(FormattableString.Invariant($"[T3MPCREATION] method={stat.Name} calls={stat.Calls} ms={Ms(stat.Ticks):F3} ownMs={Ms(stat.Own):F3} maxMs={Ms(stat.Max):F3} gcCalls={stat.GcCalls} gcCallMs={Ms(stat.GcTicks):F3}"));
        _stack = null; _depth = 0;
    }
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
