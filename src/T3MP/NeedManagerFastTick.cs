using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Timberborn.NeedSpecs;
using Timberborn.NeedSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NeedManagerFastTick
{
    private static GetNeedsDelegate? _getNeeds;
    private static GetObjectDelegate? _getCharacter;
    private static GetNeedArrayDelegate? _getNeedArray;
    private static GetBoolObjectDelegate? _getCharacterAlive;
    private static GetFloatNeedDelegate? _getPoints;
    private static SetFloatNeedDelegate? _setPoints;
    private static GetBoolNeedDelegate? _getEnabled;
    private static GetBoolNeedDelegate? _getUpdateEnabled;
    private static GetBoolNeedDelegate? _getAppliedEffect;
    private static SetBoolNeedDelegate? _setAppliedEffect;
    private static GetFloatNeedDelegate? _getDeltaPoints;
    private static GetBoolNeedDelegate? _getIsNeverPositive;
    private static GetEventCriticalDelegate? _getNeedChangedCriticalState;
    private static GetEventMinimumDelegate? _getNeedChangedIsAtMinimumState;
    private static GetEventFavorableDelegate? _getNeedChangedIsFavorable;
    private static GetEventActiveDelegate? _getNeedChangedActiveState;
    private static int _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _needsVisited;
    private static long _needsChanged;
    private static long _eventsRaised;
    private static long _deadSkipped;
    private static long _stopwatchTicks;
    private static long _maxStopwatchTicks;

    private delegate Needs? GetNeedsDelegate(NeedManager manager);

    private delegate object? GetObjectDelegate(NeedManager manager);

    private delegate Need[]? GetNeedArrayDelegate(Needs needs);

    private delegate bool GetBoolObjectDelegate(object instance);

    private delegate float GetFloatNeedDelegate(Need need);

    private delegate void SetFloatNeedDelegate(Need need, float value);

    private delegate bool GetBoolNeedDelegate(Need need);

    private delegate void SetBoolNeedDelegate(Need need, bool value);

    private delegate EventHandler<NeedChangedCriticalStateEventArgs>? GetEventCriticalDelegate(NeedManager manager);

    private delegate EventHandler<NeedChangedIsAtMinimumStateEventArgs>? GetEventMinimumDelegate(NeedManager manager);

    private delegate EventHandler<NeedChangedIsFavorableEventArgs>? GetEventFavorableDelegate(NeedManager manager);

    private delegate EventHandler<NeedChangedActiveStateEventArgs>? GetEventActiveDelegate(NeedManager manager);

    public static bool TryTick(NeedManager manager)
    {
        if (!BenchmarkSettings.EnableNeedManagerFastTick ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        var started = recordMetrics ? Stopwatch.GetTimestamp() : 0L;
        try
        {
            if (!EnsureInitialized(manager.GetType()))
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            var character = _getCharacter?.Invoke(manager);
            if (character is null || _getCharacterAlive?.Invoke(character) != true)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _deadSkipped);
                    Interlocked.Increment(ref _handled);
                }
                return true;
            }

            var needs = _getNeeds?.Invoke(manager);
            var needArray = needs is null ? null : _getNeedArray?.Invoke(needs);
            if (needArray is null)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            var changed = 0;
            var events = 0;
            for (var index = 0; index < needArray.Length; index++)
            {
                var need = needArray[index];
                if (need is null)
                {
                    continue;
                }

                if (recordMetrics)
                {
                    Interlocked.Increment(ref _needsVisited);
                }
                if (UpdateNeed(manager, need, ref events))
                {
                    changed++;
                }
            }

            if (recordMetrics && changed > 0)
            {
                Interlocked.Add(ref _needsChanged, changed);
            }
            if (recordMetrics && events > 0)
            {
                Interlocked.Add(ref _eventsRaised, events);
            }

            if (recordMetrics)
            {
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
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] NeedManagerFastTick fallback: " + exception);
            }

            return false;
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
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var needsVisited = Interlocked.Exchange(ref _needsVisited, 0);
        var needsChanged = Interlocked.Exchange(ref _needsChanged, 0);
        var eventsRaised = Interlocked.Exchange(ref _eventsRaised, 0);
        var deadSkipped = Interlocked.Exchange(ref _deadSkipped, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        var maxTicks = Interlocked.Exchange(ref _maxStopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] NeedManagerFastTick aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, deadSkipped={6}, needsVisited={7}, needsChanged={8}, eventsRaised={9}, ms={10:F3}, avgUs={11:F3}, maxMs={12:F3}",
            aggregateId,
            BenchmarkSettings.EnableNeedManagerFastTick,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            fallbacks,
            deadSkipped,
            needsVisited,
            needsChanged,
            eventsRaised,
            ToMilliseconds(ticks),
            attempts > 0 ? ToMilliseconds(ticks) * 1000.0 / attempts : 0.0,
            ToMilliseconds(maxTicks)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _needsVisited, 0);
        Interlocked.Exchange(ref _needsChanged, 0);
        Interlocked.Exchange(ref _eventsRaised, 0);
        Interlocked.Exchange(ref _deadSkipped, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
        Interlocked.Exchange(ref _maxStopwatchTicks, 0);
    }

    private static bool UpdateNeed(NeedManager manager, Need need, ref int events)
    {
        var oldPoints = _getPoints?.Invoke(need) ?? need.Points;
        var spec = need.NeedSpec;
        var enabled = _getEnabled?.Invoke(need) ?? need.Enabled;
        var isNeverPositive = _getIsNeverPositive?.Invoke(need) ?? spec.IsNeverPositive;
        var isCritical = need.IsCritical;

        var wasAtMinimum = oldPoints <= spec.MinimumValue;
        var wasFavorable = IsFavorable(oldPoints, isNeverPositive);
        var wasInCriticalState = isCritical && !wasFavorable;
        var wasActive = enabled && oldPoints != 0f;

        var newPoints = oldPoints;
        var shouldBeUpdated = enabled && (_getUpdateEnabled?.Invoke(need) ?? true);
        if (shouldBeUpdated)
        {
            if (!(_getAppliedEffect?.Invoke(need) ?? false))
            {
                newPoints = Clamp(oldPoints + (_getDeltaPoints?.Invoke(need) ?? 0f), spec.MinimumValue, spec.MaximumValue);
                if (newPoints != oldPoints)
                {
                    _setPoints?.Invoke(need, newPoints);
                }
            }

            _setAppliedEffect?.Invoke(need, false);
        }

        var isAtMinimum = newPoints <= spec.MinimumValue;
        if (wasAtMinimum != isAtMinimum)
        {
            _getNeedChangedIsAtMinimumState?.Invoke(manager)?.Invoke(manager, new NeedChangedIsAtMinimumStateEventArgs(spec, isAtMinimum));
            events++;
        }

        var isFavorable = IsFavorable(newPoints, isNeverPositive);
        if (wasFavorable != isFavorable)
        {
            _getNeedChangedIsFavorable?.Invoke(manager)?.Invoke(manager, new NeedChangedIsFavorableEventArgs(spec));
            events++;
        }

        if (isCritical)
        {
            var isInCriticalState = !isFavorable;
            if (wasInCriticalState != isInCriticalState)
            {
                _getNeedChangedCriticalState?.Invoke(manager)?.Invoke(manager, new NeedChangedCriticalStateEventArgs(spec, isInCriticalState));
                events++;
            }
        }

        var isActive = enabled && newPoints != 0f;
        if (wasActive != isActive)
        {
            _getNeedChangedActiveState?.Invoke(manager)?.Invoke(manager, new NeedChangedActiveStateEventArgs(spec, isActive));
            events++;
        }

        return newPoints != oldPoints;
    }

    private static bool EnsureInitialized(Type managerType)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return AccessorsReady();
        }

        try
        {
            _getNeeds = CreateNeedManagerFieldGetter<GetNeedsDelegate>(managerType, "_needs", typeof(Needs));
            _getCharacter = CreateNeedManagerFieldGetter<GetObjectDelegate>(managerType, "_character", typeof(object));
            _getNeedChangedCriticalState = CreateNeedManagerFieldGetter<GetEventCriticalDelegate>(managerType, "NeedChangedCriticalState", typeof(EventHandler<NeedChangedCriticalStateEventArgs>));
            _getNeedChangedIsAtMinimumState = CreateNeedManagerFieldGetter<GetEventMinimumDelegate>(managerType, "NeedChangedIsAtMinimumState", typeof(EventHandler<NeedChangedIsAtMinimumStateEventArgs>));
            _getNeedChangedIsFavorable = CreateNeedManagerFieldGetter<GetEventFavorableDelegate>(managerType, "NeedChangedIsFavorable", typeof(EventHandler<NeedChangedIsFavorableEventArgs>));
            _getNeedChangedActiveState = CreateNeedManagerFieldGetter<GetEventActiveDelegate>(managerType, "NeedChangedActiveState", typeof(EventHandler<NeedChangedActiveStateEventArgs>));

            var characterType = managerType.GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            _getCharacterAlive = CreateObjectBoolPropertyGetter(characterType, "Alive");
            _getNeedArray = CreateNeedsArrayGetter();
            _getPoints = CreateNeedFieldGetter<GetFloatNeedDelegate>("<Points>k__BackingField", typeof(float));
            _setPoints = CreateNeedFieldSetter<SetFloatNeedDelegate>("<Points>k__BackingField", typeof(float));
            _getEnabled = CreateNeedFieldGetter<GetBoolNeedDelegate>("<Enabled>k__BackingField", typeof(bool));
            _getUpdateEnabled = CreateNeedFieldGetter<GetBoolNeedDelegate>("_updateEnabled", typeof(bool));
            _getAppliedEffect = CreateNeedFieldGetter<GetBoolNeedDelegate>("_appliedEffectSinceLastUpdate", typeof(bool));
            _setAppliedEffect = CreateNeedFieldSetter<SetBoolNeedDelegate>("_appliedEffectSinceLastUpdate", typeof(bool));
            _getDeltaPoints = CreateNeedFieldGetter<GetFloatNeedDelegate>("_deltaPointsPerUpdate", typeof(float));
            _getIsNeverPositive = CreateNeedFieldGetter<GetBoolNeedDelegate>("_isNeverPositive", typeof(bool));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to initialize NeedManagerFastTick: " + exception);
        }

        Volatile.Write(ref _initialized, 1);
        return AccessorsReady();
    }

    private static bool AccessorsReady()
    {
        return _getNeeds is not null &&
            _getCharacter is not null &&
            _getNeedArray is not null &&
            _getCharacterAlive is not null &&
            _getPoints is not null &&
            _setPoints is not null &&
            _getEnabled is not null &&
            _getUpdateEnabled is not null &&
            _getAppliedEffect is not null &&
            _setAppliedEffect is not null &&
            _getDeltaPoints is not null &&
            _getIsNeverPositive is not null &&
            _getNeedChangedCriticalState is not null &&
            _getNeedChangedIsAtMinimumState is not null &&
            _getNeedChangedIsFavorable is not null &&
            _getNeedChangedActiveState is not null;
    }

    private static TDelegate? CreateNeedManagerFieldGetter<TDelegate>(Type managerType, string fieldName, Type returnType)
        where TDelegate : Delegate
    {
        var field = managerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_NeedManagerFast_Get_", fieldName),
            returnType,
            new[] { typeof(NeedManager) },
            typeof(NeedManagerFastTick).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, managerType);
        il.Emit(OpCodes.Ldfld, field);
        if (returnType == typeof(object) && field.FieldType.IsValueType)
        {
            il.Emit(OpCodes.Box, field.FieldType);
        }
        else if (returnType != field.FieldType && !returnType.IsAssignableFrom(field.FieldType))
        {
            il.Emit(OpCodes.Castclass, returnType);
        }

        il.Emit(OpCodes.Ret);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }

    private static GetBoolObjectDelegate? CreateObjectBoolPropertyGetter(Type? instanceType, string propertyName)
    {
        var getter = instanceType?.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true);
        if (instanceType is null || getter is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_NeedManagerFast_Get_", propertyName),
            typeof(bool),
            new[] { typeof(object) },
            typeof(NeedManagerFastTick).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, instanceType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (GetBoolObjectDelegate)method.CreateDelegate(typeof(GetBoolObjectDelegate));
    }

    private static GetNeedArrayDelegate? CreateNeedsArrayGetter()
    {
        var field = typeof(Needs).GetField("_needArray", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_NeedManagerFast_GetNeedArray",
            typeof(Need[]),
            new[] { typeof(Needs) },
            typeof(NeedManagerFastTick).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (GetNeedArrayDelegate)method.CreateDelegate(typeof(GetNeedArrayDelegate));
    }

    private static TDelegate? CreateNeedFieldGetter<TDelegate>(string fieldName, Type returnType)
        where TDelegate : Delegate
    {
        var field = typeof(Need).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_NeedManagerFast_Get_", fieldName),
            returnType,
            new[] { typeof(Need) },
            typeof(NeedManagerFastTick).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }

    private static TDelegate? CreateNeedFieldSetter<TDelegate>(string fieldName, Type valueType)
        where TDelegate : Delegate
    {
        var field = typeof(Need).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_NeedManagerFast_Set_", fieldName),
            typeof(void),
            new[] { typeof(Need), valueType },
            typeof(NeedManagerFastTick).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }

    private static bool IsFavorable(float points, bool isNeverPositive)
    {
        return !isNeverPositive ? points > 0f : points == 0f;
    }

    private static float Clamp(float value, float minimum, float maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        return value > maximum ? maximum : value;
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
