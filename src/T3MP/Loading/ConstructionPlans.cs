using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Bindito.Core.Internal;
using Binder = Bindito.Core.Internal.Binder;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Load-only plans. Cache executable metadata, never resolved dependencies
// or constructed objects. Unity creation and Bindito listener order are intact.
internal static partial class ConstructionPlans
{
    internal static bool Installed { get; private set; }
    private const string PatchId = "t3mp.load.construction-plans";
    private static bool _compatible;
    private static readonly MethodBase[] ProtectedMethods = typeof(InstanceCreator).Assembly.GetTypes()
        .Where(t => t == typeof(InstanceCreator) || t == typeof(ConstructorRetriever) || t == typeof(MethodInjector) ||
            t == typeof(MethodRetriever) || t == typeof(InstanceProviderFuncFactory) || t == typeof(ParameterProvider))
        .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly)).Cast<MethodBase>().ToArray();
    internal static bool Reviewed() => LoadCompatibility.Reviewed(
        "Bindito.Core|0190a2c8-03c6-4915-8b1d-53d8292182ff",
        "Timberborn.SingletonSystem|962512a9-30fb-4e42-b29f-b0115c9e9015",
        "Timberborn.BlueprintSystem|41363a54-def1-40c7-9ff3-f884cf81cf81",
        "Timberborn.BaseComponentSystem|c0f7b920-5694-4612-bdd5-b736144e1f02");
    private static bool CheckCompatibility() => Reviewed() && LoadCompatibility.Unmodified(ProtectedMethods, PatchId);
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Dictionary<MethodBase, Plan?>? _plans;
    [ThreadStatic] private static long _constructors, _injections, _fallbacks;
    [ThreadStatic] private static long _linkedHits, _linkedMisses;
    private static bool _linkProviders;
    private static bool _inlineChecks;
    private delegate object? Call(Plan plan, ParameterProvider provider, object? receiver);
    private sealed class Plan
    {
        internal MethodBase Method = null!;
        internal Type[] Types = null!;
        internal Call Invoke = null!;
        internal ParameterProvider? LinkedProvider;
        internal InstanceProviderBank? LinkedBank;
        internal InstanceProvider?[]? Providers;
        internal Func<object?>[]? Sources;
    }
    private struct Scope { internal long Constructors, Injections, Fallbacks; internal int Plans; }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (Installed || Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionBaseline")) return;
        if (!Reviewed()) { Debug.Log("[T3MPDIRECIPE] native fallback: unreviewed modules"); return; }
        _linkProviders = _inlineChecks = _boundArguments = false;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, PatchId);
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        try
        {
        void Patch(Type type, string name, string prefix, string? finalizer = null)
        {
            object? Hook(string? hook)
            {
                if (hook == null) return null;
                var result = Activator.CreateInstance(hm, typeof(ConstructionPlans).GetMethod(hook, All))!;
                if (hook == nameof(End)) hm.GetField("priority")!.SetValue(result, 900);
                return result;
            }
            patch.Invoke(harmony, new object?[] { type.GetMethod(name, All), Hook(prefix), null, null, Hook(finalizer) });
        }
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(Begin), nameof(End));
        Patch(typeof(InstanceCreator), "CreateUsingEligibleConstructor", nameof(Construct));
        Patch(typeof(MethodInjector), "InjectMethod", nameof(Inject));
        Debug.Log("[T3MPDI] executable construction plans installed (load only)");
        if (_linkProviders) Debug.Log("[T3MPDILINK] installed; lazy provider links, values remain fresh");
        if (_inlineChecks) Debug.Log("[T3MPDIINLINE] installed; direct argument and receiver checks");
        if (_boundArguments) Debug.Log("[T3MPDIBOUND] installed; context-bound lazy argument sources");
        {
            patch.Invoke(harmony, new object?[] { typeof(InstanceProviderFuncFactory).GetMethod("CreateInstanceProviderFunc", All),
                null, Activator.CreateInstance(hm, typeof(ConstructionPlans).GetMethod(nameof(AfterProviderFunction), All)), null, null });
            Debug.Log("[T3MPDIRECIPE] installed; lazy per-provider construction and injection recipes");
        }
        Installed = true;
        }
        catch (Exception e)
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { PatchId });
            Debug.LogWarning("[T3MPDIRECIPE] disabled: " + e.GetBaseException().Message);
        }
    }

    private static void Begin(out Scope __state)
    {
        _plans ??= new Dictionary<MethodBase, Plan?>();
        if (_depth == 0) { _linkedHits = _linkedMisses = 0; _compatible = CheckCompatibility(); }
        __state = new Scope { Constructors = _constructors, Injections = _injections, Fallbacks = _fallbacks, Plans = _plans.Count };
        _depth++;
    }
    private static void End(Scope __state)
    {
        _depth--;
        Debug.Log($"[T3MPDI] constructors={_constructors - __state.Constructors} injections={_injections - __state.Injections} fallbacks={_fallbacks - __state.Fallbacks} newPlans={_plans!.Count - __state.Plans} cachedPlans={_plans.Count}");
        ReportRecipes();
        ReportBoundArguments();
        if (_depth == 0)
        {
            if (_linkProviders) Debug.Log($"[T3MPDILINK] hits={_linkedHits} misses={_linkedMisses}");
            // Executable metadata survives loads; resolved providers must not keep
            // containers or game-world objects alive across scene unloads.
            foreach (var plan in _plans.Values)
                if (plan != null) { plan.LinkedProvider = null; plan.LinkedBank = null; plan.Providers = null; }
        }
    }
    private static bool Construct(InstanceCreator __instance, Type type, ref object __result)
    {
        if (_depth == 0 || !_compatible || __instance._parameterProvider.GetType() != typeof(ParameterProvider) ||
            __instance._constructorRetriever.GetType() != typeof(ConstructorRetriever)) return true;
        var constructor = __instance._constructorRetriever.GetEligibleConstructor(type);
        if (constructor == null) return true;
        var plan = GetPlan(constructor);
        if (plan == null) { _fallbacks++; return true; }
        _constructors++;
        __result = plan.Invoke(plan, (ParameterProvider)__instance._parameterProvider, null)!;
        return false;
    }
    private static bool Inject(MethodInjector __instance, object injectee, MethodBase method)
    {
        if (_depth == 0 || !_compatible || __instance._parameterProvider.GetType() != typeof(ParameterProvider)) return true;
        var plan = GetPlan(method);
        if (plan == null) { _fallbacks++; return true; }
        _injections++;
        plan.Invoke(plan, (ParameterProvider)__instance._parameterProvider, injectee);
        return false;
    }
    private static Plan? GetPlan(MethodBase method)
    {
        if (_plans!.TryGetValue(method, out var plan)) return plan;
        plan = Build(method); _plans.Add(method, plan); return plan;
    }
    private static object? Resolve(Plan plan, ParameterProvider provider, int index)
    {
        var type = plan.Types[index];
        if (_linkProviders)
        {
            if (!ReferenceEquals(plan.LinkedProvider, provider))
            {
                plan.LinkedProvider = provider;
                plan.LinkedBank = LinkableBank(provider);
                plan.Providers = plan.LinkedBank == null ? null : new InstanceProvider?[plan.Types.Length];
            }
            var bank = plan.LinkedBank;
            if (bank != null)
            {
                var linked = plan.Providers![index];
                if (linked != null) { _linkedHits++; return linked.GetInstance(); }
                if (!provider._multiBindingService.IsMultiBound(type, out _))
                {
                    if (bank.TryGetInstanceProvider(type, out linked))
                    {
                        // Link one argument immediately before its original value
                        // request. Do not eagerly resolve later parameters.
                        plan.Providers[index] = linked;
                        _linkedMisses++;
                        return linked.GetInstance();
                    }
                    throw MissingParameter(plan, type);
                }
            }
        }
        if (provider.TryGetParameter(type, out var value)) return value;
        throw MissingParameter(plan, type);
    }
    private static InvalidOperationException MissingParameter(Plan plan, Type type) =>
        new InvalidOperationException("Can't get parameter " + TypeFormatting.Format(type) + " of method " +
            TypeFormatting.Format(plan.Method.DeclaringType!) + "." + plan.Method.Name + ".");
    private static InstanceProviderBank? LinkableBank(ParameterProvider provider)
    {
        if (provider._instanceBank.GetType() != typeof(InstanceBank) ||
            provider._multiBindingService.GetType() != typeof(MultiBindingService)) return null;
        var first = ((InstanceBank)provider._instanceBank)._instanceProviderBank;
        for (var current = first; current != null; current = ((InstanceProviderBank)current)._parent)
            if (current.GetType() != typeof(InstanceProviderBank)) return null;
        return (InstanceProviderBank)first;
    }
    private static bool Fits(Plan plan, int index, object? value) => value == null || plan.Types[index].IsInstanceOfType(value);
    private static T ConvertArgument<T>(object? value) => value == null ? default! : (T)value;
    private static bool ReceiverFits(Plan plan, object? receiver) => plan.Method is ConstructorInfo || plan.Method.IsStatic ||
        receiver != null && plan.Method.DeclaringType!.IsInstanceOfType(receiver);
    private static object? Reflect(Plan plan, object? receiver, object?[] args) => plan.Method is ConstructorInfo c ? c.Invoke(args) : plan.Method.Invoke(receiver, args);

    private static Plan? Build(MethodBase method, ParameterProvider? boundProvider = null)
    {
        var types = method.GetParameters().Select(p => p.ParameterType).ToArray();
        if (method.ContainsGenericParameters || method.DeclaringType == null || method.DeclaringType.IsValueType ||
            (method.CallingConvention & CallingConventions.VarArgs) != 0 ||
            types.Any(t => t.IsByRef || t.IsPointer || t.IsByRefLike) ||
            method is MethodInfo mi && (mi.ReturnType.IsByRef || mi.ReturnType.IsPointer || mi.ReturnType.IsByRefLike)) return null;
        var plan = new Plan { Method = method, Types = types };
        if (boundProvider != null)
        {
            var bank = boundProvider._multiBindingService?.GetType() == typeof(MultiBindingService)
                ? ComponentConstructionPlans.NativeProviderBank(boundProvider._instanceBank) : null;
            if (bank != null)
            {
                plan.Sources = types.Select((_, index) => (Func<object?>)new ArgumentSource(plan, boundProvider, bank, index).First).ToArray();
                _boundPlans++;
            }
            else _unboundPlans++;
        }
        var dynamic = new DynamicMethod("T3MPDI_" + method.Name, typeof(object),
            new[] { typeof(Plan), typeof(ParameterProvider), typeof(object) }, typeof(ConstructionPlans).Module, true);
        var il = dynamic.GetILGenerator();
        var values = types.Select(_ => il.DeclareLocal(typeof(object))).ToArray();
        var typed = types.Select(il.DeclareLocal).ToArray();
        var result = il.DeclareLocal(typeof(object));
        var fallback = il.DefineLabel();
        var done = il.DefineLabel();
        // Resolve every argument in the original order before validating or
        // invoking. A fallback reuses these values rather than resolving twice.
        for (var i = 0; i < types.Length; i++)
        {
            if (plan.Sources != null)
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, typeof(Plan).GetField(nameof(Plan.Sources), All)!);
                il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Callvirt, typeof(Func<object>).GetMethod("Invoke")!);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(Resolve), All)!);
            }
            il.Emit(OpCodes.Stloc, values[i]);
        }
        if (_inlineChecks)
        {
            if (method is MethodInfo && !method.IsStatic)
            {
                il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Isinst, method.DeclaringType);
                il.Emit(OpCodes.Brfalse, fallback);
            }
        }
        else
        {
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(ReceiverFits), All)!);
            il.Emit(OpCodes.Brfalse, fallback);
        }
        for (var i = 0; i < types.Length; i++)
        {
            if (_inlineChecks)
            {
                var present = il.DefineLabel(); var converted = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, values[i]); il.Emit(OpCodes.Brtrue, present);
                // Reflection supplies default(T) for null value-type arguments.
                // unbox.any on null would instead throw for non-nullable T.
                il.Emit(OpCodes.Ldloca, typed[i]); il.Emit(OpCodes.Initobj, types[i]);
                il.Emit(OpCodes.Br, converted);
                il.MarkLabel(present);
                il.Emit(OpCodes.Ldloc, values[i]); il.Emit(OpCodes.Isinst, types[i]);
                il.Emit(OpCodes.Brfalse, fallback);
                il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(types[i].IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, types[i]);
                il.Emit(OpCodes.Stloc, typed[i]);
                il.MarkLabel(converted);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(Fits), All)!);
                il.Emit(OpCodes.Brfalse, fallback);
                il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(ConvertArgument), All)!.MakeGenericMethod(types[i]));
                il.Emit(OpCodes.Stloc, typed[i]);
            }
        }
        il.BeginExceptionBlock();
        if (method is MethodInfo && !method.IsStatic) { il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass, method.DeclaringType); }
        foreach (var argument in typed) il.Emit(OpCodes.Ldloc, argument);
        if (method is ConstructorInfo constructor) il.Emit(OpCodes.Newobj, constructor);
        else
        {
            var info = (MethodInfo)method;
            il.Emit(info.IsStatic ? OpCodes.Call : OpCodes.Callvirt, info);
            if (info.ReturnType == typeof(void)) il.Emit(OpCodes.Ldnull);
            else if (info.ReturnType.IsValueType) il.Emit(OpCodes.Box, info.ReturnType);
        }
        il.Emit(OpCodes.Stloc, result);
        il.BeginCatchBlock(typeof(Exception));
        il.Emit(OpCodes.Newobj, typeof(TargetInvocationException).GetConstructor(new[] { typeof(Exception) })!);
        il.Emit(OpCodes.Throw);
        il.EndExceptionBlock();
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(fallback);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldc_I4, values.Length); il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < values.Length; i++)
        { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ldloc, values[i]); il.Emit(OpCodes.Stelem_Ref); }
        il.Emit(OpCodes.Call, typeof(ConstructionPlans).GetMethod(nameof(Reflect), All)!);
        il.Emit(OpCodes.Stloc, result);
        il.MarkLabel(done); il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
        plan.Invoke = (Call)dynamic.CreateDelegate(typeof(Call));
        return plan;
    }

}
