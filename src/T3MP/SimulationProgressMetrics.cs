using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class SimulationProgressMetrics
{
    private static readonly ProgressCounters[] Counters =
    {
        new ProgressCounters(),
        new ProgressCounters()
    };

    private static int _totalNumberOfBuckets = 129;

    public static void RecordTickBuckets(object tickableBucketService, int numberOfBucketsToTick)
    {
        if (numberOfBucketsToTick <= 0 ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            return;
        }

        TryUpdateTotalNumberOfBuckets(tickableBucketService);
        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.BucketCalls);
        Interlocked.Add(ref counters.BucketCount, numberOfBucketsToTick);
    }

    public static void RecordFullTick()
    {
        if (!BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            return;
        }

        Interlocked.Increment(ref Counters[(int)mode].FullTicks);
    }

    public static void RecordTickerUpdate(object ticker)
    {
        if (!BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            return;
        }

        var counters = Counters[(int)mode];
        Interlocked.Increment(ref counters.TickerUpdates);
        var milliseconds = TryGetLengthOfLastTickMilliseconds(ticker);
        if (milliseconds > 0.0 && milliseconds < 5000.0)
        {
            Interlocked.Add(ref counters.TickerWorkMicroseconds, (long)(milliseconds * 1000.0));
        }
    }

    public static void LogAndResetMode(long aggregateId, BenchmarkMode mode, double sampleSeconds)
    {
        var counters = Counters[(int)mode];
        var fullTicks = Interlocked.Exchange(ref counters.FullTicks, 0);
        var bucketCalls = Interlocked.Exchange(ref counters.BucketCalls, 0);
        var bucketCount = Interlocked.Exchange(ref counters.BucketCount, 0);
        var tickerUpdates = Interlocked.Exchange(ref counters.TickerUpdates, 0);
        var tickerWorkMicroseconds = Interlocked.Exchange(ref counters.TickerWorkMicroseconds, 0);

        if (sampleSeconds <= 0.001)
        {
            return;
        }

        var totalBuckets = Math.Max(1, Volatile.Read(ref _totalNumberOfBuckets));
        var fullTicksPerSecond = fullTicks / sampleSeconds;
        var bucketCallsPerSecond = bucketCalls / sampleSeconds;
        var bucketRoundsPerSecond = bucketCount / (double)totalBuckets / sampleSeconds;
        var tickerUpdatesPerSecond = tickerUpdates / sampleSeconds;
        var averageTickerWorkMilliseconds = tickerUpdates > 0
            ? tickerWorkMicroseconds / 1000.0 / tickerUpdates
            : 0.0;

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] SimProgress mode={0}, aggregate={1}, sampleSeconds={2:F2}, fullTicks={3}, fullTicksPerSecond={4:F2}, bucketCalls={5}, bucketCallsPerSecond={6:F2}, bucketRoundsPerSecond={7:F2}, tickerUpdates={8}, tickerUpdatesPerSecond={9:F2}, avgTickerWorkMs={10:F3}, totalBuckets={11}",
            mode,
            aggregateId,
            sampleSeconds,
            fullTicks,
            fullTicksPerSecond,
            bucketCalls,
            bucketCallsPerSecond,
            bucketRoundsPerSecond,
            tickerUpdates,
            tickerUpdatesPerSecond,
            averageTickerWorkMilliseconds,
            totalBuckets));
    }

    private static void TryUpdateTotalNumberOfBuckets(object tickableBucketService)
    {
        try
        {
            var property = tickableBucketService.GetType().GetProperty("TotalNumberOfBuckets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(tickableBucketService) is int totalNumberOfBuckets && totalNumberOfBuckets > 0)
            {
                Volatile.Write(ref _totalNumberOfBuckets, totalNumberOfBuckets);
            }
        }
        catch (Exception)
        {
            // Keep the known Timberborn 1.0 fallback of 129 buckets.
        }
    }

    private static double TryGetLengthOfLastTickMilliseconds(object ticker)
    {
        try
        {
            var property = ticker.GetType().GetProperty("LengthOfLastTickInSeconds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(ticker) is double seconds)
            {
                return seconds * 1000.0;
            }
        }
        catch (Exception)
        {
            return 0.0;
        }

        return 0.0;
    }

    private sealed class ProgressCounters
    {
        public long FullTicks;
        public long BucketCalls;
        public long BucketCount;
        public long TickerUpdates;
        public long TickerWorkMicroseconds;
    }
}
