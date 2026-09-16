using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Intrusive handler attribution during the existing service caller's EventBus
// scope. Never patch EventBus/SubscriptionRegistry/ComponentCache or TubeTracker:
// those methods form the existing load router's compatibility boundary.
internal static class LoadEventProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Stat { internal long Calls, Ticks, OwnTicks, Max; }
    private struct Scope { internal long Start, Children; }
    private static readonly Dictionary<MethodBase, Stat> Stats = new Dictionary<MethodBase, Stat>();
    private static bool _installed;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static long _accounted;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestLoadEventProfile")) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.load-event-profile");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var prefix = Activator.CreateInstance(hm, typeof(LoadEventProbe).GetMethod(nameof(Begin), All));
        var finalizer = Activator.CreateInstance(hm, typeof(LoadEventProbe).GetMethod(nameof(End), All));
        var targets = new HashSet<MethodInfo>();
        var includeTube = Environment.GetCommandLineArgs().Contains("-t3mpTestProfileOnly");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("Timberborn.")))
        foreach (var type in assembly.GetTypes().Where(t => !t.ContainsGenericParameters && (includeTube || t.FullName != "Timberborn.TubeSystem.TubeTracker")))
        foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly))
            if (!method.ContainsGenericParameters && method.GetMethodBody() != null && method.IsDefined(typeof(OnEventAttribute), true)) targets.Add(method);
        foreach (var target in targets)
        {
            Stats.Add(target, new Stat());
            patch.Invoke(harmony, new object?[] { target, prefix, null, null, finalizer });
        }
        _installed = true;
        Debug.Log("[T3MPEVENTPROFILE] installed=" + targets.Count + " excluded=" + (includeTube ? "None" : "TubeTracker") + " scope=EventBus.PostLoad intrusive=True");
    }

    internal static void BeginService(IPostLoadableSingleton service)
    {
        if (!_installed || !(service is EventBus)) return;
        if (_depth++ != 0) return;
        _accounted = 0;
        foreach (var stat in Stats.Values) stat.Calls = stat.Ticks = stat.OwnTicks = stat.Max = 0;
    }
    internal static void EndService(IPostLoadableSingleton service)
    {
        if (!_installed || !(service is EventBus) || --_depth != 0) return;
        foreach (var pair in Stats.Where(p => p.Value.Calls > 0).OrderByDescending(p => p.Value.OwnTicks).Take(45))
            Debug.Log(FormattableString.Invariant($"[T3MPEVENTPROFILE] {pair.Key.DeclaringType!.FullName}.{pair.Key.Name} calls={pair.Value.Calls} ms={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F3} ownMs={pair.Value.OwnTicks * 1000.0 / Stopwatch.Frequency:F3} maxMs={pair.Value.Max * 1000.0 / Stopwatch.Frequency:F3}"));
    }
    private static void Begin(out Scope __state) => __state = _depth > 0 ? new Scope { Start = Stopwatch.GetTimestamp(), Children = _accounted } : default;
    private static void End(MethodBase __originalMethod, Scope __state)
    {
        if (__state.Start == 0) return;
        var elapsed = Stopwatch.GetTimestamp() - __state.Start;
        var own = elapsed - (_accounted - __state.Children);
        _accounted = __state.Children + elapsed;
        var stat = Stats[__originalMethod];
        stat.Calls++; stat.Ticks += elapsed; stat.OwnTicks += own; stat.Max = Math.Max(stat.Max, elapsed);
    }
}
