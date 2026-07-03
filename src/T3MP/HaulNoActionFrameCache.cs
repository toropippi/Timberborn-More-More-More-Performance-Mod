using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BehaviorSystem;
using Timberborn.Hauling;
using Timberborn.WorkSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class HaulNoActionFrameCache
{
    private static readonly Dictionary<int, int> LastNoActionFrameByBehavior = new Dictionary<int, int>();
    private static readonly Dictionary<int, OrderedWorkplaceEntry> OrderedWorkplacesByHaulingCenter = new Dictionary<int, OrderedWorkplaceEntry>();
    private static FieldInfo? _haulingCenterField;
    private static int _warningCount;

    [ThreadStatic]
    private static List<WorkplaceBehavior>? _workplaceBehaviors;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _orderedCalls;
    private static long _orderedCacheHits;
    private static long _orderedCacheMisses;
    private static long _candidateChecks;
    private static long _skips;
    private static long _releaseArms;
    private static long _transfers;
    private static long _emptyReleases;
    private static long _stopwatchTicks;

    public static void Initialize(Type haulWorkplaceBehaviorType)
    {
        _haulingCenterField = haulWorkplaceBehaviorType.GetField("_haulingCenter", BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public static bool TryDecide(object haulWorkplaceBehavior, BehaviorAgent agent, out Decision result)
    {
        result = Decision.ReleaseNow();
        if (!BenchmarkSettings.EnableHaulNoActionFrameCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        var startTimestamp = recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        var localHandled = 0L;
        var localFallbacks = 0L;
        var localOrderedCalls = 0L;
        var localCandidateChecks = 0L;
        var localSkips = 0L;
        var localReleaseArms = 0L;
        var localTransfers = 0L;
        var localEmptyReleases = 0L;
        try
        {
            if (_haulingCenterField?.GetValue(haulWorkplaceBehavior) is not HaulingCenter haulingCenter)
            {
                localFallbacks++;
                return false;
            }

            var workplaceBehaviors = GetOrderedWorkplaceBehaviors(haulingCenter, ref localOrderedCalls);

            var frame = Time.frameCount;
            for (var index = 0; index < workplaceBehaviors.Count; index++)
            {
                var workplaceBehavior = workplaceBehaviors[index];
                var key = RuntimeHelpers.GetHashCode(workplaceBehavior);
                if (recordMetrics)
                {
                    localCandidateChecks++;
                }
                if (LastNoActionFrameByBehavior.TryGetValue(key, out var noActionFrame) && noActionFrame == frame)
                {
                    if (recordMetrics)
                    {
                        localSkips++;
                    }
                    continue;
                }

                var decision = workplaceBehavior.Decide(agent);
                if (!decision.ShouldReleaseNow)
                {
                    result = Decision.TransferNow(workplaceBehavior, in decision);
                    if (recordMetrics)
                    {
                        localTransfers++;
                        localHandled++;
                    }
                    return true;
                }

                LastNoActionFrameByBehavior[key] = frame;
                if (recordMetrics)
                {
                    localReleaseArms++;
                }
            }

            if (recordMetrics)
            {
                localEmptyReleases++;
                localHandled++;
            }
            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                localFallbacks++;
            }
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] Haul no-action frame cache fallback: {exception}");
            }

            return false;
        }
        finally
        {
            _workplaceBehaviors?.Clear();
            if (recordMetrics)
            {
                Interlocked.Add(ref _handled, localHandled);
                Interlocked.Add(ref _fallbacks, localFallbacks);
                Interlocked.Add(ref _orderedCalls, localOrderedCalls);
                Interlocked.Add(ref _candidateChecks, localCandidateChecks);
                Interlocked.Add(ref _skips, localSkips);
                Interlocked.Add(ref _releaseArms, localReleaseArms);
                Interlocked.Add(ref _transfers, localTransfers);
                Interlocked.Add(ref _emptyReleases, localEmptyReleases);
            }
            if (recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                Interlocked.Add(ref _stopwatchTicks, Stopwatch.GetTimestamp() - startTimestamp);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var orderedCalls = Interlocked.Exchange(ref _orderedCalls, 0);
        var orderedCacheHits = Interlocked.Exchange(ref _orderedCacheHits, 0);
        var orderedCacheMisses = Interlocked.Exchange(ref _orderedCacheMisses, 0);
        var candidateChecks = Interlocked.Exchange(ref _candidateChecks, 0);
        var skips = Interlocked.Exchange(ref _skips, 0);
        var releaseArms = Interlocked.Exchange(ref _releaseArms, 0);
        var transfers = Interlocked.Exchange(ref _transfers, 0);
        var emptyReleases = Interlocked.Exchange(ref _emptyReleases, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        if (attempts == 0 && handled == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var skipRate = candidateChecks > 0 ? (double)skips / candidateChecks : 0.0;
        Debug.Log(
            $"[T3MP] HaulNoActionFrameCache aggregate={aggregateId}, enabled={BenchmarkSettings.EnableHaulNoActionFrameCache}, orderedCache={BenchmarkSettings.EnableHaulNoActionOrderedCache}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, orderedCalls={orderedCalls}, orderedCacheHits={orderedCacheHits}, orderedCacheMisses={orderedCacheMisses}, candidateChecks={candidateChecks}, skips={skips}, skipRate={skipRate:F3}, releaseArms={releaseArms}, transfers={transfers}, empty={emptyReleases}, ms={ToMilliseconds(ticks):F2}, entries={LastNoActionFrameByBehavior.Count}, orderedEntries={OrderedWorkplacesByHaulingCenter.Count}");
    }

    private static List<WorkplaceBehavior> GetOrderedWorkplaceBehaviors(HaulingCenter haulingCenter, ref long localOrderedCalls)
    {
        if (!BenchmarkSettings.EnableHaulNoActionOrderedCache)
        {
            var uncached = _workplaceBehaviors ??= new List<WorkplaceBehavior>(256);
            uncached.Clear();
            haulingCenter.GetWorkplaceBehaviorsOrdered(uncached);
            if (BenchmarkSettings.EnableHotOptimizerMetrics)
            {
                localOrderedCalls++;
            }
            return uncached;
        }

        var key = RuntimeHelpers.GetHashCode(haulingCenter);
        var frame = Time.frameCount;
        if (OrderedWorkplacesByHaulingCenter.TryGetValue(key, out var entry) && entry.Frame == frame)
        {
            if (BenchmarkSettings.EnableHotOptimizerMetrics)
            {
                Interlocked.Increment(ref _orderedCacheHits);
            }
            return entry.WorkplaceBehaviors;
        }

        if (entry is null)
        {
            entry = new OrderedWorkplaceEntry();
            OrderedWorkplacesByHaulingCenter[key] = entry;
        }

        entry.Frame = frame;
        entry.WorkplaceBehaviors.Clear();
        haulingCenter.GetWorkplaceBehaviorsOrdered(entry.WorkplaceBehaviors);
        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            localOrderedCalls++;
        }
        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            Interlocked.Increment(ref _orderedCacheMisses);
        }
        return entry.WorkplaceBehaviors;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class OrderedWorkplaceEntry
    {
        public int Frame = -1;
        public readonly List<WorkplaceBehavior> WorkplaceBehaviors = new List<WorkplaceBehavior>(256);
    }
}
