using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.Common;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;
namespace T3MP.Loading;

// Empty flow fields cannot intersect changed node IDs. Preserve all cache flags
// and keep native invalidation for populated fields and runtime notifications.
internal static class EmptyFlowFieldLoad
{
    private const string Owner = "t3mp.load.empty-flow-fields";
    private const BindingFlags All = LoadPatchBridge.All;
    internal static bool Installed { get; private set; }
    [ThreadStatic] private static int _depth;
    private static bool _compatible;
    private static long _skipped, _lookups;
    private static readonly MethodBase[] Protected = new[] { typeof(AccessFlowField), typeof(PathFlowField) }
        .SelectMany(t=>new[]{t.GetMethod("OnNodesChanged",All)!,t.GetMethod("HasNode",All)!})
        .Concat(new[]{typeof(ReadOnlyList<int>).GetProperty("Count")!.GetMethod!,typeof(ReadOnlyList<int>).GetProperty("Item")!.GetMethod!}).ToArray();
    private static bool Reviewed() => LoadCompatibility.Reviewed(
        "Timberborn.Navigation|b848ebf8-28b6-4c17-ab4f-f91582085afa",
        "Timberborn.Common|88d60edf-d568-470b-af44-77abc48c8bd0",
        "mscorlib|816f70bc-2a9c-4332-b0bd-119906a8d40e");
    private static bool Compatible() => Reviewed() && LoadCompatibility.Unmodified(Protected,Owner);
    internal static void Install()
    {
        if(Installed || Environment.GetCommandLineArgs().Contains("-t3mpTestEmptyFlowBaseline"))return;
        if(!Reviewed()){Debug.Log("[T3MPEMPTYFLOW] native fallback: unreviewed modules");return;}
        var ht=LoadPatchBridge.Find("HarmonyLib.Harmony");var hm=LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony=Activator.CreateInstance(ht,Owner)!;
        var patch=ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        object Hook(string name)=>Activator.CreateInstance(hm,typeof(EmptyFlowFieldLoad).GetMethod(name,All))!;
        try
        {
            patch.Invoke(harmony,new object?[]{typeof(NavigationSynchronizer).GetMethod("PostLoad",All),Hook(nameof(Begin)),null,null,Hook(nameof(End))});
            patch.Invoke(harmony,new object?[]{typeof(AccessFlowField).GetMethod("OnNodesChanged",All),Hook(nameof(Access)),null,null,null});
            patch.Invoke(harmony,new object?[]{typeof(PathFlowField).GetMethod("OnNodesChanged",All),Hook(nameof(Path)),null,null,null});
            Installed=true;Debug.Log("[T3MPEMPTYFLOW] installed; initial navigation notifications only");
        }
        catch(Exception e)
        {
            ht.GetMethod("UnpatchAll",All)!.Invoke(harmony,new object[]{Owner});
            Debug.LogWarning("[T3MPEMPTYFLOW] disabled: "+e.GetBaseException().Message);
        }
    }
    private static void Begin(out bool __state)
    {
        __state=true;
        if(_depth++!=0)return;
        _compatible=Compatible();_skipped=_lookups=0;
    }
    private static void End(bool __state)
    {
        if(!__state||_depth<=0||--_depth!=0)return;
        Debug.Log($"[T3MPEMPTYFLOW] skipped={_skipped} avoidedLookups={_lookups} compatible={_compatible}");
    }
    private static bool Access(AccessFlowField __instance, ReadOnlyList<int> nodeIds)
    {
        if(_depth==0||!_compatible||__instance.GetType()!=typeof(AccessFlowField)||__instance._nodes==null||
            __instance._nodes.Count!=0||!ReferenceEquals(__instance._nodes.Comparer,EqualityComparer<int>.Default))return true;
        return Skip(nodeIds);
    }
    private static bool Path(PathFlowField __instance, ReadOnlyList<int> nodeIds)
    {
        if(_depth==0||!_compatible||__instance.GetType()!=typeof(PathFlowField)||__instance._nodes==null||
            __instance._nodes.Count!=0||!ReferenceEquals(__instance._nodes.Comparer,EqualityComparer<int>.Default))return true;
        return Skip(nodeIds);
    }
    private static bool Skip(ReadOnlyList<int> nodeIds)
    {
        // Native code always reads Count, even for an empty field. Preserve
        // the exception for a default/null-backed ReadOnlyList.
        var count=nodeIds.Count;
        _skipped++;_lookups+=count;
        return false;
    }
}
