using System;
using System.Diagnostics;
using System.Threading;
using Timberborn.BehaviorSystem;
using Timberborn.WorkSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WorkerRootMetricsBypass
{
    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _workplaceBehaviorChecks;
    private static long _transfers;
    private static long _communityService;
    private static long _releaseNow;
    private static long _releaseNextTick;
    private static long _stopwatchTicks;
    private static int _warningCount;

    public static bool TryDecide(
        Worker worker,
        WorkerWorkingHours workerWorkingHours,
        WorkRefuser workRefuser,
        BehaviorAgent workerBehaviorAgent,
        CommunityServiceBehavior communityServiceBehavior,
        BehaviorAgent agent,
        out Decision result)
    {
        result = Decision.ReleaseNow();
        if (!BenchmarkSettings.EnableWorkerRootMetricsBypass ||
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
        var localChecks = 0L;
        var localTransfers = 0L;
        var localCommunityService = 0L;
        var localReleaseNow = 0L;
        var localReleaseNextTick = 0L;
        try
        {
            if (worker is null ||
                workerWorkingHours is null ||
                workRefuser is null ||
                workerBehaviorAgent is null ||
                communityServiceBehavior is null)
            {
                localFallbacks++;
                return false;
            }

            if (workRefuser.RefusesWork)
            {
                localReleaseNow++;
                localHandled++;
                return true;
            }

            if (!worker.Employed || !workerWorkingHours.AreWorkingHours)
            {
                result = communityServiceBehavior.Decide(agent);
                localCommunityService++;
                localHandled++;
                return true;
            }

            var workplace = worker.Workplace;
            if (workplace is null)
            {
                localFallbacks++;
                return false;
            }

            if (workplace.Overstaffed)
            {
                worker.Unemploy();
                result = Decision.ReleaseNextTick();
                localReleaseNextTick++;
                localHandled++;
                return true;
            }

            foreach (var workplaceBehavior in workplace.WorkplaceBehaviors)
            {
                localChecks++;
                var decision = workplaceBehavior.Decide(workerBehaviorAgent);
                if (!decision.ShouldReleaseNow)
                {
                    result = Decision.TransferNow(workplaceBehavior, in decision);
                    localTransfers++;
                    localHandled++;
                    return true;
                }
            }

            localReleaseNow++;
            localHandled++;
            return true;
        }
        catch (Exception exception)
        {
            localFallbacks++;
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] WorkerRoot metrics bypass fallback: {exception}");
            }

            return false;
        }
        finally
        {
            if (recordMetrics)
            {
                Interlocked.Add(ref _handled, localHandled);
                Interlocked.Add(ref _fallbacks, localFallbacks);
                Interlocked.Add(ref _workplaceBehaviorChecks, localChecks);
                Interlocked.Add(ref _transfers, localTransfers);
                Interlocked.Add(ref _communityService, localCommunityService);
                Interlocked.Add(ref _releaseNow, localReleaseNow);
                Interlocked.Add(ref _releaseNextTick, localReleaseNextTick);
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
        var checks = Interlocked.Exchange(ref _workplaceBehaviorChecks, 0);
        var transfers = Interlocked.Exchange(ref _transfers, 0);
        var communityService = Interlocked.Exchange(ref _communityService, 0);
        var releaseNow = Interlocked.Exchange(ref _releaseNow, 0);
        var releaseNextTick = Interlocked.Exchange(ref _releaseNextTick, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        if (attempts == 0 && handled == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var checksPerHandled = handled > 0 ? (double)checks / handled : 0.0;
        Debug.Log(
            $"[T3MP] WorkerRootMetricsBypass aggregate={aggregateId}, enabled={BenchmarkSettings.EnableWorkerRootMetricsBypass}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, checks={checks}, checksPerHandled={checksPerHandled:F2}, transfers={transfers}, communityService={communityService}, releaseNow={releaseNow}, releaseNextTick={releaseNextTick}, ms={ToMilliseconds(ticks):F2}");
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }
}
