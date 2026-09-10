using System;
using System.Linq;
using System.Reflection;
using Bindito.Core.Internal;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static partial class ConstructionPlanExperiment
{
    [ThreadStatic] private static long _recipeCalls, _recipeBuilds, _recipeFallbacks;

    private sealed class InjectionStep
    {
        internal MethodInfo Method = null!;
        internal bool Prepared;
        internal Plan? Plan;
    }

    // Owned by the native provider closure, not a global factory/container map.
    // Scoper still wraps this function, including its eager singleton creation.
    private sealed class Recipe
    {
        private readonly InstanceProviderFuncFactory _factory;
        private readonly Type _type;
        private readonly Func<object> _original;
        private bool _prepared;
        private Plan? _constructor;
        private ParameterProvider _parameters = null!;
        private MethodInjector _injector = null!;
        private InjectionStep[]? _injections;

        internal Recipe(InstanceProviderFuncFactory factory, Type type, Func<object> original)
        { _factory = factory; _type = type; _original = original; }

        internal object Invoke()
        {
            if (_depth == 0) return _original();
            if (!_prepared) Prepare();
            if (_constructor == null) { _recipeFallbacks++; return _original(); }
            _recipeCalls++; _constructors++;
            var instance = _constructor.Invoke(_constructor, _parameters, null)!;

            // Native retrieval occurs after the constructor. In particular,
            // a throwing constructor must not cause injection discovery.
            if (_injections == null)
                _injections = _injector._methodRetriever.GetInjectedMethods(instance.GetType())
                    .Select(method => new InjectionStep { Method = method }).ToArray();
            foreach (var injection in _injections)
            {
                if (!injection.Prepared)
                {
                    injection.Plan = _boundArguments ? Build(injection.Method, (ParameterProvider)_injector._parameterProvider) : GetPlan(injection.Method);
                    injection.Prepared = true;
                }
                var plan = injection.Plan;
                if (plan == null) _injector.InjectMethod(instance, injection.Method);
                else
                {
                    ConstructionPlanExperiment._injections++;
                    plan.Invoke(plan, (ParameterProvider)_injector._parameterProvider, instance);
                }
            }
            // Keep live listener lists and their native iteration, including
            // mutation, reentrancy and exception behavior. Never capture them.
            _injector._injectionListenerNotifier.NotifyAllListeners(instance);
            _factory._provisionListenerNotifier.NotifyAllListeners(instance);
            return instance;
        }

        private void Prepare()
        {
            // User-defined creators/retrievers may change behavior on each
            // invocation. Only native metadata retrieval can be reused.
            if (_factory._instanceCreator.GetType() != typeof(InstanceCreator) ||
                _factory._methodInjector.GetType() != typeof(MethodInjector))
            { _prepared = true; return; }
            var creator = (InstanceCreator)_factory._instanceCreator;
            var injector = (MethodInjector)_factory._methodInjector;
            if (creator._constructorRetriever.GetType() != typeof(ConstructorRetriever) ||
                creator._parameterProvider.GetType() != typeof(ParameterProvider) ||
                injector._methodRetriever.GetType() != typeof(MethodRetriever) ||
                injector._parameterProvider.GetType() != typeof(ParameterProvider))
            { _prepared = true; return; }
            var method = creator._constructorRetriever.GetEligibleConstructor(_type);
            var plan = method == null ? null : _boundArguments ? Build(method, (ParameterProvider)creator._parameterProvider) : GetPlan(method);
            _constructor = plan;
            _parameters = (ParameterProvider)creator._parameterProvider;
            _injector = injector;
            _prepared = true;
            if (plan != null) _recipeBuilds++;
        }
    }

    private static void AfterProviderFunction(InstanceProviderFuncFactory __instance,
        ProvisionBinding provisionBinding, ref Func<object> __result)
    {
        if (provisionBinding.Type == null || provisionBinding.Instance != null ||
            provisionBinding.ProviderType != null || provisionBinding.ProviderInstance != null ||
            provisionBinding.ProvidingMethod != null) return;
        // Capture before LoadAll too: many native providers are established
        // while the scene container is being configured. Outside load the
        // wrapper invokes its original function and does not prepare a recipe.
        __result = new Recipe(__instance, provisionBinding.Type, __result).Invoke;
    }

    private static void ReportRecipes()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionRecipes")) return;
        Debug.Log($"[T3MPDIRECIPE] calls={_recipeCalls} builds={_recipeBuilds} fallbacks={_recipeFallbacks}");
        if (_depth == 0) _recipeCalls = _recipeBuilds = _recipeFallbacks = 0;
    }
}
