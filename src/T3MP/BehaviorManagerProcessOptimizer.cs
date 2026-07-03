using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Timberborn.BehaviorSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class BehaviorManagerProcessOptimizer
{
    private static readonly object LockObject = new object();
    private static FieldInfo? _returnToBehaviorField;
    private static FieldInfo? _runningBehaviorField;
    private static FieldInfo? _rootBehaviorsField;
    private static FieldInfo? _metricsServiceField;
    private static ProcessBehaviorDelegate? _processBehavior;
    private static BoolGetter? _getMetricsEnabled;
    private static BoolGetter? _getReturnToBehavior;
    private static BehaviorGetter? _getRunningBehavior;
    private static RootBehaviorsGetter? _getRootBehaviors;
    private static ObjectGetter? _getMetricsService;
    private static bool _initialized;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _returnToChecks;
    private static long _returnToHits;
    private static long _rootChecks;
    private static long _rootHits;
    private static long _metricsFallbacks;
    private static int _warningLogged;

    private delegate bool ProcessBehaviorDelegate(object manager, Behavior behavior);
    private delegate bool BoolGetter(object instance);
    private delegate Behavior? BehaviorGetter(object instance);
    private delegate List<RootBehavior> RootBehaviorsGetter(object instance);
    private delegate object? ObjectGetter(object instance);

    public static bool TryProcessBehaviors(object behaviorManager)
    {
        if (!BenchmarkSettings.EnableBehaviorManagerProcessOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        try
        {
            if (!EnsureInitialized(behaviorManager.GetType()) ||
                _processBehavior is null ||
                _getReturnToBehavior is null ||
                _getRunningBehavior is null ||
                _getRootBehaviors is null)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            var stats = default(ProcessStats);
            if (MetricsEnabled(behaviorManager))
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _metricsFallbacks);
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            if (_getReturnToBehavior(behaviorManager) &&
                _getRunningBehavior(behaviorManager) is Behavior runningBehavior &&
                runningBehavior)
            {
                if (recordMetrics)
                {
                    stats.ReturnToChecks++;
                }
                if (_processBehavior(behaviorManager, runningBehavior))
                {
                    if (recordMetrics)
                    {
                        stats.ReturnToHits++;
                        RecordStats(stats);
                        Interlocked.Increment(ref _handled);
                    }
                    return true;
                }
            }

            if (_getRootBehaviors(behaviorManager) is not List<RootBehavior> rootBehaviors)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            for (var index = 0; index < rootBehaviors.Count; index++)
            {
                var rootBehavior = rootBehaviors[index];
                if (recordMetrics)
                {
                    stats.RootChecks++;
                }
                if (_processBehavior(behaviorManager, rootBehavior))
                {
                    if (recordMetrics)
                    {
                        stats.RootHits++;
                    }
                    break;
                }
            }

            if (recordMetrics)
            {
                RecordStats(stats);
                Interlocked.Increment(ref _handled);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            if (Interlocked.Exchange(ref _warningLogged, 1) == 0)
            {
                Debug.LogWarning($"[T3MP] BehaviorManagerProcessOptimizer failed once; falling back to vanilla. {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var returnToChecks = Interlocked.Exchange(ref _returnToChecks, 0);
        var returnToHits = Interlocked.Exchange(ref _returnToHits, 0);
        var rootChecks = Interlocked.Exchange(ref _rootChecks, 0);
        var rootHits = Interlocked.Exchange(ref _rootHits, 0);
        var metricsFallbacks = Interlocked.Exchange(ref _metricsFallbacks, 0);
        if (attempts == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var rootChecksPerAttempt = attempts > 0 ? (double)rootChecks / attempts : 0.0;
        Debug.Log(
            $"[T3MP] BehaviorManagerProcessOptimizer aggregate={aggregateId}, enabled={BenchmarkSettings.EnableBehaviorManagerProcessOptimizer}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, metricsFallbacks={metricsFallbacks}, returnToChecks={returnToChecks}, returnToHits={returnToHits}, rootChecks={rootChecks}, rootHits={rootHits}, rootChecksPerAttempt={rootChecksPerAttempt:F2}");
    }

    private static bool MetricsEnabled(object behaviorManager)
    {
        if (_getMetricsService?.Invoke(behaviorManager) is not { } metricsService)
        {
            return false;
        }

        return _getMetricsEnabled?.Invoke(metricsService) == true;
    }

    private static bool EnsureInitialized(Type behaviorManagerType)
    {
        if (_initialized)
        {
            return _processBehavior is not null;
        }

        lock (LockObject)
        {
            if (_initialized)
            {
                return _processBehavior is not null;
            }

            _returnToBehaviorField = behaviorManagerType.GetField("_returnToBehavior", BindingFlags.Instance | BindingFlags.NonPublic);
            _runningBehaviorField = behaviorManagerType.GetField("_runningBehavior", BindingFlags.Instance | BindingFlags.NonPublic);
            _rootBehaviorsField = behaviorManagerType.GetField("_rootBehaviors", BindingFlags.Instance | BindingFlags.NonPublic);
            _metricsServiceField = behaviorManagerType.GetField("_metricsService", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_returnToBehaviorField is not null)
            {
                _getReturnToBehavior = CreateFieldGetter<BoolGetter>(_returnToBehaviorField, typeof(bool), behaviorManagerType);
            }
            if (_runningBehaviorField is not null)
            {
                _getRunningBehavior = CreateFieldGetter<BehaviorGetter>(_runningBehaviorField, typeof(Behavior), behaviorManagerType);
            }
            if (_rootBehaviorsField is not null)
            {
                _getRootBehaviors = CreateFieldGetter<RootBehaviorsGetter>(_rootBehaviorsField, typeof(List<RootBehavior>), behaviorManagerType);
            }
            if (_metricsServiceField is not null)
            {
                _getMetricsService = CreateFieldGetter<ObjectGetter>(_metricsServiceField, typeof(object), behaviorManagerType);
            }

            var processBehaviorMethod = behaviorManagerType.GetMethod("ProcessBehavior", BindingFlags.Instance | BindingFlags.NonPublic);
            if (processBehaviorMethod is not null)
            {
                _processBehavior = CreateProcessBehaviorDelegate(behaviorManagerType, processBehaviorMethod);
            }

            var metricsServiceType = _metricsServiceField?.FieldType;
            var metricsEnabledGetter = metricsServiceType?.GetProperty("MetricsEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true);
            if (metricsServiceType is not null && metricsEnabledGetter is not null)
            {
                _getMetricsEnabled = CreateBoolGetter(metricsServiceType, metricsEnabledGetter);
            }

            _initialized = true;
            return _processBehavior is not null &&
                _getReturnToBehavior is not null &&
                _getRunningBehavior is not null &&
                _getRootBehaviors is not null;
        }
    }

    private static void RecordStats(ProcessStats stats)
    {
        Interlocked.Add(ref _returnToChecks, stats.ReturnToChecks);
        Interlocked.Add(ref _returnToHits, stats.ReturnToHits);
        Interlocked.Add(ref _rootChecks, stats.RootChecks);
        Interlocked.Add(ref _rootHits, stats.RootHits);
    }

    private static TDelegate? CreateFieldGetter<TDelegate>(FieldInfo field, Type returnType, Type declaringType)
        where TDelegate : Delegate
    {
        try
        {
            var method = new DynamicMethod(
                string.Concat("T3MP_BehaviorManager_Get_", field.Name),
                returnType,
                new[] { typeof(object) },
                typeof(BehaviorManagerProcessOptimizer).Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (returnType != field.FieldType && returnType != typeof(object))
            {
                il.Emit(OpCodes.Castclass, returnType);
            }
            il.Emit(OpCodes.Ret);
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to create BehaviorManager field getter: " + exception.Message);
            return null;
        }
    }

    private static ProcessBehaviorDelegate CreateProcessBehaviorDelegate(Type behaviorManagerType, MethodInfo processBehaviorMethod)
    {
        var method = new DynamicMethod(
            "T3MP_ProcessBehavior",
            typeof(bool),
            new[] { typeof(object), typeof(Behavior) },
            typeof(BehaviorManagerProcessOptimizer).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, behaviorManagerType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, processBehaviorMethod);
        il.Emit(OpCodes.Ret);
        return (ProcessBehaviorDelegate)method.CreateDelegate(typeof(ProcessBehaviorDelegate));
    }

    private static BoolGetter CreateBoolGetter(Type ownerType, MethodInfo getter)
    {
        var method = new DynamicMethod(
            "T3MP_GetMetricsEnabled",
            typeof(bool),
            new[] { typeof(object) },
            typeof(BehaviorManagerProcessOptimizer).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ownerType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (BoolGetter)method.CreateDelegate(typeof(BoolGetter));
    }

    private struct ProcessStats
    {
        public long ReturnToChecks;
        public long ReturnToHits;
        public long RootChecks;
        public long RootHits;
    }
}
