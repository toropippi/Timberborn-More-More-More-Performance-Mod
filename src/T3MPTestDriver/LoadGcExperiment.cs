using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine.Scripting;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Explicit development switch. Defers GC only within LoadAll and
// pays for one full collection BEFORE LoadAll returns and the scene timer ends.
internal static class LoadGcExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int _depth;
    private static bool _ownsMode, _participated;
    private static bool _headroomBudget;
    private static long _limit, _peak;
    private static int _startGc;
    private static GarbageCollector.Mode _previous, _requested;
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        var production = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Loading.LoadGcBudget")).FirstOrDefault(t => t != null);
        if (production != null && (bool)(production.GetProperty("Installed", All)?.GetValue(null) ?? false)) return;
        if (!args.Contains("-t3mpTestLoadGcBatch") && !args.Contains("-t3mpTestLoadGcDisabled")) return;
        _requested = args.Contains("-t3mpTestLoadGcDisabled") ? GarbageCollector.Mode.Disabled : GarbageCollector.Mode.Manual;
        _headroomBudget = args.Contains("-t3mpTestLoadGcHeadroom");
        if (_headroomBudget) ValidateBudgetPolicy();
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.load-gc");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name)
        {
            var hook = Activator.CreateInstance(hm, typeof(LoadGcExperiment).GetMethod(name, All))!;
            hm.GetField("priority")!.SetValue(hook, 800); return hook;
        }
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All),
            Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        Debug.Log("[T3MPLOADGC] installed; requestedMode=" + _requested + "; final collection included");
    }
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _previous = GarbageCollector.GCMode;
        _peak = GC.GetTotalMemory(false); _startGc = GC.CollectionCount(0);
        // A soft budget sampled at coarse load-phase boundaries, not a hard
        // allocation cap. Leave adequate free memory before this experiment.
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryKnown = GlobalMemoryStatusEx(ref memory);
        _limit = memoryKnown ? ChooseLimit(memory.AvailablePhysical, _headroomBudget) : 0;
        _ownsMode = _previous == GarbageCollector.Mode.Enabled && _limit > 0 && _peak < _limit;
        _participated = _ownsMode;
        if (_ownsMode) GarbageCollector.GCMode = _requested;
        Debug.Log("[T3MPLOADGC] begin mode=" + GarbageCollector.GCMode + " participating=" + _participated + " heapMB=" + _peak / 1048576 + " softLimitMB=" + _limit / 1048576 + " headroomBudget=" + _headroomBudget + " availablePhysicalMB=" + (memoryKnown ? memory.AvailablePhysical / 1048576 : 0));
    }
    private static long ChooseLimit(ulong available, bool headroomBudget) => available < (16UL << 30) ? 0 :
        headroomBudget && available >= (24UL << 30) ? 12L << 30 : 8L << 30;

    private static void ValidateBudgetPolicy()
    {
        foreach (var adaptive in new[] { false, true })
        foreach (var free in new[] { 0UL, (16UL << 30) - 1, 16UL << 30, (24UL << 30) - 1, 24UL << 30, 96UL << 30 })
        {
            var chosen = ChooseLimit(free, adaptive);
            if (chosen < 0 || chosen > (12L << 30) || (ulong)chosen > free / 2 ||
                (free < (16UL << 30) && chosen != 0) ||
                (free >= (16UL << 30) && chosen != ((adaptive && free >= (24UL << 30) ? 12L : 8L) << 30)))
                throw new InvalidOperationException("Invalid load GC memory budget");
        }
        Debug.Log("[T3MPLOADGC] headroom policy boundary checks PASS cases=12");
    }
    internal static void Checkpoint()
    {
        if (_depth == 0) return;
        _peak = Math.Max(_peak, GC.GetTotalMemory(false));
        Debug.Log("[T3MPLOADGC] checkpoint mode=" + GarbageCollector.GCMode + " heapMB=" + _peak / 1048576 + " collections=" + (GC.CollectionCount(0) - _startGc));
        if (_ownsMode && _headroomBudget)
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory) || memory.AvailablePhysical < (8UL << 30))
            {
                Restore();
                Debug.Log("[T3MPLOADGC] physical memory reserve reached or unavailable; automatic GC restored");
            }
        }
        if (_ownsMode && _peak > _limit)
        {
            Restore();
            Debug.Log("[T3MPLOADGC] soft memory budget reached; automatic GC restored");
        }
    }
    private static void Restore()
    {
        if (_ownsMode && GarbageCollector.GCMode == _requested) GarbageCollector.GCMode = _previous;
        _ownsMode = false;
    }
    private static void End()
    {
        if (--_depth != 0) return;
        _peak = Math.Max(_peak, GC.GetTotalMemory(false));
        var timer = Stopwatch.StartNew();
        try
        {
            // Also settle remaining garbage when the soft budget previously
            // restored automatic GC. Otherwise its cost could escape this timer.
            Restore();
            if (_participated && GarbageCollector.GCMode != GarbageCollector.Mode.Disabled) GC.Collect();
        }
        finally { Restore(); }
        timer.Stop();
        Debug.Log(FormattableString.Invariant($"[T3MPLOADGC] finalCollectionMs={timer.Elapsed.TotalMilliseconds:F3} observedPeakMB={_peak / 1048576.0:F3} retainedMB={GC.GetTotalMemory(false) / 1048576.0:F3} collections={GC.CollectionCount(0) - _startGc} restoredMode={GarbageCollector.GCMode}"));
    }
}
