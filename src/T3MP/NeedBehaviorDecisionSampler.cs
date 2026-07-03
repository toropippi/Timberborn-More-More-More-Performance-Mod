using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NeedBehaviorDecisionSampler
{
    private const int TravelCallerSampleRate = 512;
    private const int PickBestCallerSampleRate = 32;
    private const int MaxTopCallers = 6;

    private static readonly ModeCounters[] Counters =
    {
        new ModeCounters(BenchmarkMode.Vanilla),
        new ModeCounters(BenchmarkMode.Optimized)
    };

    private static readonly object SamplesLock = new object();
    private static readonly Dictionary<CallerSampleKey, int> CallerSamples = new Dictionary<CallerSampleKey, int>();
    private static int _travelCallerCounter;
    private static int _pickBestCallerCounter;

    [ThreadStatic]
    private static int _threadTravelEstimateCalls;

    public static void RecordPickBestCall(out PickBestCallState state)
    {
        if (!BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            state = PickBestCallState.Inactive;
            return;
        }

        if ((Interlocked.Increment(ref _pickBestCallerCounter) % PickBestCallerSampleRate) == 0)
        {
            RecordCallerSample(mode, "PickBestAction", FindExternalCaller());
        }

        state = new PickBestCallState(
            true,
            mode,
            Stopwatch.GetTimestamp(),
            _threadTravelEstimateCalls);
    }

    public static void RecordPickBestReturn(PickBestCallState state, object? result)
    {
        if (!state.Active)
        {
            return;
        }

        Counters[(int)state.Mode].RecordPickBest(
            Stopwatch.GetTimestamp() - state.StartTimestamp,
            _threadTravelEstimateCalls - state.TravelEstimateCallsBefore,
            result != null);
    }

    public static void RecordTravelEstimateCall()
    {
        if (!BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            return;
        }

        _threadTravelEstimateCalls++;
        Counters[(int)mode].RecordTravelEstimate();
        if ((Interlocked.Increment(ref _travelCallerCounter) % TravelCallerSampleRate) == 0)
        {
            RecordCallerSample(mode, "TravelTimeBetween", FindExternalCaller());
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        foreach (var counter in Counters)
        {
            var snapshot = counter.SnapshotAndReset();
            if (snapshot.PickBestCalls == 0 && snapshot.TravelEstimateCalls == 0)
            {
                continue;
            }

            var pickBestMs = snapshot.PickBestStopwatchTicks * 1000.0 / Stopwatch.Frequency;
            var pickBestAvgUs = snapshot.PickBestCalls > 0
                ? snapshot.PickBestStopwatchTicks * 1000000.0 / Stopwatch.Frequency / snapshot.PickBestCalls
                : 0;
            var travelCallsPerPickBest = snapshot.PickBestCalls > 0
                ? (double)snapshot.TravelEstimateCallsInsidePickBest / snapshot.PickBestCalls
                : 0;
            Debug.Log(
                $"[T3MP] NeedBehaviorDecision aggregate={aggregateId}, mode={snapshot.Mode}, pickBestCalls={snapshot.PickBestCalls}, pickBestMs={pickBestMs:F2}, pickBestAvgUs={pickBestAvgUs:F2}, selected={snapshot.SelectedActions}, noAction={snapshot.NoActions}, travelEstimateCalls={snapshot.TravelEstimateCalls}, travelInsidePickBest={snapshot.TravelEstimateCallsInsidePickBest}, travelCallsPerPickBest={travelCallsPerPickBest:F2}");
        }

        List<KeyValuePair<CallerSampleKey, int>> sampleSnapshot;
        lock (SamplesLock)
        {
            sampleSnapshot = CallerSamples.ToList();
            CallerSamples.Clear();
        }

        foreach (var group in sampleSnapshot.GroupBy(pair => new CallerGroupKey(pair.Key.Mode, pair.Key.Kind)))
        {
            var totalSamples = group.Sum(pair => pair.Value);
            var top = group
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key.Caller, StringComparer.Ordinal)
                .Take(MaxTopCallers)
                .Select(pair => $"{pair.Key.Caller}:{pair.Value}");

            Debug.Log(
                $"[T3MP] NeedBehaviorCallerSamples aggregate={aggregateId}, mode={group.Key.Mode}, kind={group.Key.Kind}, samples={totalSamples}, top={string.Join(" | ", top)}");
        }
    }

    private static void RecordCallerSample(BenchmarkMode mode, string kind, string caller)
    {
        lock (SamplesLock)
        {
            var key = new CallerSampleKey(mode, kind, caller);
            CallerSamples.TryGetValue(key, out var count);
            CallerSamples[key] = count + 1;
        }
    }

    private static string FindExternalCaller()
    {
        var stackTrace = new StackTrace(false);
        for (var index = 0; index < stackTrace.FrameCount; index++)
        {
            var method = stackTrace.GetFrame(index)?.GetMethod();
            var declaringType = method?.DeclaringType;
            var typeName = declaringType?.FullName;
            if (method == null || string.IsNullOrEmpty(typeName))
            {
                continue;
            }

            if (ShouldSkip(typeName, method.Name))
            {
                continue;
            }

            return $"{typeName}.{method.Name}";
        }

        return "unknown";
    }

    private static bool ShouldSkip(string typeName, string methodName)
    {
        return typeName.StartsWith("T3MP", StringComparison.Ordinal)
            || typeName.StartsWith("HarmonyLib", StringComparison.Ordinal)
            || typeName.StartsWith("MonoMod.", StringComparison.Ordinal)
            || typeName.StartsWith("System.", StringComparison.Ordinal)
            || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
            || typeName == "Timberborn.NeedBehaviorSystem.ActionDurationCalculator"
            || typeName == "Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService"
            || typeName == "Timberborn.WalkingSystem.Walker"
            || typeName == "Timberborn.Navigation.NavigationService"
            || typeName.Contains("DynamicMethodDefinition")
            || methodName.Contains("_Patch")
            || methodName.StartsWith("lambda_method", StringComparison.Ordinal);
    }

    public readonly struct PickBestCallState
    {
        public static readonly PickBestCallState Inactive = new PickBestCallState(false, BenchmarkMode.Vanilla, 0, 0);

        public PickBestCallState(bool active, BenchmarkMode mode, long startTimestamp, int travelEstimateCallsBefore)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            TravelEstimateCallsBefore = travelEstimateCallsBefore;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
        public int TravelEstimateCallsBefore { get; }
    }

    private sealed class ModeCounters
    {
        private readonly BenchmarkMode _mode;
        private long _pickBestCalls;
        private long _pickBestStopwatchTicks;
        private long _selectedActions;
        private long _noActions;
        private long _travelEstimateCalls;
        private long _travelEstimateCallsInsidePickBest;

        public ModeCounters(BenchmarkMode mode)
        {
            _mode = mode;
        }

        public void RecordPickBest(long stopwatchTicks, int travelEstimateCallsInsidePickBest, bool selected)
        {
            Interlocked.Increment(ref _pickBestCalls);
            Interlocked.Add(ref _pickBestStopwatchTicks, stopwatchTicks);
            Interlocked.Add(ref _travelEstimateCallsInsidePickBest, Math.Max(0, travelEstimateCallsInsidePickBest));
            if (selected)
            {
                Interlocked.Increment(ref _selectedActions);
            }
            else
            {
                Interlocked.Increment(ref _noActions);
            }
        }

        public void RecordTravelEstimate()
        {
            Interlocked.Increment(ref _travelEstimateCalls);
        }

        public Snapshot SnapshotAndReset()
        {
            return new Snapshot(
                _mode,
                Interlocked.Exchange(ref _pickBestCalls, 0),
                Interlocked.Exchange(ref _pickBestStopwatchTicks, 0),
                Interlocked.Exchange(ref _selectedActions, 0),
                Interlocked.Exchange(ref _noActions, 0),
                Interlocked.Exchange(ref _travelEstimateCalls, 0),
                Interlocked.Exchange(ref _travelEstimateCallsInsidePickBest, 0));
        }
    }

    private readonly struct Snapshot
    {
        public Snapshot(
            BenchmarkMode mode,
            long pickBestCalls,
            long pickBestStopwatchTicks,
            long selectedActions,
            long noActions,
            long travelEstimateCalls,
            long travelEstimateCallsInsidePickBest)
        {
            Mode = mode;
            PickBestCalls = pickBestCalls;
            PickBestStopwatchTicks = pickBestStopwatchTicks;
            SelectedActions = selectedActions;
            NoActions = noActions;
            TravelEstimateCalls = travelEstimateCalls;
            TravelEstimateCallsInsidePickBest = travelEstimateCallsInsidePickBest;
        }

        public BenchmarkMode Mode { get; }
        public long PickBestCalls { get; }
        public long PickBestStopwatchTicks { get; }
        public long SelectedActions { get; }
        public long NoActions { get; }
        public long TravelEstimateCalls { get; }
        public long TravelEstimateCallsInsidePickBest { get; }
    }

    private readonly struct CallerSampleKey : IEquatable<CallerSampleKey>
    {
        public CallerSampleKey(BenchmarkMode mode, string kind, string caller)
        {
            Mode = mode;
            Kind = kind;
            Caller = caller;
        }

        public BenchmarkMode Mode { get; }
        public string Kind { get; }
        public string Caller { get; }

        public bool Equals(CallerSampleKey other)
        {
            return Mode == other.Mode
                && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                && string.Equals(Caller, other.Caller, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is CallerSampleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Mode;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Kind);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Caller);
                return hash;
            }
        }
    }

    private readonly struct CallerGroupKey : IEquatable<CallerGroupKey>
    {
        public CallerGroupKey(BenchmarkMode mode, string kind)
        {
            Mode = mode;
            Kind = kind;
        }

        public BenchmarkMode Mode { get; }
        public string Kind { get; }

        public bool Equals(CallerGroupKey other)
        {
            return Mode == other.Mode && string.Equals(Kind, other.Kind, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is CallerGroupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ StringComparer.Ordinal.GetHashCode(Kind);
            }
        }
    }
}
