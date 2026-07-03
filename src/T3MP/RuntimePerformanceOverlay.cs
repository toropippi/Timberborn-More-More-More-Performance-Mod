using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace T3MP;

internal static class RuntimePerformanceOverlay
{
    private static readonly object LockObject = new object();
    private static readonly Queue<FrameSample> FrameSamples = new Queue<FrameSample>();
    private static readonly Queue<BucketSample> BucketSamples = new Queue<BucketSample>();
    private static readonly Queue<float> FullTickSamples = new Queue<float>();
    private static readonly Queue<TickWorkSample> TickWorkSamples = new Queue<TickWorkSample>();
    private static readonly FrameTiming[] FrameTimings = new FrameTiming[1];

    private static double _frameMillisecondsSum;
    private static long _bucketCountSum;
    private static double _tickWorkMillisecondsSum;
    private static double _lastFrameMilliseconds;
    private static bool _hasFrameTiming;
    private static double _cpuMainThreadMilliseconds;
    private static double _cpuRenderThreadMilliseconds;
    private static double _cpuWaitMilliseconds;
    private static double _cpuTotalMilliseconds;
    private static double _gpuMilliseconds;
    private static bool _hasTimberbornFps;
    private static double _timberbornFramesPerSecond;
    private static double _timberbornAverageFramesPerSecond;
    private static double _timberbornMinFramesPerSecond;
    private static Type? _timberbornFpsCounterType;
    private static PropertyInfo? _timberbornFramesPerSecondProperty;
    private static PropertyInfo? _timberbornAverageFramesPerSecondProperty;
    private static PropertyInfo? _timberbornMinFramesPerSecondProperty;
    private static int _totalNumberOfBuckets = 129;

    public static void RecordFrame(float timestamp, float deltaSeconds)
    {
        if (deltaSeconds <= 0f || deltaSeconds > 5f)
        {
            return;
        }

        var milliseconds = deltaSeconds * 1000f;
        CaptureFrameTiming();
        lock (LockObject)
        {
            _lastFrameMilliseconds = milliseconds;
            FrameSamples.Enqueue(new FrameSample(timestamp, milliseconds));
            _frameMillisecondsSum += milliseconds;
            TrimFrameSamples(timestamp);
        }
    }

    public static void RecordTimberbornFpsCounter(object counter)
    {
        try
        {
            EnsureTimberbornFpsProperties(counter.GetType());
            if (_timberbornFramesPerSecondProperty is null ||
                _timberbornAverageFramesPerSecondProperty is null ||
                _timberbornMinFramesPerSecondProperty is null)
            {
                return;
            }

            var framesPerSecond = Convert.ToDouble(_timberbornFramesPerSecondProperty.GetValue(counter), CultureInfo.InvariantCulture);
            var averageFramesPerSecond = Convert.ToDouble(_timberbornAverageFramesPerSecondProperty.GetValue(counter), CultureInfo.InvariantCulture);
            var minFramesPerSecond = Convert.ToDouble(_timberbornMinFramesPerSecondProperty.GetValue(counter), CultureInfo.InvariantCulture);
            lock (LockObject)
            {
                _hasTimberbornFps = true;
                _timberbornFramesPerSecond = framesPerSecond;
                _timberbornAverageFramesPerSecond = averageFramesPerSecond;
                _timberbornMinFramesPerSecond = minFramesPerSecond;
            }
        }
        catch (Exception)
        {
            // The Timberborn FPS counter is diagnostic only. Keep the overlay alive if its API changes.
        }
    }

    public static void RecordTickBuckets(object tickableBucketService, int numberOfBucketsToTick)
    {
        if (numberOfBucketsToTick <= 0)
        {
            return;
        }

        TryUpdateTotalNumberOfBuckets(tickableBucketService);
        var timestamp = Time.unscaledTime;
        lock (LockObject)
        {
            BucketSamples.Enqueue(new BucketSample(timestamp, numberOfBucketsToTick));
            _bucketCountSum += numberOfBucketsToTick;
            TrimBucketSamples(timestamp);
        }
    }

    public static void RecordFullTick()
    {
        var timestamp = Time.unscaledTime;
        lock (LockObject)
        {
            FullTickSamples.Enqueue(timestamp);
            TrimFullTickSamples(timestamp);
        }
    }

