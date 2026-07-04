using System;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// Replaces the closure EventBus.RegisterMethod builds for every [OnEvent]
/// handler. Vanilla registers
///     e => method.Invoke(subscriber, new object[1] { e })
/// so EVERY event delivery pays a reflection invoke plus an object[]
/// allocation - measured most painfully at load, where EntityInitializedEvent
/// alone is ~26k posts x ~680 subscriber handlers (~17.7M reflective calls),
/// and at runtime on every entity spawn/delete and need/state event.
///
/// This optimizer registers a compiled delegate instead:
///     Action&lt;T&gt; typed = Delegate.CreateDelegate(...);
///     e => { try { typed((T)e); } catch (e2) { throw new TargetInvocationException(e2); } }
/// Semantics are identical: same handler, same subscriber, same registration
/// order (the same SubscriptionRegistry.Add call), same validation exceptions
/// (replicated verbatim), and handler exceptions are wrapped in
/// TargetInvocationException exactly like MethodInfo.Invoke would. The cast
/// cannot fail because the EventBus routes events by exact runtime type.
/// Any unexpected shape falls back to the vanilla registration path.
/// </summary>
internal static class EventBusFastDelegates
{
    private static bool _initialized;
    private static bool _disabled;
    private static int _warnCount;
    private static FieldInfo? _subscriptionsField;
    private static MethodInfo? _registryAddMethod;
    private static MethodInfo? _createWrapperDefinition;

    public static bool TryRegisterMethod(object busInstance, object subscriber, MethodInfo method)
    {
        if (!BenchmarkSettings.EnableEventBusFastDelegates || _disabled)
        {
            return true;
        }

        if (!_initialized)
        {
            Initialize(busInstance.GetType());
            if (_disabled)
            {
                return true;
            }
        }

        // Replicate the vanilla validations verbatim so invalid subscribers
        // fail with the exact same exceptions.
        if (method.ReturnType != typeof(void))
        {
            throw new ArgumentException($"Can't register {method} of {subscriber.GetType()}. " + "Listening methods must return void.");
        }

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != 1)
        {
            throw new ArgumentException($"Can't register {method} of {subscriber.GetType()}. " + "Listening methods must have exactly one parameter.");
        }

        var parameterType = parameters[0].ParameterType;
        Action<object> wrapper;
        try
        {
            wrapper = (Action<object>)_createWrapperDefinition!
                .MakeGenericMethod(parameterType)
                .Invoke(null, new object[] { subscriber, method })!;
        }
        catch (Exception exception)
        {
            // Shapes CreateDelegate cannot bind fall back to the vanilla
            // reflective registration for this one handler.
            if (_warnCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] EventBus fast delegate fallback for {subscriber.GetType().Name}.{method.Name}: {exception.GetType().Name}");
            }

            return true;
        }

        try
        {
            var registry = _subscriptionsField!.GetValue(busInstance);
            _registryAddMethod!.Invoke(registry, new[] { (object)parameterType, subscriber, wrapper });
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException is not null)
        {
            // SubscriptionRegistry.Add throws (duplicate subscriber) - rethrow
            // the raw exception exactly like the vanilla direct call would.
            throw invocationException.InnerException;
        }

        return false;
    }

    public static Action<object> CreateWrapper<T>(object subscriber, MethodInfo method)
    {
        var typed = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), subscriber, method);
        return eventObject =>
        {
            try
            {
                typed((T)eventObject);
            }
            catch (Exception exception)
            {
                // MethodInfo.Invoke wraps handler exceptions; keep that shape.
                throw new TargetInvocationException(exception);
            }
        };
    }

    private static void Initialize(Type eventBusType)
    {
        _initialized = true;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _subscriptionsField = eventBusType.GetField("_subscriptions", instanceFlags);
        _registryAddMethod = _subscriptionsField?.FieldType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _createWrapperDefinition = typeof(EventBusFastDelegates).GetMethod(nameof(CreateWrapper), BindingFlags.Static | BindingFlags.Public);
        if (_subscriptionsField is null || _registryAddMethod is null || _createWrapperDefinition is null)
        {
            Disable("EventBus internals were not found.");
            return;
        }

        var addParameters = _registryAddMethod.GetParameters();
        if (addParameters.Length != 3 ||
            addParameters[0].ParameterType != typeof(Type) ||
            addParameters[1].ParameterType != typeof(object) ||
            addParameters[2].ParameterType != typeof(Action<object>))
        {
            Disable("SubscriptionRegistry.Add had an unexpected signature.");
        }
    }

    private static void Disable(string reason)
    {
        _disabled = true;
        Debug.LogWarning($"[T3MP] EventBus fast delegates disabled: {reason}");
    }
}
