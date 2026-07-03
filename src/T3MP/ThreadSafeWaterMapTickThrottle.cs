using System;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace T3MP;

internal static class ThreadSafeWaterMapTickThrottle
{
    private static int _tickCount;
    private static long _attempts;
    private static long _ranOriginal;
    private static long _skipped;

    public static bool ShouldRunOriginal()
    {
        if (!BenchmarkSettings.EnableThreadSafeWaterMapTickThrottle ||
            BenchmarkSettings.ThreadSafeWaterMapTickThrottleTicks <= 1 ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            !BenchmarkModeController.RenderBlackoutActive)
        {
            return true;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }

        var interval = Math.Max(1, BenchmarkSettings.ThreadSafeWaterMapTickThrottleTicks);
        var count = Interlocked.Increment(ref _tickCount);
        var shouldRun = (count - 1) % interval == 0;
        if (recordMetrics)
        {
            if (shouldRun)
            {
                Interlocked.Increment(ref _ranOriginal);
            }
            else
            {
                Interlocked.Increment(ref _skipped);
            }
        }

        return shouldRun;
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var ranOriginal = Interlocked.Exchange(ref _ranOriginal, 0);
        var skipped = Interlocked.Exchange(ref _skipped, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] ThreadSafeWaterMapTickThrottle aggregate={0}, enabled={1}, interval={2}, attempts={3}, ranOriginal={4}, skipped={5}, skipRate={6:F3}",
            aggregateId,
            BenchmarkSettings.EnableThreadSafeWaterMapTickThrottle,
            BenchmarkSettings.ThreadSafeWaterMapTickThrottleTicks,
            attempts,
            ranOriginal,
            skipped,
            attempts > 0 ? (double)skipped / attempts : 0.0));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _ranOriginal, 0);
        Interlocked.Exchange(ref _skipped, 0);
    }
}
