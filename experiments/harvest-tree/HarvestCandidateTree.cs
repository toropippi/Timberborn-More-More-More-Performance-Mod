using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Common;
using Timberborn.InventorySystem;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Experimental common final selection for farm/forestry and other native
// YielderFinder callers. Native eligibility, per-good closest selection and
// all path queries execute every time. This is not a persistent spatial index.
internal static class HarvestCandidateTree
{
    private const string Owner = "t3mp.experiment.harvest-tree";
    private static Type? _harmony;
    private static MethodInfo? _target;
    private static RuntimePatches.Shape? _shape;
    private static int _generation, _invalidated;
    private static readonly ConditionalWeakTable<ClosestYielderFinder, Workspace> Workspaces = new ConditionalWeakTable<ClosestYielderFinder, Workspace>();
    private static readonly ConditionalWeakTable<ClosestYielderFinder, Workspace>.CreateValueCallback CreateWorkspace = _ => new Workspace();
    private sealed class Workspace { internal bool Busy; internal readonly CandidateMinTree<ReachableYielder> Tree = new CandidateMinTree<ReachableYielder>(); }
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;
    private static readonly bool Validate = Environment.GetCommandLineArgs().Contains("-t3mpTestHarvestTreeValidate", StringComparer.OrdinalIgnoreCase);
    private static long _calls, _validated, _farm, _forestry;
    private static readonly long[] Groups = new long[9];
    private static MethodInfo[] _dependencies = Array.Empty<MethodInfo>();

