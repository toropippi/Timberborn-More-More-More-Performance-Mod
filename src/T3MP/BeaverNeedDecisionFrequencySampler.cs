using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class BeaverNeedDecisionFrequencySampler
{
    private static readonly object Lock = new object();
    private static readonly Dictionary<int, BeaverStats> StatsByBeaver = new Dictionary<int, BeaverStats>();
    private static long _calls;
    private static long _vanillaCalls;
    private static long _optimizedCalls;

    public static void RecordCall(object beaverNeedBehaviorPicker)
    {
        if (!BenchmarkSettings.EnableBeaverDecisionFrequencySampler)
        {
            return;
        }

        var frame = Time.frameCount;
        var seconds = Time.realtimeSinceStartup;
        var mode = BenchmarkModeController.CurrentMode;
        var key = RuntimeHelpers.GetHashCode(beaverNeedBehaviorPicker);

        Interlocked.Increment(ref _calls);
        if (mode == BenchmarkMode.Vanilla)
        {
            Interlocked.Increment(ref _vanillaCalls);
        }
        else
        {
            Interlocked.Increment(ref _optimizedCalls);
        }

        lock (Lock)
        {
            if (!StatsByBeaver.TryGetValue(key, out var stats))
            {
                stats = new BeaverStats();
                StatsByBeaver.Add(key, stats);
            }

            stats.Record(frame, seconds);
        }
    }

    public static void LogAndReset(long aggregateId, float elapsedSeconds)
    {
        if (!BenchmarkSettings.EnableBeaverDecisionFrequencySampler)
        {
            return;
        }

        var calls = Interlocked.Exchange(ref _calls, 0);
        var vanillaCalls = Interlocked.Exchange(ref _vanillaCalls, 0);
        var optimizedCalls = Interlocked.Exchange(ref _optimizedCalls, 0);

        List<BeaverSnapshot> snapshots;
        lock (Lock)
        {
            if (StatsByBeaver.Count == 0 && calls == 0)
            {
                return;
            }

            snapshots = StatsByBeaver.Values.Select(stats => stats.ToSnapshot()).ToList();
            StatsByBeaver.Clear();
        }

        var beavers = snapshots.Count;
        var repeatBeavers = snapshots.Count(snapshot => snapshot.Calls > 1);
        var maxCalls = snapshots.Count > 0 ? snapshots.Max(snapshot => snapshot.Calls) : 0;
        var callsPerBeaver = snapshots.Select(snapshot => snapshot.Calls).OrderBy(value => value).ToList();
        var intervalsFrames = new List<int>();
        var intervalsSeconds = new List<float>();
        foreach (var snapshot in snapshots)
        {
            intervalsFrames.AddRange(snapshot.IntervalFrames);
            intervalsSeconds.AddRange(snapshot.IntervalSeconds);
        }

        var callsPerBeaverAvg = beavers > 0 ? (double)calls / beavers : 0;
        var callsPerBeaverPerSecond = beavers > 0 && elapsedSeconds > 0 ? calls / (double)beavers / elapsedSeconds : 0;
        var secondsPerBeaverCall = callsPerBeaverPerSecond > 0 ? 1.0 / callsPerBeaverPerSecond : 0;
        var intervalAvgFrames = intervalsFrames.Count > 0 ? intervalsFrames.Average() : 0;
        var intervalAvgSeconds = intervalsSeconds.Count > 0 ? intervalsSeconds.Average() : 0;

        Debug.Log(
            $"[T3MP] BeaverDecisionFrequency aggregate={aggregateId}, elapsedSeconds={elapsedSeconds:F2}, beavers={beavers}, repeatBeavers={repeatBeavers}, calls={calls}, vanillaCalls={vanillaCalls}, optimizedCalls={optimizedCalls}, callsPerBeaver={callsPerBeaverAvg:F2}, callsPerBeaverPerSecond={callsPerBeaverPerSecond:F3}, secondsPerBeaverCall={secondsPerBeaverCall:F2}, callsPerBeaverP50={Percentile(callsPerBeaver, 0.50):F0}, callsPerBeaverP95={Percentile(callsPerBeaver, 0.95):F0}, callsPerBeaverMax={maxCalls}, intervalCount={intervalsFrames.Count}, intervalAvgFrames={intervalAvgFrames:F2}, intervalP50Frames={Percentile(intervalsFrames, 0.50):F0}, intervalP95Frames={Percentile(intervalsFrames, 0.95):F0}, intervalMaxFrames={MaxOrZero(intervalsFrames)}, intervalAvgSeconds={intervalAvgSeconds:F2}, intervalP50Seconds={Percentile(intervalsSeconds, 0.50):F2}, intervalP95Seconds={Percentile(intervalsSeconds, 0.95):F2}, intervalMaxSeconds={MaxOrZero(intervalsSeconds):F2}");
    }

    private static double Percentile(IReadOnlyList<int> sortedOrUnsortedValues, double percentile)
    {
        if (sortedOrUnsortedValues.Count == 0)
        {
            return 0;
        }

        var values = sortedOrUnsortedValues.OrderBy(value => value).ToList();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Max(0, Math.Min(values.Count - 1, index))];
    }

    private static double Percentile(IReadOnlyList<float> sortedOrUnsortedValues, double percentile)
    {
        if (sortedOrUnsortedValues.Count == 0)
        {
            return 0;
        }

        var values = sortedOrUnsortedValues.OrderBy(value => value).ToList();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Max(0, Math.Min(values.Count - 1, index))];
    }

    private static int MaxOrZero(IReadOnlyList<int> values)
    {
        return values.Count > 0 ? values.Max() : 0;
    }

    private static float MaxOrZero(IReadOnlyList<float> values)
    {
        return values.Count > 0 ? values.Max() : 0;
    }

    private sealed class BeaverStats
    {
        private readonly List<int> _intervalFrames = new List<int>();
        private readonly List<float> _intervalSeconds = new List<float>();
        private int _calls;
        private int _lastFrame = -1;
        private float _lastSeconds;

        public void Record(int frame, float seconds)
        {
            if (_lastFrame >= 0)
            {
                _intervalFrames.Add(frame - _lastFrame);
                _intervalSeconds.Add(seconds - _lastSeconds);
            }

            _lastFrame = frame;
            _lastSeconds = seconds;
            _calls++;
        }

        public BeaverSnapshot ToSnapshot()
        {
            return new BeaverSnapshot(_calls, _intervalFrames.ToArray(), _intervalSeconds.ToArray());
        }
    }

    private readonly struct BeaverSnapshot
    {
        public BeaverSnapshot(int calls, int[] intervalFrames, float[] intervalSeconds)
        {
            Calls = calls;
            IntervalFrames = intervalFrames;
            IntervalSeconds = intervalSeconds;
        }

        public int Calls { get; }
        public int[] IntervalFrames { get; }
        public float[] IntervalSeconds { get; }
    }
}
