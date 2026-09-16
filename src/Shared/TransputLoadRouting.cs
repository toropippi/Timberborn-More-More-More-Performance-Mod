using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.MechanicalSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Spatial routing during native entity PostInitialize. The original map
// mutation, subscription order and matching handler bodies still run.
internal static class TransputLoadRouting
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Plan
    {
        internal readonly Dictionary<Vector3Int, List<int>> ByCoordinate = new Dictionary<Vector3Int, List<int>>();
        internal EventHandler<Transput>[] Handlers = null!;
        internal List<Transput>[] Ports = null!;
    }
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static EventHandler<Transput>? _last;
    [ThreadStatic] private static Plan? _plan;
    private static Type _activator = null!;
    private static FieldInfo _ports = null!;
    private static MethodInfo _handler = null!;
    private static bool _validate;
    private static bool _incremental;
    private static long _appends;
    private static long _events, _skipped, _delivered, _validated, _fallbacks;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static bool Installed { get; private set; }
    private static bool _production, _compatible;
    private static string _owner = "";

    internal static void Install(string owner = "t3mp.test.transput-routing", bool production = false)
    {
        var args = Environment.GetCommandLineArgs();
        if (Installed || LoadCompatibility.MainInstalled(typeof(TransputLoadRouting))) return;
        if (production ? args.Contains("-t3mpTestTransputBaseline") : !args.Contains("-t3mpTestTransputRouting")) return;
        _production = production; _owner = owner;
        if (production && !Reviewed()) { Debug.Log("[T3MPTRANSPUT] native fallback: unreviewed game modules"); return; }
        _validate = args.Contains("-t3mpTestTransputRoutingValidate");
        _incremental = production || args.Contains("-t3mpTestTransputIncremental");
        _activator = Find("Timberborn.MechanicalConnectorSystem.MechanicalConnectorActivator");
        _ports = _activator.GetField("_connectableTransputs", All)!;
        _handler = _activator.GetMethod("OnTransputAdded", All)!;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, owner);
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        try
        {
            object Hook(string name) => Activator.CreateInstance(hm, typeof(TransputLoadRouting).GetMethod(name, All))!;
            patch.Invoke(harmony, new object?[] { Find("Timberborn.WorldPersistence.EntitiesLoader").GetMethod("PostInitialize", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
            var rewrite = LoadPatchBridge.Create("T3MP.TransputBridge", typeof(TransputLoadRouting).GetMethod(nameof(Rewrite), All)!);
            patch.Invoke(harmony, new object?[] { typeof(TransputMap).GetMethod("SetTransput", All), null, null, Activator.CreateInstance(hm, rewrite), null });
            Installed = true;
            Debug.Log("[T3MPTRANSPUT] installed validate=" + _validate);
        }
        catch (Exception e)
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner });
            Debug.LogWarning("[T3MPTRANSPUT] disabled: " + e.GetBaseException().Message);
        }
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = instructions.ToList();
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!;
        var original = typeof(EventHandler<Transput>).GetMethod("Invoke")!;
        var matches = list.Where(i => Equals(operand.GetValue(i), original)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Unexpected SetTransput event invocation shape");
        operand.SetValue(matches[0], typeof(TransputLoadRouting).GetMethod(nameof(Dispatch), All));
        opcode.SetValue(matches[0], OpCodes.Call);
        return list;
    }

    private static bool Reviewed() => LoadCompatibility.Reviewed(
        "Timberborn.MechanicalSystem|22e640db-81af-4a3a-94fd-72cb8a063d7f",
        "Timberborn.MechanicalConnectorSystem|d5f0a196-1a1f-4540-83ba-4cc0f1cad0a5",
        "Timberborn.WorldPersistence|322e064a-da3b-4021-9703-113b13123a6f");
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _last = null; _plan = null;
        _events = _skipped = _delivered = _validated = _fallbacks = _appends = 0;
        _compatible = !_production || Reviewed() && LoadCompatibility.Unmodified(
            new[] { _activator, typeof(Transput), typeof(TransputMap) }
                .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly)), _owner);
        if (!_compatible) Debug.Log("[T3MPTRANSPUT] native fallback: modified connector methods");
    }
    private static void End()
    {
        if (--_depth != 0) return;
        if (_validate && _plan != null) ValidateTransitions(_plan.Handlers);
        _last = null; _plan = null;
        Debug.Log($"[T3MPTRANSPUT] events={_events} skipped={_skipped} delivered={_delivered} validated={_validated} fallbacks={_fallbacks}");
        if (_incremental) Debug.Log("[T3MPTRANSPUTPLAN] incremental appends=" + _appends);
    }

    private static void ValidateTransitions(EventHandler<Transput>[] live)
    {
        if (live.Length < 2) throw new InvalidOperationException("Missing connector validation fixtures");
        var saved = _plan; var incremental = _incremental; var appends = _appends;
        var cases = 0;
        try
        {
            _incremental = true; _plan = null;
            var first = live[0]; var second = live[1];
            EventHandler<Transput> foreign = (_, __) => throw new InvalidOperationException("Fixture must not dispatch");
            foreach (var handlers in new[] { first, first + first, first + first + second,
                         second + first, second, second + foreign, first + second })
            {
                var old = _plan;
                var snapshot = old?.ByCoordinate.ToDictionary(p => p.Key, p => p.Value.ToArray());
                var actual = Build(handlers!);
                _incremental = false;
                var expected = Build(handlers!);
                _incremental = true;
                if ((actual == null) != (expected == null) || actual != null &&
                    (!actual.Handlers.SequenceEqual(expected!.Handlers) ||
                     actual.ByCoordinate.Count != expected.ByCoordinate.Count ||
                     actual.ByCoordinate.Any(p => !expected.ByCoordinate.TryGetValue(p.Key, out var indices) || !p.Value.SequenceEqual(indices))))
                    throw new InvalidOperationException("Incremental connector index differs from full rebuild");
                if (old != null && snapshot!.Any(p => !p.Value.SequenceEqual(old.ByCoordinate[p.Key])))
                    throw new InvalidOperationException("Nested connector dispatch snapshot was mutated");
                _plan = actual; cases++;
            }
            Debug.Log("[T3MPTRANSPUTPLAN] VALIDATE PASS transitions=" + cases);
        }
        finally { _plan = saved; _incremental = incremental; _appends = appends; }
    }

    private static Plan? Build(EventHandler<Transput> handlers)
    {
        var invocations = handlers.GetInvocationList();
        if (invocations.Any(d => d.Target?.GetType() != _activator || d.Method != _handler)) return null;
        if (_incremental && _plan != null && invocations.Length > _plan.Handlers.Length &&
            !_plan.Handlers.Where((h, i) => !h.Equals(invocations[i]) || !ReferenceEquals(_plan.Ports[i], _ports.GetValue(h.Target))).Any())
        {
            // Native exact-type activators fill their port list once, before
            // subscribing. Copy changed buckets so reentrant older dispatches
            // retain their immutable candidate lists and invocation order.
            var previous = _plan;
            var appended = new Plan { Handlers = invocations.Cast<EventHandler<Transput>>().ToArray(), Ports = new List<Transput>[invocations.Length] };
            Array.Copy(previous.Ports, appended.Ports, previous.Ports.Length);
            foreach (var pair in previous.ByCoordinate) appended.ByCoordinate.Add(pair.Key, pair.Value);
            var copied = new HashSet<Vector3Int>();
            for (var i = previous.Handlers.Length; i < invocations.Length; i++)
            {
                var ports = appended.Ports[i] = (List<Transput>)_ports.GetValue(invocations[i].Target)!;
                foreach (var coordinate in ports.Select(p => p.Coordinates).Distinct())
                {
                    if (!appended.ByCoordinate.TryGetValue(coordinate, out var bucket)) appended.ByCoordinate.Add(coordinate, bucket = new List<int>());
                    else if (copied.Add(coordinate)) appended.ByCoordinate[coordinate] = bucket = new List<int>(bucket);
                    bucket.Add(i);
                }
            }
            _appends++; return appended;
        }
        var plan = new Plan { Handlers = invocations.Cast<EventHandler<Transput>>().ToArray(), Ports = new List<Transput>[invocations.Length] };
        for (var i = 0; i < invocations.Length; i++)
        {
            var ports = plan.Ports[i] = (List<Transput>)_ports.GetValue(invocations[i].Target)!;
            var unique = new HashSet<Vector3Int>();
            foreach (var port in ports)
            {
                if (!unique.Add(port.Coordinates)) continue;
                if (!plan.ByCoordinate.TryGetValue(port.Coordinates, out var bucket)) plan.ByCoordinate.Add(port.Coordinates, bucket = new List<int>());
                bucket.Add(i); // Invocation-list order, including duplicate subscriptions.
            }
        }
        return plan;
    }

    private static void Dispatch(EventHandler<Transput> handlers, object sender, Transput other)
    {
        if (_depth == 0 || !_compatible) { handlers(sender, other); return; }
        _events++;
        if (!ReferenceEquals(_last, handlers)) { _plan = Build(handlers); _last = handlers; }
        var plan = _plan;
        if (plan == null) { _fallbacks++; handlers(sender, other); return; }
        plan.ByCoordinate.TryGetValue(other.Target, out var candidates);
        if (_validate)
        {
            for (var i = 0; i < plan.Ports.Length; i++)
            {
                if (candidates != null && candidates.Contains(i)) continue;
                foreach (var port in plan.Ports[i])
                    if (port.Faces(other)) throw new Exception("Spatial routing omitted a facing connector");
                _validated++;
            }
        }
        _skipped += plan.Handlers.Length - (candidates?.Count ?? 0);
        if (candidates == null) return;
        foreach (var i in candidates) { _delivered++; plan.Handlers[i](sender, other); }
    }
}
