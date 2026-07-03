using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Timberborn.BehaviorSystem;
using Timberborn.Emptying;
using Timberborn.Fields;
using Timberborn.InventorySystem;
using Timberborn.Planting;
using Timberborn.Yielding;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class FarmHouseBehaviorDirectOptimizer
{
    private static FieldInfo? _farmHouseField;
    private static FieldInfo? _plantablePrioritizerField;
    private static FieldInfo? _planterBuildingStatusUpdaterField;
    private static FieldInfo? _inventoryField;
    private static FieldInfo? _emptyOutputWorkplaceBehaviorField;
    private static FieldInfo? _inRangeYieldersField;
    private static int _reflectionInitialized;
    private static int _exceptionLogs;

    private static long _handled;
    private static long _fallbacks;
    private static long _prioritizedAttempts;
    private static long _plantDecisions;
    private static long _harvestDecisions;
    private static long _emptyOutputDecisions;
    private static long _releaseDecisions;

    public static bool TryDecide(object instance, BehaviorAgent agent, ref Decision result)
    {
        if (!BenchmarkSettings.EnableFarmHouseBehaviorDirectOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!TryInitializeReflection(instance.GetType()))
        {
            Interlocked.Increment(ref _fallbacks);
            return true;
        }

        try
        {
            var farmHouse = (FarmHouse?)_farmHouseField?.GetValue(instance);
            var plantablePrioritizer = (PlantablePrioritizer?)_plantablePrioritizerField?.GetValue(instance);
            var statusUpdater = (PlanterBuildingStatusUpdater?)_planterBuildingStatusUpdaterField?.GetValue(instance);
            var inventory = (Inventory?)_inventoryField?.GetValue(instance);
            var emptyOutputBehavior = (EmptyOutputWorkplaceBehavior?)_emptyOutputWorkplaceBehaviorField?.GetValue(instance);
            var inRangeYielders = (InRangeYielders?)_inRangeYieldersField?.GetValue(instance);
            if (farmHouse is null ||
                plantablePrioritizer is null ||
                statusUpdater is null ||
                inventory is null ||
                emptyOutputBehavior is null ||
                inRangeYielders is null)
            {
                Interlocked.Increment(ref _fallbacks);
                return true;
            }

            var prioritizedSpec = plantablePrioritizer.PrioritizedPlantableSpec;
            if (prioritizedSpec is not null)
            {
                Interlocked.Increment(ref _prioritizedAttempts);
                var prioritizedDecision = Decide(agent, farmHouse, statusUpdater, inventory, emptyOutputBehavior, inRangeYielders, prioritizedSpec.TemplateName);
                if (!prioritizedDecision.ShouldReleaseNow)
                {
                    result = prioritizedDecision;
                    Interlocked.Increment(ref _handled);
                    return false;
                }
            }

            result = Decide(agent, farmHouse, statusUpdater, inventory, emptyOutputBehavior, inRangeYielders, null);
            Interlocked.Increment(ref _handled);
            return false;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Increment(ref _exceptionLogs) <= 3)
            {
                Debug.LogWarning("[T3MP] FarmHouse direct behavior fallback: " + exception);
            }

            return true;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var prioritizedAttempts = Interlocked.Exchange(ref _prioritizedAttempts, 0);
        var plantDecisions = Interlocked.Exchange(ref _plantDecisions, 0);
        var harvestDecisions = Interlocked.Exchange(ref _harvestDecisions, 0);
        var emptyOutputDecisions = Interlocked.Exchange(ref _emptyOutputDecisions, 0);
        var releaseDecisions = Interlocked.Exchange(ref _releaseDecisions, 0);
        if (handled == 0 && fallbacks == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] FarmHouseDirect aggregate={0}, handled={1}, fallbacks={2}, prioritizedAttempts={3}, plantDecisions={4}, harvestDecisions={5}, emptyOutputDecisions={6}, releaseDecisions={7}",
            aggregateId,
            handled,
            fallbacks,
            prioritizedAttempts,
            plantDecisions,
            harvestDecisions,
            emptyOutputDecisions,
            releaseDecisions));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _prioritizedAttempts, 0);
        Interlocked.Exchange(ref _plantDecisions, 0);
        Interlocked.Exchange(ref _harvestDecisions, 0);
        Interlocked.Exchange(ref _emptyOutputDecisions, 0);
        Interlocked.Exchange(ref _releaseDecisions, 0);
    }

    private static Decision Decide(
        BehaviorAgent agent,
        FarmHouse farmHouse,
        PlanterBuildingStatusUpdater statusUpdater,
        Inventory inventory,
        EmptyOutputWorkplaceBehavior emptyOutputBehavior,
        InRangeYielders inRangeYielders,
        string? prioritizedName)
    {
        var plantBehavior = agent.GetComponent<PlantBehavior>();
        var harvestStarter = agent.GetComponent<HarvestStarter>();
        if (farmHouse.PlantingPrioritized)
        {
            if (TryPlant(agent, plantBehavior, statusUpdater, out var plantDecision))
            {
                return plantDecision;
            }

            var harvestDecision = harvestStarter.StartHarvesting(inventory, inRangeYielders, prioritizedName);
            if (!harvestDecision.ShouldReleaseNow)
            {
                statusUpdater.DeactivateStatus();
                Interlocked.Increment(ref _harvestDecisions);
                return harvestDecision;
            }
        }
        else
        {
            var harvestDecision = harvestStarter.StartHarvesting(inventory, inRangeYielders, prioritizedName);
            if (!harvestDecision.ShouldReleaseNow)
            {
                statusUpdater.DeactivateStatus();
                Interlocked.Increment(ref _harvestDecisions);
                return harvestDecision;
            }

            var emptyOutputAgent = plantBehavior.GetComponent<BehaviorAgent>();
            var emptyOutputDecision = emptyOutputBehavior.Decide(emptyOutputAgent);
            if (!emptyOutputDecision.ShouldReleaseNow)
            {
                Interlocked.Increment(ref _emptyOutputDecisions);
                return Decision.TransferNow(emptyOutputBehavior, in emptyOutputDecision);
            }

            if (TryPlant(agent, plantBehavior, statusUpdater, out var plantDecision))
            {
                return plantDecision;
            }
        }

        statusUpdater.UpdateStatus();
        Interlocked.Increment(ref _releaseDecisions);
        return Decision.ReleaseNow();
    }

    private static bool TryPlant(
        BehaviorAgent agent,
        PlantBehavior plantBehavior,
        PlanterBuildingStatusUpdater statusUpdater,
        out Decision decision)
    {
        var plantDecision = plantBehavior.StartPlanting(agent);
        if (!plantDecision.ShouldReleaseNow)
        {
            statusUpdater.DeactivateStatus();
            decision = Decision.TransferNow(plantBehavior, in plantDecision);
            Interlocked.Increment(ref _plantDecisions);
            return true;
        }

        decision = default;
        return false;
    }

    private static bool TryInitializeReflection(Type farmHouseWorkplaceBehaviorType)
    {
        if (Volatile.Read(ref _reflectionInitialized) == 1)
        {
            return _farmHouseField is not null &&
                _plantablePrioritizerField is not null &&
                _planterBuildingStatusUpdaterField is not null &&
                _inventoryField is not null &&
                _emptyOutputWorkplaceBehaviorField is not null &&
                _inRangeYieldersField is not null;
        }

        _farmHouseField = farmHouseWorkplaceBehaviorType.GetField("_farmHouse", BindingFlags.Instance | BindingFlags.NonPublic);
        _plantablePrioritizerField = farmHouseWorkplaceBehaviorType.GetField("_plantablePrioritizer", BindingFlags.Instance | BindingFlags.NonPublic);
        _planterBuildingStatusUpdaterField = farmHouseWorkplaceBehaviorType.GetField("_planterBuildingStatusUpdater", BindingFlags.Instance | BindingFlags.NonPublic);
        _inventoryField = farmHouseWorkplaceBehaviorType.GetField("_inventory", BindingFlags.Instance | BindingFlags.NonPublic);
        _emptyOutputWorkplaceBehaviorField = farmHouseWorkplaceBehaviorType.GetField("_emptyOutputWorkplaceBehavior", BindingFlags.Instance | BindingFlags.NonPublic);
        _inRangeYieldersField = farmHouseWorkplaceBehaviorType.GetField("_inRangeYielders", BindingFlags.Instance | BindingFlags.NonPublic);
        Volatile.Write(ref _reflectionInitialized, 1);

        if (_farmHouseField is null ||
            _plantablePrioritizerField is null ||
            _planterBuildingStatusUpdaterField is null ||
            _inventoryField is null ||
            _emptyOutputWorkplaceBehaviorField is null ||
            _inRangeYieldersField is null)
        {
            Debug.LogWarning("[T3MP] FarmHouse direct behavior could not find expected fields.");
            return false;
        }

        return true;
    }
}
