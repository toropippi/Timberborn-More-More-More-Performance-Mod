using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static class EventRegistrationPlanValidation
{
    public sealed class Message { public int Value; }
    public struct ValueMessage { public int Value; }
    public class Listener
    {
        public Action<Message> Action = null!;
        [OnEvent] public virtual void OnMessage(Message message) => Action(message);
    }
    public sealed class Derived : Listener { public override void OnMessage(Message message) => Action(message); }
    public sealed class ValueListener
    {
        public Action<ValueMessage> Action = null!;
        [OnEvent] public void OnValue(ValueMessage value) => Action(value);
    }
    public sealed class InvalidReturn { [OnEvent] public int Handle(Message value) => 0; }
    public sealed class InvalidArity { [OnEvent] public void Handle() { } }
    public sealed class InvalidPrivate { [OnEvent] private void Handle(Message value) { } }
    public sealed class ByRef { [OnEvent] public void Handle(ref ValueMessage value) { value.Value++; } }

    internal static void Run()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.EventRegistrationPlans")).FirstOrDefault(t => t != null);
        if (type == null)
        {
            if (Environment.GetCommandLineArgs().Contains("-t3mpTestEventRegistrationValidate")) throw new TypeLoadException("Event registration candidate module is required");
            return;
        }
        var enabled = type.GetField("Enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        Debug.Log("[T3MPREGPLAN] enabled=" + enabled.GetValue(null));
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestEventRegistrationValidate")) return;
        var before = enabled.GetValue(null);
        try
        {
            foreach (var scenario in new[] { "order", "duplicate", "mutation", "nested", "exception", "value", "derived", "invalid-return", "invalid-arity", "invalid-private", "byref" })
            {
                enabled.SetValue(null, false); var expected = Trace(scenario);
                enabled.SetValue(null, true); var actual = Trace(scenario);
                if (expected != actual) throw new Exception("Registration plan mismatch " + scenario + "\n" + expected + "\n" + actual);
                Debug.Log("[T3MPREGPLAN] PASS " + scenario);
            }
        }
        finally { enabled.SetValue(null, before); }
    }
    private static string Trace(string scenario)
    {
        var trace = new List<string>(); var bus = new EventBus();
        void Record(Action action)
        {
            try { action(); }
            catch (Exception e) { trace.Add(e.GetType().Name + ":" + e.Message + ":" + e.InnerException?.GetType().Name); }
        }
        if (scenario.StartsWith("invalid-") || scenario == "byref")
        {
            object invalid = scenario == "invalid-return" ? new InvalidReturn() : scenario == "invalid-arity" ? new InvalidArity() :
                scenario == "invalid-private" ? new InvalidPrivate() : (object)new ByRef();
            Record(() => bus.Register(invalid)); Record(() => bus.Register(invalid));
            Record(() => bus.Unregister(invalid)); return string.Join(";", trace);
        }
        if (scenario == "value")
        {
            var value = new ValueListener { Action = v => trace.Add("value:" + v.Value) };
            bus.Register(value); bus.Post(new ValueMessage { Value = 7 }); bus.PostLoad();
            bus.Post(new ValueMessage { Value = 11 }); bus.Unregister(value); bus.Post(new ValueMessage { Value = 13 });
            return string.Join(";", trace);
        }
        var late = new Listener { Action = m => trace.Add("late:" + m.Value) };
        Listener first = scenario == "derived" ? new Derived() : new Listener();
        var changed = false;
        first.Action = m =>
        {
            trace.Add("first:" + m.Value);
            if (changed) return;
            changed = true;
            if (scenario == "mutation") { bus.Register(late); bus.Unregister(first); }
            if (scenario == "nested") bus.Post(new Message { Value = 2 });
            if (scenario == "exception") throw new InvalidOperationException("callback");
        };
        bus.Register(first);
        bus.Register(new Listener { Action = m => trace.Add("second:" + m.Value) });
        if (scenario == "duplicate") Record(() => bus.Register(first));
        bus.PostLoad(); Record(() => bus.Post(new Message { Value = 1 }));
        Record(() => bus.Post(new Message { Value = 3 }));
        bus.Unregister(first); bus.Unregister(new object());
        Record(() => bus.Post(new Message { Value = 4 }));
        return string.Join(";", trace);
    }
}
