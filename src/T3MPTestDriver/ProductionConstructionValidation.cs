using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Bindito.Core;
using Bindito.Core.Internal;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// The existing semantic fixtures execute methods and recipes from Code.dll,
// including its emitted delegates and native Harmony hooks, not a driver copy.
internal static class ProductionConstructionValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type Target => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Loading.ConstructionPlans")).First(t => t != null)!;
    private static object? Get(string name) => Target.GetField(name, All)!.GetValue(null);
    private static void Set(string name, object value) => Target.GetField(name, All)!.SetValue(null, value);
    internal static void Validate()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestProductionRecipesValidate") || !(bool)Target.GetProperty("Installed", All)!.GetValue(null)!) return;
        ValidatePlans(); ValidateRecipes();
        Debug.Log("[T3MPPRODUCTIONRECIPES] main DLL plan and provider fixtures PASS");
    }
    private sealed class Plan
    {
        internal MethodBase Method = null!;
        internal Func<Plan, ParameterProvider, object?, object?> Invoke = null!;
    }
    private static Plan? Build(MethodBase method)
    {
        var native = Target.GetMethod("Build", All)!.Invoke(null, new object?[] { method, null });
        if (native == null) return null;
        var call = (Delegate)native.GetType().GetField("Invoke", All)!.GetValue(native)!;
        return new Plan { Method = method, Invoke = (_, provider, receiver) => {
            try { return call.DynamicInvoke(native, provider, receiver); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        } };
    }
    private static object? Reflect(Plan plan, object? receiver, object?[] args) => plan.Method is ConstructorInfo c ? c.Invoke(args) : plan.Method.Invoke(receiver, args);
    private sealed class Recipe
    {
        private readonly Func<object> _invoke;
        internal Recipe(InstanceProviderFuncFactory factory, Type type, Func<object> original)
        {
            var nested = Target.GetNestedType("Recipe", All)!;
            var native = Activator.CreateInstance(nested, All, null, new object[] { factory, type, original }, null)!;
            _invoke = (Func<object>)nested.GetMethod("Invoke", All)!.CreateDelegate(typeof(Func<object>), native);
        }
        internal object Invoke() => _invoke();
    }
    private static int _depth { get => (int)Get("_depth")!; set => Set("_depth", value); }
    private static long _constructors { get => (long)Get("_constructors")!; set => Set("_constructors", value); }
    private static long _injections { get => (long)Get("_injections")!; set => Set("_injections", value); }
    private static long _fallbacks { get => (long)Get("_fallbacks")!; set => Set("_fallbacks", value); }
    private static long _recipeCalls { get => (long)Get("_recipeCalls")!; set => Set("_recipeCalls", value); }
    private static long _recipeBuilds { get => (long)Get("_recipeBuilds")!; set => Set("_recipeBuilds", value); }
    private static long _recipeFallbacks { get => (long)Get("_recipeFallbacks")!; set => Set("_recipeFallbacks", value); }
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
    private static Action<string> _recipeRecord = null!;
    private class RecipeSubject
    {
        private string _state;
        internal RecipeSubject(string text, int number)
        { _state = text + ":" + number; _recipeRecord("ctor:" + _state); }
        [Inject] internal virtual void First(string text)
        { _state += ":first:" + text; _recipeRecord("first:" + text); }
        [Inject] internal void Second(int number)
        { _state += ":second:" + number; _recipeRecord("second:" + number); }
        public override string ToString() => _state;
    }
    private sealed class RecipeEmpty
    {
        private readonly string _state;
        internal RecipeEmpty(string text) { _state = text; _recipeRecord("ctor:" + text); }
        public override string ToString() => _state;
    }
    private sealed class RecipeListener : IInjectionListener, IProvisionListener
    {
        private readonly Action<object> _listen;
        internal RecipeListener(Action<object> listen) { _listen = listen; }
        public void Listen(object instance) => _listen(instance);
    }
    private sealed class RecipeCreator : IInstanceCreator
    {
        internal IInstanceCreator Native = null!;
        public object CreateInstance(Type type) { _recipeRecord("custom-creator"); return Native.CreateInstance(type); }
    }
    private sealed class RecipeInjector : IMethodInjector
    {
        internal IMethodInjector Native = null!;
        public void Inject(object instance) { _recipeRecord("custom-injector"); Native.Inject(instance); }
    }
    private sealed class RecipeRetriever : IMethodRetriever
    {
        private readonly MethodRetriever _native = new MethodRetriever();
        public IEnumerable<MethodInfo> GetInjectedMethods(Type type)
        { _recipeRecord("custom-retriever"); return _native.GetInjectedMethods(type); }
    }

    private static void ValidateRecipes()
    {
        var depth = _depth;
        var previousRecord = _recipeRecord;
        var counts = new[] { _constructors, _injections, _fallbacks, _recipeCalls, _recipeBuilds, _recipeFallbacks };
        var cases = 0;
        try
        {
            foreach (var mode in new[] { "normal", "null", "numeric", "wrong", "missing", "empty",
                         "ctor", "first", "second", "injection-listener", "provision-listener",
                         "live-list", "reentrant", "outside", "custom-creator", "custom-injector", "custom-retriever" })
            foreach (var scope in new[] { Bindito.Core.Internal.Scope.Transient, Bindito.Core.Internal.Scope.Singleton })
            {
                string Run(bool fast)
                {
                    _depth = fast ? 1 : 0;
                    var trace = new List<string>();
                    _recipeRecord = value =>
                    {
                        trace.Add(value);
                        if ((mode == "ctor" || mode == "first" || mode == "second" ||
                             mode == "injection-listener" || mode == "provision-listener") && value.Split(':')[0] == mode)
                            throw new InvalidOperationException("fixture:" + mode);
                    };
                    var requests = 0;
                    var bank = new Bank { Missing = mode == "missing", Value = type =>
                    {
                        trace.Add("resolve:" + type.Name + ":" + (++requests));
                        if (mode == "null") return null;
                        if (mode == "wrong") return new object();
                        if (type == typeof(int)) return mode == "numeric" ? (object)(short)5 : 7;
                        return "value" + requests;
                    } };
                    var parameter = new ParameterProvider(bank, bank);
                    IInstanceCreator creator = new InstanceCreator(parameter, new ConstructorRetriever());
                    var injectionListeners = new InjectionListenerNotifier();
                    var provisionListeners = new ProvisionListenerNotifier();
                    IMethodInjector injector = new MethodInjector(parameter,
                        mode == "custom-retriever" ? (IMethodRetriever)new RecipeRetriever() : new MethodRetriever(), injectionListeners);
                    if (mode == "custom-creator") creator = new RecipeCreator { Native = creator };
                    if (mode == "custom-injector") injector = new RecipeInjector { Native = injector };
                    var factory = new InstanceProviderFuncFactory(creator, injector, provisionListeners, null!, bank);
                    var type = mode == "empty" ? typeof(RecipeEmpty) : typeof(RecipeSubject);
                    Func<object> original = () => factory.ProvideCreatedInstance(type);
                    var recipe = new Recipe(factory, type, original);
                    Func<object> source = fast ? recipe.Invoke : original;
                    Func<object>? scoped = null;
                    var changed = false; var inside = false;
                    injectionListeners.AddListener(new RecipeListener(value =>
                    {
                        _recipeRecord("injection-listener:" + value);
                        if (mode == "live-list" && !changed)
                        {
                            changed = true;
                            injectionListeners.AddListener(new RecipeListener(_ => _recipeRecord("late-listener")));
                        }
                    }));
                    provisionListeners.AddListener(new RecipeListener(value =>
                    {
                        _recipeRecord("provision-listener:" + value);
                        if (mode == "reentrant" && !inside)
                        {
                            inside = true;
                            // During eager singleton creation the scoped
                            // delegate has not been returned yet.
                            trace.Add("nested-result:" + source());
                            inside = false;
                        }
                    }));
                    object? first = null;
                    for (var call = 0; call < 3; call++)
                    {
                        _depth = fast && !(mode == "outside" && call == 1) ? 1 : 0;
                        try
                        {
                            scoped ??= new Scoper().PlaceInScope(source, scope);
                            var value = scoped();
                            trace.Add("result:" + value + ":same=" + ReferenceEquals(first, value));
                            first ??= value;
                        }
                        catch (Exception error)
                        {
                            for (var e = error; e != null; e = e.InnerException)
                                trace.Add("error:" + e.GetType().FullName + ":" + e.Message);
                        }
                    }
                    return string.Join("|", trace) + "/bank:" + string.Join(",", bank.Trace);
                }
                var expected = Run(false); var actual = Run(true);
                if (expected != actual)
                    throw new Exception("Construction recipe differs: " + mode + "/" + scope + " expected=" + expected + " actual=" + actual);
                cases++;
            }
            Debug.Log($"[T3MPDIRECIPE] VALIDATE PASS cases={cases} (fresh dependencies, singleton identity/eagerness, ordered injection/listeners, failures, reentrancy, live listener mutation, outside-load and custom-service fallback)");
        }
        finally
        {
            _depth = depth; _recipeRecord = previousRecord;
            _constructors = counts[0]; _injections = counts[1]; _fallbacks = counts[2];
            _recipeCalls = counts[3]; _recipeBuilds = counts[4]; _recipeFallbacks = counts[5];
        }
    }
}
