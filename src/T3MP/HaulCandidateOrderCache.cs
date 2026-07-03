using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.WorkSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class HaulCandidateOrderCache
{
    private static readonly object LockObject = new object();
    private static readonly Dictionary<object, CacheEntry> Entries =
        new Dictionary<object, CacheEntry>(ReferenceComparer.Instance);

    private static long _attempts;
    private static long _hits;
    private static long _misses;
    private static long _stores;
    private static long _returnedBehaviors;
    private static long _savedStopwatchTicks;

    public static bool TryUse(object districtHaulCandidates, IList<WorkplaceBehavior> workplaceBehaviors, out CallState state)
    {
        if (!BenchmarkSettings.EnableHaulCandidateOrderCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            state = CallState.Inactive;
            return true;
        }

        state = new CallState(Stopwatch.GetTimestamp(), false);
        Interlocked.Increment(ref _attempts);
        var frame = Time.frameCount;
        lock (LockObject)
        {
            if (!Entries.TryGetValue(districtHaulCandidates, out var entry) || entry.Frame != frame)
            {
                Interlocked.Increment(ref _misses);
                return true;
            }

            for (var index = 0; index < entry.WorkplaceBehaviors.Count; index++)
            {
                workplaceBehaviors.Add(entry.WorkplaceBehaviors[index]);
            }

            Interlocked.Increment(ref _hits);
            Interlocked.Add(ref _returnedBehaviors, entry.WorkplaceBehaviors.Count);
            state = new CallState(state.StartTimestamp, true);
            return false;
        }
    }

    public static void Store(object districtHaulCandidates, IList<WorkplaceBehavior> workplaceBehaviors, CallState state)
    {
        if (!BenchmarkSettings.EnableHaulCandidateOrderCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            !state.Active ||
            state.UsedCached)
        {
            return;
        }

        var elapsed = Stopwatch.GetTimestamp() - state.StartTimestamp;
        lock (LockObject)
        {
            if (!Entries.TryGetValue(districtHaulCandidates, out var entry))
            {
                entry = new CacheEntry();
                Entries.Add(districtHaulCandidates, entry);
            }

            entry.Frame = Time.frameCount;
            entry.WorkplaceBehaviors.Clear();
            for (var index = 0; index < workplaceBehaviors.Count; index++)
            {
                var behavior = workplaceBehaviors[index];
                if (behavior is not null)
                {
                    entry.WorkplaceBehaviors.Add(behavior);
                }
            }
        }

        Interlocked.Increment(ref _stores);
        Interlocked.Add(ref _savedStopwatchTicks, elapsed);
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var hits = Interlocked.Exchange(ref _hits, 0);
        var misses = Interlocked.Exchange(ref _misses, 0);
        var stores = Interlocked.Exchange(ref _stores, 0);
        var returnedBehaviors = Interlocked.Exchange(ref _returnedBehaviors, 0);
        var savedTicks = Interlocked.Exchange(ref _savedStopwatchTicks, 0);
        if (attempts == 0 && stores == 0)
        {
            return;
        }

        int entries;
        lock (LockObject)
        {
            entries = Entries.Count;
        }

        var hitRate = attempts > 0 ? (double)hits / attempts : 0.0;
        Debug.Log(
            $"[T3MP] HaulCandidateOrderCache aggregate={aggregateId}, enabled={BenchmarkSettings.EnableHaulCandidateOrderCache}, attempts={attempts}, hits={hits}, misses={misses}, hitRate={hitRate:F3}, stores={stores}, returnedBehaviors={returnedBehaviors}, storeMs={ToMilliseconds(savedTicks):F2}, entries={entries}");
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class CacheEntry
    {
        public int Frame { get; set; } = -1;
        public List<WorkplaceBehavior> WorkplaceBehaviors { get; } = new List<WorkplaceBehavior>(128);
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(0, false);

        public CallState(long startTimestamp, bool usedCached)
        {
            StartTimestamp = startTimestamp;
            UsedCached = usedCached;
        }

        public bool Active => StartTimestamp > 0;

        public long StartTimestamp { get; }

        public bool UsedCached { get; }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
