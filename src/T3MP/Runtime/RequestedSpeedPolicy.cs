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
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
            apply(RuntimePatches.Find(typeof(SpeedManager), nameof(SpeedManager.ChangeSpeedScale), new[] { typeof(float) }),
                nameof(KeepRequestedSpeed), null, null, null), typeof(RequestedSpeedPolicy));
        Installed = true;
        Debug.Log("[T3MP] Requested speed policy installed (population scaling disabled; speed buttons x1/x3/x7).");
    }

    private static void KeepRequestedSpeed(ref float __0)
    {
        // Leave invalid arguments to native validation. Do not skip the method
        // or change the requested speed, deferred speed change, or lock state.
        if (__0 >= 0f && __0 <= 1f) __0 = 1f;
    }
}
