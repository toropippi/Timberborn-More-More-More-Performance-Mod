using System;
using System.Reflection;
using Timberborn.TubeSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Vanilla skips inside characters before clearing their previous tube visit.
// End that visual membership without showing the hidden building occupant.
internal static class TubeVisitFix
{
    private const string Owner = "t3mp.runtime.tube-visit";
    internal static long Repairs;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
            apply(RuntimePatches.Find(typeof(TubeVisitor), "UpdateVisit", Type.EmptyTypes), null, nameof(AfterUpdateVisit), null, null),
            typeof(TubeVisitFix));
        Installed = true;
        Debug.Log("[T3MP] Tube visit fix installed.");
    }

    private static void AfterUpdateVisit(TubeVisitor __instance)
    {
        if (__instance._enterer.IsInside && __instance._currentTube)
        {
            // RemoveVisitor notifies listeners before ExitTube finishes updating membership.
            // Let failures propagate as on a native exit; the partial change cannot be rolled back here.
            __instance.ExitTube(__instance._currentTube, false);
            // Recompute membership on the next outside sample, even in the old cell.
            __instance._lastGridPosition = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            Repairs++;
        }
    }
}
