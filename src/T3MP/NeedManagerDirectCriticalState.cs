using System;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Timberborn.NeedSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NeedManagerDirectCriticalState
{
    private static GetNeedsDelegate? _getNeeds;
    private static int _initialized;
    private static int _warningCount;

    private static long _calls;
    private static long _handled;
    private static long _fallbacks;
    private static long _needsChecked;
    private static long _criticalHits;

    private delegate Needs? GetNeedsDelegate(NeedManager needManager);

    public static bool TryAnyNeedIsInCriticalState(object instance, ref bool result)
    {
        if (!BenchmarkSettings.EnableNeedManagerDirectCriticalState ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        Interlocked.Increment(ref _calls);
        if (instance is not NeedManager needManager || !EnsureInitialized())
        {
            Interlocked.Increment(ref _fallbacks);
            return true;
        }

        try
        {
            var needs = _getNeeds?.Invoke(needManager);
            if (needs is null)
            {
                Interlocked.Increment(ref _fallbacks);
                return true;
            }

            var allNeeds = needs.AllNeeds;
            var localChecks = 0L;
            for (var index = 0; index < allNeeds.Length; index++)
            {
                localChecks++;
                if (allNeeds[index].IsInCriticalState)
                {
                    result = true;
                    Interlocked.Add(ref _needsChecked, localChecks);
                    Interlocked.Increment(ref _criticalHits);
                    Interlocked.Increment(ref _handled);
                    return false;
                }
            }

            result = false;
            Interlocked.Add(ref _needsChecked, localChecks);
            Interlocked.Increment(ref _handled);
            return false;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] NeedManager direct critical-state fallback: " + exception);
            }

            return true;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var calls = Interlocked.Exchange(ref _calls, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var needsChecked = Interlocked.Exchange(ref _needsChecked, 0);
        var criticalHits = Interlocked.Exchange(ref _criticalHits, 0);
        if (calls == 0 && handled == 0 && fallbacks == 0)
        {
            return;
        }

        var handledRate = calls > 0 ? (double)handled / calls : 0.0;
        var avgChecks = handled > 0 ? (double)needsChecked / handled : 0.0;
        Debug.Log(
            $"[T3MP] NeedManagerDirectCriticalState aggregate={aggregateId}, enabled={BenchmarkSettings.EnableNeedManagerDirectCriticalState}, calls={calls}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, needsChecked={needsChecked}, avgChecks={avgChecks:F2}, criticalHits={criticalHits}");
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _needsChecked, 0);
        Interlocked.Exchange(ref _criticalHits, 0);
    }

    private static bool EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return _getNeeds is not null;
        }

        var field = typeof(NeedManager).GetField("_needs", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            Volatile.Write(ref _initialized, 1);
            return false;
        }

        try
        {
            var method = new DynamicMethod(
                "T3MP_NeedManager_GetNeeds",
                typeof(Needs),
                new[] { typeof(NeedManager) },
                typeof(NeedManagerDirectCriticalState).Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            _getNeeds = (GetNeedsDelegate)method.CreateDelegate(typeof(GetNeedsDelegate));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to create NeedManager._needs getter: " + exception.Message);
        }

        Volatile.Write(ref _initialized, 1);
        return _getNeeds is not null;
    }
}
