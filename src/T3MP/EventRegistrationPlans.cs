using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using Timberborn.SingletonSystem;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Keep the existing typed per-subscriber delegate and native registry behavior.
// Cache only immutable method metadata and its generic wrapper factory.
internal static class EventRegistrationPlans
{
    internal static bool Enabled = Environment.GetCommandLineArgs().Contains("-t3mpTestEventRegistrationPlans");
    private sealed class Plan
    {
        internal readonly Type EventType;
        internal readonly Func<object, MethodInfo, Action<object>> Factory;
        internal Plan(Type eventType)
        {
            EventType = eventType;
            Factory = (Func<object, MethodInfo, Action<object>>)Delegate.CreateDelegate(
                typeof(Func<object, MethodInfo, Action<object>>),
                typeof(EventBusFastDelegates).GetMethod(nameof(EventBusFastDelegates.CreateWrapper))!.MakeGenericMethod(eventType));
        }
    }
    private static readonly ConcurrentDictionary<MethodInfo, Plan> Plans = new();
    private static int _warnings;

    internal static bool TryRegister(EventBus bus, object subscriber, MethodInfo method)
    {
        if (!Plans.TryGetValue(method, out var plan))
        {
            if (method.ReturnType != typeof(void))
                throw new ArgumentException($"Can't register {method} of {subscriber.GetType()}. Listening methods must return void.");
            var parameters = method.GetParameters();
            if (parameters.Length != 1)
                throw new ArgumentException($"Can't register {method} of {subscriber.GetType()}. Listening methods must have exactly one parameter.");
            try { plan = Plans.GetOrAdd(method, _ => new Plan(parameters[0].ParameterType)); }
            catch (Exception e) { Warn(method, e); return true; }
        }
        Action<object> wrapper;
        try { wrapper = plan.Factory(subscriber, method); }
        catch (Exception e) { Warn(method, e); return true; }
        // Keep the existing observer's exception behavior, independently of the
        // registry: direct Add already throws the same native exception.
        try { LoadEventRouter.RememberHandler(wrapper, subscriber, method); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        bus._subscriptions.Add(plan.EventType, subscriber, wrapper);
        return false;
    }
    private static void Warn(MethodInfo method, Exception exception)
    {
        if (_warnings++ < 3) Debug.LogWarning($"[T3MPREGPLAN] fallback {method.DeclaringType!.Name}.{method.Name}: {exception.GetType().Name}");
    }
}
