using System;
using System.Reflection;
using Timberborn.TimeSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Product speed policy, separate from reducing CPU work: retain the original
// T3MP behavior where population does not reduce the selected 1/3/7 speed.
// Native speed changes, pause/lock handling and notifications still execute.
internal static class RequestedSpeedPolicy
{
    private const string Owner = "t3mp.speed.requested";
    // Identical ChangeSpeedScale(float) IL on 1.0.13.1 and 1.1.2.4.
    private const string ReviewedBody = "B139BD751F2DCE8355ED0C23B9A4475CCB0311E093F24B12BC474D1C850077EB";
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            var target = typeof(SpeedManager).GetMethod(nameof(SpeedManager.ChangeSpeedScale),
                RuntimePatches.All, null, new[] { typeof(float) }, null)
                ?? throw new MissingMethodException("SpeedManager.ChangeSpeedScale(float)");
            if (!RuntimePatches.ReviewedBody(target, ReviewedBody))
                throw new InvalidOperationException("SpeedManager.ChangeSpeedScale is not a reviewed build");
            apply(target, nameof(KeepRequestedSpeed), null, null, null);
        }, typeof(RequestedSpeedPolicy));
        if (Installed) Debug.Log("[T3MP] Requested speed policy installed (population scaling disabled; speed buttons x1/x3/x7).");
    }

    private static void KeepRequestedSpeed(ref float __0)
    {
        // Leave invalid arguments to native validation. Do not skip the method
        // or change the requested speed, deferred speed change, or lock state.
        if (__0 >= 0f && __0 <= 1f) __0 = 1f;
    }
}
