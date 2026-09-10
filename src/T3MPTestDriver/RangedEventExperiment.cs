using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development experiment: one ordered event store per ranged-effect applier.
// All native subscriber bodies, arguments, ordering and exception propagation
// remain intact. Existing stores survive load so later unsubscription works.
internal static class RangedEventExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly object Gate = new object();
    private static readonly ConditionalWeakTable<object, Store> Stores = new ConditionalWeakTable<object, Store>();
    private static FieldInfo _field = null!;
    private static Type _storeType = null!, _applierType = null!, _argumentType = null!;
    [ThreadStatic] private static int _depth;
    private static bool _installed, _verify, _testEnrollment;
    private static long _owners, _adds, _removes, _raises, _checks, _fallbacks, _peak, _avoidedCopyEntries;

    private abstract class Store
    {
        internal bool Native;
        internal abstract Delegate Proxy { get; }
        internal abstract void Add(Delegate value);
        internal abstract void Remove(Delegate value);
        internal abstract Delegate? Restore();
    }

    private sealed class NativeStore : Store
    {
        internal static readonly NativeStore Instance = new NativeStore();
        private NativeStore() { Native = true; }
        internal override Delegate Proxy => throw new NotSupportedException();
        internal override void Add(Delegate value) => throw new NotSupportedException();
        internal override void Remove(Delegate value) => throw new NotSupportedException();
        internal override Delegate? Restore() => throw new NotSupportedException();
    }

    private sealed class Ordered<T> : Store
    {
        private sealed class Node
        {
            internal EventHandler<T> Handler = null!;
            internal Node? Previous, Next, EqualPrevious;
        }
        private readonly Dictionary<EventHandler<T>, Node> _last = new Dictionary<EventHandler<T>, Node>();
        private readonly EventHandler<T> _proxy;
        private Node? _first, _tail;
        private int _count;
        private EventHandler<T>[]? _snapshot;
        private EventHandler<T>? _shadow;
        private readonly bool _validate = _verify, _countMetrics = !_testEnrollment;
        public Ordered() { _proxy = Raise; }
        internal override Delegate Proxy => _proxy;

        internal override void Add(Delegate value)
        {
            var handler = (EventHandler<T>)value;
            _last.TryGetValue(handler, out var equal);
            var node = new Node { Handler = handler, Previous = _tail, EqualPrevious = equal };
            if (_tail != null) _tail.Next = node; else _first = node;
            _tail = node; _last[handler] = node; _count++; _snapshot = null;
            if (_countMetrics)
            {
                _adds++; _peak = Math.Max(_peak, _count);
                if (_count > 1) _avoidedCopyEntries += _count;
            }
            if (_validate) { _shadow += handler; Check(); }
        }

        internal override void Remove(Delegate value)
        {
            var handler = (EventHandler<T>)value;
            if (_countMetrics) _removes++;
            if (_last.TryGetValue(handler, out var node))
            {
                if (node.Previous != null) node.Previous.Next = node.Next; else _first = node.Next;
                if (node.Next != null) node.Next.Previous = node.Previous; else _tail = node.Previous;
                if (node.EqualPrevious == null) _last.Remove(handler); else _last[handler] = node.EqualPrevious;
                _count--; _snapshot = null;
                if (_countMetrics && _count > 1) _avoidedCopyEntries += _count;
            }
            if (_validate) { _shadow -= handler; Check(); }
        }

        private EventHandler<T>[] Snapshot()
        {
            if (_snapshot != null) return _snapshot;
            var result = new EventHandler<T>[_count]; var i = 0;
            for (var node = _first; node != null; node = node.Next) result[i++] = node.Handler;
            return _snapshot = result;
        }

        private void Check()
        {
            var expected = _shadow?.GetInvocationList() ?? Array.Empty<Delegate>();
            var actual = Snapshot();
            if (expected.Length != actual.Length) throw new Exception("Ranged event subscriber count differs");
            for (var i = 0; i < actual.Length; i++)
                if (!expected[i].Equals(actual[i])) throw new Exception("Ranged event subscriber order differs at " + i);
            if (_countMetrics) _checks++;
        }

        private void Raise(object sender, T args)
        {
            EventHandler<T>[] snapshot;
            lock (Gate)
            {
                if (_validate) Check();
                snapshot = Snapshot();
                if (_countMetrics) _raises++;
            }
            // Never hold the subscription lock during callbacks. A callback may
            // mutate subscriptions or raise recursively; this invocation retains
            // exactly the snapshot taken before the first callback.
            foreach (var handler in snapshot) handler(sender, args);
        }

        internal override Delegate? Restore()
        {
            EventHandler<T>? result = null;
            foreach (var handler in Snapshot()) result += handler;
            // Preserve a snapshot already captured by a concurrently in-flight
            // native invocation of the proxy. Future operations use the native
            // field; the old proxy continues to represent its old delegate list.
            return result;
        }
    }

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        _verify = args.Contains("-t3mpTestRangedEventsValidate");
        if (!args.Contains("-t3mpTestRangedEvents") && !_verify) return;
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        _applierType = Find("Timberborn.RangedEffectSystem.RangedEffectApplier");
        var evt = _applierType.GetEvent("ActiveChanged", All)!;
        _argumentType = evt.EventHandlerType!.GetGenericArguments()[0];
        _field = _applierType.GetField("ActiveChanged", All)!;
        _storeType = typeof(Ordered<>).MakeGenericType(_argumentType);
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.ranged-events");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(RangedEventExperiment).GetMethod(name, All))!;
        foreach (var pair in new[] { (evt.GetAddMethod(true)!, nameof(Add)), (evt.GetRemoveMethod(true)!, nameof(Remove)) })
            patch.Invoke(harmony, new object?[] { pair.Item1, Hook(pair.Item2), null, null, Hook(nameof(EndAccess)) });
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(BeginLoad)), null, null, Hook(nameof(EndLoad)) });
        _installed = true;
        Debug.Log("[T3MPRANGEDEVENT] installed; ordered subscriber store, native handlers, snapshot dispatch; validate=" + _verify);
    }

    private static void BeginLoad() { _depth++; }
    private static void EndLoad() { _depth--; Report("load-end"); }
    private static bool Add(object __instance, Delegate? value, out bool __state) => Access(__instance, value, true, out __state);
    private static bool Remove(object __instance, Delegate? value, out bool __state) => Access(__instance, value, false, out __state);
    private static bool Access(object owner, Delegate? value, bool add, out bool held)
    {
        held = false;
        Monitor.Enter(Gate); held = true;
        // Lock remains held through an original accessor too, so enrollment and
        // fallback cannot race another subscription using that accessor.
        if (value == null) return false;
        var single = value.GetInvocationList();
        var simple = single.Length == 1 && ReferenceEquals(single[0], value);
        if (!Stores.TryGetValue(owner, out var store))
        {
            if (!add || !simple || (_depth == 0 && !_testEnrollment) || _field.GetValue(owner) != null) return true;
            store = (Store)Activator.CreateInstance(_storeType)!;
            Stores.Add(owner, store);
            _field.SetValue(owner, store.Proxy);
            if (!_testEnrollment) _owners++;
        }
        if (store.Native) return true;
        if (!simple)
        {
            // Mono has special multicast-removal behavior, including single-
            // target vs multicast representation differences. Preserve it by
            // restoring the original event before any multicast operand.
            _field.SetValue(owner, store.Restore());
            // The marker must not retain old subscribers after native removals.
            // An in-flight proxy reference retains its own old store as needed.
            Stores.Remove(owner); Stores.Add(owner, NativeStore.Instance);
            if (!_testEnrollment) _fallbacks++;
            return true;
        }
        if (add) store.Add(value); else store.Remove(value);
        return false;
    }
    private static void EndAccess(bool __state) { if (__state) Monitor.Exit(Gate); }

    internal static void Report(string stage)
    {
        if (_installed) Debug.Log($"[T3MPRANGEDEVENT] stage={stage} owners={_owners} adds={_adds} removes={_removes} raises={_raises} peakSubscribers={_peak} verified={_checks} fallbacks={_fallbacks} avoidedDelegateArrayEntries={_avoidedCopyEntries}");
    }

    internal static void Validate()
    {
        if (!_verify) return;
        typeof(RangedEventExperiment).GetMethod(nameof(ValidateTyped), All)!.MakeGenericMethod(_argumentType).Invoke(null, null);
    }

    private static void ValidateTyped<T>()
    {
        var evt = _applierType.GetEvent("ActiveChanged", All)!;
        var update = _applierType.GetMethod("UpdateActiveState", All)!;
        var random = new Random(62093);
        var actions = Enumerable.Range(0, 2048).Select(_ => (random.Next(3), random.Next(9))).ToArray();
        foreach (var scenario in new[] { "single", "multicast", "reentrant", "exception" })
        {
            var expected = Run(false, scenario); var actual = Run(true, scenario);
            if (expected != actual) throw new Exception("Ranged event native semantics differ: " + scenario);
        }
        Debug.Log("[T3MPRANGEDEVENT] VALIDATE PASS randomizedOperations=2048 scenarios=4 (duplicates, absent removals, ordered calls, sender/args, multicast fallback, reentrant add/remove/raise, exceptions)");

        string Run(bool optimized, string scenario)
        {
            var owner = Activator.CreateInstance(_applierType, All, null, new object?[] { null }, null)!;
            var log = new List<string>(); var depth = 0; var changed = false;
            var handlers = new EventHandler<T>[9];
            void Edit(bool add, Delegate? value) => (add ? evt.GetAddMethod(true)! : evt.GetRemoveMethod(true)!).Invoke(owner, new object?[] { value });
            void Raise() => update.Invoke(owner, new object[] { true });
            for (var i = 0; i < handlers.Length; i++)
            {
                var id = i;
                handlers[i] = (sender, a) =>
                {
                    if (!ReferenceEquals(sender, owner) || (object?)a == null) throw new Exception("Ranged event sender/args changed");
                    log.Add(id + "/" + depth + "/" + a.GetType().GetProperty("State")!.GetValue(a));
                    if (scenario == "exception" && id == 3) throw new InvalidOperationException("event test sentinel");
                    if (scenario == "reentrant" && id == 0 && !changed)
                    { changed = true; Edit(false, handlers[1]); Edit(true, handlers[7]); depth++; Raise(); depth--; }
                };
            }
            try
            {
                _testEnrollment = optimized;
                if (scenario == "single")
                {
                    foreach (var action in actions)
                    { if (action.Item1 == 2) Raise(); else Edit(action.Item1 == 0, handlers[action.Item2]); }
                }
                else
                {
                    Edit(true, handlers[0]); Edit(true, handlers[1]); Edit(true, handlers[3]); Edit(true, handlers[1]);
                    if (scenario == "multicast")
                    {
                        Edit(true, handlers[0] + handlers[1]);
                        Edit(false, handlers[0] + handlers[1]);
                        Edit(false, handlers[1] + handlers[3]);
                    }
                    Raise(); Raise();
                }
                Edit(true, null); Edit(false, null);
            }
            catch (TargetInvocationException e) { log.Add(e.InnerException!.GetType().FullName + "|" + e.InnerException.Message); }
            finally { _testEnrollment = false; }
            if (optimized && scenario != "multicast" && (!Stores.TryGetValue(owner, out var store) || store.Native))
                throw new Exception("Ranged event validation did not engage");
            if (optimized && scenario == "multicast" && (!Stores.TryGetValue(owner, out var escaped) || !escaped.Native))
                throw new Exception("Ranged event multicast fallback did not engage");
            return string.Join(",", log);
        }
    }
}
