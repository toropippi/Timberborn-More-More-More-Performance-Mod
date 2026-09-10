using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Timberborn.SingletonSystem;
using UnityEngine.Scripting;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Runs after snapshots, outside every timing measurement. Exercise the actual
// production prefix/finalizer around a throwing/nested fixture, with small
// synthetic memory readings rather than allocating gigabytes to cause pressure.
internal static class LoadGcBudgetValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static ulong? _memory;
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Fixture(Action body) => body();
    private static bool ReadMemory(ref ulong __result) { if (!_memory.HasValue) return true; __result = _memory.Value; return false; }
    private static void ForeignMode() { }
    internal static void Run()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestLoadGcBudgetValidate")) return;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Loading.LoadGcBudget")).First(t => t != null)!;
        if (!(bool)type.GetProperty("Installed", All)!.GetValue(null)!)
        { Debug.Log("[T3MPGCBUDGETTEST] native fallback; feature not installed"); return; }
        var prior = GarbageCollector.GCMode;
        var listeners = typeof(GarbageCollector).GetField("GCModeChanged", All)!;
        var priorListeners = listeners.GetValue(null);
        if (prior != GarbageCollector.Mode.Enabled || priorListeners != null) throw new Exception("GC fixture requires unowned enabled mode");
        var ht = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HarmonyLib.Harmony")).First(t => t != null)!;
        var hm = ht.Assembly.GetType("HarmonyLib.HarmonyMethod")!;
        const string owner = "t3mp.test.gc-budget-validation";
        var harmony = Activator.CreateInstance(ht, owner)!;
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(MethodInfo method) => Activator.CreateInstance(hm, method)!;
        object? Call(string method, params object[] args) => type.GetMethod(method, All)!.Invoke(null, args);
        object? Field(string name) => type.GetField(name, All)!.GetValue(null);
        void Check(bool condition, string name) { if (!condition) throw new Exception("GC budget validation: " + name); }
        var cases = 0;
        void Case(string name, Action body, GarbageCollector.Mode expected = GarbageCollector.Mode.Enabled)
        {
            _memory = 48UL << 30;
            Fixture(body);
            Check((int)Field("_depth")! == 0 && GarbageCollector.GCMode == expected && !(bool)Field("_ownsMode")! && listeners.GetValue(null) == priorListeners, name + " cleanup");
            cases++; Debug.Log("[T3MPGCBUDGETTEST] PASS " + name);
        }
        void Start() { Call("StartBudget"); Check(GarbageCollector.GCMode == GarbageCollector.Mode.Disabled && (bool)Field("_participated")!, "start"); }
        try
        {
            patch.Invoke(harmony, new object?[] { typeof(LoadGcBudgetValidation).GetMethod(nameof(Fixture), All), Hook(type.GetMethod("Begin", All)!), null, null, Hook(type.GetMethod("End", All)!) });
            patch.Invoke(harmony, new object?[] { type.GetMethod("AvailableMemory", All), Hook(typeof(LoadGcBudgetValidation).GetMethod(nameof(ReadMemory), All)!), null, null, null });
            foreach (var free in new[] { 0UL, (8UL << 30) - 1, 8UL << 30, (24UL << 30) - 1, 24UL << 30, 96UL << 30 })
            foreach (var heap in new[] { 0L, (12L << 30) - 1, 12L << 30 })
            {
                Check((bool)Call("CanStart", free, heap)! == (free >= (24UL << 30) && heap < (12L << 30)), "entry boundary");
                Check((bool)Call("MustRestore", free, heap)! == (free < (8UL << 30) || heap >= (12L << 30)), "exit boundary");
            }
            Debug.Log("[T3MPGCBUDGETTEST] PASS policy boundaries=36");
            Case("menu", () => {
                var service = (SingletonLifecycleService)FormatterServices.GetUninitializedObject(typeof(SingletonLifecycleService));
                service._nonSingletonLoaders = ImmutableArray<INonSingletonLoader>.Empty;
                Call("BeforeSingletons", service);
                Check(!(bool)Field("_started")! && GarbageCollector.GCMode == prior, "menu skipped");
            });
            Case("normal", Start);
            Case("nested", () => {
                Start(); Fixture(() => { Call("StartBudget"); Check((int)Field("_depth")! == 2, "nested depth"); });
                Check(GarbageCollector.GCMode == GarbageCollector.Mode.Disabled, "inner finalizer keeps budget");
            });
            var sentinel = new InvalidOperationException("gc-fixture-sentinel");
            _memory = 48UL << 30;
            try { Fixture(() => { Start(); Fixture(() => throw sentinel); }); throw new Exception("Exception lost"); }
            catch (InvalidOperationException e) { Check(ReferenceEquals(e, sentinel), "exception identity"); }
            Check((int)Field("_depth")! == 0 && GarbageCollector.GCMode == prior && listeners.GetValue(null) == priorListeners, "exception restore");
            cases++; Debug.Log("[T3MPGCBUDGETTEST] PASS nested exception identity and restore");
            Case("missing memory at entry", () => { _memory = 0; Call("StartBudget"); Check(!(bool)Field("_participated")!, "no participation"); });
            Case("memory read failure after start", () => { Start(); _memory = 0; Call("Checkpoint"); Check(GarbageCollector.GCMode == prior, "failure restore"); });
            Case("pressure and no reentry", () => {
                Start(); _memory = (8UL << 30) - 1; Call("Checkpoint");
                _memory = 48UL << 30; Call("StartBudget"); Check(GarbageCollector.GCMode == prior, "cannot restart after reserve");
            });
            Case("heap soft limit", () => { Start(); type.GetField("_peak", All)!.SetValue(null, 12L << 30); Call("Checkpoint"); Check(GarbageCollector.GCMode == prior, "heap restore"); });
            foreach (var mode in new[] { GarbageCollector.Mode.Manual, GarbageCollector.Mode.Disabled })
            {
                GarbageCollector.GCMode = mode;
                Case("prior " + mode, () => { Call("StartBudget"); Check(!(bool)Field("_participated")!, "prior policy untouched"); }, mode);
                GarbageCollector.GCMode = prior;
            }
            Case("external Manual", () => { Start(); GarbageCollector.GCMode = GarbageCollector.Mode.Manual; }, GarbageCollector.Mode.Manual);
            GarbageCollector.GCMode = prior;
            Case("external changes away and back", () => {
                Start(); GarbageCollector.GCMode = prior; GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                Check(!(bool)Field("_ownsMode")!, "ownership relinquished");
            }, GarbageCollector.Mode.Disabled);
            GarbageCollector.GCMode = prior;
            Action<GarbageCollector.Mode> listener = _ => throw new Exception("Existing listener should never be invoked");
            Case("existing observer", () => {
                GarbageCollector.GCModeChanged += listener;
                try { Call("StartBudget"); Check(!(bool)Field("_participated")!, "observer fallback"); }
                finally { GarbageCollector.GCModeChanged -= listener; }
            });
            patch.Invoke(harmony, new object?[] { typeof(GarbageCollector).GetProperty("GCMode", All)!.SetMethod,
                Hook(typeof(LoadGcBudgetValidation).GetMethod(nameof(ForeignMode), All)!), null, null, null });
            Case("foreign setter patch", () => { Call("StartBudget"); Check(!(bool)Field("_participated")!, "patch fallback"); });
            Debug.Log("[T3MPGCBUDGETTEST] VALIDATE PASS lifecycleCases=" + cases + " policyCases=36");
        }
        finally
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner });
            _memory = null;
            GarbageCollector.GCMode = prior;
        }
    }
}
