using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Timberborn.Carrying;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class CarryAmountCalculatorOptimizer
{
    private static readonly FieldInfo? GoodServiceField =
        typeof(CarryAmountCalculator).GetField("_goodService", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object GoodServiceCacheLock = new object();
    private static readonly Dictionary<CarryAmountCalculator, IGoodService> GoodServiceCache =
        new Dictionary<CarryAmountCalculator, IGoodService>(CarryAmountCalculatorReferenceComparer.Instance);
    private static CarryAmountCalculator? _lastCalculator;
    private static IGoodService? _lastGoodService;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _zeroResults;
    private static long _positiveResults;
    private static long _stopwatchTicks;
    private static int _warningCount;

    public static GoodAmount AmountToCarry(
        CarryAmountCalculator calculator,
        int liftingCapacity,
        GoodAmount good,
        IAmountProvider input)
    {
        if (TryGetGoodService(calculator, out var goodService))
        {
            var result = default(GoodAmount);
            if (TryAmountToCarry(goodService, liftingCapacity, good, input, ref result))
            {
                return result;
            }
        }

        return calculator.AmountToCarry(liftingCapacity, good, input);
    }

    public static bool TryAmountToCarry(
        IGoodService goodService,
        int liftingCapacity,
        GoodAmount good,
        IAmountProvider input,
        ref GoodAmount result)
    {
        if (!BenchmarkSettings.EnableCarryAmountCalculatorOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            _attempts++;
        }
        var startTimestamp = recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        try
        {
            var goodId = good.GoodId;
            var weight = goodService.GetGood(goodId).Weight;
            var maxByWeight = Math.Max(liftingCapacity / weight, 1);
            var amount = Mathf.Min(maxByWeight, good.Amount, input.UnreservedCapacity(goodId));
            result = new GoodAmount(goodId, amount);
            if (recordMetrics && amount > 0)
            {
                _positiveResults++;
            }
            else if (recordMetrics)
            {
                _zeroResults++;
            }

            if (recordMetrics)
            {
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
                Debug.LogWarning("[T3MP] CarryAmount calculator optimizer fallback: " + exception);
            }

            return false;
        }
        finally
        {
            if (recordMetrics && BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                _stopwatchTicks += Stopwatch.GetTimestamp() - startTimestamp;
            }
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
        var zeroResults = _zeroResults;
        var positiveResults = _positiveResults;
        var stopwatchTicks = _stopwatchTicks;

        _attempts = 0;
        _handled = 0;
        _fallbacks = 0;
        _zeroResults = 0;
        _positiveResults = 0;
        _stopwatchTicks = 0;

        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] CarryAmountCalculator aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, positive={6}, zero={7}, ms={8:F2}",
            aggregateId,
            BenchmarkSettings.EnableCarryAmountCalculatorOptimizer,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            fallbacks,
            positiveResults,
            zeroResults,
            ToMilliseconds(stopwatchTicks)));
    }

    public static void Reset()
    {
        _attempts = 0;
        _handled = 0;
        _fallbacks = 0;
        _zeroResults = 0;
        _positiveResults = 0;
        _stopwatchTicks = 0;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static bool TryGetGoodService(CarryAmountCalculator calculator, out IGoodService goodService)
    {
        if (ReferenceEquals(_lastCalculator, calculator) && _lastGoodService is { } cachedGoodService)
        {
            goodService = cachedGoodService;
            return true;
        }

        lock (GoodServiceCacheLock)
        {
            if (GoodServiceCache.TryGetValue(calculator, out goodService!))
            {
                _lastCalculator = calculator;
                _lastGoodService = goodService;
                return true;
            }
        }

        if (GoodServiceField?.GetValue(calculator) is not IGoodService service)
        {
            goodService = null!;
            return false;
        }

        lock (GoodServiceCacheLock)
        {
            GoodServiceCache[calculator] = service;
        }

        _lastCalculator = calculator;
        _lastGoodService = service;
        goodService = service;
        return true;
    }

    private sealed class CarryAmountCalculatorReferenceComparer : IEqualityComparer<CarryAmountCalculator>
    {
        public static readonly CarryAmountCalculatorReferenceComparer Instance = new CarryAmountCalculatorReferenceComparer();

        public bool Equals(CarryAmountCalculator? x, CarryAmountCalculator? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(CarryAmountCalculator obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
