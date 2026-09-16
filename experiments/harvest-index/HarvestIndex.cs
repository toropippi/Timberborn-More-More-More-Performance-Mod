using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.InventorySystem;
using Timberborn.YielderFinding;
using Debug=UnityEngine.Debug;

namespace T3MP.Runtime;

// Compile/CLI opt-in prototype. Native reachability, eligibility, living-state
// checks and enumeration execute every query. Only nearest-per-good reduction
// uses a shared synchronized segment index. Native final capacity selection stays.
internal static class HarvestIndex
{
    private const string Owner="t3mp.experiment.harvest-index";
    private sealed class Workspace
    {
        internal int Id;
        internal SharedClosestIndex? Index;
        internal readonly List<HarvestDistance> Distances=new List<HarvestDistance>();
        internal readonly List<ReachableYielder> Candidates=new List<ReachableYielder>();
        internal bool Busy,UseIndex;
    }
    private sealed class PersistentIndex
    {
        internal readonly int Id=Interlocked.Increment(ref _ids);
        internal readonly SharedClosestIndex Index=new SharedClosestIndex();
    }
    private static readonly ConditionalWeakTable<Inventory,PersistentIndex> Indexes=new ConditionalWeakTable<Inventory,PersistentIndex>();
    private static readonly ConditionalWeakTable<Inventory,PersistentIndex>.CreateValueCallback CreateIndex=_=>new PersistentIndex();
    [ThreadStatic] private static Inventory? _inventoryContext;
    private static readonly ConditionalWeakTable<ClosestYielderFinder,Workspace> Workspaces=new ConditionalWeakTable<ClosestYielderFinder,Workspace>();
    private static readonly ConditionalWeakTable<ClosestYielderFinder,Workspace>.CreateValueCallback Create=_=>new Workspace();
    private static Type? _harmony;
    private static MethodInfo? _target,_add,_parent;
    private static RuntimePatches.Shape? _shape;
    private static int _generation,_invalidated;
    private static int _ids;
    private static readonly bool Validate=Environment.GetCommandLineArgs().Contains("-t3mpTestHarvestIndexValidate",StringComparer.OrdinalIgnoreCase);
    internal static bool Installed {get;private set;}
    internal static bool Active=>Installed && Volatile.Read(ref _invalidated)==0;
    private static long _calls,_validated,_farm,_forestry,_inputs,_rebuilds,_updates;