    public static void RecordTickerUpdate(object ticker)
    {
        var milliseconds = TryGetLengthOfLastTickMilliseconds(ticker);
        if (milliseconds <= 0.0 || milliseconds > 5000.0)
        {
            return;
        }

        var timestamp = Time.unscaledTime;
        lock (LockObject)
        {
            TickWorkSamples.Enqueue(new TickWorkSample(timestamp, milliseconds));
            _tickWorkMillisecondsSum += milliseconds;
            TrimTickWorkSamples(timestamp);
        }
    }

    public static Snapshot GetSnapshot()
    {
        var timestamp = Time.unscaledTime;
        lock (LockObject)
        {
            TrimFrameSamples(timestamp);
            TrimBucketSamples(timestamp);
            TrimFullTickSamples(timestamp);
            TrimTickWorkSamples(timestamp);

            var frameCount = FrameSamples.Count;
            var averageFrameMilliseconds = frameCount > 0 ? _frameMillisecondsSum / frameCount : 0.0;
            var maxFrameMilliseconds = 0.0;
            foreach (var sample in FrameSamples)
            {
                if (sample.Milliseconds > maxFrameMilliseconds)
                {
                    maxFrameMilliseconds = sample.Milliseconds;
                }
            }

            var tickWorkCount = TickWorkSamples.Count;
            var averageTickWorkMilliseconds = tickWorkCount > 0 ? _tickWorkMillisecondsSum / tickWorkCount : 0.0;
            var maxTickWorkMilliseconds = 0.0;
            foreach (var sample in TickWorkSamples)
            {
                if (sample.Milliseconds > maxTickWorkMilliseconds)
                {
                    maxTickWorkMilliseconds = sample.Milliseconds;
                }
            }

            var tickWindowSeconds = EffectiveWindowSeconds(timestamp, FullTickSamples.Count > 0 ? FullTickSamples.Peek() : timestamp);
            var updatesPerSecond = tickWindowSeconds > 0.001f ? FullTickSamples.Count / tickWindowSeconds : 0.0;
            var bucketWindowSeconds = EffectiveWindowSeconds(timestamp, BucketSamples.Count > 0 ? BucketSamples.Peek().Timestamp : timestamp);
            var bucketCallsPerSecond = bucketWindowSeconds > 0.001f ? BucketSamples.Count / bucketWindowSeconds : 0.0;
            var bucketRoundsPerSecond = bucketWindowSeconds > 0.001f && _totalNumberOfBuckets > 0
                ? _bucketCountSum / (double)_totalNumberOfBuckets / bucketWindowSeconds
                : 0.0;
            var tickerWindowSeconds = EffectiveWindowSeconds(timestamp, TickWorkSamples.Count > 0 ? TickWorkSamples.Peek().Timestamp : timestamp);
            var tickerUpdatesPerSecond = tickerWindowSeconds > 0.001f ? TickWorkSamples.Count / tickerWindowSeconds : 0.0;
            return new Snapshot(
                updatesPerSecond,
                bucketRoundsPerSecond,
                bucketCallsPerSecond,
                tickerUpdatesPerSecond,
                averageFrameMilliseconds,
                maxFrameMilliseconds,
                _lastFrameMilliseconds,
                averageTickWorkMilliseconds,
                maxTickWorkMilliseconds,
                _hasFrameTiming,
                _cpuMainThreadMilliseconds,
                _cpuRenderThreadMilliseconds,
                _cpuWaitMilliseconds,
                _cpuTotalMilliseconds,
                _gpuMilliseconds,
                _hasTimberbornFps,
                _timberbornFramesPerSecond,
                _timberbornAverageFramesPerSecond,
                _timberbornMinFramesPerSecond,
                frameCount,
                FullTickSamples.Count,
                BucketSamples.Count,
                tickWorkCount,
                _totalNumberOfBuckets);
        }
    }

