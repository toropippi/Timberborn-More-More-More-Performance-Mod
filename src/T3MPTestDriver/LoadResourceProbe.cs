using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Coarse scopes only: no per-cell or per-entity measurement overhead.
internal static class LoadResourceProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryThreadCycleTime(IntPtr handle, out ulong cycles);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long value);
    internal struct State { public long Wall, Qpc, ThreadCpu, ProcessCpu, Heap, Allocated; public ulong Cycles; public int Gc; public uint Thread; }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestResourceProfile")) return;
        var ht = Find("HarmonyLib.Harmony");
        var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.load-resources");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var prefix = Activator.CreateInstance(hm, typeof(LoadResourceProbe).GetMethod(nameof(Begin), All));
        var finalizer = Activator.CreateInstance(hm, typeof(LoadResourceProbe).GetMethod(nameof(End), All));
        var targets = new HashSet<MethodInfo>();
        foreach (var entry in new[] {
            "Timberborn.SingletonSystem.SingletonLifecycleService|LoadAll,LoadSingletons,LoadNonSingletons,PostLoadSingletons",
            "Timberborn.WorldPersistence.WorldEntitiesLoader|InstantiateEntities,PostLoadNonSingletons",
            "Timberborn.WorldPersistence.EntitiesLoader|Load,BatchLoad,PreInitialize,Initialize,PostInitialize",
            "Timberborn.WorldSerialization.WorldSerializer|ReadFromSaveEntryStream",
            "Timberborn.Navigation.NavigationSynchronizer|PostLoad,ProcessPreviewChanges,ProcessInstantChanges,ProcessRegularChanges,NotifyAllNavmeshChanges",
            "Timberborn.WaterSystem.WaterSimulator|Load,PostLoad"
        })
        {
            var parts = entry.Split('|');
            var type = Find(parts[0]);
            foreach (var method in type.GetMethods(All).Where(m => parts[1].Split(',').Contains(m.Name) && !m.ContainsGenericParameters))
            {
                if (type.Name == "EntitiesLoader" && method.Name == "Load" && method.IsStatic) continue;
                targets.Add(method);
            }
        }
        foreach (var method in targets) patch.Invoke(harmony, new object?[] { method, prefix, null, null, finalizer });
        if (Environment.GetCommandLineArgs().Contains("-t3mpTestSingletonProfile"))
        {
            // Instrument two call sites rather than patching hundreds of service
            // methods, which used to trip the event router's compatibility guard.
            var rewrite = typeof(LoadResourceProbe).GetMethod(nameof(RewriteServiceCalls), All)!
                .MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
            foreach (var name in new[] { "LoadSingletons", "PostLoadSingletons" })
                patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod(name, All),
                    null, null, Activator.CreateInstance(hm, rewrite), null });
            Debug.Log("[T3MPSERVICE] caller-site attribution installed");
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var allocation = new byte[16384];
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(allocation);
        Debug.Log("[T3MPRESOURCE] allocationCounterProbe=" + delta + " (zero means unsupported; heapDelta is not allocated bytes)");
        Debug.Log("[T3MPRESOURCE] gcIncremental=" + UnityEngine.Scripting.GarbageCollector.isIncremental +
            " gcMode=" + UnityEngine.Scripting.GarbageCollector.GCMode +
            " gcSliceNs=" + UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds);
    }
    private static IEnumerable<T> RewriteServiceCalls<T>(IEnumerable<T> instructions)
    {
        var operand = typeof(T).GetField("operand")!;
        var opcode = typeof(T).GetField("opcode")!;
        var matches = 0;
        foreach (var instruction in instructions)
        {
            if (operand.GetValue(instruction) is MethodInfo method &&
                (method.DeclaringType == typeof(ILoadableSingleton) || method.DeclaringType == typeof(IPostLoadableSingleton)))
            {
                var hook = method.DeclaringType == typeof(ILoadableSingleton) ? nameof(LoadService) : nameof(PostLoadService);
                operand.SetValue(instruction, typeof(LoadResourceProbe).GetMethod(hook, All));
                opcode.SetValue(instruction, OpCodes.Call); matches++;
            }
            yield return instruction;
        }
        if (matches != 1) throw new Exception("Unexpected service call-site count: " + matches);
    }
    private static void LoadService(ILoadableSingleton service)
    {
        Begin(out var state);
        try { service.Load(); }
        finally { ReportService(service, "Load", state); }
    }
    private static void PostLoadService(IPostLoadableSingleton service)
    {
        EventSubscriptionProbe.BeforeEventDrain(service);
        LoadEventProbe.BeginService(service);
        NavigationNotificationProbe.BeginService(service);
        Begin(out var state);
        try { service.PostLoad(); }
        finally
        {
            ReportService(service, "PostLoad", state);
            LoadEventProbe.EndService(service);
            NavigationNotificationProbe.EndService(service);
        }
    }
    private static void ReportService(object service, string phase, State state)
    {
        var wall = (Stopwatch.GetTimestamp() - state.Wall) * 1000.0 / Stopwatch.Frequency;
        var heap = (GC.GetTotalMemory(false) - state.Heap) / 1048576.0;
        if (wall < 10 && Math.Abs(heap) < 1) return;
        GetThreadTimes(GetCurrentThread(), out _, out _, out var tk, out var tu);
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var pk, out var pu);
        Debug.Log(FormattableString.Invariant($"[T3MPSERVICE] {service.GetType().FullName}.{phase} wallMs={wall:F3} threadCpuMs={(tk + tu - state.ThreadCpu) / 10000.0:F3} processCpuMs={(pk + pu - state.ProcessCpu) / 10000.0:F3} heapDeltaMB={heap:F3} gc={GC.CollectionCount(0) - state.Gc}"));
    }
    private static void Begin(out State __state)
    {
        if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var tk, out var tu) ||
            !GetProcessTimes(GetCurrentProcess(), out _, out _, out var pk, out var pu) ||
            !QueryThreadCycleTime(GetCurrentThread(), out var cycles)) throw new Exception("CPU counters unavailable");
        QueryPerformanceCounter(out var qpc);
        __state = new State { Wall = Stopwatch.GetTimestamp(), Qpc = qpc, ThreadCpu = tk + tu, ProcessCpu = pk + pu,
            Heap = GC.GetTotalMemory(false), Allocated = GC.GetAllocatedBytesForCurrentThread(), Cycles = cycles,
            Gc = GC.CollectionCount(0), Thread = GetCurrentThreadId() };
    }
    private static void End(MethodBase __originalMethod, State __state, Exception? __exception)
    {
        LoadGcExperiment.Checkpoint();
        var wall = (Stopwatch.GetTimestamp() - __state.Wall) * 1000.0 / Stopwatch.Frequency;
        if (wall < 100 && __exception == null) return;
        GetThreadTimes(GetCurrentThread(), out _, out _, out var tk, out var tu);
        GetProcessTimes(GetCurrentProcess(), out _, out _, out var pk, out var pu);
        QueryThreadCycleTime(GetCurrentThread(), out var cycles);
        QueryPerformanceFrequency(out var frequency);
        Debug.Log(FormattableString.Invariant($"[T3MPRESOURCE] {__originalMethod.DeclaringType!.Name}.{__originalMethod.Name} wallMs={wall:F3} threadCpuMs={(tk + tu - __state.ThreadCpu) / 10000.0:F3} processCpuMs={(pk + pu - __state.ProcessCpu) / 10000.0:F3} threadCycles={cycles - __state.Cycles} gc={GC.CollectionCount(0) - __state.Gc} heapDeltaMB={(GC.GetTotalMemory(false) - __state.Heap) / 1048576.0:F3} allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - __state.Allocated} tid={__state.Thread} qpcStart={__state.Qpc} qpcFrequency={frequency} utc={DateTime.UtcNow:O} failed={__exception != null}"));
    }
}
