using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Effects;
using Timberborn.NeedSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class RangedEffectSubjectThrottle
{
    private static readonly ConditionalWeakTable<object, TickState> States = new ConditionalWeakTable<object, TickState>();

    private static GetObjectDelegate? _getAffectingEffects;
    private static GetNeedManagerDelegate? _getNeedManager;
    private static GetObjectDelegate? _getDayNightCycle;
    private static GetFixedDeltaDelegate? _getFixedDeltaTimeInHours;
    private static ToContinuousEffectDelegate? _toContinuousEffect;
    private static int _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _skipped;
    private static long _ranScaled;
    private static long _fallbacks;
    private static long _effectsApplied;
    private static long _stopwatchTicks;
    private static long _maxStopwatchTicks;

    private delegate object? GetObjectDelegate(object instance);

    private delegate NeedManager? GetNeedManagerDelegate(object instance);

    private delegate float GetFixedDeltaDelegate(object instance);

    private delegate ContinuousEffect ToContinuousEffectDelegate(object instance);

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableRangedEffectSubjectThrottle ||
            BenchmarkSettings.RangedEffectSubjectThrottleTicks <= 1 ||
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
        if (state.TickCount % BenchmarkSettings.RangedEffectSubjectThrottleTicks != 0)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _skipped);
            }
            return false;
        }

        if (!EnsureInitialized(instance))
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            return true;
        }

        var started = recordMetrics ? Stopwatch.GetTimestamp() : 0L;
        try
        {
            var applied = ApplyScaledEffects(instance);
            if (recordMetrics)
            {
                Interlocked.Increment(ref _handled);
                Interlocked.Increment(ref _ranScaled);
                Interlocked.Add(ref _effectsApplied, applied);
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
                Debug.LogWarning("[T3MP] RangedEffectSubjectThrottle fallback: " + exception);
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
        var ranScaled = Interlocked.Exchange(ref _ranScaled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var effectsApplied = Interlocked.Exchange(ref _effectsApplied, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        var maxTicks = Interlocked.Exchange(ref _maxStopwatchTicks, 0);
        if (attempts == 0 && handled == 0 && skipped == 0 && fallbacks == 0)
        {
            return;
        }

        var skipRate = attempts > 0 ? (double)skipped / attempts : 0.0;
        var avgEffects = ranScaled > 0 ? (double)effectsApplied / ranScaled : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RangedEffectSubjectThrottle aggregate={0}, enabled={1}, interval={2}, attempts={3}, handled={4}, skipped={5}, skipRate={6:F3}, ranScaled={7}, fallbacks={8}, effectsApplied={9}, avgEffectsPerRun={10:F2}, scaledMs={11:F3}, scaledMaxMs={12:F3}",
            aggregateId,
            BenchmarkSettings.EnableRangedEffectSubjectThrottle,
            BenchmarkSettings.RangedEffectSubjectThrottleTicks,
            attempts,
            handled,
            skipped,
            skipRate,
            ranScaled,
            fallbacks,
            effectsApplied,
            avgEffects,
            ToMilliseconds(ticks),
            ToMilliseconds(maxTicks)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _skipped, 0);
        Interlocked.Exchange(ref _ranScaled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _effectsApplied, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
        Interlocked.Exchange(ref _maxStopwatchTicks, 0);
    }

    private static int ApplyScaledEffects(object instance)
    {
        var effectsObject = _getAffectingEffects?.Invoke(instance);
        var needManager = _getNeedManager?.Invoke(instance);
        var dayNightCycle = _getDayNightCycle?.Invoke(instance);
        if (effectsObject is not IEnumerable effects ||
            needManager is null ||
            dayNightCycle is null ||
            _getFixedDeltaTimeInHours is null ||
            _toContinuousEffect is null)
        {
            throw new InvalidOperationException("Ranged effect throttle accessors are not initialized.");
        }

        var delta = _getFixedDeltaTimeInHours(dayNightCycle) * BenchmarkSettings.RangedEffectSubjectThrottleTicks;
        var applied = 0;
        foreach (var effectObject in effects)
        {
            if (effectObject is null)
            {
                continue;
            }

            var continuousEffect = _toContinuousEffect(effectObject);
            needManager.ApplyEffect(continuousEffect, delta);
            applied++;
        }

        return applied;
    }

    private static bool EnsureInitialized(object instance)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return _getAffectingEffects is not null &&
                _getNeedManager is not null &&
                _getDayNightCycle is not null &&
                _getFixedDeltaTimeInHours is not null &&
                _toContinuousEffect is not null;
        }

        try
        {
            var subjectType = instance.GetType();
            var getEffectsMethod = subjectType.GetMethod("GetAffectingEffects", BindingFlags.Instance | BindingFlags.NonPublic);
            var needManagerField = subjectType.GetField("_needManager", BindingFlags.Instance | BindingFlags.NonPublic);
            var dayNightCycleField = subjectType.GetField("_dayNightCycle", BindingFlags.Instance | BindingFlags.NonPublic);
            var effectType = getEffectsMethod?.ReturnType.GetGenericArguments().FirstOrDefault();
            var toContinuousEffectMethod = effectType?.GetMethod("ToContinuousEffect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fixedDeltaGetter = dayNightCycleField?.FieldType.GetProperty("FixedDeltaTimeInHours", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod();

            if (getEffectsMethod is null ||
                needManagerField is null ||
                dayNightCycleField is null ||
                effectType is null ||
                toContinuousEffectMethod is null ||
                fixedDeltaGetter is null)
            {
                Volatile.Write(ref _initialized, 1);
                return false;
            }

            _getAffectingEffects = CreateObjectMethodGetter(subjectType, getEffectsMethod, "T3MP_RangedEffect_GetAffectingEffects");
            _getNeedManager = CreateNeedManagerFieldGetter(subjectType, needManagerField);
            _getDayNightCycle = CreateObjectFieldGetter(subjectType, dayNightCycleField, "T3MP_RangedEffect_GetDayNightCycle");
            _getFixedDeltaTimeInHours = CreateFixedDeltaGetter(dayNightCycleField.FieldType, fixedDeltaGetter);
            _toContinuousEffect = CreateToContinuousEffect(effectType, toContinuousEffectMethod);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to initialize RangedEffectSubjectThrottle: " + exception);
        }

        Volatile.Write(ref _initialized, 1);
        return _getAffectingEffects is not null &&
            _getNeedManager is not null &&
            _getDayNightCycle is not null &&
            _getFixedDeltaTimeInHours is not null &&
            _toContinuousEffect is not null;
    }

    private static GetObjectDelegate CreateObjectMethodGetter(Type instanceType, MethodInfo method, string name)
    {
        var dynamicMethod = new DynamicMethod(name, typeof(object), new[] { typeof(object) }, typeof(RangedEffectSubjectThrottle).Module, true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Call, method);
        if (method.ReturnType.IsValueType)
        {
            il.Emit(OpCodes.Box, method.ReturnType);
        }

        il.Emit(OpCodes.Ret);
        return (GetObjectDelegate)dynamicMethod.CreateDelegate(typeof(GetObjectDelegate));
    }

    private static GetObjectDelegate CreateObjectFieldGetter(Type instanceType, FieldInfo field, string name)
    {
        var dynamicMethod = new DynamicMethod(name, typeof(object), new[] { typeof(object) }, typeof(RangedEffectSubjectThrottle).Module, true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldfld, field);
        if (field.FieldType.IsValueType)
        {
            il.Emit(OpCodes.Box, field.FieldType);
        }

        il.Emit(OpCodes.Ret);
        return (GetObjectDelegate)dynamicMethod.CreateDelegate(typeof(GetObjectDelegate));
    }

    private static GetNeedManagerDelegate CreateNeedManagerFieldGetter(Type instanceType, FieldInfo field)
    {
        var dynamicMethod = new DynamicMethod("T3MP_RangedEffect_GetNeedManager", typeof(NeedManager), new[] { typeof(object) }, typeof(RangedEffectSubjectThrottle).Module, true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (GetNeedManagerDelegate)dynamicMethod.CreateDelegate(typeof(GetNeedManagerDelegate));
    }

    private static GetFixedDeltaDelegate CreateFixedDeltaGetter(Type instanceType, MethodInfo getter)
    {
        var dynamicMethod = new DynamicMethod("T3MP_RangedEffect_GetFixedDelta", typeof(float), new[] { typeof(object) }, typeof(RangedEffectSubjectThrottle).Module, true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (GetFixedDeltaDelegate)dynamicMethod.CreateDelegate(typeof(GetFixedDeltaDelegate));
    }

    private static ToContinuousEffectDelegate CreateToContinuousEffect(Type effectType, MethodInfo method)
    {
        var dynamicMethod = new DynamicMethod("T3MP_RangedEffect_ToContinuousEffect", typeof(ContinuousEffect), new[] { typeof(object) }, typeof(RangedEffectSubjectThrottle).Module, true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, effectType);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return (ToContinuousEffectDelegate)dynamicMethod.CreateDelegate(typeof(ToContinuousEffectDelegate));
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
