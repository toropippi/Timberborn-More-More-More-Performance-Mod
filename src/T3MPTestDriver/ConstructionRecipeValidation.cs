using System;
using System.Collections.Generic;
using System.Reflection;
using Bindito.Core;
using Bindito.Core.Internal;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static partial class ConstructionPlanExperiment
{
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
