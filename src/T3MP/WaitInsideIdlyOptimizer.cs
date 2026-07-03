using System;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BehaviorSystem;
using Timberborn.EnterableSystem;
using Timberborn.WalkingSystem;
using Timberborn.WorkSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WaitInsideIdlyOptimizer
{
    private static readonly object CacheLock = new object();
    private static readonly ConditionalWeakTable<BehaviorAgent, AgentComponents> AgentComponentsByAgent = new ConditionalWeakTable<BehaviorAgent, AgentComponents>();

    private static GetWorkplaceDelegate? _getWorkplace;
    private static GetEnterableDelegate? _getEnterable;
    private static int _reflectionInitialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _successes;
    private static long _failures;
    private static long _running;
    private static long _agentCacheMisses;

    private delegate Workplace? GetWorkplaceDelegate(object instance);
    private delegate Enterable? GetEnterableDelegate(object instance);

    public static bool TryDecide(object instance, BehaviorAgent agent, ref Decision result)
    {
        if (!BenchmarkSettings.EnableWaitInsideIdlyOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Increment(ref _attempts);
        if (!TryInitializeReflection(instance.GetType()) ||
            _getWorkplace?.Invoke(instance) is not Workplace workplace ||
            _getEnterable?.Invoke(instance) is not Enterable enterable)
        {
            Increment(ref _fallbacks);
            return false;
        }

        try
        {
            var components = GetAgentComponents(agent);
            switch (components.WalkInsideExecutor.LaunchForLimitedTime(enterable))
            {
                case ExecutorStatus.Success:
                    components.WaitExecutor.LaunchForIdleTime();
                    result = Decision.ReleaseWhenFinished(components.WaitExecutor);
                    Increment(ref _successes);
                    break;
                case ExecutorStatus.Failure:
                    workplace.UnassignWorker(components.Worker);
                    result = Decision.ReleaseNextTick();
                    Increment(ref _failures);
                    break;
                case ExecutorStatus.Running:
                    result = Decision.ReleaseWhenFinished(components.WalkInsideExecutor);
                    Increment(ref _running);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            Increment(ref _handled);
            return true;
        }
        catch (Exception exception)
        {
            Increment(ref _fallbacks);
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] WaitInsideIdly optimizer fallback: " + exception);
            }

            return false;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var successes = Interlocked.Exchange(ref _successes, 0);
        var failures = Interlocked.Exchange(ref _failures, 0);
        var running = Interlocked.Exchange(ref _running, 0);
        var agentCacheMisses = Interlocked.Exchange(ref _agentCacheMisses, 0);
        if (attempts == 0 && handled == 0 && fallbacks == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] WaitInsideIdlyOptimizer aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, successes={6}, failures={7}, running={8}, agentCacheMisses={9}",
            aggregateId,
            BenchmarkSettings.EnableWaitInsideIdlyOptimizer,
            attempts,
            handled,
            attempts > 0 ? handled / (double)attempts : 0.0,
            fallbacks,
            successes,
            failures,
            running,
            agentCacheMisses));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _successes, 0);
        Interlocked.Exchange(ref _failures, 0);
        Interlocked.Exchange(ref _running, 0);
        Interlocked.Exchange(ref _agentCacheMisses, 0);
    }

    private static AgentComponents GetAgentComponents(BehaviorAgent agent)
    {
        if (AgentComponentsByAgent.TryGetValue(agent, out var components))
        {
            return components;
        }

        lock (CacheLock)
        {
            if (AgentComponentsByAgent.TryGetValue(agent, out components))
            {
                return components;
            }

            components = new AgentComponents(
                agent.GetComponent<WalkInsideExecutor>(),
                agent.GetComponent<WaitExecutor>(),
                agent.GetComponent<Worker>());
            AgentComponentsByAgent.Add(agent, components);
            Increment(ref _agentCacheMisses);
            return components;
        }
    }

    private static void Increment(ref long value)
    {
        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            Interlocked.Increment(ref value);
        }
    }

    private static bool TryInitializeReflection(Type behaviorType)
    {
        if (Volatile.Read(ref _reflectionInitialized) == 1)
        {
            return _getWorkplace is not null && _getEnterable is not null;
        }

        var workplaceField = behaviorType.GetField("_workplace", BindingFlags.Instance | BindingFlags.NonPublic);
        var enterableField = behaviorType.GetField("_enterable", BindingFlags.Instance | BindingFlags.NonPublic);
        _getWorkplace = CreateFieldGetter<GetWorkplaceDelegate>(workplaceField, typeof(Workplace), behaviorType);
        _getEnterable = CreateFieldGetter<GetEnterableDelegate>(enterableField, typeof(Enterable), behaviorType);
        Volatile.Write(ref _reflectionInitialized, 1);
        if (_getWorkplace is null || _getEnterable is null)
        {
            Debug.LogWarning("[T3MP] WaitInsideIdly optimizer could not find expected fields.");
            return false;
        }

        return true;
    }

    private static TDelegate? CreateFieldGetter<TDelegate>(FieldInfo? field, Type returnType, Type declaringType)
        where TDelegate : Delegate
    {
        if (field is null)
        {
            return null;
        }

        try
        {
            var method = new DynamicMethod(
                string.Concat("T3MP_WaitInside_Get_", field.Name),
                returnType,
                new[] { typeof(object) },
                typeof(WaitInsideIdlyOptimizer).Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType != returnType)
            {
                il.Emit(OpCodes.Castclass, returnType);
            }

            il.Emit(OpCodes.Ret);
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to create WaitInside field getter: " + exception.Message);
            return null;
        }
    }

    private sealed class AgentComponents
    {
        public AgentComponents(WalkInsideExecutor walkInsideExecutor, WaitExecutor waitExecutor, Worker worker)
        {
            WalkInsideExecutor = walkInsideExecutor;
            WaitExecutor = waitExecutor;
            Worker = worker;
        }

        public WalkInsideExecutor WalkInsideExecutor { get; }
        public WaitExecutor WaitExecutor { get; }
        public Worker Worker { get; }
    }
}
