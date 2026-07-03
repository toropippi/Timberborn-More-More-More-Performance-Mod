using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Timberborn.Effects;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.NeedBehaviorSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class InventoryNeedGoodOptimizer
{
    private static readonly object LockObject = new object();
    private static readonly Dictionary<string, InstantEffect[]> EffectsByGoodId = new Dictionary<string, InstantEffect[]>(StringComparer.Ordinal);
    private static FieldInfo? _goodServiceField;
    private static int _reflectionInitialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _candidates;
    private static long _effectCacheHits;
    private static long _effectCacheMisses;
    private static long _positiveResults;
    private static long _zeroResults;
    private static long _stopwatchTicks;

    public static bool TryFindMostOptimalGood(object instance, Appraiser appraiser, Inventory inventory, ref GoodAmount result)
    {
        if (!BenchmarkSettings.EnableInventoryNeedGoodOptimizer ||
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
        var localCandidates = 0L;
        try
        {
            if (!TryInitializeReflection(instance.GetType()) ||
                _goodServiceField?.GetValue(instance) is not IGoodService goodService)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            var bestPoints = 0f;
            var bestGood = default(GoodAmount);
            foreach (var stock in inventory.UnreservedTakeableStock())
            {
                if (recordMetrics)
                {
                    localCandidates++;
                }
                var goodId = stock.GoodId;
                var effects = GetEffects(goodService, goodId);
                if (effects.Length == 0)
                {
                    continue;
                }

                var points = appraiser.AppraiseEffects(effects);
                if (points > bestPoints)
                {
                    bestPoints = points;
                    bestGood = new GoodAmount(goodId, 1);
                }
            }

            result = bestGood;
            if (recordMetrics && bestGood.Amount > 0)
            {
                Interlocked.Increment(ref _positiveResults);
            }
            else if (recordMetrics)
            {
                Interlocked.Increment(ref _zeroResults);
            }

            if (recordMetrics)
            {
                Interlocked.Increment(ref _handled);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] InventoryNeed good optimizer fallback: " + exception);
            }

            return false;
        }
        finally
        {
            if (recordMetrics)
            {
                Interlocked.Add(ref _candidates, localCandidates);
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
        var candidates = Interlocked.Exchange(ref _candidates, 0);
        var cacheHits = Interlocked.Exchange(ref _effectCacheHits, 0);
        var cacheMisses = Interlocked.Exchange(ref _effectCacheMisses, 0);
        var positive = Interlocked.Exchange(ref _positiveResults, 0);
        var zero = Interlocked.Exchange(ref _zeroResults, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var avgCandidates = handled > 0 ? (double)candidates / handled : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] InventoryNeedGood aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, candidates={6}, avgCandidates={7:F2}, positive={8}, zero={9}, effectCacheHits={10}, effectCacheMisses={11}, ms={12:F2}",
            aggregateId,
            BenchmarkSettings.EnableInventoryNeedGoodOptimizer,
            attempts,
            handled,
            handledRate,
            fallbacks,
            candidates,
            avgCandidates,
            positive,
            zero,
            cacheHits,
            cacheMisses,
            ToMilliseconds(ticks)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _candidates, 0);
        Interlocked.Exchange(ref _effectCacheHits, 0);
        Interlocked.Exchange(ref _effectCacheMisses, 0);
        Interlocked.Exchange(ref _positiveResults, 0);
        Interlocked.Exchange(ref _zeroResults, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
    }

    private static InstantEffect[] GetEffects(IGoodService goodService, string goodId)
    {
        lock (LockObject)
        {
            if (EffectsByGoodId.TryGetValue(goodId, out var effects))
            {
                if (BenchmarkSettings.EnableHotOptimizerMetrics)
                {
                    Interlocked.Increment(ref _effectCacheHits);
                }
                return effects;
            }

            if (BenchmarkSettings.EnableHotOptimizerMetrics)
            {
                Interlocked.Increment(ref _effectCacheMisses);
            }
            var specs = goodService.GetGood(goodId).ConsumptionEffects;
            var built = new InstantEffect[specs.Length];
            for (var index = 0; index < specs.Length; index++)
            {
                built[index] = InstantEffect.FromSpec(specs[index], 1);
            }

            EffectsByGoodId[goodId] = built;
            return built;
        }
    }

    private static bool TryInitializeReflection(Type inventoryNeedBehaviorType)
    {
        if (Volatile.Read(ref _reflectionInitialized) == 1)
        {
            return _goodServiceField is not null;
        }

        lock (LockObject)
        {
            if (_reflectionInitialized == 1)
            {
                return _goodServiceField is not null;
            }

            _goodServiceField = inventoryNeedBehaviorType.GetField("_goodService", BindingFlags.Instance | BindingFlags.NonPublic);
            _reflectionInitialized = 1;
            if (_goodServiceField is null)
            {
                Debug.LogWarning("[T3MP] InventoryNeed good optimizer could not find _goodService.");
            }

            return _goodServiceField is not null;
        }
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

}
