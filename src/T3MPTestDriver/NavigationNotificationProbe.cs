using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using Timberborn.Common;
using Debug = UnityEngine.Debug;
namespace T3MPTestDriver;

// Instrument registry call sites, not listener bodies or optimized game methods.
internal static class NavigationNotificationProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Stat { internal long Calls, Ticks; }
    private static readonly Dictionary<(Type,Type),Stat> Stats = new();
    private static bool _installed;
    private static long _flowCalls, _emptyFlows, _emptyLookups, _nonemptyNodes;
    [ThreadStatic] private static int _depth;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name)).First(t=>t!=null)!;
    private static class Callback<T> { internal static Action<T,NavMeshUpdate> Action = null!; }
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestNavNotificationProfile")) return;
        var ht=Find("HarmonyLib.Harmony"); var hm=Find("HarmonyLib.HarmonyMethod");
        var harmony=Activator.CreateInstance(ht,"t3mp.test.nav-notification-profile");
        var rewrite=typeof(NavigationNotificationProbe).GetMethod(nameof(Rewrite),All)!.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
        var targets=new[]{typeof(NavMeshListenerSingletonRegistry),typeof(NavMeshListenerEntityRegistry)}
            .SelectMany(t=>t.GetMethods(All).Where(m=>m.Name.StartsWith("NotifyAll"))).ToArray();
        foreach(var target in targets) ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,new object?[]{target,null,null,Activator.CreateInstance(hm,rewrite),null});
        foreach(var type in new[]{typeof(AccessFlowField),typeof(PathFlowField)})
            ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,new object?[]{type.GetMethod("OnNodesChanged",All),Activator.CreateInstance(hm,typeof(NavigationNotificationProbe).GetMethod(nameof(Flow),All)),null,null,null});
        _installed=true; Debug.Log("[T3MPNAVNOTIFY] registry call-site profile installed");
    }
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> code)
    {
        var op=typeof(T).GetField("opcode")!; var operand=typeof(T).GetField("operand")!; var count=0;
        foreach(var instruction in code)
        {
            if(operand.GetValue(instruction) is MethodInfo m && m.DeclaringType!.IsInterface &&
                m.Name.StartsWith("On") && m.GetParameters().Length==1 && m.GetParameters()[0].ParameterType==typeof(NavMeshUpdate))
            {
                var type=m.DeclaringType; var callback=typeof(Callback<>).MakeGenericType(type);
                callback.GetField("Action",All)!.SetValue(null,Delegate.CreateDelegate(typeof(Action<,>).MakeGenericType(type,typeof(NavMeshUpdate)),m));
                operand.SetValue(instruction,typeof(NavigationNotificationProbe).GetMethod(nameof(Notify),All)!.MakeGenericMethod(type));
                op.SetValue(instruction,OpCodes.Call); count++;
            }
            yield return instruction;
        }
        if(count==0)throw new Exception("No navigation listener calls found");
    }
    private static void Notify<T>(T listener,NavMeshUpdate update)
    {
        if(_depth==0){Callback<T>.Action(listener,update);return;}
        var key=(typeof(T),listener!.GetType());
        if(!Stats.TryGetValue(key,out var stat))Stats.Add(key,stat=new Stat());
        var start=Stopwatch.GetTimestamp();
        try{Callback<T>.Action(listener,update);}finally{stat.Calls++;stat.Ticks+=Stopwatch.GetTimestamp()-start;}
    }
    private static void Flow(object __instance, ReadOnlyList<int> nodeIds)
    {
        if(_depth==0)return;
        var count=__instance is AccessFlowField a?a._nodes.Count:((PathFlowField)__instance)._nodes.Count;
        _flowCalls++; _nonemptyNodes+=count;
        if(count==0){_emptyFlows++;_emptyLookups+=nodeIds.Count;}
    }
    internal static void BeginService(IPostLoadableSingleton service)
    {if(_installed&&service is NavigationSynchronizer&&_depth++==0){Stats.Clear();_flowCalls=_emptyFlows=_emptyLookups=_nonemptyNodes=0;}}
    internal static void EndService(IPostLoadableSingleton service)
    {
        if(!_installed||!(service is NavigationSynchronizer)||--_depth!=0)return;
        Debug.Log($"[T3MPNAVNOTIFY] flowCalls={_flowCalls} emptyFlows={_emptyFlows} emptyLookups={_emptyLookups} storedNodes={_nonemptyNodes}");
        foreach(var pair in Stats.OrderByDescending(p=>p.Value.Ticks))
            Debug.Log(FormattableString.Invariant($"[T3MPNAVNOTIFY] {pair.Key.Item1.Name}:{pair.Key.Item2.FullName} calls={pair.Value.Calls} ms={pair.Value.Ticks*1000.0/Stopwatch.Frequency:F3}"));
    }
}
