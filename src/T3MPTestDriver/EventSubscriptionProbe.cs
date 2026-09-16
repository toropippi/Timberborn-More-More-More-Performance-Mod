using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.IO;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Intrusive registration attribution, never a clean timing configuration.
// Remove EventBus instrumentation BEFORE its PostLoad compatibility check.
// The router's guard and all native registration/delivery semantics stay intact.
internal static class EventSubscriptionProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Owner = "t3mp.test.event-subscription-profile";
    private sealed class Stat { internal long Calls, Ticks, Own, Failed; }
    private struct Scope { internal long Start, Children; internal Stat? Stat; }
    private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();
    private static readonly Dictionary<string, long> Subscribers = new Dictionary<string, long>();
    private static readonly List<string> Census = new List<string>();
    private static readonly List<MethodInfo> RegistrationTargets = new List<MethodInfo>();
    private static object _harmony = null!;
    private static Type _ht = null!;
    [ThreadStatic] private static bool _active;
    [ThreadStatic] private static long _accounted;
    [ThreadStatic] private static string? _phase;
    private static bool _installed, _removed, _world;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestEventSubscriptionProfile")) return;
        _ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        _harmony = Activator.CreateInstance(_ht, Owner)!;
        var patch = _ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name, int priority = 400)
        {
            if (name == null) return null;
            var h = Activator.CreateInstance(hm, typeof(EventSubscriptionProbe).GetMethod(name, All))!;
            hm.GetField("priority")!.SetValue(h, priority); return h;
        }
        void Patch(MethodInfo target, string? begin, string? end) => patch.Invoke(_harmony,
            new[] { (object)target, Hook(begin, 1000), Hook(end, -1000), null, null });
        foreach (var method in typeof(EventBus).GetMethods(All | BindingFlags.DeclaredOnly)
                     .Where(m => new[] { "Register", "RegisterNow", "RegisterMethod", "Unregister", "UnregisterNow" }.Contains(m.Name))
                     .Concat(typeof(SubscriptionRegistry).GetMethods(All | BindingFlags.DeclaredOnly)
                         .Where(m => m.Name == "Add" || m.Name == "RemoveAll")))
        {
            Patch(method, nameof(Begin), nameof(End)); RegistrationTargets.Add(method);
        }
        var applier = Find("Timberborn.RangedEffectSystem.RangedEffectApplier");
        foreach (var name in new[] { "add_ActiveChanged", "remove_ActiveChanged" })
            Patch(applier.GetMethod(name, All)!, nameof(Begin), nameof(End));
        // Selected from the preceding live delegate-field census: the largest
        // per-owner lists plus high aggregate entity-local subscription counts.
        foreach (var name in new[] {
            "Timberborn.NaturalResourcesLifecycle.LivingNaturalResource",
            "Timberborn.StatusSystem.StatusSubject",
            "Timberborn.NaturalResourcesLifecycle.DyingNaturalResource",
            "Timberborn.InventorySystem.Inventory",
            "Timberborn.BuilderPrioritySystem.BuilderPrioritizable",
            "Timberborn.Growing.Growable",
            "Timberborn.Demolishing.Demolishable",
            "Timberborn.WorkSystem.Worker",
            "Timberborn.Characters.Character",
            "Timberborn.Hauling.DistrictHaulCandidates",
            "Timberborn.MechanicalSystem.TransputMap",
            "Timberborn.TerrainSystem.TerrainService" })
        foreach (var evt in Find(name).GetEvents(All | BindingFlags.DeclaredOnly))
        foreach (var method in new[] { evt.GetAddMethod(true), evt.GetRemoveMethod(true) }.Where(m => m != null))
            Patch(method!, nameof(Begin), nameof(End));
        Patch(typeof(SingletonLifecycleService).GetMethod("LoadAll", All)!, nameof(BeginLoad), nameof(EndLoad));
        foreach (var entry in new[] {
                     "Timberborn.SingletonSystem.SingletonLifecycleService|LoadSingletons,LoadNonSingletons,PostLoadSingletons",
                     "Timberborn.WorldPersistence.WorldEntitiesLoader|InstantiateEntities",
                     "Timberborn.WorldPersistence.EntitiesLoader|PreInitialize,Initialize,PostInitialize" })
        {
            var parts = entry.Split('|');
            foreach (var method in Find(parts[0]).GetMethods(All).Where(m => parts[1].Split(',').Contains(m.Name)))
                Patch(method, nameof(BeginPhase), nameof(EndPhase));
        }
        _installed = true;
        Debug.Log("[T3MPSUBSCRIBE] installed=True intrusive=True scope=LoadAll registrationStopsBeforeEventDrain=True");
    }

    private static void BeginLoad()
    {
        Stats.Clear(); Census.Clear(); Subscribers.Clear();
        _active = true; _phase = "Other"; _accounted = 0; _world = false;
    }
    private static void EndLoad() { _active = false; }
    private static void BeginPhase(MethodBase __originalMethod, out string? __state)
    {
        __state = _phase; _phase = __originalMethod.Name;
        if (_phase == "InstantiateEntities") _world = true;
    }
    private static void EndPhase(string? __state) { _phase = __state; }
    private static Scope Start(string name)
    {
        if (!_active) return default;
        var key = (_phase ?? "Other") + "|" + name;
        if (!Stats.TryGetValue(key, out var stat)) Stats.Add(key, stat = new Stat());
        return new Scope { Stat = stat, Children = _accounted, Start = Stopwatch.GetTimestamp() };
    }
    private static void Stop(Scope state, bool failed)
    {
        if (state.Stat == null) return;
        var elapsed = Stopwatch.GetTimestamp() - state.Start;
        state.Stat.Calls++; state.Stat.Ticks += elapsed;
        state.Stat.Own += elapsed - (_accounted - state.Children);
        if (failed) state.Stat.Failed++;
        _accounted = state.Children + elapsed;
    }
    private static void Begin(MethodBase __originalMethod, out Scope __state)
        => __state = Start(__originalMethod.DeclaringType!.Name + "." + __originalMethod.Name);
    // Successful-call diagnostic. An exception invalidates the run and the
    // runner aborts; do not use a Harmony finalizer on Mono's foreach EH bodies.
    private static void End(Scope __state) => Stop(__state, false);

    internal static void BeforeEventDrain(IPostLoadableSingleton service)
    {
        if (!_installed || _removed || !_world || !(service is EventBus bus)) return;
        // No weakened owner allowlist: by the time native PostLoad checks its
        // guards, every added registration hook has actually been removed.
        var unpatch = _ht.GetMethods().Single(m => m.Name == "Unpatch" && m.GetParameters().Length == 3);
        var allPatches = Enum.Parse(unpatch.GetParameters()[1].ParameterType, "All");
        foreach (var target in RegistrationTargets) unpatch.Invoke(_harmony, new[] { (object)target, allPatches, Owner });
        _removed = true;
        foreach (var pair in bus._subscriptions._subscriptions.OrderByDescending(p => p.Value.Count))
        {
            Census.Add(pair.Key.FullName + "|" + pair.Value.Count);
            foreach (var subscriber in pair.Value.Keys)
            {
                var name = subscriber.GetType().FullName!;
                Subscribers.TryGetValue(name, out var count); Subscribers[name] = count + 1;
            }
        }
    }
    internal static void Report(IEnumerable<EntityComponent> entities, Bindito.Core.IContainer container)
    {
        if (!_installed) return;
        if (!_removed) throw new Exception("Subscription profiler did not remove registration hooks before EventBus.PostLoad");
        foreach (var pair in Stats.OrderByDescending(p => p.Value.Own))
            Debug.Log(FormattableString.Invariant($"[T3MPSUBSCRIBE] key={pair.Key} calls={pair.Value.Calls} ms={pair.Value.Ticks * 1000.0 / Stopwatch.Frequency:F3} ownMs={pair.Value.Own * 1000.0 / Stopwatch.Frequency:F3} failed={pair.Value.Failed}"));
        foreach (var pair in Subscribers.OrderByDescending(p => p.Value))
            Debug.Log($"[T3MPSUBSCRIBE] subscriberType={pair.Key} handlers={pair.Value}");
        foreach (var entry in Census) Debug.Log("[T3MPSUBSCRIBE] registry=" + entry);
        Debug.Log("[T3MPSUBSCRIBE] removedBeforeEventDrain=True");
        // After scene load: read direct delegate fields of entities and services.
        // This is a census, not timing, and does not recursively walk heap graphs.
        var fields = new Dictionary<Type, FieldInfo[]>();
        var rows = new Dictionary<FieldInfo, (long Owners, long Handlers, int Peak)>();
        foreach (var value in entities.SelectMany(e => e.AllComponents).Cast<object>()
                     .Concat(container.GetInstance<ISingletonRepository>().GetSingletons<object>()))
        {
            var type = value.GetType();
            if (!fields.TryGetValue(type, out var selected))
            {
                var list = new List<FieldInfo>();
                for (var t = type; t != null; t = t.BaseType)
                    list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType)));
                fields.Add(type, selected = list.ToArray());
            }
            foreach (var field in selected)
            {
                if (!(field.GetValue(value) is Delegate handler)) continue;
                var count = handler.GetInvocationList().Length;
                rows.TryGetValue(field, out var row);
                rows[field] = (row.Owners + 1, row.Handlers + count, Math.Max(row.Peak, count));
            }
        }
        var args = Environment.GetCommandLineArgs();
        var directory = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        using (var writer = new StreamWriter(Path.Combine(directory, "delegate-fields.tsv")))
        {
            writer.WriteLine("field\townerOccurrences\thandlerOccurrences\tpeakPerOwner");
            foreach (var pair in rows.OrderByDescending(p => p.Value.Peak))
                writer.WriteLine($"{pair.Key.DeclaringType!.FullName}.{pair.Key.Name}\t{pair.Value.Owners}\t{pair.Value.Handlers}\t{pair.Value.Peak}");
        }
    }
}
