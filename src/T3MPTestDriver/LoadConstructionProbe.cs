using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only entity-instantiation attribution. Nested totals overlap;
// ownMs excludes instrumented children but includes instrumentation overhead.
internal static class LoadConstructionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Slot { public long Calls, Ticks, OwnTicks, Max; }
    private struct Scope { internal long Start, Children; internal Type? CreatedType; }
    private static readonly Dictionary<MethodBase, Slot> Slots = new Dictionary<MethodBase, Slot>();
    private static readonly Dictionary<Type, Slot> CreationSlots = new Dictionary<Type, Slot>();
    [ThreadStatic] private static long _accounted;
    private static bool _enabled;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionProfile")) return;
        var harmonyType = Find("HarmonyLib.Harmony");
        var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.construction-profile");
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var prefix = Activator.CreateInstance(hm, typeof(LoadConstructionProbe).GetMethod(nameof(Begin), All));
        var finalizer = Activator.CreateInstance(hm, typeof(LoadConstructionProbe).GetMethod(nameof(End), All));
        var targets = new HashSet<MethodInfo>();
        foreach (var entry in new[] {
            "Timberborn.BaseComponentSystem.BaseInstantiator|InstantiateInactive,InstantiateComponents",
            "Timberborn.TemplateInstantiation.TemplateInstantiator|Instantiate",
            "Bindito.Unity.Instantiator|InstantiateInactive,InjectIntoObjectAndChildren",
            "Bindito.Core.Internal.InstanceCreator|CreateInstance",
            "Bindito.Core.Internal.MethodInjector|Inject",
            "Bindito.Core.Internal.InstanceBank|TryGetInstance",
            "Bindito.Core.Internal.ProvisionListenerNotifier|NotifyAllListeners"
        })
        {
            var parts = entry.Split('|');
            foreach (var m in Find(parts[0]).GetMethods(All).Where(m => parts[1].Split(',').Contains(m.Name) && !m.ContainsGenericParameters)) targets.Add(m);
        }
        var awake = Find("Timberborn.BaseComponentSystem.IAwakableComponent");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Timberborn.")))
        foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.ContainsGenericParameters && awake.IsAssignableFrom(t)))
        foreach (var method in type.GetInterfaceMap(awake).TargetMethods)
            targets.Add((MethodInfo)method.Module.ResolveMethod(method.MetadataToken)!);
        foreach (var target in targets)
        {
            Slots.Add(target, new Slot());
            var before = target.DeclaringType!.FullName == "Bindito.Core.Internal.InstanceCreator" ?
                Activator.CreateInstance(hm, typeof(LoadConstructionProbe).GetMethod(nameof(BeginCreation), All)) : prefix;
            patch.Invoke(harmony, new object?[] { target, before, null, null, finalizer });
        }
        var entityPhase = Find("Timberborn.WorldPersistence.WorldEntitiesLoader").GetMethod("InstantiateEntities", All)!;
        patch.Invoke(harmony, new object?[] { entityPhase,
            Activator.CreateInstance(hm, typeof(LoadConstructionProbe).GetMethod(nameof(BeginEntityPhase), All)), null, null,
            Activator.CreateInstance(hm, typeof(LoadConstructionProbe).GetMethod(nameof(Report), All)) });
        Debug.Log("[T3MPCONSTRUCTION] installed methods=" + targets.Count);
    }

    private static void BeginEntityPhase()
    {
        foreach (var slot in Slots.Values) slot.Calls = slot.Ticks = slot.OwnTicks = slot.Max = 0;
        CreationSlots.Clear(); _accounted = 0; _enabled = true;
        Debug.Log("[T3MPCONSTRUCTION] scope=WorldEntitiesLoader.InstantiateEntities");
    }

    private static void Begin(out Scope __state) => __state = _enabled ? new Scope { Start = Stopwatch.GetTimestamp(), Children = _accounted } : default;
    private static void BeginCreation(Type type, out Scope __state) { Begin(out __state); __state.CreatedType = type; }
    private static void End(MethodBase __originalMethod, Scope __state)
    {
        if (__state.Start == 0) return;
        var ticks = Stopwatch.GetTimestamp() - __state.Start;
        var own = ticks - (_accounted - __state.Children);
        _accounted = __state.Children + ticks;
        var slot = Slots[__originalMethod];
        Record(slot, ticks, own);
        if (__state.CreatedType != null)
        {
            if (!CreationSlots.TryGetValue(__state.CreatedType, out var created)) CreationSlots.Add(__state.CreatedType, created = new Slot());
            Record(created, ticks, own);
        }
    }
    private static void Record(Slot slot, long ticks, long own)
    { slot.Calls++; slot.Ticks += ticks; slot.OwnTicks += own; slot.Max = Math.Max(slot.Max, ticks); }

    internal static void Report()
    {
        if (!_enabled) return;
        _enabled = false;
        foreach (var pair in Slots.Where(p => p.Value.Calls > 0).OrderByDescending(p => p.Value.Ticks).Take(55))
            Debug.Log(FormattableString.Invariant($"[T3MPCONSTRUCTION] {pair.Key.DeclaringType!.FullName}.{pair.Key.Name} calls={pair.Value.Calls} ms={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F3} ownMs={pair.Value.OwnTicks * 1000.0 / Stopwatch.Frequency:F3} maxMs={pair.Value.Max * 1000.0 / Stopwatch.Frequency:F3}"));
        foreach (var pair in CreationSlots.OrderByDescending(p => p.Value.OwnTicks).Take(35))
            Debug.Log(FormattableString.Invariant($"[T3MPCONSTRUCTOR] {pair.Key.FullName} calls={pair.Value.Calls} ms={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F3} ownMs={pair.Value.OwnTicks * 1000.0 / Stopwatch.Frequency:F3}"));
    }
}
