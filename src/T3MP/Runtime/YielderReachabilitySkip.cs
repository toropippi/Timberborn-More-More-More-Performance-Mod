using System;
using System.Collections.Generic;
using System.Reflection;
using Timberborn.InventorySystem;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.Navigation;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// YielderFinder projects every candidate yielder through a navigation path
// query (RegularYielderAsReachable / AccessibleYielderAsReachable) before
// ClosestYielderFinder decides whether the candidate matters. A candidate that
// is not yielding contributes only to the "something is in range" flag, and
// only when that flag is still false and it is a living resource; once the
// flag is set, or when it is neither yielding nor alive, its path query result
// is never used. Those queries are skipped. Every path query that can affect
// the result is still made through the original helpers, in the original
// candidate order, and the closest-yielder selection is unchanged.
internal static class YielderReachabilitySkip
{
    private const string Owner = "t3mp.runtime.yielder-reachability";
    internal static long Queried, Skipped;
    internal static bool Installed { get; private set; }
    // Plain counters for the driver's state report: candidates queried through a
    // path search and candidates skipped.
    internal static string Summary() => !Installed ? "disabled" : "queried=" + Queried + ",skipped=" + Skipped;

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            apply(RuntimePatches.Find(typeof(YielderFinder), "FindLivingYielderWithoutAccessible"), null, null, nameof(RewriteLiving), null);
            apply(RuntimePatches.Find(typeof(YielderFinder), "FindYielderWithAccessible"), null, null, nameof(RewriteAccessible), null);
        }, typeof(YielderReachabilitySkip));
        Installed = true;
        Debug.Log("[T3MP] Yielder reachability skip installed.");
    }

    private static YielderSearchResult FindLivingYielderWithoutAccessible(YielderFinder self, Inventory receivingInventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders) =>
        Search(self._closestYielderFinder, receivingInventory, start, liftingCapacity, yielders, true, false);

    private static YielderSearchResult FindYielderWithAccessible(YielderFinder self, Inventory receivingInventory, Accessible start, int liftingCapacity, IEnumerable<Yielder> yielders) =>
        Search(self._closestYielderFinder, receivingInventory, start, liftingCapacity, yielders, false, true);

    // ClosestYielderFinder.FindYielder(inventory, capacity, projected, isLiving)
    // with the projection fused into the candidate loop.
    private static YielderSearchResult Search(ClosestYielderFinder finder, Inventory receivingInventory, Accessible start, int liftingCapacity,
        IEnumerable<Yielder> yielders, bool isLiving, bool accessible)
    {
        bool flag = false;
        bool queried = false;
        foreach (Yielder candidate in yielders)
        {
            bool prechecked = false;
            bool candidateYielding = false;
            // The first candidate is always queried: the native query fills the
            // start node's terrain flow field as a side effect, and one query
            // leaves that cache in the same state as native's queries.
            // Lifetime-invalid candidates take the native sequence unchanged.
            if (queried && (bool)candidate)
            {
                prechecked = true;
                candidateYielding = candidate.IsYielding;
                bool contributes;
                if (candidateYielding) contributes = true;
                else if (flag || !isLiving) contributes = false;
                else
                {
                    // IsAlive() dereferences the component; a candidate without it
                    // is left to the native sequence (which throws only if reachable).
                    var resource = candidate.GetComponent<LivingNaturalResource>();
                    contributes = ReferenceEquals(resource, null) || !resource.IsDead;
                }
                if (!contributes) { Skipped++; continue; }
            }
            Queried++;
            queried = true;
            ReachableYielder reachableYielder = accessible ? YielderFinder.AccessibleYielderAsReachable(start, candidate) : YielderFinder.RegularYielderAsReachable(start, candidate);
            Yielder yielder = reachableYielder.Yielder;
            if ((bool)yielder)
            {
                bool isYielding = prechecked ? candidateYielding : yielder.IsYielding;
                flag = flag || isYielding || (isLiving && yielder.IsAlive());
                if (isYielding) finder.AddCloserYielder(reachableYielder);
            }
        }
        if (!flag) return YielderSearchResult.CreateNoYielderInRange();
        YielderSearchResult result = finder.FindYielder(receivingInventory, liftingCapacity);
        finder._yielders.Clear();
        finder._orderedYielders.Clear();
        return result;
    }

    private static IEnumerable<T> RewriteLiving<T>(IEnumerable<T> instructions) =>
        RuntimePatches.CallAndReturn<T>(typeof(YielderReachabilitySkip).GetMethod(nameof(FindLivingYielderWithoutAccessible), RuntimePatches.All)!, 5);
    private static IEnumerable<T> RewriteAccessible<T>(IEnumerable<T> instructions) =>
        RuntimePatches.CallAndReturn<T>(typeof(YielderReachabilitySkip).GetMethod(nameof(FindYielderWithAccessible), RuntimePatches.All)!, 5);
}
