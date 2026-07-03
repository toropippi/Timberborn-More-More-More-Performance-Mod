using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WaterObjectServiceThrottle
{
    private static readonly ConditionalWeakTable<object, State> States = new ConditionalWeakTable<object, State>();
    private static long _attempts;
    private static long _ranOriginal;
    private static long _skipped;
    private static long _fallbacks;
    private static int _warningLogged;

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableWaterObjectServiceThrottle ||
            BenchmarkSettings.WaterObjectServiceThrottleTicks <= 1 ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        Interlocked.Increment(ref _attempts);
        try
        {
            var state = States.GetOrCreateValue(instance);
            state.TickCount++;
            if (state.TickCount % BenchmarkSettings.WaterObjectServiceThrottleTicks == 0)
            {
                Interlocked.Increment(ref _ranOriginal);
                return true;
            }

            Interlocked.Increment(ref _skipped);
            return false;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Exchange(ref _warningLogged, 1) == 0)
            {
                Debug.LogWarning($"[T3MP] WaterObjectService throttle failed once; falling back. {exception.GetType().Name}: {exception.Message}");
            }

            return true;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var ranOriginal = Interlocked.Exchange(ref _ranOriginal, 0);
        var skipped = Interlocked.Exchange(ref _skipped, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        if (attempts == 0)
        {
            return;
        }

        var skipRate = attempts > 0 ? (double)skipped / attempts : 0.0;
        Debug.Log(
            $"[T3MP] WaterObjectServiceThrottle aggregate={aggregateId}, enabled={BenchmarkSettings.EnableWaterObjectServiceThrottle}, interval={BenchmarkSettings.WaterObjectServiceThrottleTicks}, attempts={attempts}, ranOriginal={ranOriginal}, skipped={skipped}, skipRate={skipRate:F3}, fallbacks={fallbacks}");
    }

    private sealed class State
    {
        public int TickCount;
    }
}
