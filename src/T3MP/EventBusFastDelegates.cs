using System;
using System.Reflection;
using T3MP.Loading;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

/// <summary>
/// Replaces the closure EventBus.RegisterMethod builds for every [OnEvent]
/// handler. Vanilla registers
///     e => method.Invoke(subscriber, new object[1] { e })
/// so every event delivery pays a reflection invoke plus an object[]
/// allocation. This registers a compiled delegate instead:
///     Action&lt;T&gt; typed = Delegate.CreateDelegate(...);
///     e => { if (e is T a) { try { typed(a); } catch (x) { throw new TargetInvocationException(x); } } else method.Invoke(subscriber, new[] { e }); }
/// Semantics are identical: same handler, same subscriber, same registration
/// order (the same SubscriptionRegistry.Add call), same validation exceptions
/// (replicated verbatim), handler exceptions wrapped in TargetInvocationException
/// exactly like MethodInfo.Invoke, and an argument of another type keeps the
/// reflective call and its binder exception. Shapes CreateDelegate cannot bind
/// fall back to the vanilla registration.
/// </summary>
internal static class EventBusFastDelegates
{
    private const string Owner = "t3mp.runtime.events";
    private static bool _initialized;
    private static bool _disabled;
    private static int _warnCount;
    private static FieldInfo? _subscriptionsField;
    private static MethodInfo? _registryAddMethod;
    private static MethodInfo? _createWrapperDefinition;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        var bus = typeof(Timberborn.SingletonSystem.EventBus);
        var register = bus.GetMethod("RegisterMethod", RuntimePatches.All | BindingFlags.DeclaredOnly, null,
            new[] { typeof(object), typeof(MethodInfo) }, null);
        if (register == null)
        {
            Debug.LogWarning("[T3MP] EventBus fast delegates: RegisterMethod not found; vanilla retained.");
            return;
        }
        // Raw-IL SHA256 of the reviewed vanilla body (same on 1.0.13.1, 1.1.2.0 and 1.1.2.4).
        if (!RuntimePatches.ReviewedBody(register, "0BAEB08D685FB927EE904C5842CCF2B9E107AED986A9DF3014B380F428BBA44C"))
        {
            Debug.LogWarning("[T3MP] EventBus fast delegates: RegisterMethod is not a reviewed build; vanilla retained.");
            return;
        }
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch,
            apply => apply(register, nameof(Prefix), null, null, null), typeof(EventBusFastDelegates));
        if (Installed) Debug.Log("[T3MP] EventBus fast delegates installed.");
    }

    private static bool Prefix(object __instance, object subscriber, MethodInfo method) =>
        TryRegisterMethod(__instance, subscriber, method);

    // Returns true when the vanilla registration must run.
    internal static bool TryRegisterMethod(object busInstance, object subscriber, MethodInfo method)
    {
        if (_disabled || subscriber is null || method is null) return true;
        if (!_initialized)
        {
            try { Initialize(busInstance.GetType()); }
            catch (Exception exception) { Disable("EventBus inspection failed: " + exception.GetBaseException().Message); }
            if (_disabled) return true;
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
        if (method.ContainsGenericParameters || method.IsStatic || parameterType.IsByRef || parameterType.IsPointer)
        {
            return true;
        }

        Action<object> wrapper;
        try
        {
            wrapper = (Action<object>)_createWrapperDefinition!
                .MakeGenericMethod(parameterType)
                .Invoke(null, new object[] { subscriber, method })!;
        }
        catch (Exception exception)
        {
            if (_warnCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] EventBus fast delegate fallback for {subscriber.GetType().Name}.{method.Name}: {exception.GetBaseException().GetType().Name}");
            }
            return true;
        }

        try
        {
            LoadEventRouter.RememberHandler(wrapper, subscriber, method);
            var registry = _subscriptionsField!.GetValue(busInstance);
            _registryAddMethod!.Invoke(registry, new[] { (object)parameterType, subscriber, wrapper });
        }
        catch (TargetInvocationException invocationException) when (invocationException.InnerException is not null)
        {
            // SubscriptionRegistry.Add throws (duplicate subscriber): rethrow
            // the raw exception exactly like the vanilla direct call would.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(invocationException.InnerException).Throw();
            throw invocationException.InnerException;
        }

        return false;
    }

    public static Action<object> CreateWrapper<T>(object subscriber, MethodInfo method)
    {
        var typed = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), subscriber, method);
        return eventObject =>
        {
            if (eventObject is T argument)
            {
                try
                {
                    typed(argument);
                }
                catch (Exception exception)
                {
                    // MethodInfo.Invoke wraps handler exceptions; keep that shape.
                    throw new TargetInvocationException(exception);
                }
            }
            else
            {
                method.Invoke(subscriber, new[] { eventObject });
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