    internal static void Install(Type harmony,Type harmonyMethod,MethodInfo patch)
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestHarvestIndex",StringComparer.OrdinalIgnoreCase))return;
        _harmony=harmony;
        Installed=RuntimePatches.TryInstall(Owner,harmony,harmonyMethod,patch,apply=>{
            _target=typeof(ClosestYielderFinder).GetMethod("FindClosestYielders",RuntimePatches.All)!;
            _add=typeof(ClosestYielderFinder).GetMethod("AddCloserYielder",RuntimePatches.All)!;
            _parent=typeof(ClosestYielderFinder).GetMethods(RuntimePatches.All).Single(m=>m.Name=="FindYielder"&&m.GetParameters().Length==4);
            if(!RuntimePatches.ReviewedBody(_parent,"D46A7AC4203CE43CAB8296CBBDB18A9C75C59919165F35B32EB65EB7D157AAF3","81FEDC0BACDF67938C59600DE83E2740EC8A23D41F34A4D550B587B310C197AF")||RuntimePatches.ForeignPatched(harmony,_parent,Owner,false))
                throw new InvalidOperationException("Unreviewed or patched harvest scope");
            // Filled from reviewed native IL for both supported API versions.
            if(!RuntimePatches.ReviewedBody(_target, "BECF5BAC8A29432835519699A314C4548CA411943E7DBC7587D3CEE06B62BE19","A3BB453339F9C460EC8EF0A766182CBF412D3ABA50D28053956839F24CAADE58") || !RuntimePatches.ReviewedBody(_add,"26CE5D889174E25467F690112A0B830B2EA9FAA1593238ACD23D9105F14B6E34"))
                throw new InvalidOperationException("Unreviewed closest-yielder reducer");
            if(RuntimePatches.ForeignPatched(harmony,_target,Owner,false)||RuntimePatches.ForeignPatched(harmony,_add,Owner,false))
                throw new InvalidOperationException("Patched closest-yielder reducer");
            _shape=RuntimePatches.OriginalShape(harmony,_target);
            apply(_parent,nameof(BeforeParent),null,null,nameof(AfterParent));
            apply(_target,nameof(Begin),nameof(End),nameof(Rewrite),nameof(Finish));
        },typeof(HarvestIndex));
        if(Installed)Debug.Log("[T3MP] Experimental synchronized harvest index installed.");
    }

    internal static void Revalidate()
    {
        if(!Installed||_harmony==null||_target==null||_add==null||_parent==null)return;
        if(!RuntimePatches.ForeignPatched(_harmony,_target,Owner,false)&&!RuntimePatches.ForeignPatched(_harmony,_add,Owner,false)&&!RuntimePatches.ForeignPatched(_harmony,_parent,Owner,false))return;
        Interlocked.Exchange(ref _invalidated,1);RuntimePatches.Unpatch(_harmony,Owner);Installed=false;
    }

    private static void BeforeParent(Inventory __0,out Inventory? __state)
    {__state=_inventoryContext;_inventoryContext=__0;}
    private static void AfterParent(Inventory? __state)=>_inventoryContext=__state;

    private static void Begin(ClosestYielderFinder __instance,out Workspace? __state)
    {
        __state=null;
        if(Workspaces.TryGetValue(__instance,out var existing)&&existing.Busy)
        {if(existing.UseIndex)Flush(__instance,existing);existing.UseIndex=false;return;}
        if(!Active||_inventoryContext==null||__instance._yielders.Count!=0||!ReferenceEquals(__instance._yielders.Comparer,EqualityComparer<string>.Default))return;
        var work=Workspaces.GetValue(__instance,Create);
        var persistent=Indexes.GetValue(_inventoryContext,CreateIndex);work.Id=persistent.Id;work.Index=persistent.Index;
        work.Busy=work.UseIndex=true;__state=work;
    }

    private static void Capture(ClosestYielderFinder instance,ReachableYielder candidate)
    {
        if(!Workspaces.TryGetValue(instance,out var work)||!work.UseIndex)
        {instance.AddCloserYielder(candidate);return;}
        if(!Active){Flush(instance,work);work.UseIndex=false;instance.AddCloserYielder(candidate);return;}
        var good=candidate.Yielder.Yield.GoodId;
        if(good==null){Flush(instance,work);work.UseIndex=false;throw new ArgumentNullException("key");}
        work.Distances.Add(new HarvestDistance(good,candidate.Distance));
        work.Candidates.Add(candidate);
        if(Validate)instance.AddCloserYielder(candidate);
    }

    private static void End(ClosestYielderFinder __instance,Workspace? __state)
    {
        if(__state==null||!__state.UseIndex)return;
        if(!Active){Flush(__instance,__state);__state.UseIndex=false;return;}
        var index=__state.Index!;var rebuilds=index.Rebuilds;var updates=index.PointUpdates;
        index.Synchronize(__state.Distances);
        if(Validate)
        {
            if(index.GroupCount!=__instance._yielders.Count)throw new InvalidOperationException("Harvest index group count mismatch");
            var group=0;
            foreach(var pair in __instance._yielders)
            {
                var got=__state.Candidates[index.Winner(group)];
                if(index.Good(group)!=pair.Key || !ReferenceEquals(got.Yielder,pair.Value.Yielder)||!got.Distance.Equals(pair.Value.Distance))
                    throw new InvalidOperationException("Harvest index candidate identity mismatch");
                group++;
            }
            _validated++;
            var frames=new System.Diagnostics.StackTrace().GetFrames();
            if(frames.Any(f=>f.GetMethod()?.DeclaringType?.Name=="HarvestStarter"))_farm++;
            if(frames.Any(f=>f.GetMethod()?.DeclaringType?.Name=="LumberjackFlagWorkplaceBehavior"))_forestry++;
            if(_validated<=256)Debug.Log("[T3MPHARVESTINDEXINPUT] "+UnityEngine.JsonUtility.ToJson(new Snapshot {
                finder=__state.Id,goods=__state.Distances.Select(x=>x.Good).ToArray(),distances=__state.Distances.Select(x=>x.Distance).ToArray()}));
        }
        else for(var group=0;group<index.GroupCount;group++)__instance._yielders.Add(index.Good(group),__state.Candidates[index.Winner(group)]);
        _calls++;_inputs+=__state.Candidates.Count;_rebuilds+=index.Rebuilds-rebuilds;_updates+=index.PointUpdates-updates;
    }

    private static void Flush(ClosestYielderFinder instance,Workspace work)
    {
        if(Validate)return; // Native additions already happened in shadow mode.
        for(var i=0;i<work.Candidates.Count;i++)
        {
            var key=work.Distances[i].Good;var candidate=work.Candidates[i];
            if(!instance._yielders.TryGetValue(key,out var prior)||candidate.Distance<prior.Distance)instance._yielders[key]=candidate;
        }
    }

    private static void Finish(ClosestYielderFinder __instance,Workspace? __state,Exception? __exception)
    {
        if(__state==null)return;
        try {if(__exception!=null && __state.UseIndex && !Validate)Flush(__instance,__state);}
        finally {__state.Candidates.Clear();__state.Distances.Clear();__state.Index=null;__state.Busy=__state.UseIndex=false;}
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list=new List<T>(instructions);
        if(Interlocked.Increment(ref _generation)!=1 || _shape==null || !RuntimePatches.SameShape(_shape,RuntimePatches.DescribeShape(list)))
        {Interlocked.Exchange(ref _invalidated,1);return list;}
        var op=typeof(T).GetField("opcode")!;var operand=typeof(T).GetField("operand")!;var matches=0;
        foreach(var instruction in list)
            if(Equals(operand.GetValue(instruction),_add))
            {op.SetValue(instruction,OpCodes.Call);operand.SetValue(instruction,typeof(HarvestIndex).GetMethod(nameof(Capture),RuntimePatches.All));matches++;}
        if(matches!=1)throw new InvalidOperationException("Unexpected reducer call sites");
        return list;
    }
    [Serializable]private sealed class Snapshot {public int finder;public string[] goods=Array.Empty<string>();public float[] distances=Array.Empty<float>();}
    internal static string Summary()=>"calls="+_calls+",validated="+_validated+",farm="+_farm+",forestry="+_forestry+",inputs="+_inputs+",rebuilds="+_rebuilds+",pointUpdates="+_updates;
}