    private static void CaptureFrameTiming()
    {
        if (!FrameTimingManager.IsFeatureEnabled())
        {
            return;
        }

        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1u, FrameTimings) == 0)
        {
            return;
        }

        var frameTiming = FrameTimings[0];
        lock (LockObject)
        {
            _hasFrameTiming = true;
            _cpuMainThreadMilliseconds = 0.001 * frameTiming.cpuMainThreadFrameTime;
            _cpuRenderThreadMilliseconds = 0.001 * frameTiming.cpuRenderThreadFrameTime;
            _cpuWaitMilliseconds = 0.001 * frameTiming.cpuMainThreadPresentWaitTime;
            _cpuTotalMilliseconds = 0.001 * frameTiming.cpuFrameTime;
            _gpuMilliseconds = 0.001 * frameTiming.gpuFrameTime;
        }
    }

    private static void TryUpdateTotalNumberOfBuckets(object tickableBucketService)
    {
        try
        {
            var property = tickableBucketService.GetType().GetProperty("TotalNumberOfBuckets", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(tickableBucketService) is int totalNumberOfBuckets && totalNumberOfBuckets > 0)
            {
                _totalNumberOfBuckets = totalNumberOfBuckets;
            }
        }
        catch (Exception)
        {
            // Keep the known Timberborn 1.0 fallback of 129 buckets.
        }
    }

    private static void EnsureTimberbornFpsProperties(Type counterType)
    {
        if (_timberbornFpsCounterType == counterType)
        {
            return;
        }

        _timberbornFpsCounterType = counterType;
        _timberbornFramesPerSecondProperty = counterType.GetProperty("FramesPerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _timberbornAverageFramesPerSecondProperty = counterType.GetProperty("AverageFramesPerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _timberbornMinFramesPerSecondProperty = counterType.GetProperty("MinFramesPerSecond", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

    private static void TrimFrameSamples(float timestamp)
    {
        var threshold = timestamp - BenchmarkSettings.RuntimeOverlayWindowSeconds;
        while (FrameSamples.Count > 0 && FrameSamples.Peek().Timestamp < threshold)
        {
            _frameMillisecondsSum -= FrameSamples.Dequeue().Milliseconds;
        }
    }

    private static void TrimFullTickSamples(float timestamp)
    {
        var threshold = timestamp - BenchmarkSettings.RuntimeOverlayWindowSeconds;
        while (FullTickSamples.Count > 0 && FullTickSamples.Peek() < threshold)
        {
            FullTickSamples.Dequeue();
        }
    }

    private static void TrimTickWorkSamples(float timestamp)
    {
        var threshold = timestamp - BenchmarkSettings.RuntimeOverlayWindowSeconds;
        while (TickWorkSamples.Count > 0 && TickWorkSamples.Peek().Timestamp < threshold)
        {
            _tickWorkMillisecondsSum -= TickWorkSamples.Dequeue().Milliseconds;
        }
    }

    private static void TrimBucketSamples(float timestamp)
    {
        var threshold = timestamp - BenchmarkSettings.RuntimeOverlayWindowSeconds;
        while (BucketSamples.Count > 0 && BucketSamples.Peek().Timestamp < threshold)
        {
            _bucketCountSum -= BucketSamples.Dequeue().Count;
        }
    }

    private static float EffectiveWindowSeconds(float timestamp, float oldestTimestamp)
    {
        return Math.Min(BenchmarkSettings.RuntimeOverlayWindowSeconds, Math.Max(0.001f, timestamp - oldestTimestamp));
    }

    public readonly struct Snapshot
    {
        public Snapshot(
            double updatesPerSecond,
            double bucketRoundsPerSecond,
            double bucketCallsPerSecond,
            double tickerUpdatesPerSecond,
            double averageFrameMilliseconds,
            double maxFrameMilliseconds,
            double lastFrameMilliseconds,
            double averageTickWorkMilliseconds,
            double maxTickWorkMilliseconds,
            bool hasFrameTiming,
            double cpuMainThreadMilliseconds,
            double cpuRenderThreadMilliseconds,
            double cpuWaitMilliseconds,
            double cpuTotalMilliseconds,
            double gpuMilliseconds,
            bool hasTimberbornFps,
            double timberbornFramesPerSecond,
            double timberbornAverageFramesPerSecond,
            double timberbornMinFramesPerSecond,
            int frameSamples,
            int fullTickSamples,
            int bucketSamples,
            int tickWorkSamples,
            int totalNumberOfBuckets)
        {
            UpdatesPerSecond = updatesPerSecond;
            BucketRoundsPerSecond = bucketRoundsPerSecond;
            BucketCallsPerSecond = bucketCallsPerSecond;
            TickerUpdatesPerSecond = tickerUpdatesPerSecond;
            AverageFrameMilliseconds = averageFrameMilliseconds;
            MaxFrameMilliseconds = maxFrameMilliseconds;
            LastFrameMilliseconds = lastFrameMilliseconds;
            AverageTickWorkMilliseconds = averageTickWorkMilliseconds;
            MaxTickWorkMilliseconds = maxTickWorkMilliseconds;
            HasFrameTiming = hasFrameTiming;
            CpuMainThreadMilliseconds = cpuMainThreadMilliseconds;
            CpuRenderThreadMilliseconds = cpuRenderThreadMilliseconds;
            CpuWaitMilliseconds = cpuWaitMilliseconds;
            CpuTotalMilliseconds = cpuTotalMilliseconds;
            GpuMilliseconds = gpuMilliseconds;
            HasTimberbornFps = hasTimberbornFps;
            TimberbornFramesPerSecond = timberbornFramesPerSecond;
            TimberbornAverageFramesPerSecond = timberbornAverageFramesPerSecond;
            TimberbornMinFramesPerSecond = timberbornMinFramesPerSecond;
            FrameSamples = frameSamples;
            FullTickSamples = fullTickSamples;
            BucketSamples = bucketSamples;
            TickWorkSamples = tickWorkSamples;
            TotalNumberOfBuckets = totalNumberOfBuckets;
        }

        public double UpdatesPerSecond { get; }
        public double BucketRoundsPerSecond { get; }
        public double BucketCallsPerSecond { get; }
        public double TickerUpdatesPerSecond { get; }
        public double AverageFrameMilliseconds { get; }
        public double MaxFrameMilliseconds { get; }
        public double LastFrameMilliseconds { get; }
        public double AverageTickWorkMilliseconds { get; }
        public double MaxTickWorkMilliseconds { get; }
        public bool HasFrameTiming { get; }
        public double CpuMainThreadMilliseconds { get; }
        public double CpuRenderThreadMilliseconds { get; }
        public double CpuWaitMilliseconds { get; }
        public double CpuTotalMilliseconds { get; }
        public double GpuMilliseconds { get; }
        public bool HasTimberbornFps { get; }
        public double TimberbornFramesPerSecond { get; }
        public double TimberbornAverageFramesPerSecond { get; }
        public double TimberbornMinFramesPerSecond { get; }
        public int FrameSamples { get; }
        public int FullTickSamples { get; }
        public int BucketSamples { get; }
        public int TickWorkSamples { get; }
        public int TotalNumberOfBuckets { get; }

        public string ToOverlayText(BenchmarkMode mode)
        {
            var timingLine = HasFrameTiming
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "\nCPU {0:F1}/{1:F1}/{2:F1} GPU {3:F1}",
                    CpuMainThreadMilliseconds,
                    CpuRenderThreadMilliseconds,
                    CpuWaitMilliseconds,
                    GpuMilliseconds)
                : string.Empty;
            var timberbornFpsLine = HasTimberbornFps
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "TB FPS {0:F0}/{1:F0}/{2:F0}\n",
                    TimberbornFramesPerSecond,
                    TimberbornAverageFramesPerSecond,
                    TimberbornMinFramesPerSecond)
                : string.Empty;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}Sim {1:F1}/s bucket {2:F0}/s\nTicker {3:F1}/s full {4:F1}/s\nFrame {5:F1} ms now / {6:F1} avg / {7:F1} max\nTick {8:F2} ms avg / {9:F2} max{10}\nMode {11}",
                timberbornFpsLine,
                BucketRoundsPerSecond,
                BucketCallsPerSecond,
                TickerUpdatesPerSecond,
                UpdatesPerSecond,
                LastFrameMilliseconds,
                AverageFrameMilliseconds,
                MaxFrameMilliseconds,
                AverageTickWorkMilliseconds,
                MaxTickWorkMilliseconds,
                timingLine,
                mode);
        }
    }

    private readonly struct FrameSample
    {
        public FrameSample(float timestamp, double milliseconds)
        {
            Timestamp = timestamp;
            Milliseconds = milliseconds;
        }

        public float Timestamp { get; }
        public double Milliseconds { get; }
    }

    private readonly struct BucketSample
    {
        public BucketSample(float timestamp, int count)
        {
            Timestamp = timestamp;
            Count = count;
        }

        public float Timestamp { get; }
        public int Count { get; }
    }

    private readonly struct TickWorkSample
    {
        public TickWorkSample(float timestamp, double milliseconds)
        {
            Timestamp = timestamp;
            Milliseconds = milliseconds;
        }

        public float Timestamp { get; }
        public double Milliseconds { get; }
    }
}
