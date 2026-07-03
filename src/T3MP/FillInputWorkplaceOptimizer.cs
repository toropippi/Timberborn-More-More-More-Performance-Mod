using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Timberborn.BehaviorSystem;
using Timberborn.Carrying;
using Timberborn.Emptying;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class FillInputWorkplaceOptimizer
{
    [ThreadStatic]
    private static List<GoodAmount>? _inputGoodsBuffer;

    private static readonly object LockObject = new object();
    private static InventoriesGetter? _getInventories;
    private static EmptiableGetter? _getEmptiable;
    private static int _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _enabledInventories;
    private static long _inputGoods;
    private static long _carryAttempts;
    private static long _startedCarrying;
    private static long _releaseNow;
    private static long _stopwatchTicks;

    public static bool TryDecide(object instance, BehaviorAgent agent, ref Decision result)
    {
        if (!BenchmarkSettings.EnableFillInputWorkplaceOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _attempts);
        var startTimestamp = BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        var localHandled = 0L;
        var localFallbacks = 0L;
        var localEnabledInventories = 0L;
        var localInputGoods = 0L;
        var localCarryAttempts = 0L;
        var localStartedCarrying = 0L;
        var localReleaseNow = 0L;

        try
        {
            if (!EnsureInitialized(instance.GetType()) ||
                _getInventories?.Invoke(instance) is not { } inventories ||
                _getEmptiable?.Invoke(instance) is not { } emptiable)
            {
                localFallbacks++;
                return false;
            }

            if (emptiable == null || !emptiable.IsMarkedForEmptying)
            {
                var carrierInventoryFinder = agent.GetComponent<CarrierInventoryFinder>();
                foreach (var inventory in inventories.EnabledInventories)
                {
                    localEnabledInventories++;
                    if (TryStartCarrying(inventory, carrierInventoryFinder, ref localInputGoods, ref localCarryAttempts))
                    {
                        result = Decision.ReleaseNextTick();
                        localStartedCarrying++;
                        localHandled++;
                        return true;
                    }
                }
            }

            result = Decision.ReleaseNow();
            localReleaseNow++;
            localHandled++;
            return true;
        }
        catch (Exception exception)
        {
            localFallbacks++;
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] FillInput optimizer fallback: " + exception);
            }

            return false;
        }
        finally
        {
            _inputGoodsBuffer?.Clear();
            Interlocked.Add(ref _handled, localHandled);
            Interlocked.Add(ref _fallbacks, localFallbacks);
            Interlocked.Add(ref _enabledInventories, localEnabledInventories);
            Interlocked.Add(ref _inputGoods, localInputGoods);
            Interlocked.Add(ref _carryAttempts, localCarryAttempts);
            Interlocked.Add(ref _startedCarrying, localStartedCarrying);
            Interlocked.Add(ref _releaseNow, localReleaseNow);
            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                Interlocked.Add(ref _stopwatchTicks, Stopwatch.GetTimestamp() - startTimestamp);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var enabledInventories = Interlocked.Exchange(ref _enabledInventories, 0);
        var inputGoods = Interlocked.Exchange(ref _inputGoods, 0);
        var carryAttempts = Interlocked.Exchange(ref _carryAttempts, 0);
        var startedCarrying = Interlocked.Exchange(ref _startedCarrying, 0);
        var releaseNow = Interlocked.Exchange(ref _releaseNow, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] FillInputWorkplaceOptimizer aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, enabledInventories={6}, inputGoods={7}, carryAttempts={8}, startedCarrying={9}, releaseNow={10}, ms={11:F2}",
            aggregateId,
            BenchmarkSettings.EnableFillInputWorkplaceOptimizer,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            fallbacks,
            enabledInventories,
            inputGoods,
            carryAttempts,
            startedCarrying,
            releaseNow,
            ToMilliseconds(ticks)));
    }

    private static bool TryStartCarrying(
        Inventory inventory,
        CarrierInventoryFinder carrierInventoryFinder,
        ref long localInputGoods,
        ref long localCarryAttempts)
    {
        var inputGoods = _inputGoodsBuffer ??= new List<GoodAmount>(8);
        inputGoods.Clear();
        foreach (var goodId in inventory.InputGoods)
        {
            inputGoods.Add(new GoodAmount(goodId, inventory.UnreservedAmountInStock(goodId)));
            localInputGoods++;
        }

        inputGoods.Sort(CompareGoodAmounts);
        for (var index = 0; index < inputGoods.Count; index++)
        {
            localCarryAttempts++;
            if (carrierInventoryFinder.TryCarryFromAnyInventory(inputGoods[index].GoodId, inventory))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EnsureInitialized(Type fillInputWorkplaceBehaviorType)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return _getInventories is not null && _getEmptiable is not null;
        }

        lock (LockObject)
        {
            if (_initialized == 1)
            {
                return _getInventories is not null && _getEmptiable is not null;
            }

            var inventoriesField = fillInputWorkplaceBehaviorType.GetField("_inventories", BindingFlags.Instance | BindingFlags.NonPublic);
            var emptiableField = fillInputWorkplaceBehaviorType.GetField("_emptiable", BindingFlags.Instance | BindingFlags.NonPublic);
            if (inventoriesField is not null)
            {
                _getInventories = CreateFieldGetter<InventoriesGetter>(inventoriesField, typeof(Inventories), fillInputWorkplaceBehaviorType);
            }

            if (emptiableField is not null)
            {
                _getEmptiable = CreateFieldGetter<EmptiableGetter>(emptiableField, typeof(Emptiable), fillInputWorkplaceBehaviorType);
            }

            _initialized = 1;
            if (_getInventories is null || _getEmptiable is null)
            {
                Debug.LogWarning("[T3MP] FillInput optimizer could not find expected fields.");
            }

            return _getInventories is not null && _getEmptiable is not null;
        }
    }

    private static TDelegate? CreateFieldGetter<TDelegate>(FieldInfo field, Type returnType, Type declaringType)
        where TDelegate : Delegate
    {
        try
        {
            var method = new DynamicMethod(
                string.Concat("T3MP_FillInput_Get_", field.Name),
                returnType,
                new[] { typeof(object) },
                typeof(FillInputWorkplaceOptimizer).Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to create FillInput field getter: " + exception.Message);
            return null;
        }
    }

    private static int CompareGoodAmounts(GoodAmount left, GoodAmount right)
    {
        var amountComparison = left.Amount.CompareTo(right.Amount);
        return amountComparison != 0
            ? amountComparison
            : string.Compare(left.GoodId, right.GoodId, StringComparison.Ordinal);
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private delegate Inventories InventoriesGetter(object instance);

    private delegate Emptiable EmptiableGetter(object instance);
}