    internal static void Install(Type harmony, Type harmonyMethod, MethodInfo patch)
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestHarvestTree",StringComparer.OrdinalIgnoreCase)) return;
        _harmony = harmony;
        Installed = RuntimePatches.TryInstall(Owner, harmony, harmonyMethod, patch, apply => {
            _target = typeof(ClosestYielderFinder).GetMethod("FindYielder", RuntimePatches.All, null, new[]{typeof(Inventory),typeof(int)}, null)!;
            var compare = typeof(ReachableYielder).GetMethod("CompareTo")!;
            var order = typeof(Yielder).GetProperty("InstantiationOrder")!.GetMethod!;
            if (!RuntimePatches.ReviewedBody(_target,
                "20E6F81AD527EE539DD04A5FDB87D7D447A7874AFE2D754BA8E5DD639BFBD95D",
                "D3015308C1056406509263C45CE5EE5CF0A7C1F5F7C9D6859563B1896C4B7575") ||
                !RuntimePatches.ReviewedBody(compare,"9D3BFC4127F1291C509A0B19AFCBE61B2360742BC3852519816BDC8CFA386F19") ||
                !RuntimePatches.ReviewedBody(order,"941E45AF9EB1E273001CBC15AD0B1F0D9E8B04346EA858BB407E92C00F4C137B","1581B0F63F37A7C2F1BBE6C9F2EFA5701E0842735FFDD84FF915696ECAE3345D"))
                throw new InvalidOperationException("Unreviewed yielder selection IL");
            _dependencies = new[]{compare,order}.Concat(typeof(SortedSet<ReachableYielder>).GetMethods(RuntimePatches.All))
                .Concat(typeof(Timberborn.Carrying.CarryAmountCalculator).GetMethods(RuntimePatches.All | BindingFlags.DeclaredOnly)).Distinct().ToArray();
            foreach(var m in _dependencies.Append(_target))
                if(RuntimePatches.ForeignPatched(harmony,m,Owner,false)) throw new InvalidOperationException("Patched yielder dependency: "+m.Name);
            _shape=RuntimePatches.OriginalShape(harmony,_target);
            apply(_target,null,null,nameof(Rewrite),null);
        }, typeof(HarvestCandidateTree));
        if(Installed) Debug.Log("[T3MP] Experimental shared harvest candidate tree installed.");
    }

    internal static void Revalidate()
    {
        if(!Installed || _harmony==null || _target==null) return;
        if(!_dependencies.Append(_target).Any(m=>RuntimePatches.ForeignPatched(_harmony,m,Owner,false))) return;
        Interlocked.Exchange(ref _invalidated,1);
        RuntimePatches.Unpatch(_harmony,Owner); Installed=false;
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list=new List<T>(instructions);
        if(Interlocked.Increment(ref _generation)!=1 || _shape==null || !RuntimePatches.SameShape(_shape,RuntimePatches.DescribeShape(list)))
        {Interlocked.Exchange(ref _invalidated,1);return list;}
        return RuntimePatches.CallAndReturn<T>(typeof(HarvestCandidateTree).GetMethod(nameof(Find),RuntimePatches.All)!,3);
    }

    private static YielderSearchResult Native(ClosestYielderFinder instance, Inventory inventory, int capacity)
    {
        instance._orderedYielders.AddRange(instance._yielders.Values);
        foreach(var reachable in instance._orderedYielders)
        {
            var yielder=reachable.Yielder;
            var amount=instance._carryAmountCalculator.AmountToCarry(capacity,yielder.Yield,inventory);
            if(amount.Amount>0) return YielderSearchResult.CreateSearchResult(yielder,amount);
        }
        return YielderSearchResult.CreateEmpty();
    }

    private static YielderSearchResult Find(ClosestYielderFinder instance, Inventory inventory, int capacity)
    {
        if(!Active || instance._orderedYielders.Count!=0 || !ReferenceEquals(instance._orderedYielders.Comparer,Comparer<ReachableYielder>.Default))
            return Native(instance,inventory,capacity);
        var workspace=Workspaces.GetValue(instance,CreateWorkspace);
        if(workspace.Busy) return Native(instance,inventory,capacity);
        workspace.Busy=true;
        var tree=workspace.Tree;
        try
        {
            tree.Build(instance._yielders.Values);
            if(Validate) ValidateOrder(instance,tree);
            _calls++;
            Groups[Math.Min(8,tree.Count)]++;
            while(tree.TryPop(out var reachable))
            {
                var yielder=reachable.Yielder;
                var amount=instance._carryAmountCalculator.AmountToCarry(capacity,yielder.Yield,inventory);
                if(amount.Amount>0) return YielderSearchResult.CreateSearchResult(yielder,amount);
            }
            return YielderSearchResult.CreateEmpty();
        }
        catch
        {
            // Native fills this set before invoking inventory/good callbacks.
            // Preserve its post-exception contents without repeating callbacks.
            try {instance._orderedYielders.AddRange(instance._yielders.Values);} catch { }
            throw;
        }
        finally {tree.Clear();workspace.Busy=false;}
    }

    private static void ValidateOrder(ClosestYielderFinder instance, CandidateMinTree<ReachableYielder> tree)
    {
        var native=new SortedSet<ReachableYielder>();native.AddRange(instance._yielders.Values);
        foreach(var expected in native)
            if(!tree.TryPop(out var got) || !ReferenceEquals(got.Yielder,expected.Yielder) || !got.Distance.Equals(expected.Distance))
                throw new InvalidOperationException("Harvest candidate identity/order mismatch");
        if(tree.TryPop(out _)) throw new InvalidOperationException("Extra harvest candidate");
        _validated++;
        var frames=new System.Diagnostics.StackTrace().GetFrames();
        if(frames.Any(f=>f.GetMethod()?.DeclaringType?.Name=="HarvestStarter")) _farm++;
        if(frames.Any(f=>f.GetMethod()?.DeclaringType?.Name=="LumberjackFlagWorkplaceBehavior")) _forestry++;
        if(_validated<=512)
            Debug.Log("[T3MPHARVESTINPUT] "+UnityEngine.JsonUtility.ToJson(new Snapshot {
                distances=instance._yielders.Values.Select(x=>x.Distance).ToArray(),
                orders=instance._yielders.Values.Select(x=>x.Yielder.InstantiationOrder).ToArray() }));
        tree.Build(instance._yielders.Values);
    }

    [Serializable] private sealed class Snapshot {public float[] distances=Array.Empty<float>(); public int[] orders=Array.Empty<int>();}

    internal static string Summary() => "calls="+_calls+",validated="+_validated+",farm="+_farm+",forestry="+_forestry+",groups0to8plus="+string.Join("/",Groups);
}
