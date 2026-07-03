using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BehaviorSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WorkplaceNoActionFrameCache
{
    private static readonly Dictionary<int, int> LastNoActionFrameByBehavior = new Dictionary<int, int>();

    private static long _attempts;
    private static long _skips;
    private static long _releaseArms;
    private static long _nonRelease;
    private static long _stopwatchTicks;

    public static bool TrySkip(object workplaceBehavior, out Decision result, out CallState state)
    {
        result = Decision.ReleaseNow();
        if (!BenchmarkSettings.EnableWorkplaceNoActionFrameCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            state = CallState.Inactive;
            return true;
        }

        Interlocked.Increment(ref _attempts);
        var key = RuntimeHelpers.GetHashCode(workplaceBehavior);
        var frame = Time.frameCount;
        if (LastNoActionFrameByBehavior.TryGetValue(key, out var noActionFrame) && noActionFrame == frame)
        {
            Interlocked.Increment(ref _skips);
            state = CallState.Skipped;
            return false;
        }

        state = new CallState(true, key, BenchmarkSettings.EnableWorkplaceNoActionFrameCacheTiming ? Stopwatch.GetTimestamp() : 0);
        return true;
    }

    public static void Record(object workplaceBehavior, Decision result, CallState state)
    {
        if (!state.Active)
        {
            return;
        }

        if (BenchmarkSettings.EnableWorkplaceNoActionFrameCacheTiming && state.StartTimestamp > 0)
        {
            Interlocked.Add(ref _stopwatchTicks, Stopwatch.GetTimestamp() - state.StartTimestamp);
        }
        if (result.ShouldReleaseNow)
        {
            LastNoActionFrameByBehavior[state.Key] = Time.frameCount;
            Interlocked.Increment(ref _releaseArms);
        }
        else
        {
            Interlocked.Increment(ref _nonRelease);
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var skips = Interlocked.Exchange(ref _skips, 0);
        var releaseArms = Interlocked.Exchange(ref _releaseArms, 0);
        var nonRelease = Interlocked.Exchange(ref _nonRelease, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        if (attempts == 0 && skips == 0 && releaseArms == 0 && nonRelease == 0)
        {
            return;
        }

        var skipRate = attempts > 0 ? (double)skips / attempts : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] WorkplaceNoActionFrameCache aggregate={0}, enabled={1}, attempts={2}, skips={3}, skipRate={4:F3}, releaseArms={5}, nonRelease={6}, ms={7:F2}, entries={8}",
            aggregateId,
            BenchmarkSettings.EnableWorkplaceNoActionFrameCache,
            attempts,
            skips,
            skipRate,
            releaseArms,
            nonRelease,
            ticks * 1000.0 / Stopwatch.Frequency,
            LastNoActionFrameByBehavior.Count));
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, 0, 0);
        public static readonly CallState Skipped = new CallState(false, 0, 0);

        public CallState(bool active, int key, long startTimestamp)
        {
            Active = active;
            Key = key;
            StartTimestamp = startTimestamp;
        }

        public bool Active { get; }

        public int Key { get; }

        public long StartTimestamp { get; }
    }
}
