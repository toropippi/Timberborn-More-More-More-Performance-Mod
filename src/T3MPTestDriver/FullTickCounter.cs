using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace T3MPTestDriver;

// Development-only counter of TickProgressService.Tick in the singleton bucket.
// This marks cycle starts, not completion of the later entity buckets. Probes
// needing completed world state must also verify the native bucket boundary.
// Lives in the test driver so the shipped mod carries no probes.
internal static class FullTickCounter
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static long _fullTicks;
    private static bool _installed;

    internal static long FullTicks => Interlocked.Read(ref _fullTicks);
    internal static bool Available { get; private set; }

    internal static void Install()
    {
        if (_installed) return;
        // A stray driver install without any -t3mpTest* argument patches nothing.
        if (!Environment.GetCommandLineArgs().Any(a => a.StartsWith("-t3mpTest", StringComparison.OrdinalIgnoreCase))) return;
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
            Available = true;
            Debug.Log("[T3MPTEST] Full tick counter installed.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPTEST] Full tick counter unavailable: " + exception.GetBaseException().Message);
        }
    }

    private static void Postfix()
    {
        var tick = Interlocked.Increment(ref _fullTicks);
        RoadReachabilityExperiment.SimulationStarted();
        FixedTickBenchmark.RecordTick(tick);
    }
}
