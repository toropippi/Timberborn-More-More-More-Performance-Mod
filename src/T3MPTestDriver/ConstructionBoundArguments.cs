using System;
using Bindito.Core.Internal;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static partial class ConstructionPlanExperiment
{
    private static bool _boundArguments;
    [ThreadStatic] private static long _boundPlans, _boundSources, _multiSources, _unboundPlans;

    private sealed class ArgumentSource
    {
        private readonly Plan _plan;
        private readonly ParameterProvider _provider;
        private readonly InstanceProviderBank _bank;
        private readonly int _index;
        internal ArgumentSource(Plan plan, ParameterProvider provider, InstanceProviderBank bank, int index)
        { _plan = plan; _provider = provider; _bank = bank; _index = index; }

        internal object? First()
        {
            var type = _plan.Types[_index];
            if (_provider._multiBindingService.IsMultiBound(type, out _))
            {
                // Each multi request still enumerates current native bindings
                // and creates a fresh array in their original order.
                _plan.Sources![_index] = Original;
                _multiSources++;
                return Original();
            }
            if (!_bank.TryGetInstanceProvider(type, out var source)) throw MissingParameter(_plan, type);
            // The emitted code subsequently calls the native provider delegate
            // directly, with no provider-context/slot-cache checks per request.
            // Store before GetInstance: native positive entries also survive
            // a throwing transient getter. Missing entries remain unresolved.
            _plan.Sources![_index] = source.GetInstance;
            _boundSources++;
            return source.GetInstance();
        }
        private object? Original() => Resolve(_plan, _provider, _index);
    }

    private static void ReportBoundArguments()
    {
        if (!_boundArguments) return;
        Debug.Log($"[T3MPDIBOUND] plans={_boundPlans} resolvedSources={_boundSources} multiSources={_multiSources} unboundPlans={_unboundPlans}");
        if (_depth == 0) _boundPlans = _boundSources = _multiSources = _unboundPlans = 0;
    }
}
