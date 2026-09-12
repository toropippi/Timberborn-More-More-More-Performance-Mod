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
    // Raw-IL SHA256 of the guarded methods (scripts/IlFingerprint),
    // identical on the installed 1.0.13.1 and 1.1.2.0 builds.
    private const string ReviewedBody = "A0C77CBEED89E055729E17887CA4298209256E68C4FBF6D53A5198BF05191BA3";
    private const string ReviewedExitTubeBody = "63DFEF6D522BAC851A92C9D713A717EFE8B775E61A3C1C1DD546135BCB6CDA35";
    private const string ReviewedRemoveVisitorBody = "AB5522F91EC6E7532EBD7B53B2204B73E9628046054B554A8CD033168BC70016";
    private static Type? _harmonyType;
    private static MethodInfo? _target;
    private static MethodInfo? _exitTube;
    private static MethodInfo? _removeVisitor;
    private static bool _failureLogged;
    internal static long Repairs;
    internal static long Failures;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            var method = typeof(TubeVisitor).GetMethod("UpdateVisit", RuntimePatches.All | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(typeof(TubeVisitor).FullName, "UpdateVisit()");
            var exitTube = typeof(TubeVisitor).GetMethod("ExitTube", RuntimePatches.All | BindingFlags.DeclaredOnly,
                null, new[] { typeof(Tube), typeof(bool) }, null) ?? throw new MissingMethodException(typeof(TubeVisitor).FullName, "ExitTube(Tube, bool)");
            var removeVisitor = typeof(Tube).GetMethod("RemoveVisitor", RuntimePatches.All | BindingFlags.DeclaredOnly,
                null, new[] { typeof(TubeVisitor) }, null) ?? throw new MissingMethodException(typeof(Tube).FullName, "RemoveVisitor(TubeVisitor)");
            if (!RuntimePatches.ReviewedBody(method, ReviewedBody))
                throw new InvalidOperationException("TubeVisitor.UpdateVisit() body is not a reviewed build");
            if (!RuntimePatches.ReviewedBody(exitTube, ReviewedExitTubeBody))
                throw new InvalidOperationException("TubeVisitor.ExitTube(Tube, bool) body is not a reviewed build");
            if (!RuntimePatches.ReviewedBody(removeVisitor, ReviewedRemoveVisitorBody))
                throw new InvalidOperationException("Tube.RemoveVisitor(TubeVisitor) body is not a reviewed build");
            if (method.GetMethodBody()?.ExceptionHandlingClauses.Count != 0)
                throw new InvalidOperationException("TubeVisitor.UpdateVisit() has exception handling; not the reviewed build");
            if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, transpilersOnly: false))
                throw new InvalidOperationException("another mod patches TubeVisitor.UpdateVisit()");
            if (RuntimePatches.ForeignPatched(harmonyType, exitTube, Owner, transpilersOnly: false))
                throw new InvalidOperationException("another mod patches TubeVisitor.ExitTube(Tube, bool)");
            if (RuntimePatches.ForeignPatched(harmonyType, removeVisitor, Owner, transpilersOnly: false))
                throw new InvalidOperationException("another mod patches Tube.RemoveVisitor(TubeVisitor)");
            _target = method;
            _exitTube = exitTube;
            _removeVisitor = removeVisitor;
            apply(method, null, nameof(AfterUpdateVisit), null, null);
        }, typeof(TubeVisitFix));
        _harmonyType = harmonyType;
        if (Installed) Debug.Log("[T3MP] Tube visit fix installed.");
    }

    // Re-check at every world load for hooks installed later by another mod.
    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _target == null || _exitTube == null || _removeVisitor == null) return;
        try
        {
            if (!RuntimePatches.ForeignPatched(_harmonyType, _target, Owner, transpilersOnly: false) &&
                !RuntimePatches.ForeignPatched(_harmonyType, _exitTube, Owner, transpilersOnly: false) &&
                !RuntimePatches.ForeignPatched(_harmonyType, _removeVisitor, Owner, transpilersOnly: false)) return;
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
            Debug.Log("[T3MP] Tube visit fix removed: another mod now patches TubeVisitor.UpdateVisit(), TubeVisitor.ExitTube(Tube, bool), or Tube.RemoveVisitor(TubeVisitor); vanilla restored.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Tube visit revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static void AfterUpdateVisit(TubeVisitor __instance)
    {
        // BaseComponent's implicit bool preserves Unity's destroyed-object check.
        if (!__instance) return;
        if (__instance._enterer.IsInside && __instance._currentTube)
        {
            try
            {
                // Vanilla ExitTube allocates an EventHandler per repair, just as on a normal tube exit.
                __instance.ExitTube(__instance._currentTube, false);
                // Recompute membership on the next outside sample, even in the old cell.
                __instance._lastGridPosition = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
                Repairs++;
            }
            catch (Exception exception)
            {
                Failures++;
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    Debug.LogWarning("[T3MP] Tube visit repair failed; vanilla behavior retained: " + exception.Message);
                }
            }
        }
    }
}
