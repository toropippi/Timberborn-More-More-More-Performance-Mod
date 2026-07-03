using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WaterObjectServiceFastSkip
{
    private static GetListDelegate? _getWaterObjects;
    private static GetObjectDelegate? _getWaterMap;
    private static GetBoolObjectDelegate? _getAnyColumnChanged;
    private static UpdateWaterAboveBaseDelegate? _updateWaterAboveBase;
    private static int _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _skippedNoWaterChange;
    private static long _directRuns;
    private static long _objectsVisited;
    private static long _stopwatchTicks;
    private static long _maxStopwatchTicks;

    private delegate IList? GetListDelegate(object instance);

    private delegate object? GetObjectDelegate(object instance);

    private delegate bool GetBoolObjectDelegate(object instance);

    private delegate void UpdateWaterAboveBaseDelegate(object instance);

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableWaterObjectServiceFastSkip ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        var started = recordMetrics ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (!EnsureInitialized(instance.GetType()))
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return true;
            }

            var waterObjects = _getWaterObjects?.Invoke(instance);
            if (waterObjects is null || waterObjects.Count == 0)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _handled);
                }
                return false;
            }

            var first = waterObjects[0];
            var waterMap = first is null ? null : _getWaterMap?.Invoke(first);
            if (waterMap is null)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return true;
            }

            if (_getAnyColumnChanged?.Invoke(waterMap) != true)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _skippedNoWaterChange);
                    Interlocked.Increment(ref _handled);
                }
                return false;
            }

            for (var index = 0; index < waterObjects.Count; index++)
            {
                var waterObject = waterObjects[index];
                if (waterObject is null)
                {
                    continue;
                }

                _updateWaterAboveBase?.Invoke(waterObject);
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _objectsVisited);
                }
            }

            if (recordMetrics)
            {
                Interlocked.Increment(ref _directRuns);
                Interlocked.Increment(ref _handled);
            }
            return false;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] WaterObjectServiceFastSkip fallback: " + exception);
            }

            return true;
        }
        finally
        {
            if (recordMetrics)
            {
                var elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref _stopwatchTicks, elapsed);
                UpdateMax(ref _maxStopwatchTicks, elapsed);
            }
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
        var skippedNoWaterChange = Interlocked.Exchange(ref _skippedNoWaterChange, 0);
        var directRuns = Interlocked.Exchange(ref _directRuns, 0);
        var objectsVisited = Interlocked.Exchange(ref _objectsVisited, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        var maxTicks = Interlocked.Exchange(ref _maxStopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] WaterObjectServiceFastSkip aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, skippedNoWaterChange={6}, directRuns={7}, skipRate={8:F3}, objectsVisited={9}, avgObjectsPerDirectRun={10:F1}, ms={11:F3}, avgUs={12:F3}, maxMs={13:F3}",
            aggregateId,
            BenchmarkSettings.EnableWaterObjectServiceFastSkip,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            fallbacks,
            skippedNoWaterChange,
            directRuns,
            attempts > 0 ? (double)skippedNoWaterChange / attempts : 0.0,
            objectsVisited,
            directRuns > 0 ? (double)objectsVisited / directRuns : 0.0,
            ToMilliseconds(ticks),
            attempts > 0 ? ToMilliseconds(ticks) * 1000.0 / attempts : 0.0,
            ToMilliseconds(maxTicks)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _skippedNoWaterChange, 0);
        Interlocked.Exchange(ref _directRuns, 0);
        Interlocked.Exchange(ref _objectsVisited, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
        Interlocked.Exchange(ref _maxStopwatchTicks, 0);
    }

    private static bool EnsureInitialized(Type serviceType)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return AccessorsReady();
        }

        try
        {
            _getWaterObjects = CreateWaterObjectsGetter(serviceType);
            var waterObjectType = serviceType.Assembly.GetType("Timberborn.WaterObjects.WaterObject");
            _getWaterMap = CreateObjectFieldGetter(waterObjectType, "_threadSafeWaterMap");
            var waterMapType = waterObjectType?.GetField("_threadSafeWaterMap", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            _getAnyColumnChanged = CreateBoolPropertyGetter(waterMapType, "AnyColumnChanged");
            _updateWaterAboveBase = CreateUpdateWaterAboveBase(waterObjectType);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to initialize WaterObjectServiceFastSkip: " + exception);
        }

        Volatile.Write(ref _initialized, 1);
        return AccessorsReady();
    }

    private static bool AccessorsReady()
    {
        return _getWaterObjects is not null &&
            _getWaterMap is not null &&
            _getAnyColumnChanged is not null &&
            _updateWaterAboveBase is not null;
    }

    private static GetListDelegate? CreateWaterObjectsGetter(Type serviceType)
    {
        var field = serviceType.GetField("_waterObjects", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_WaterObjectFast_GetList",
            typeof(IList),
            new[] { typeof(object) },
            typeof(WaterObjectServiceFastSkip).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, serviceType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Castclass, typeof(IList));
        il.Emit(OpCodes.Ret);
        return (GetListDelegate)method.CreateDelegate(typeof(GetListDelegate));
    }

    private static GetObjectDelegate? CreateObjectFieldGetter(Type? instanceType, string fieldName)
    {
        var field = instanceType?.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (instanceType is null || field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_WaterObjectFast_Get_", fieldName),
            typeof(object),
            new[] { typeof(object) },
            typeof(WaterObjectServiceFastSkip).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldfld, field);
        if (field.FieldType.IsValueType)
        {
            il.Emit(OpCodes.Box, field.FieldType);
        }

        il.Emit(OpCodes.Ret);
        return (GetObjectDelegate)method.CreateDelegate(typeof(GetObjectDelegate));
    }

    private static GetBoolObjectDelegate? CreateBoolPropertyGetter(Type? instanceType, string propertyName)
    {
        var getter = instanceType?.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true);
        if (instanceType is null || getter is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_WaterObjectFast_Get_", propertyName),
            typeof(bool),
            new[] { typeof(object) },
            typeof(WaterObjectServiceFastSkip).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (GetBoolObjectDelegate)method.CreateDelegate(typeof(GetBoolObjectDelegate));
    }

    private static UpdateWaterAboveBaseDelegate? CreateUpdateWaterAboveBase(Type? instanceType)
    {
        var methodInfo = instanceType?.GetMethod("UpdateWaterAboveBase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (instanceType is null || methodInfo is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_WaterObjectFast_UpdateWaterAboveBase",
            typeof(void),
            new[] { typeof(object) },
            typeof(WaterObjectServiceFastSkip).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, methodInfo);
        il.Emit(OpCodes.Ret);
        return (UpdateWaterAboveBaseDelegate)method.CreateDelegate(typeof(UpdateWaterAboveBaseDelegate));
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }
}
