using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Explicit dev-only test, run before the game world is loaded. Fresh EventBus
// instances compare native registration with the installed delegate rewrite.
internal static class EventDelegateValidation
{
    private const string Owner = "t3mp.test.event-delegates";
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _foreignBodies, _prefixes, _postfixes;
    private static bool _expectTyped;
    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestEventDelegates", StringComparison.OrdinalIgnoreCase));

    internal static void Run()
    {
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var feature = Find("T3MP.Runtime.EventBusFastDelegates");
        bool Active() => (bool)feature.GetProperty("Installed", All)!.GetValue(null)!;
        if (!Active()) throw new InvalidOperationException("Event delegate validation requires the active T3MP rewrite");
        var harmonyType = Find("HarmonyLib.Harmony");
        var harmonyMethodType = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(harmonyType, Owner)!;
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var target = typeof(EventBus).GetMethod("RegisterMethod", All)!;
        object Hook(string name, bool transpiler = false)
        {
            var method = typeof(EventDelegateValidation).GetMethod(name, All)!;
            if (transpiler) method = T3MP.Loading.LoadPatchBridge.Create(Owner + "." + name, method);
            return Activator.CreateInstance(harmonyMethodType, method)!;
        }
        string native;
        try
        {
            // Runs at Harmony's default priority, before T3MP's last-priority
            // shape check. The added body call must never be hidden.
            patch.Invoke(harmony, new object?[] { target, Hook(nameof(Before)), Hook(nameof(After)), Hook(nameof(ForeignBody), true), null });
            if (Active()) throw new InvalidOperationException("Changed registration stream still reported active");
            native = Cases(false);
            if (_foreignBodies == 0 || _prefixes == 0 || _postfixes == 0)
                throw new InvalidOperationException("Foreign registration hooks were bypassed");
        }
        finally { harmonyType.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner }); }
        if (!Active()) throw new InvalidOperationException("Removing the foreign transpiler did not restore typed registration");
        var typed = Cases(true);
        if (native != typed) throw new InvalidOperationException("Native/typed event results differ: " + native + " / " + typed);
        try
        {
            // Prefix/postfix hooks alone keep working with the typed body.
            _prefixes = _postfixes = 0;
            patch.Invoke(harmony, new object?[] { target, Hook(nameof(Before)), Hook(nameof(After)), null, null });
            if (!Active() || Cases(true) != typed || _prefixes == 0 || _postfixes == 0)
                throw new InvalidOperationException("Prefix/postfix coexistence failed");
        }
        finally { harmonyType.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner }); }
        Debug.Log("[T3MPTEST] Event delegate validation PASS: native validation/dispatch, binder and handler exceptions, foreign body, prefix/postfix, patch removal.");
    }

    private static void Before() => _prefixes++;
    private static void After() => _postfixes++;
    private static void BodyObserved() => _foreignBodies++;
    private static IEnumerable<T> ForeignBody<T>(IEnumerable<T> instructions)
    {
        yield return (T)Activator.CreateInstance(typeof(T), OpCodes.Call, typeof(EventDelegateValidation).GetMethod(nameof(BodyObserved), All)!)!;
        foreach (var instruction in instructions) yield return instruction;
    }

    private static string Cases(bool expectTyped)
    {
        _expectTyped = expectTyped;
        var log = new List<string>();
        var listener = new Listener(log, "a");
        var second = new Listener(log, "b");
        var bus = new EventBus();
        bus.PostLoad();
        bus.Register(listener);
        bus.Register(second);
        log.Add("duplicate:" + Outcome(() => bus.Register(listener)));
        bus.Post(new ReferenceEvent { Value = 2 });
        bus.Post(new ReferenceEvent { Value = 4 });
        bus.Unregister(listener);
        bus.Post(new ReferenceEvent { Value = 9 });
        bus.Unregister(second);
        bus.Post(new ReferenceEvent { Value = 10 });

        foreach (var name in new[] { nameof(Listener.ReturnsValue), nameof(Listener.NoArguments), nameof(Listener.TwoArguments) })
        {
            var method = typeof(Listener).GetMethod(name)!;
            log.Add(name + ":" + Outcome(() => new EventBus().RegisterMethod(listener, method)));
        }
        log.Add("null-subscriber:" + Outcome(() => new EventBus().RegisterMethod(null!, typeof(Listener).GetMethod(nameof(Listener.First))!)));
        CheckAction(log, listener, nameof(Listener.First), typeof(ReferenceEvent), new ReferenceEvent { Value = 7 }, null!, "wrong");
        CheckAction(log, listener, nameof(Listener.Value), typeof(ValueEvent), new ValueEvent { Value = 8 }, null!, "wrong");
        CheckAction(log, listener, nameof(Listener.Throw), typeof(ReferenceEvent), new ReferenceEvent());
        // Invalid binding must retain native deferred invocation failure.
        CheckAction(log, new object(), nameof(Listener.First), typeof(ReferenceEvent), new ReferenceEvent());
        return string.Join("|", log);
    }

    private static void CheckAction(List<string> log, object subscriber, string methodName, Type eventType, params object[] values)
    {
        var bus = new EventBus();
        bus.RegisterMethod(subscriber, typeof(Listener).GetMethod(methodName)!);
        var action = bus._subscriptions.Get(eventType).Single().Action;
        var typed = action.Method.DeclaringType?.FullName?.StartsWith("T3MP.Runtime.EventBusFastDelegates+", StringComparison.Ordinal) == true;
        if (typed != (_expectTyped && subscriber is Listener))
            throw new InvalidOperationException("Unexpected registered delegate implementation for " + methodName);
        foreach (var value in values) log.Add(methodName + ":" + Outcome(() => action(value)));
    }

    private static string Outcome(Action action)
    {
        try { action(); return "OK"; }
        catch (Exception exception)
        {
            return exception.GetType().FullName + ":" + exception.Message + ":inner=" +
                   exception.InnerException?.GetType().FullName + ":" + exception.InnerException?.Message;
        }
    }

    public sealed class ReferenceEvent { public int Value; }
    public struct ValueEvent { public int Value; }
    public sealed class Listener
    {
        private readonly List<string> _log;
        private readonly string _name;
        public Listener(List<string> log, string name) { _log = log; _name = name; }
        [OnEvent] public void First(ReferenceEvent value) => _log.Add(_name + ":" + (value?.Value ?? -1));
        public void Value(ValueEvent value) => _log.Add("value:" + value.Value);
        public void Throw(ReferenceEvent value) => throw new InvalidOperationException("handler-test");
        public int ReturnsValue(ReferenceEvent value) => 0;
        public void NoArguments() { }
        public void TwoArguments(ReferenceEvent first, ReferenceEvent second) { }
    }
}
