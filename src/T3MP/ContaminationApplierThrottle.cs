using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class ContaminationApplierThrottle
{
    private const float MinimumWaterContamination = 0.05f;
    private const float ContaminationProbability = 0.01f;

    private static readonly ConditionalWeakTable<object, TickState> States = new ConditionalWeakTable<object, TickState>();

    private static GetObjectDelegate? _getWaterMap;
    private static GetObjectDelegate? _getRandomNumberGenerator;
    private static GetObjectDelegate? _getWaterResistor;
    private static GetObjectDelegate? _getContaminationIncubator;
    private static GetObjectDelegate? _getContaminable;
    private static GetTransformDelegate? _getTransform;
    private static GetBoolDelegate? _getIsWaterResistant;
    private static GetBoolDelegate? _getIsIncubating;
    private static GetBoolDelegate? _getIsContaminated;
    private static CellIsUnderwaterDelegate? _cellIsUnderwater;
    private static ColumnContaminationDelegate? _columnContamination;
    private static CheckProbabilityDelegate? _checkProbability;
    private static StartIncubationDelegate? _startIncubation;
    private static int _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _skipped;
    private static long _fallbacks;
    private static long _waterChecks;
    private static long _underwaterHits;
    private static long _probabilityChecks;
    private static long _incubations;
    private static long _stopwatchTicks;
    private static long _maxStopwatchTicks;

    private delegate object? GetObjectDelegate(object instance);

    private delegate Transform? GetTransformDelegate(object instance);

    private delegate bool GetBoolDelegate(object instance);

    private delegate bool CellIsUnderwaterDelegate(object instance, Vector3Int coordinates);

    private delegate float ColumnContaminationDelegate(object instance, Vector3Int coordinates);

    private delegate bool CheckProbabilityDelegate(object instance, float probability);

    private delegate void StartIncubationDelegate(object instance);

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableContaminationApplierThrottle ||
            BenchmarkSettings.ContaminationApplierThrottleTicks <= 1 ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        var state = States.GetValue(instance, _ => new TickState());
        state.TickCount++;
        if (state.TickCount % BenchmarkSettings.ContaminationApplierThrottleTicks != 0)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _skipped);
            }
            return false;
        }

        var started = recordMetrics ? Stopwatch.GetTimestamp() : 0L;
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

            TryApplyScaled(instance, recordMetrics);
            if (recordMetrics)
            {
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
                Debug.LogWarning("[T3MP] ContaminationApplierThrottle fallback: " + exception);
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
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var skipped = Interlocked.Exchange(ref _skipped, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var waterChecks = Interlocked.Exchange(ref _waterChecks, 0);
        var underwaterHits = Interlocked.Exchange(ref _underwaterHits, 0);
        var probabilityChecks = Interlocked.Exchange(ref _probabilityChecks, 0);
        var incubations = Interlocked.Exchange(ref _incubations, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        var maxTicks = Interlocked.Exchange(ref _maxStopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] ContaminationApplierThrottle aggregate={0}, enabled={1}, interval={2}, attempts={3}, handled={4}, skipped={5}, skipRate={6:F3}, fallbacks={7}, waterChecks={8}, underwaterHits={9}, probabilityChecks={10}, incubations={11}, ms={12:F3}, avgUs={13:F3}, maxMs={14:F3}",
            aggregateId,
            BenchmarkSettings.EnableContaminationApplierThrottle,
            BenchmarkSettings.ContaminationApplierThrottleTicks,
            attempts,
            handled,
            skipped,
            attempts > 0 ? (double)skipped / attempts : 0.0,
            fallbacks,
            waterChecks,
            underwaterHits,
            probabilityChecks,
            incubations,
            ToMilliseconds(ticks),
            attempts > 0 ? ToMilliseconds(ticks) * 1000.0 / attempts : 0.0,
            ToMilliseconds(maxTicks)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _skipped, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _waterChecks, 0);
        Interlocked.Exchange(ref _underwaterHits, 0);
        Interlocked.Exchange(ref _probabilityChecks, 0);
        Interlocked.Exchange(ref _incubations, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
        Interlocked.Exchange(ref _maxStopwatchTicks, 0);
    }

    private static void TryApplyScaled(object instance, bool recordMetrics)
    {
        var waterResistor = _getWaterResistor?.Invoke(instance);
        if (waterResistor is not null && _getIsWaterResistant?.Invoke(waterResistor) == true)
        {
            return;
        }

        var contaminationIncubator = _getContaminationIncubator?.Invoke(instance);
        if (contaminationIncubator is null || _getIsIncubating?.Invoke(contaminationIncubator) == true)
        {
            return;
        }

        var contaminable = _getContaminable?.Invoke(instance);
        if (contaminable is null || _getIsContaminated?.Invoke(contaminable) == true)
        {
            return;
        }

        var transform = _getTransform?.Invoke(instance);
        var waterMap = _getWaterMap?.Invoke(instance);
        var randomNumberGenerator = _getRandomNumberGenerator?.Invoke(instance);
        if (transform is null || waterMap is null || randomNumberGenerator is null)
        {
            throw new InvalidOperationException("Contamination accessors are not initialized.");
        }

        var coordinates = NavigationCoordinateSystem.WorldToGridInt(transform.position);
        if (recordMetrics)
        {
            Interlocked.Increment(ref _waterChecks);
        }
        if (_cellIsUnderwater?.Invoke(waterMap, coordinates) != true)
        {
            return;
        }

        if (recordMetrics)
        {
            Interlocked.Increment(ref _underwaterHits);
        }
        var contamination = _columnContamination?.Invoke(waterMap, coordinates) ?? 0f;
        if (contamination < MinimumWaterContamination)
        {
            return;
        }

        if (recordMetrics)
        {
            Interlocked.Increment(ref _probabilityChecks);
        }
        var perTickProbability = Mathf.Clamp01(contamination * ContaminationProbability);
        var scaledProbability = 1f - Mathf.Pow(1f - perTickProbability, BenchmarkSettings.ContaminationApplierThrottleTicks);
        if (_checkProbability?.Invoke(randomNumberGenerator, scaledProbability) == true)
        {
            _startIncubation?.Invoke(contaminationIncubator);
            if (recordMetrics)
            {
                Interlocked.Increment(ref _incubations);
            }
        }
    }

    private static bool EnsureInitialized(Type instanceType)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return AccessorsReady();
        }

        try
        {
            _getWaterMap = CreateObjectFieldGetter(instanceType, "_threadSafeWaterMap");
            _getRandomNumberGenerator = CreateObjectFieldGetter(instanceType, "_randomNumberGenerator");
            _getWaterResistor = CreateObjectFieldGetter(instanceType, "_waterResistor");
            _getContaminationIncubator = CreateObjectFieldGetter(instanceType, "_contaminationIncubator");
            _getContaminable = CreateObjectFieldGetter(instanceType, "_contaminable");
            _getTransform = CreateTransformGetter(instanceType);

            var waterResistorType = instanceType.GetField("_waterResistor", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            var incubatorType = instanceType.GetField("_contaminationIncubator", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            var contaminableType = instanceType.GetField("_contaminable", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            var waterMapType = instanceType.GetField("_threadSafeWaterMap", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            var rngType = instanceType.GetField("_randomNumberGenerator", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;

            _getIsWaterResistant = CreateBoolPropertyGetter(waterResistorType, "IsWaterResistant");
            _getIsIncubating = CreateBoolPropertyGetter(incubatorType, "IsIncubating");
            _getIsContaminated = CreateBoolPropertyGetter(contaminableType, "IsContaminated");
            _cellIsUnderwater = CreateCellIsUnderwater(waterMapType);
            _columnContamination = CreateColumnContamination(waterMapType);
            _checkProbability = CreateCheckProbability(rngType);
            _startIncubation = CreateStartIncubation(incubatorType);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to initialize ContaminationApplierThrottle: " + exception);
        }

        Volatile.Write(ref _initialized, 1);
        return AccessorsReady();
    }

    private static bool AccessorsReady()
    {
        return _getWaterMap is not null &&
            _getRandomNumberGenerator is not null &&
            _getWaterResistor is not null &&
            _getContaminationIncubator is not null &&
            _getContaminable is not null &&
            _getTransform is not null &&
            _getIsIncubating is not null &&
            _getIsContaminated is not null &&
            _cellIsUnderwater is not null &&
            _columnContamination is not null &&
            _checkProbability is not null &&
            _startIncubation is not null;
    }

    private static GetObjectDelegate? CreateObjectFieldGetter(Type instanceType, string fieldName)
    {
        var field = instanceType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_Contamination_Get_", fieldName),
            typeof(object),
            new[] { typeof(object) },
            typeof(ContaminationApplierThrottle).Module,
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

    private static GetTransformDelegate? CreateTransformGetter(Type instanceType)
    {
        var property = instanceType.GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var getter = property?.GetGetMethod(nonPublic: true);
        if (getter is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_Contamination_GetTransform",
            typeof(Transform),
            new[] { typeof(object) },
            typeof(ContaminationApplierThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (GetTransformDelegate)method.CreateDelegate(typeof(GetTransformDelegate));
    }

    private static GetBoolDelegate? CreateBoolPropertyGetter(Type? instanceType, string propertyName)
    {
        var getter = instanceType?.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true);
        if (instanceType is null || getter is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_Contamination_Get_", propertyName),
            typeof(bool),
            new[] { typeof(object) },
            typeof(ContaminationApplierThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (GetBoolDelegate)method.CreateDelegate(typeof(GetBoolDelegate));
    }

    private static CellIsUnderwaterDelegate? CreateCellIsUnderwater(Type? instanceType)
    {
        var methodInfo = instanceType?.GetMethod("CellIsUnderwater", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3Int) }, null);
        if (instanceType is null || methodInfo is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_Contamination_CellIsUnderwater",
            typeof(bool),
            new[] { typeof(object), typeof(Vector3Int) },
            typeof(ContaminationApplierThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, methodInfo);
        il.Emit(OpCodes.Ret);
        return (CellIsUnderwaterDelegate)method.CreateDelegate(typeof(CellIsUnderwaterDelegate));
    }

    private static ColumnContaminationDelegate? CreateColumnContamination(Type? instanceType)
    {
        var methodInfo = instanceType?.GetMethod("ColumnContamination", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3Int) }, null);
        if (instanceType is null || methodInfo is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_Contamination_ColumnContamination",
            typeof(float),
            new[] { typeof(object), typeof(Vector3Int) },
            typeof(ContaminationApplierThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, methodInfo);
        il.Emit(OpCodes.Ret);
        return (ColumnContaminationDelegate)method.CreateDelegate(typeof(ColumnContaminationDelegate));
    }

    private static CheckProbabilityDelegate? CreateCheckProbability(Type? instanceType)
    {
        var methodInfo = instanceType?.GetMethod("CheckProbability", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(float) }, null);
        if (instanceType is null || methodInfo is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_Contamination_CheckProbability",
            typeof(bool),
            new[] { typeof(object), typeof(float) },
            typeof(ContaminationApplierThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, methodInfo);
        il.Emit(OpCodes.Ret);
        return (CheckProbabilityDelegate)method.CreateDelegate(typeof(CheckProbabilityDelegate));
    }

    private static StartIncubationDelegate? CreateStartIncubation(Type? instanceType)
    {
        var methodInfo = instanceType?.GetMethod("StartIncubation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (instanceType is null || methodInfo is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_Contamination_StartIncubation",
            typeof(void),
            new[] { typeof(object) },
            typeof(ContaminationApplierThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, methodInfo);
        il.Emit(OpCodes.Ret);
        return (StartIncubationDelegate)method.CreateDelegate(typeof(StartIncubationDelegate));
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

    private sealed class TickState
    {
        public int TickCount;
    }
}
