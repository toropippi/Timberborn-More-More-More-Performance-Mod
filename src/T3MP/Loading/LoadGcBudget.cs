using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Timberborn.SingletonSystem;
using UnityEngine.Scripting;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Borrow spare memory during a synchronous world load. The full collection is
// paid before LoadAll returns, including when the load throws. This is a soft
// phase-boundary budget, not an allocation cap or an out-of-memory guarantee.
internal static class LoadGcBudget
{
    internal static bool Installed { get; private set; }
    private const BindingFlags All = LoadPatchBridge.All;
    private const string Owner = "t3mp.load.gc-budget";
    private const long HeapLimit = 12L << 30;
    private const ulong EntryReserve = 24UL << 30, ExitReserve = 8UL << 30;
    private static int _depth, _startGc;
    private static bool _started, _ownsMode, _participated, _settingMode, _subscribed;
    private static long _peak;
    private static readonly FieldInfo ModeListeners = typeof(GarbageCollector).GetField("GCModeChanged", All)!;
    private static readonly MethodInfo[] ModeMethods = typeof(GarbageCollector).GetMethods(All)
        .Where(m => new[] { "get_GCMode", "set_GCMode", "GetMode", "SetMode" }.Contains(m.Name)).ToArray();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    internal static void Install()
    {
        if (Installed || Environment.GetCommandLineArgs().Contains("-t3mpTestLoadGcBaseline")) return;
        Type? ht = null; object? harmony = null;
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Reviewed() || ModeListeners == null || ModeMethods.Length != 4)
            { Debug.Log("[T3MPGCBUDGET] native fallback: unreviewed platform/modules"); return; }
            ht = LoadPatchBridge.Find("HarmonyLib.Harmony");
            var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
            harmony = Activator.CreateInstance(ht, Owner)!;
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            object Hook(string name) {
                var hook = Activator.CreateInstance(hm, typeof(LoadGcBudget).GetMethod(name, All))!;
                hm.GetField("priority")!.SetValue(hook, 800); return hook;
            }
            void Patch(MethodInfo method, string? before = null, string? final = null) =>
                patch.Invoke(harmony, new object?[] { method, before == null ? null : Hook(before), null, null, final == null ? null : Hook(final) });
            Patch(typeof(SingletonLifecycleService).GetMethod("LoadAll", All)!, nameof(Begin), nameof(End));
            // LoadAll has populated the metadata by this point. Do not resolve
            // any additional services merely to decide whether this is a world.
            Patch(typeof(SingletonLifecycleService).GetMethod("LoadSingletons", All)!, nameof(BeforeSingletons));
            foreach (var row in new[] {
                "Timberborn.SingletonSystem.SingletonLifecycleService|LoadSingletons,LoadNonSingletons,PostLoadSingletons,PostLoadNonSingletons",
                "Timberborn.WorldPersistence.WorldEntitiesLoader|InstantiateEntities",
                "Timberborn.WorldPersistence.EntitiesLoader|Load,BatchLoad,PreInitialize,Initialize,PostInitialize",
                "Timberborn.Navigation.NavigationSynchronizer|PostLoad,ProcessPreviewChanges,ProcessInstantChanges,ProcessRegularChanges,NotifyAllNavmeshChanges",
                "Timberborn.WaterSystem.WaterSimulator|Load,PostLoad"
            }) {
                var parts = row.Split('|');
                foreach (var method in LoadPatchBridge.Find(parts[0]).GetMethods(All).Where(m => !m.IsStatic && !m.ContainsGenericParameters && parts[1].Split(',').Contains(m.Name)))
                    Patch(method, final: nameof(Checkpoint));
            }
            Installed = true;
            Debug.Log("[T3MPGCBUDGET] installed; world loads only; entryFreeGiB=24 heapLimitGiB=12 reserveGiB=8; final GC included");
        }
        catch (Exception e)
        {
            if (ht != null && harmony != null) ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
            Debug.LogWarning("[T3MPGCBUDGET] disabled: " + e.GetBaseException().Message);
        }
    }

    private static bool Reviewed() => LoadCompatibility.Reviewed(
        "Timberborn.SingletonSystem|962512a9-30fb-4e42-b29f-b0115c9e9015",
        "Timberborn.WorldPersistence|322e064a-da3b-4021-9703-113b13123a6f",
        "UnityEngine.CoreModule|61dee272-fd45-47db-9c13-40fe43fbdc1a");

    private static ulong AvailableMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? Math.Min(status.AvailablePhysical, status.AvailablePageFile) : 0;
    }
    private static bool CanStart(ulong available, long heap) => available >= EntryReserve && heap < HeapLimit;
    private static bool MustRestore(ulong available, long heap) => available < ExitReserve || heap >= HeapLimit;

    private static void Begin(out bool __state)
    {
        __state = true;
        if (_depth++ != 0) return;
        _started = _participated = false; _peak = 0;
    }
    private static void BeforeSingletons(SingletonLifecycleService __instance)
    {
        if (_depth != 1 || _started) return;
        foreach (var loader in __instance._nonSingletonLoaders)
            if (loader.GetType().FullName == "Timberborn.WorldPersistence.WorldEntitiesLoader") { StartBudget(); return; }
    }
    private static void StartBudget()
    {
        if (_depth != 1 || _started) return;
        _started = true;
        try
        {
            // Respect a GC policy already in use, including mod observers.
            if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled || ModeListeners.GetValue(null) != null ||
                !Reviewed() || !LoadCompatibility.Unmodified(ModeMethods, Owner)) return;
            _peak = GC.GetTotalMemory(false);
            var available = AvailableMemory();
            if (!CanStart(available, _peak)) { Debug.Log("[T3MPGCBUDGET] native fallback: insufficient memory headroom"); return; }
            _startGc = GC.CollectionCount(0);
            GarbageCollector.GCModeChanged += OnModeChanged; _subscribed = true;
            _ownsMode = true;
            SetMode(GarbageCollector.Mode.Disabled);
            _participated = _ownsMode && GarbageCollector.GCMode == GarbageCollector.Mode.Disabled;
            Debug.Log(FormattableString.Invariant($"[T3MPGCBUDGET] begin participating={_participated} availableMB={available / 1048576.0:F3} heapMB={_peak / 1048576.0:F3}"));
        }
        catch (Exception e) { Restore(); Debug.LogWarning("[T3MPGCBUDGET] start fallback: " + e.GetBaseException().Message); }
    }
    private static void OnModeChanged(GarbageCollector.Mode mode)
    {
        // A different owner changed the policy. Never overwrite that decision,
        // even if it switches away and back again between phase checkpoints.
        if (!_settingMode) _ownsMode = false;
    }
    private static void SetMode(GarbageCollector.Mode mode)
    {
        _settingMode = true;
        try { GarbageCollector.GCMode = mode; }
        finally { _settingMode = false; }
    }
    private static void Restore()
    {
        try
        {
            if (_ownsMode && GarbageCollector.GCMode == GarbageCollector.Mode.Disabled) SetMode(GarbageCollector.Mode.Enabled);
        }
        finally { _ownsMode = false; }
    }
    private static void Checkpoint()
    {
        if (_depth == 0 || !_participated) return;
        try
        {
            _peak = Math.Max(_peak, GC.GetTotalMemory(false));
            if (_ownsMode && MustRestore(AvailableMemory(), _peak))
            { Restore(); Debug.Log("[T3MPGCBUDGET] budget/reserve reached; automatic GC restored"); }
        }
        catch (Exception e) { Restore(); Debug.LogWarning("[T3MPGCBUDGET] checkpoint fallback: " + e.GetBaseException().Message); }
    }
    private static void End(bool __state)
    {
        if (!__state || _depth <= 0 || --_depth != 0) return;
        var collectStart = Stopwatch.GetTimestamp();
        try
        {
            // Restore before any fallible accounting. Preserve exceptions from
            // the native load; this finalizer neither replaces nor suppresses them.
            Restore();
            if (!_participated) return;
            _peak = Math.Max(_peak, GC.GetTotalMemory(false));
            if (GarbageCollector.GCMode == GarbageCollector.Mode.Enabled) GC.Collect();
            Debug.Log(FormattableString.Invariant($"[T3MPGCBUDGET] finalCollectionMs={(Stopwatch.GetTimestamp() - collectStart) * 1000.0 / Stopwatch.Frequency:F3} observedPeakMB={_peak / 1048576.0:F3} retainedMB={GC.GetTotalMemory(false) / 1048576.0:F3} collections={GC.CollectionCount(0) - _startGc} restoredMode={GarbageCollector.GCMode}"));
        }
        catch (Exception e) { Debug.LogError("[T3MPGCBUDGET] cleanup failed: " + e); }
        finally
        {
            if (_subscribed) GarbageCollector.GCModeChanged -= OnModeChanged;
            _subscribed = _participated = false;
        }
    }
}
