using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using T3MP.Loading;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Preserve RegisterMethod's native validation, closure and registry call.
// Only substitute the resulting Action<object> before native registration.
internal static class EventBusFastDelegates
{
    private const string Owner = "t3mp.runtime.events";
    private static readonly MethodInfo WrapperFactory = typeof(EventBusFastDelegates)
        .GetMethod(nameof(CreateWrapper), BindingFlags.Public | BindingFlags.Static)!;

    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
            apply(RuntimePatches.Find(typeof(Timberborn.SingletonSystem.EventBus), "RegisterMethod", new[] { typeof(object), typeof(MethodInfo) }),
                null, null, nameof(Rewrite), null), typeof(EventBusFastDelegates));
        Installed = true;
        Debug.Log("[T3MP] EventBus fast delegates installed.");
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        var opcode = typeof(T).GetField("opcode", RuntimePatches.All)!;
        var operand = typeof(T).GetField("operand", RuntimePatches.All)!;
        var actionConstructors = new List<int>();
        for (var i = 0; i < list.Count; i++)
        {
            if ((OpCode)opcode.GetValue(list[i])! == OpCodes.Newobj &&
                operand.GetValue(list[i]) is ConstructorInfo constructor && constructor.DeclaringType == typeof(Action<object>))
                actionConstructors.Add(i);
        }
        if (actionConstructors.Count != 1)
            throw new InvalidOperationException("EventBus.RegisterMethod: expected one Action<object> constructor, found " + actionConstructors.Count);
        // The existing delegate is already on the stack. Keep every original
        // instruction, label and exception marker at its original instruction.
        var index = actionConstructors[0] + 1;
        var replacement = typeof(EventBusFastDelegates).GetMethod(nameof(PreferTyped), RuntimePatches.All)!;
        T Instruction(OpCode code, object? value = null) => (T)Activator.CreateInstance(typeof(T), code, value)!;
        list.InsertRange(index, new[] { Instruction(OpCodes.Ldarg_1), Instruction(OpCodes.Ldarg_2), Instruction(OpCodes.Call, replacement) });
        return list;
    }

    // EventBus delivers an event only to subscriptions of its exact type, so the
    // typed delegate replaces the reflection call for every [OnEvent] handler.
    internal static Action<object> PreferTyped(Action<object> original, object subscriber, MethodInfo method)
    {
        var wrapper = (Action<object>)WrapperFactory.MakeGenericMethod(method.GetParameters()[0].ParameterType)
            .Invoke(null, new object[] { subscriber, method })!;
        LoadEventRouter.RememberHandler(wrapper, subscriber, method);
        return wrapper;
    }

    public static Action<object> CreateWrapper<T>(object subscriber, MethodInfo method)
    {
        var typed = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), subscriber, method);
        return eventObject => typed((T)eventObject);
    }
}
