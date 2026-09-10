using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace T3MPTestDriver;

// Development-only full-tick counter (postfix on TickProgressService.Tick).
// Lives in the test driver so the shipped mod carries no probes.
internal static class FullTickCounter
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static long _fullTicks;
    private static bool _installed;

    internal static long FullTicks => Interlocked.Read(ref _fullTicks);

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
            var ht = Find("HarmonyLib.Harmony");
            var hm = Find("HarmonyLib.HarmonyMethod");
            var harmony = Activator.CreateInstance(ht, "t3mp.test.fulltick")!;
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            var target = Find("Timberborn.TimeSystem.TickProgressService").GetMethods(All | BindingFlags.DeclaredOnly)
                .First(m => m.Name == "Tick" && m.GetParameters().Length == 0);
            var postfix = Activator.CreateInstance(hm, typeof(FullTickCounter).GetMethod(nameof(Postfix), All));
            patch.Invoke(harmony, new object?[] { target, null, postfix, null, null });
            Debug.Log("[T3MPTEST] Full tick counter installed.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPTEST] Full tick counter unavailable: " + exception.GetBaseException().Message);
        }
    }

    private static void Postfix() => Interlocked.Increment(ref _fullTicks);
}
