using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Bindito.Core.Internal;
using Binder = Bindito.Core.Internal.Binder;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Load-only experiment. Cache executable metadata, never resolved dependencies
// or constructed objects. Unity creation and Bindito listener order are intact.
internal static partial class ConstructionPlanExperiment
{
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
        var main = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Loading.ConstructionPlans")).FirstOrDefault(t => t != null);
        if (main != null && (bool)(main.GetProperty("Installed", All)?.GetValue(null) ?? false)) return;
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionPlan")) return;
        _linkProviders = Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionLinks");
        _inlineChecks = Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionInlineChecks");
        _boundArguments = Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionBoundArguments");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.construction-plan");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        void Patch(Type type, string name, string prefix, string? finalizer = null)
        {
            object? Hook(string? hook)
            {
                if (hook == null) return null;
                var result = Activator.CreateInstance(hm, typeof(ConstructionPlanExperiment).GetMethod(hook, All))!;
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
        if (Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionRecipes"))
        {
            patch.Invoke(harmony, new object?[] { typeof(InstanceProviderFuncFactory).GetMethod("CreateInstanceProviderFunc", All),
                null, Activator.CreateInstance(hm, typeof(ConstructionPlanExperiment).GetMethod(nameof(AfterProviderFunction), All)), null, null });
            Debug.Log("[T3MPDIRECIPE] installed; lazy per-provider construction and injection recipes");
        }
    }

    private static void Begin(out Scope __state)
    {
        _plans ??= new Dictionary<MethodBase, Plan?>();
        if (_depth == 0) _linkedHits = _linkedMisses = 0;
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
        if (_depth == 0 || __instance._parameterProvider.GetType() != typeof(ParameterProvider) ||
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
        if (_depth == 0 || __instance._parameterProvider.GetType() != typeof(ParameterProvider)) return true;
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
                ? ComponentConstructionExperiment.NativeProviderBank(boundProvider._instanceBank) : null;
            if (bank != null)
            {
                plan.Sources = types.Select((_, index) => (Func<object?>)new ArgumentSource(plan, boundProvider, bank, index).First).ToArray();
                _boundPlans++;
            }
            else _unboundPlans++;
        }
        var dynamic = new DynamicMethod("T3MPDI_" + method.Name, typeof(object),
            new[] { typeof(Plan), typeof(ParameterProvider), typeof(object) }, typeof(ConstructionPlanExperiment).Module, true);
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
                il.Emit(OpCodes.Call, typeof(ConstructionPlanExperiment).GetMethod(nameof(Resolve), All)!);
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
            il.Emit(OpCodes.Call, typeof(ConstructionPlanExperiment).GetMethod(nameof(ReceiverFits), All)!);
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
                il.Emit(OpCodes.Call, typeof(ConstructionPlanExperiment).GetMethod(nameof(Fits), All)!);
                il.Emit(OpCodes.Brfalse, fallback);
                il.Emit(OpCodes.Ldloc, values[i]);
                il.Emit(OpCodes.Call, typeof(ConstructionPlanExperiment).GetMethod(nameof(ConvertArgument), All)!.MakeGenericMethod(types[i]));
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
        il.Emit(OpCodes.Call, typeof(ConstructionPlanExperiment).GetMethod(nameof(Reflect), All)!);
        il.Emit(OpCodes.Stloc, result);
        il.MarkLabel(done); il.Emit(OpCodes.Ldloc, result); il.Emit(OpCodes.Ret);
        plan.Invoke = (Call)dynamic.CreateDelegate(typeof(Call));
        return plan;
    }

    internal static void ValidateIfRequested()
    {
        if (Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionBoundArgumentsValidate")) ValidateBoundArguments();
        if (Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionRecipesValidate")) ValidateRecipes();
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionPlanValidate")) return;
        ValidatePlans();
        if (_linkProviders) ValidateLinks();
    }

    private sealed class Bank : IInstanceBank, IMultiBindingService
    {
        internal readonly List<string> Trace = new List<string>();
        internal Func<Type, object?> Value = t => t == typeof(string) ? "value" : t == typeof(int) ? (object)7 : null;
        internal bool Missing;
        public bool TryGetInstance(Type type, out object instance) { Trace.Add(type.Name); instance = Value(type)!; return !Missing; }
        public bool TryGetExportedInstance(Type type, out object instance) => TryGetInstance(type, out instance);
        public IEnumerable<object> GetInstances(Type type) { Trace.Add("multi:" + type.Name); return new object[] { Value(type)!, Value(type)! }; }
        public IEnumerable<object> GetExportedInstances(Type type) => GetInstances(type);
        public bool IsMultiBound(Type parameterType, out Type type) { type = typeof(string); return parameterType == typeof(string[]); }
    }
    private class Sample
    {
        internal string State = "";
        internal Sample(string text, int number) { State = text + ":" + number; }
        internal virtual string Set(string text, int number) => State = text + ":" + number;
        internal void Multi(string[] values) { State = string.Join(",", values); }
        internal void MultiEnumerable(IEnumerable<string> values) { State = string.Join(",", values); }
        internal void Throw(string text) { throw new InvalidOperationException(text); }
        internal void Nullable(int? number) { State = number?.ToString() ?? "null"; }
        internal void Enum(DayOfWeek day) { State = day.ToString(); }
        internal void Interface(IComparable value) { State = value?.ToString() ?? "null"; }
        internal void Ref(ref int number) { number++; }
        internal static int Static(int number) => number + 1;
    }
    private sealed class Derived : Sample
    {
        internal Derived() : base("derived", 0) { }
        internal override string Set(string text, int number) => State = "override:" + text + ":" + number;
    }
    private sealed class Throwing { internal Throwing(string text) { throw new ArgumentException(text); } }
    private static void ValidateLinks(bool bound = false)
    {
        var checks = 0;
        foreach (var mode in new[] { "local", "parent", "private", "null", "throw", "multi", "switch", "late" })
        {
            string Run(bool fast)
            {
                var trace = new List<string>(); var count = 0;
                var roots = new InstanceProviderBank[2]; var banks = new InstanceProviderBank[2];
                for (var i = 0; i < 2; i++)
                {
                    var label = i.ToString();
                    var root = roots[i] = new InstanceProviderBank(new Binder(null!), null!);
                    var bank = banks[i] = new InstanceProviderBank(new Binder(mode == "local" ? null! : root._binder), mode == "local" ? null! : root);
                    var source = mode == "local" ? bank : root;
                    InstanceProvider Value(string name, bool exported) => new InstanceProvider(() =>
                    {
                        trace.Add(label + name + (++count));
                        if (mode == "throw" && name == "number") throw new InvalidOperationException("provider failure");
                        if (name == "number") return 7;
                        return mode == "null" ? null! : label + name + count;
                    }, exported);
                    if (mode != "late") source._singleInstanceProviders.Add(typeof(string), Value("text", mode != "private"));
                    source._singleInstanceProviders.Add(typeof(int), Value("number", true));
                    root._multiInstanceProviders.Add(typeof(string), new List<InstanceProvider> { Value("first", true), Value("hidden", false), Value("last", true) });
                    bank._multiInstanceProviders.Add(typeof(string), new List<InstanceProvider> { Value("child", false) });
                }
                var providers = banks.Select(b => new ParameterProvider(new InstanceBank(b), new MultiBindingService())).ToArray();
                MethodBase method = mode == "multi" ? typeof(Sample).GetMethod(nameof(Sample.MultiEnumerable), All)! : typeof(Sample).GetConstructors(All).Single();
                var plan = Build(method)!;
                var boundPlans = bound && fast ? providers.Select(provider => Build(method, provider)!).ToArray() : null;
                for (var call = 0; call < 4; call++)
                {
                    if (mode == "late" && call == 1)
                        roots[0]._singleInstanceProviders.Add(typeof(string), new InstanceProvider(() => { trace.Add("late"); return "bound"; }, true));
                    var provider = providers[mode == "switch" ? call % 2 : 0];
                    if (boundPlans != null) plan = boundPlans[mode == "switch" ? call % 2 : 0];
                    var receiver = new Sample("before", 0);
                    try
                    {
                        var value = fast ? plan.Invoke(plan, provider, receiver) : Reflect(plan, receiver, provider.GetParameters(method));
                        trace.Add("result:" + (value is Sample sample ? sample.State : receiver.State));
                    }
                    catch (Exception e)
                    {
                        for (var error = e; error != null; error = error.InnerException)
                            trace.Add(error.GetType().FullName + ":" + error.Message);
                    }
                }
                return string.Join("|", trace);
            }
            var expected = Run(false); var actual = Run(true);
            if (expected != actual) throw new Exception("Provider link differs: " + mode + " expected=" + expected + " actual=" + actual);
            checks++;
        }
        Debug.Log((bound ? "[T3MPDIBOUND] LINK VALIDATE PASS cases=" : "[T3MPDILINK] VALIDATE PASS cases=") + checks + " (fresh values, parent exports, private, null, exceptions, multi order, provider switch, late positive binding)");
    }
    private static void ValidatePlans()
    {
        var ctor = typeof(Sample).GetConstructors(All).Single();
        var methods = new MethodBase[] { ctor, typeof(Sample).GetMethod("Set", All)!, typeof(Sample).GetMethod("Multi", All)!,
            typeof(Sample).GetMethod("Throw", All)!, typeof(Sample).GetMethod("Nullable", All)!, typeof(Sample).GetMethod("Static", All)!,
            typeof(Sample).GetMethod("Enum", All)!, typeof(Sample).GetMethod("Interface", All)!,
            typeof(Throwing).GetConstructors(All).Single() };
        var cases = 0;
        foreach (var method in methods)
        foreach (var mode in new[] { "normal", "null", "numeric", "wrong", "missing", "receiver", "virtual", "boxed", "nullReceiver" })
        {
            string Execute(bool fast)
            {
                var bank = new Bank { Missing = mode == "missing" };
                if (mode == "null") bank.Value = _ => null;
                if (mode == "numeric") bank.Value = t => t == typeof(int) ? (object)(short)5 : "value";
                if (mode == "wrong") bank.Value = _ => new object();
                if (mode == "boxed") bank.Value = t => t == typeof(int?) || t == typeof(int) ? (object)7 :
                    t == typeof(DayOfWeek) ? DayOfWeek.Wednesday : "value";
                var provider = new ParameterProvider(bank, bank);
                object? receiver = mode == "nullReceiver" ? null : mode == "receiver" ? new object() : mode == "virtual" ? new Derived() : new Sample("initial", 0);
                string result;
                try
                {
                    var plan = Build(method)!;
                    var value = fast ? plan.Invoke(plan, provider, receiver) : Reflect(plan, receiver, provider.GetParameters(method));
                    result = value is Sample created ? created.State : value?.ToString() ?? "void";
                }
                catch (Exception ex)
                {
                    result = "";
                    for (var e = ex; e != null; e = e.InnerException) result += e.GetType().FullName + ":" + e.Message + "|";
                }
                return string.Join(",", bank.Trace) + "/" + (receiver as Sample)?.State + "/" + result;
            }
            if (Execute(false) != Execute(true)) throw new Exception("Construction plan mismatch: " + method + " mode=" + mode + " original=" + Execute(false) + " optimized=" + Execute(true));
            cases++;
        }
        if (Build(typeof(Sample).GetMethod("Ref", All)!) != null) throw new Exception("By-ref fallback failed");
        var count = 0;
        var fresh = new Bank { Value = _ => "transient" + (++count) };
        var multi = Build(typeof(Sample).GetMethod("Multi", All)!)!;
        var target = new Sample("initial", 0); var pp = new ParameterProvider(fresh, fresh);
        multi.Invoke(multi, pp, target); var first = target.State;
        multi.Invoke(multi, pp, target);
        if (first == target.State || count != 4) throw new Exception("Dependencies were cached");
        Debug.Log($"[T3MPDI] VALIDATE PASS cases={cases + 2} (arguments, exceptions, virtual dispatch, fallback, fresh dependencies)");
    }
}
