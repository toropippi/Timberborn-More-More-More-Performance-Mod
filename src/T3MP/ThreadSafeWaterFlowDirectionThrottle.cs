using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class ThreadSafeWaterFlowDirectionThrottle
{
    private static GetBoolDelegate? _getAnyColumnChanged;
    private static int _initialized;
    private static int _callsSinceOriginal;
    private static int _warningCount;

    private delegate bool GetBoolDelegate(object instance);

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableThreadSafeWaterFlowDirectionThrottle ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            !BenchmarkModeController.RenderBlackoutActive)
        {
            return true;
        }

        try
        {
            if (!EnsureInitialized(instance.GetType()))
            {
                return true;
            }

            if (_getAnyColumnChanged?.Invoke(instance) == true)
            {
                Volatile.Write(ref _callsSinceOriginal, 0);
                return true;
            }

            var calls = Interlocked.Increment(ref _callsSinceOriginal);
            if (calls >= BenchmarkSettings.ThreadSafeWaterFlowDirectionIntervalTicks)
            {
                Volatile.Write(ref _callsSinceOriginal, 0);
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] ThreadSafeWaterFlowDirectionThrottle fallback: " + exception.Message);
            }

            return true;
        }
    }

    private static bool EnsureInitialized(Type threadSafeWaterMapType)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return _getAnyColumnChanged is not null;
        }

        _getAnyColumnChanged = CreateBoolGetter(threadSafeWaterMapType, "AnyColumnChanged");
        Volatile.Write(ref _initialized, 1);
        return _getAnyColumnChanged is not null;
    }

    private static GetBoolDelegate? CreateBoolGetter(Type type, string propertyName)
    {
        var getter = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetGetMethod(nonPublic: true);
        if (getter is null)
        {
            return null;
        }

        var method = new DynamicMethod(
            "T3MP_ThreadSafeWaterFlow_Get_" + propertyName,
            typeof(bool),
            new[] { typeof(object) },
            typeof(ThreadSafeWaterFlowDirectionThrottle).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, type);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return (GetBoolDelegate)method.CreateDelegate(typeof(GetBoolDelegate));
    }
}
