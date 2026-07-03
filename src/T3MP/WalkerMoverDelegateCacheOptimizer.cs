using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Timberborn.EnterableSystem;
using Timberborn.TickSystem;
using Timberborn.WalkingSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WalkerMoverDelegateCacheOptimizer
{
    private const string WalkingAnimation = "Walking";
    private static readonly ConditionalWeakTable<WalkerSpeedManager, Func<float>> SpeedDelegates = new ConditionalWeakTable<WalkerSpeedManager, Func<float>>();

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _exits;
    private static long _moves;
    private static long _delegateMisses;
    private static int _warningCount;

    public static bool TryMove(Enterer enterer, Walker walker, WalkerSpeedManager walkerSpeedManager, ITickService tickService)
    {
        if (!BenchmarkSettings.EnableWalkerMoverDelegateCacheOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            _attempts++;
        }
        try
        {
            if (enterer.IsInside)
            {
                enterer.Exit();
                if (recordMetrics)
                {
                    _exits++;
                    _handled++;
                }
                return true;
            }

            walker.PathFollower.MoveAlongPath(
                tickService.TickIntervalInSeconds,
                WalkingAnimation,
                SpeedDelegates.GetValue(walkerSpeedManager, CreateSpeedDelegate));
            if (recordMetrics)
            {
                _moves++;
                _handled++;
            }
            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                _fallbacks++;
            }
            if (_warningCount++ < 3)
            {
                Debug.LogWarning("[T3MP] WalkerMover delegate cache fallback: " + exception);
            }

            return false;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var attempts = _attempts;
        var handled = _handled;
        var fallbacks = _fallbacks;
        var exits = _exits;
        var moves = _moves;
        var delegateMisses = _delegateMisses;

        _attempts = 0;
        _handled = 0;
        _fallbacks = 0;
        _exits = 0;
        _moves = 0;
        _delegateMisses = 0;

        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] WalkerMoverDelegateCache aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, moves={6}, exits={7}, delegateMisses={8}",
            aggregateId,
            BenchmarkSettings.EnableWalkerMoverDelegateCacheOptimizer,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            fallbacks,
            moves,
            exits,
            delegateMisses));
    }

    public static void Reset()
    {
        _attempts = 0;
        _handled = 0;
        _fallbacks = 0;
        _exits = 0;
        _moves = 0;
        _delegateMisses = 0;
    }

    private static Func<float> CreateSpeedDelegate(WalkerSpeedManager walkerSpeedManager)
    {
        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            _delegateMisses++;
        }
        return walkerSpeedManager.GetWalkerSpeedAtCurrentPosition;
    }
}
