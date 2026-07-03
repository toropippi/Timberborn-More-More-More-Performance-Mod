using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace T3MP;

internal static class StatusAggregatorThrottle
{
    private static long _attempts;
    private static long _ranOriginal;
    private static long _skipped;

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableStatusAggregatorThrottle ||
            BenchmarkSettings.StatusAggregatorThrottleFrames <= 1 ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        Interlocked.Increment(ref _attempts);
        var hash = RuntimeHelpers.GetHashCode(instance) & int.MaxValue;
        if ((Time.frameCount + hash) % BenchmarkSettings.StatusAggregatorThrottleFrames == 0)
        {
            Interlocked.Increment(ref _ranOriginal);
            return true;
        }

        Interlocked.Increment(ref _skipped);
        return false;
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var ranOriginal = Interlocked.Exchange(ref _ranOriginal, 0);
        var skipped = Interlocked.Exchange(ref _skipped, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] StatusAggregatorThrottle aggregate={0}, enabled={1}, interval={2}, attempts={3}, ranOriginal={4}, skipped={5}, skipRate={6:F3}",
            aggregateId,
            BenchmarkSettings.EnableStatusAggregatorThrottle,
            BenchmarkSettings.StatusAggregatorThrottleFrames,
            attempts,
            ranOriginal,
            skipped,
            attempts > 0 ? skipped / (double)attempts : 0.0));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _ranOriginal, 0);
        Interlocked.Exchange(ref _skipped, 0);
    }
}
