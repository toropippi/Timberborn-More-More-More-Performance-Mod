using System;
using System.Globalization;
using System.Threading;
using Timberborn.BonusSystem;
using Timberborn.CharacterModelSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WorkerWorkingSpeedOptimizer
{
    private const string WorkingSpeedBonusId = "WorkingSpeed";
    private const float Epsilon = 0.0001f;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _unchangedSkips;
    private static long _updates;
    private static int _warningCount;

    public static bool TryTick(
        BonusManager bonusManager,
        CharacterAnimator characterAnimator,
        ref float workingSpeedMultiplier)
    {
        if (!BenchmarkSettings.EnableWorkerWorkingSpeedNoRepeatSet ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        if (bonusManager is null || characterAnimator is null)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            return true;
        }

        try
        {
            var multiplier = bonusManager.Multiplier(WorkingSpeedBonusId);
            if (Mathf.Abs(multiplier - workingSpeedMultiplier) <= Epsilon)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _unchangedSkips);
                    Interlocked.Increment(ref _handled);
                }
                return false;
            }

            workingSpeedMultiplier = multiplier;
            characterAnimator.SetFloat(WorkingSpeedBonusId, multiplier);
            if (recordMetrics)
            {
                Interlocked.Increment(ref _updates);
                Interlocked.Increment(ref _handled);
            }
            return false;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] Worker working-speed optimizer fallback: " + exception);
            }

            return true;
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
        var unchangedSkips = Interlocked.Exchange(ref _unchangedSkips, 0);
        var updates = Interlocked.Exchange(ref _updates, 0);
        if (attempts == 0 && handled == 0 && fallbacks == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var skipRate = handled > 0 ? (double)unchangedSkips / handled : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] WorkerWorkingSpeedOptimizer aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, unchangedSkips={6}, updates={7}, skipRate={8:F3}",
            aggregateId,
            BenchmarkSettings.EnableWorkerWorkingSpeedNoRepeatSet,
            attempts,
            handled,
            handledRate,
            fallbacks,
            unchangedSkips,
            updates,
            skipRate));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _unchangedSkips, 0);
        Interlocked.Exchange(ref _updates, 0);
    }
}
