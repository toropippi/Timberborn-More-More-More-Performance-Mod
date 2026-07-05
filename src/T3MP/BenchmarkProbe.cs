using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BehaviorSystem;
using Timberborn.BonusSystem;
using Timberborn.Carrying;
using Timberborn.CharacterModelSystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Timberborn.NeedBehaviorSystem;
using Timberborn.NeedSystem;
using Timberborn.ReservableSystem;
using Timberborn.CharacterMovementSystem;
using Timberborn.EnterableSystem;
using Timberborn.TickSystem;
using Timberborn.WalkingSystem;
using Timberborn.WorkSystem;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using Timberborn.Fields;
using Timberborn.Planting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class BenchmarkProbe
{
    private static readonly string[] NavigationMethodNames =
    {
        "DestinationIsReachableUnlimitedRange",
        "FindRoadPath",
        "FindTerrainPath",
        "FindPathUnlimitedRange"
    };

    private static int _installed;
    private static int _pathFollowerNreGuardLogs;
    private static int _characterRotatorNreGuardLogs;
    private static int _movementAnimatorNreGuardLogs;
    private static readonly object MovementAnimatorGuardLock = new object();
    private static readonly HashSet<int> BadMovementAnimators = new HashSet<int>();
    private static int[] BadMovementAnimatorSnapshot = Array.Empty<int>();

    [ThreadStatic]
    private static int _yielderFinderDepth;

    public static void TryInstall()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        try
        {
            var harmonyType = FindType("HarmonyLib.Harmony");
            var harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
            if (harmonyType is null || harmonyMethodType is null)
            {
                Debug.LogWarning("[T3MP] Harmony is not loaded. Benchmark probe was not installed.");
                return;
            }

            var harmony = Activator.CreateInstance(harmonyType, "local.gpupathinginvestigation.benchmarkprobe");
            var patchMethod = FindPatchMethod(harmonyType, harmonyMethodType);
            if (harmony is null || patchMethod is null)
            {
                Debug.LogWarning("[T3MP] Harmony patch API was not found. Benchmark probe was not installed.");
                return;
            }

            var patchedYielderMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchYielderFinder(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedFarmMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchHarvestStarter(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedFarmHouseMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchFarmHouseBehaviorDirectOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedPlantingSpotMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchPlantingSpotFinder(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedLumberjackMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchLumberjackFindCuttable(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedGatherMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchGatherWorkplaceFindYielder(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedInRangeMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchInRangeYielders(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedNavigationMethods = BenchmarkSettings.EnableRuntimeProbes &&
                (BenchmarkSettings.EnableNavigationProfiler || BenchmarkSettings.EnableWalkerDistanceCache)
                    ? PatchNavigationService(harmony, harmonyType, harmonyMethodType, patchMethod)
                    : 0;
            var patchedWalkerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWalkerTravelTime(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWalkerMoverMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWalkerMoverDelegateCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedPathFollowerNoAnimationMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchPathFollowerNoAnimationFastMove(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedPathFollowerProfilerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchPathFollowerProfiler(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedAnimatedPathFollowerHorizontalMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchAnimatedPathFollowerHorizontalOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedCarryAmountMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchCarryAmountCalculatorOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedLiftingCapacityMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchGoodCarrierLiftingCapacityCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedNeedBehaviorMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchNeedBehaviorDecision(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedNeedManagerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchNeedManagerDirectCriticalState(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedNeedManagerFastTickMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchNeedManagerFastTick(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedBeaverDecisionMethods = BenchmarkSettings.EnableRuntimeProbes &&
                (BenchmarkSettings.EnableBeaverDecisionFrequencySampler || BenchmarkSettings.EnableNoActionCooldown)
                    ? PatchBeaverNeedDecisionFrequency(harmony, harmonyType, harmonyMethodType, patchMethod)
                    : 0;
            var patchedReservableMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchReservableChanges(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedHaulCandidateMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchHaulCandidateOrderCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedHaulNoActionMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchHaulNoActionFrameCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWorkplaceNoActionMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWorkplaceNoActionFrameCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedInventoryStockMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchInventoryStockDistanceCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedInventoryNeedGoodMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchInventoryNeedGoodOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedInventoryCapacityMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchInventoryCapacityDistanceCache(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedInventoryCapacityVectorProfilerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchInventoryCapacityVectorProfiler(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedFillInputMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchFillInputWorkplaceOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWaitInsideMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWaitInsideIdlyOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWorkerRootMetricsMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWorkerRootMetricsBypass(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWorkerWorkingSpeedMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWorkerWorkingSpeedOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedBehaviorManagerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchBehaviorManagerProcessOptimizer(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedExecutorTickProfilerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchExecutorTickProfiler(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedDistrictResourceCounterMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchDistrictResourceCounterThrottle(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWaterObjectServiceMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWaterObjectServiceThrottle(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedWaterObjectServiceFastSkipMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchWaterObjectServiceFastSkip(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedThreadSafeWaterMapTickMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchThreadSafeWaterMapTickThrottle(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedThreadSafeWaterFlowMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchThreadSafeWaterFlowDirectionThrottle(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedRangedEffectSubjectThrottleMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchRangedEffectSubjectThrottle(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedContaminationApplierThrottleMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchContaminationApplierThrottle(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedTickDispatchMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchTickDispatchOptimizer(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedFlatDispatchHookMethods = BenchmarkSettings.EnableRuntimeProbes && BenchmarkSettings.EnableTickDispatchOptimizer && BenchmarkSettings.EnableFlatTickDispatch
                ? PatchFlatTickDispatchHooks(harmony, harmonyMethodType, patchMethod)
                : 0;
            var patchedSmoothPacingMethods = BenchmarkSettings.EnableRuntimeProbes && BenchmarkSettings.EnableSmoothFramePacing
                ? PatchSmoothFramePacing(harmony, harmonyMethodType, patchMethod)
                : 0;
            var patchedEventBusFastDelegateMethods = BenchmarkSettings.EnableRuntimeProbes && BenchmarkSettings.EnableEventBusFastDelegates
                ? PatchEventBusFastDelegates(harmony, harmonyMethodType, patchMethod)
                : 0;
            var patchedThrottlerRemovalMethods = BenchmarkSettings.EnableRuntimeProbes && BenchmarkSettings.EnableGameSpeedThrottlerRemoval
                ? PatchGameSpeedThrottlerRemoval(harmony, harmonyMethodType, patchMethod)
                : 0;
            var patchedInvisiblePoseSkipMethods = BenchmarkSettings.EnableRuntimeProbes && BenchmarkSettings.EnableInvisibleAnimatorPoseSkip
                ? PatchInvisibleAnimatorPoseSkip(harmony, harmonyMethodType, patchMethod)
                : 0;
            var patchedMenuBlackoutCancelMethods = BenchmarkSettings.EnableRuntimeProbes && BenchmarkSettings.EnableOptimizedRenderBlackout && BenchmarkSettings.EnableRenderBlackoutToggleKey
                ? PatchOptionsMenuBlackoutCancel(harmony, harmonyMethodType, patchMethod)
                : 0;
            var patchedTopologyUiProbeMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchTopologyUiProbe(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedTopologyUiScenarioMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchTopologyUiScenario(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedTopologyUiOptimizerMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchTopologyUiOptimizers(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedDecideSplitProbeMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchDecideSplitProbe(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedSpawnSplitProbeMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchSpawnSplitProbe(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedSentinelTemplateMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchSentinelTemplateInjection(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedRegistryFastRemoveMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchRegistryFastRemoves(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedRoadReachabilityCacheMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchRoadReachabilityCache(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedEmptyInventoriesFastPathMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchEmptyInventoriesFastPath(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedNavMeshInvalidationMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchNavMeshUpdateInvalidation(harmony, harmonyMethodType, patchMethod) : 0;
            var patchedTickMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchTickBuckets(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedFpsCounterMethods = BenchmarkSettings.EnableRuntimeProbes ? PatchFramesPerSecondCounter(harmony, harmonyType, harmonyMethodType, patchMethod) : 0;
            var patchedSpeedManagerMethods = PatchSpeedManagerProbe(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedTimeSpeedButtonMethods = PatchTimeSpeedButtonGroupProbe(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedLoadProfilerMethods = PatchLoadProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedLoadComponentProfilerMethods = PatchLoadComponentProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedLoadSingletonProfilerMethods = PatchLoadSingletonProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedLoadEventProfilerMethods = PatchLoadEventProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedLoadHotspotProfilerMethods = PatchLoadHotspotProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedMechanicalGraphLoadBatcherMethods = PatchMechanicalGraphLoadBatcher(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedStutterDetailMethods = PatchStutterDetailProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedRangedEffectSubjectProfilerMethods = PatchRangedEffectSubjectProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedRuntimeHotspotMethods = PatchRuntimeHotspotProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedMainLoopProfilerMethods = PatchMainLoopProfiler(harmony, harmonyType, harmonyMethodType, patchMethod);
            var patchedAnimatorThrottleMethods = PatchAnimatorRegistry(harmony, harmonyMethodType, patchMethod);
            var patchedDefaultMechanicalAnimatorMethods = PatchDefaultMechanicalAnimatorOptimizer(harmony, harmonyMethodType, patchMethod);
            var patchedVisualThrottleMethods = PatchVisualUpdateThrottles(harmony, harmonyMethodType, patchMethod);
            var patchedStatusAggregatorMethods = PatchStatusAggregatorThrottle(harmony, harmonyMethodType, patchMethod);
            var patchedTickVisualThrottleMethods = PatchTickVisualSingletonThrottles(harmony, harmonyMethodType, patchMethod);
            var patchedUnattendedVisualSuppressionMethods = PatchUnattendedVisualSuppression(harmony, harmonyMethodType, patchMethod);
            var patchedSoundListenerMethods = PatchSoundListenerStaticCameraOptimizer(harmony, harmonyMethodType, patchMethod);
            var patchedPathFollowerGuardMethods = PatchMovementNreGuard(harmony, harmonyType, harmonyMethodType, patchMethod);

            Debug.Log($"[T3MP] Benchmark probe installed. YielderFinder={patchedYielderMethods}, HarvestStarter={patchedFarmMethods}, FarmHouseDirect={patchedFarmHouseMethods}, PlantingSpot={patchedPlantingSpotMethods}, Lumberjack={patchedLumberjackMethods}, Gather={patchedGatherMethods}, InRangeYielders={patchedInRangeMethods}, NavigationService={patchedNavigationMethods}, Walker={patchedWalkerMethods}, WalkerMover={patchedWalkerMoverMethods}, PathFollowerNoAnimation={patchedPathFollowerNoAnimationMethods}, PathFollowerProfiler={patchedPathFollowerProfilerMethods}, AnimatedPathFollowerHorizontal={patchedAnimatedPathFollowerHorizontalMethods}, CarryAmount={patchedCarryAmountMethods}, LiftingCapacity={patchedLiftingCapacityMethods}, NeedBehavior={patchedNeedBehaviorMethods}, NeedManager={patchedNeedManagerMethods}, NeedManagerFastTick={patchedNeedManagerFastTickMethods}, BeaverDecisionFrequency={patchedBeaverDecisionMethods}, Reservable={patchedReservableMethods}, HaulCandidateOrder={patchedHaulCandidateMethods}, HaulNoAction={patchedHaulNoActionMethods}, WorkplaceNoAction={patchedWorkplaceNoActionMethods}, InventoryStock={patchedInventoryStockMethods}, InventoryNeedGood={patchedInventoryNeedGoodMethods}, InventoryCapacity={patchedInventoryCapacityMethods}, InventoryCapacityVectorProfiler={patchedInventoryCapacityVectorProfilerMethods}, FillInput={patchedFillInputMethods}, WaitInside={patchedWaitInsideMethods}, WorkerRootMetrics={patchedWorkerRootMetricsMethods}, WorkerWorkingSpeed={patchedWorkerWorkingSpeedMethods}, BehaviorManager={patchedBehaviorManagerMethods}, ExecutorTickProfiler={patchedExecutorTickProfilerMethods}, DistrictResourceCounter={patchedDistrictResourceCounterMethods}, WaterObjectService={patchedWaterObjectServiceMethods}, WaterObjectServiceFastSkip={patchedWaterObjectServiceFastSkipMethods}, ThreadSafeWaterMapTick={patchedThreadSafeWaterMapTickMethods}, ThreadSafeWaterFlow={patchedThreadSafeWaterFlowMethods}, RangedEffectSubjectThrottle={patchedRangedEffectSubjectThrottleMethods}, ContaminationApplierThrottle={patchedContaminationApplierThrottleMethods}, TickDispatch={patchedTickDispatchMethods}, EmptyInvFast={patchedEmptyInventoriesFastPathMethods}, NavMeshInvalidate={patchedNavMeshInvalidationMethods}, TickBuckets={patchedTickMethods}, FpsCounter={patchedFpsCounterMethods}, SpeedManager={patchedSpeedManagerMethods}, TimeSpeedButtonGroup={patchedTimeSpeedButtonMethods}, LoadProfiler={patchedLoadProfilerMethods}, LoadComponentProfiler={patchedLoadComponentProfilerMethods}, LoadSingletonProfiler={patchedLoadSingletonProfilerMethods}, LoadEventProfiler={patchedLoadEventProfilerMethods}, LoadHotspotProfiler={patchedLoadHotspotProfilerMethods}, MechanicalGraphLoadBatcher={patchedMechanicalGraphLoadBatcherMethods}, StutterDetail={patchedStutterDetailMethods}, RangedEffectSubjectProfiler={patchedRangedEffectSubjectProfilerMethods}, RuntimeHotspot={patchedRuntimeHotspotMethods}, MainLoopProfiler={patchedMainLoopProfilerMethods}, AnimatorThrottle={patchedAnimatorThrottleMethods}, DefaultMechanicalAnimator={patchedDefaultMechanicalAnimatorMethods}, VisualThrottle={patchedVisualThrottleMethods}, StatusAggregator={patchedStatusAggregatorMethods}, TickVisualThrottle={patchedTickVisualThrottleMethods}, UnattendedVisualSuppression={patchedUnattendedVisualSuppressionMethods}, SoundListener={patchedSoundListenerMethods}, PathFollowerGuard={patchedPathFollowerGuardMethods}, MenuBlackoutCancel={patchedMenuBlackoutCancelMethods}, TopoUiProbe={patchedTopologyUiProbeMethods}, TopoUiScenario={patchedTopologyUiScenarioMethods}, TopoUiOptimizer={patchedTopologyUiOptimizerMethods}");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] Failed to install benchmark probe: {exception}");
        }
    }

    private static int PatchYielderFinder(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.YielderFinding.YielderFinder");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Benchmark target type was not found: Timberborn.YielderFinding.YielderFinder");
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordYielderCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordYielderReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Benchmark yielder patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => method.Name == "FindLivingYielderWithoutAccessible" || method.Name == "FindYielderWithAccessible"))
        {
            patchMethod.Invoke(harmony, new object?[] { method, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
            patched++;
        }

        return patched;
    }

    private static int PatchHarvestStarter(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.Fields.HarvestStarter");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Benchmark target type was not found: Timberborn.Fields.HarvestStarter");
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordFarmCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordFarmReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Benchmark farm patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => method.Name == "FindYielder"))
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 3 &&
                parameters[0].ParameterType.FullName == "Timberborn.InventorySystem.Inventory" &&
                parameters[1].ParameterType.FullName == "Timberborn.Yielding.InRangeYielders" &&
                parameters[2].ParameterType == typeof(string))
            {
                patchMethod.Invoke(harmony, new object?[] { method, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
                patched++;
            }
        }

        return patched;
    }

    private static int PatchFarmHouseBehaviorDirectOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableFarmHouseBehaviorDirectOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Fields.FarmHouseWorkplaceBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseFarmHouseBehaviorDirectOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] FarmHouse direct behavior target was not found.");
            return 0;
        }

        var behaviorAgentType = FindType("Timberborn.BehaviorSystem.BehaviorAgent");
        if (behaviorAgentType is null)
        {
            Debug.LogWarning("[T3MP] FarmHouse direct behavior BehaviorAgent type was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Decide" || method.ReturnType != typeof(Decision) || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == behaviorAgentType;
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] FarmHouseWorkplaceBehavior.Decide(BehaviorAgent) was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchPlantingSpotFinder(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnablePlantingSpotFinderOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Planting.PlantingSpotFinder") ??
            TryLoadAssemblyAndFindType("Timberborn.Planting", "Timberborn.Planting.PlantingSpotFinder");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UsePlantingSpotFinderOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] PlantingSpotFinder optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "FindClosest" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Vector3);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] PlantingSpotFinder.FindClosest(Vector3) method was not found.");
            return 0;
        }

        PlantingSpotFinderOptimizer.Initialize(targetType);
        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var patched = TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;

        var inRangeType = FindType("Timberborn.Planting.InRangePlantingCoordinates") ??
            TryLoadAssemblyAndFindType("Timberborn.Planting", "Timberborn.Planting.InRangePlantingCoordinates");
        var invalidationPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordPlantingCoordinatesChanged), BindingFlags.Static | BindingFlags.NonPublic);
        if (inRangeType is null || invalidationPostfix is null)
        {
            return patched;
        }

        var invalidationHarmonyMethod = Activator.CreateInstance(harmonyMethodType, invalidationPostfix);
        foreach (var methodName in new[] { "OnPlantingCoordinatesSet", "OnPlantingCoordinatesUnset", "OnRangeChanged" })
        {
            var method = inRangeType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate => candidate.Name == methodName && !candidate.ContainsGenericParameters);
            if (method is not null && TryPatch(harmony, patchMethod, method, null, invalidationHarmonyMethod))
            {
                patched++;
            }
        }

        return patched;
    }

    private static int PatchLumberjackFindCuttable(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableLumberjackYielderOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Forestry.LumberjackFlagWorkplaceBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.Forestry", "Timberborn.Forestry.LumberjackFlagWorkplaceBehavior");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Lumberjack optimizer target type was not found.");
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseLumberjackYielderOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] Lumberjack optimizer patch method was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "FindCuttable" ||
                    method.ReturnType != typeof(YielderSearchResult) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] Lumberjack FindCuttable(int) method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var patched = TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;

        var treeCuttingAreaType = FindType("Timberborn.Forestry.TreeCuttingArea") ??
            TryLoadAssemblyAndFindType("Timberborn.Forestry", "Timberborn.Forestry.TreeCuttingArea");
        var invalidationPostfix = typeof(BenchmarkProbe).GetMethod(nameof(InvalidateLumberjackAreaCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (treeCuttingAreaType is null || invalidationPostfix is null)
        {
            return patched;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, invalidationPostfix);
        foreach (var methodName in new[] { "AddYielder", "RemoveYielder", "OnTerrainHeightChanged" })
        {
            foreach (var method in treeCuttingAreaType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(method => method.Name == methodName))
            {
                if (TryPatch(harmony, patchMethod, method, null, postfixHarmonyMethod))
                {
                    patched++;
                }
            }
        }

        return patched;
    }

    private static int PatchGatherWorkplaceFindYielder(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableGatherWorkplaceOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Gathering.GatherWorkplaceBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.Gathering", "Timberborn.Gathering.GatherWorkplaceBehavior");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Gather workplace optimizer target type was not found.");
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseGatherWorkplaceOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] Gather workplace optimizer patch method was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "FindYielder" ||
                    method.ReturnType != typeof(YielderSearchResult) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(Accessible) &&
                    parameters[1].ParameterType == typeof(int) &&
                    parameters[2].ParameterType == typeof(string);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] Gather FindYielder(Accessible,int,string) method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchInRangeYielders(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.Yielding.InRangeYielders");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Benchmark target type was not found: Timberborn.Yielding.InRangeYielders");
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordInRangeCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordInRangeReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Benchmark InRangeYielders patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => method.Name == "GetYielders"))
        {
            patchMethod.Invoke(harmony, new object?[] { method, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
            patched++;
        }

        return patched;
    }

    private static int PatchNavigationService(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.Navigation.NavigationService");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Benchmark target type was not found: Timberborn.Navigation.NavigationService");
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordNavigationCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordNavigationReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Benchmark navigation patch method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => NavigationMethodNames.Contains(method.Name, StringComparer.Ordinal)))
        {
            patchMethod.Invoke(harmony, new object?[] { method, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
            patched++;
        }

        return patched;
    }

    private static int PatchWalkerTravelTime(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.WalkingSystem.Walker");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Benchmark target type was not found: Timberborn.WalkingSystem.Walker");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "CalculateTravelTimeInHours" || method.ReturnType != typeof(float))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(Vector3) &&
                    parameters[1].ParameterType == typeof(Vector3);
            });
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordWalkerTravelTimeCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordWalkerTravelTimeReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Benchmark walker travel time patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchWalkerMoverDelegateCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWalkerMoverDelegateCacheOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WalkingSystem.WalkerMover");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseWalkerMoverDelegateCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] WalkerMover delegate cache target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "Move" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] WalkerMover.Move method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchPathFollowerNoAnimationFastMove(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnablePathFollowerNoAnimationFastMove)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UsePathFollowerNoAnimationFastMove), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] PathFollower no-animation fast move patch method was not found.");
            return 0;
        }

        var targetMethod = typeof(PathFollower).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "MoveAlongPath" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 3 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] PathFollower.MoveAlongPath method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchCarryAmountCalculatorOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableCarryAmountCalculatorOptimizer)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseCarryAmountCalculatorOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] CarryAmount calculator optimizer patch method was not found.");
            return 0;
        }

        var targetMethod = typeof(CarryAmountCalculator).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "AmountToCarry" ||
                    method.ReturnType != typeof(GoodAmount) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(int) &&
                    parameters[1].ParameterType == typeof(GoodAmount) &&
                    parameters[2].ParameterType == typeof(IAmountProvider);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] CarryAmountCalculator.AmountToCarry(int, GoodAmount, IAmountProvider) was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchGoodCarrierLiftingCapacityCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableGoodCarrierLiftingCapacityFrameCache)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseGoodCarrierLiftingCapacityCache), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordGoodCarrierLiftingCapacityCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] GoodCarrier lifting capacity cache patch methods were not found.");
            return 0;
        }

        var targetMethod = typeof(GoodCarrier).GetProperty(nameof(GoodCarrier.LiftingCapacity), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(nonPublic: true);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] GoodCarrier.LiftingCapacity getter was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static int PatchPathFollowerProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnablePathFollowerProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordPathFollowerProfilerCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordPathFollowerProfilerReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] PathFollower profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        var patchedMethods = new HashSet<MethodBase>();

        patched += PatchNamedMethods(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.CharacterMovementSystem.PathFollower",
            "MoveAlongPath",
            "GetSpeedLimitIfCloseToTarget",
            "GetRemainingDistance",
            "GetMovementSpeed",
            "AddAnimatedPathCorner",
            "AddSmoothingAnimatedPathCorner",
            "NotifyAfterMovement",
            "ReachedLastPathCorner");
        patched += PatchNamedMethods(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.CharacterMovementSystem.MovementAnimator",
            "AnimateMovementAlongPath",
            "UpdateTransform",
            "UpdateRotation",
            "UpdateGroupId",
            "UpdateCharacterAnimator",
            "UpdateAnimationSpeed",
            "UpdateSpeedInCharacterAnimator",
            "StartAnimation",
            "StopAnimation",
            "Update");

        return patched;
    }

    private static int PatchNamedMethods(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod,
        HashSet<MethodBase> patchedMethods,
        string typeName,
        params string[] methodNames)
    {
        var targetType = FindType(typeName);
        if (targetType is null)
        {
            Debug.LogWarning($"[T3MP] PathFollower profiler target type was not found: {typeName}");
            return 0;
        }

        var methodNameSet = new HashSet<string>(methodNames);
        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (!methodNameSet.Contains(method.Name) ||
                method.IsAbstract ||
                method.ContainsGenericParameters ||
                !patchedMethods.Add(method))
            {
                continue;
            }

            if (TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
            {
                patched++;
            }
        }

        return patched;
    }

    private static int PatchNeedBehaviorDecision(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var patched = 0;

        var actionDurationCalculatorType = FindType("Timberborn.NeedBehaviorSystem.ActionDurationCalculator");
        var travelTimePrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordNeedBehaviorTravelEstimateCall), BindingFlags.Static | BindingFlags.NonPublic);
        var travelTimePostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordNeedBehaviorTravelEstimateReturn), BindingFlags.Static | BindingFlags.NonPublic);
        var durationPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordDurationWithReturnCall), BindingFlags.Static | BindingFlags.NonPublic);
        var durationPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordDurationWithReturnReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (actionDurationCalculatorType is not null && travelTimePrefix is not null && travelTimePostfix is not null)
        {
            var travelTimeMethod = actionDurationCalculatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "TravelTimeBetween")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(Vector3) &&
                        parameters[1].ParameterType == typeof(Vector3);
                });

            if (travelTimeMethod is not null)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, travelTimePrefix);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, travelTimePostfix);
                patchMethod.Invoke(harmony, new object?[] { travelTimeMethod, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
                patched++;
            }

            var durationWithReturnMethod = actionDurationCalculatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "DurationWithReturnInHours")
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(Vector3) &&
                        parameters[1].ParameterType == typeof(Vector3);
                });

            if (durationWithReturnMethod is not null && durationPrefix is not null && durationPostfix is not null)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, durationPrefix);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, durationPostfix);
                patchMethod.Invoke(harmony, new object?[] { durationWithReturnMethod, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
                patched++;
            }
        }

        var districtNeedBehaviorServiceType = FindType("Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService");
        var pickBestPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordNeedBehaviorPickBestCall), BindingFlags.Static | BindingFlags.NonPublic);
        var pickBestPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordNeedBehaviorPickBestReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (districtNeedBehaviorServiceType is not null && pickBestPrefix is not null && pickBestPostfix is not null)
        {
            var pickBestMethod = districtNeedBehaviorServiceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method => method.Name == "PickBestAction" && method.GetParameters().Length == 4);
            if (pickBestMethod is not null)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, pickBestPrefix);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, pickBestPostfix);
                patchMethod.Invoke(harmony, new object?[] { pickBestMethod, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
                patched++;
            }
        }

        if (patched == 0)
        {
            Debug.LogWarning("[T3MP] Need behavior decision probe targets were not found.");
        }

        return patched;
    }

    private static int PatchNeedManagerDirectCriticalState(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableNeedManagerDirectCriticalState)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.NeedSystem.NeedManager") ??
            TryLoadAssemblyAndFindType("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseNeedManagerDirectCriticalState), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] NeedManager direct critical-state target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "AnyNeedIsInCriticalState" && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] NeedManager.AnyNeedIsInCriticalState method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchNeedManagerFastTick(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableNeedManagerFastTick)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.NeedSystem.NeedManager") ??
            TryLoadAssemblyAndFindType("Timberborn.NeedSystem", "Timberborn.NeedSystem.NeedManager");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseNeedManagerFastTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] NeedManager fast tick target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Tick" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] NeedManager.Tick method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchBeaverNeedDecisionFrequency(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.BeaverBehavior.BeaverNeedBehaviorPicker");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordBeaverNeedDecisionFrequencyCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordBeaverNeedDecisionFrequencyReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Beaver decision frequency probe target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "GetBestNeedBehavior")
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 &&
                    parameters[0].ParameterType.FullName == "Timberborn.NeedBehaviorSystem.NeedFilter";
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] Beaver GetBestNeedBehavior(NeedFilter) method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchReservableChanges(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.ReservableSystem.Reservable");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordReservableChanged), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Reservable change probe target was not found.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => (method.Name == "Reserve" || method.Name == "Unreserve") && method.GetParameters().Length == 0))
        {
            patchMethod.Invoke(harmony, new object?[] { method, null, postfixHarmonyMethod, null, null });
            patched++;
        }

        return patched;
    }

    private static int PatchTickBuckets(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var patched = 0;
        patched += PatchTickerUpdate(harmony, harmonyType, harmonyMethodType, patchMethod);
        patched += PatchFullTick(harmony, harmonyType, harmonyMethodType, patchMethod);

        var targetType = FindType("Timberborn.TickSystem.TickableBucketService");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] Tick bucket probe target was not found.");
            return patched;
        }

        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTickBuckets), BindingFlags.Static | BindingFlags.NonPublic);
        if (postfix is null)
        {
            Debug.LogWarning("[T3MP] Tick bucket probe method was not found.");
            return patched;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "TickBuckets")
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] TickBuckets(int) method was not found.");
            return patched;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, null, postfixHarmonyMethod, null, null });
        return patched + 1;
    }

    private static int PatchMovementNreGuard(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnablePathFollowerNreGuard)
        {
            return 0;
        }

        var patched = 0;
        var targetType = FindType("Timberborn.CharacterMovementSystem.PathFollower");
        var pathFollowerFinalizer = typeof(BenchmarkProbe).GetMethod(nameof(SuppressReachedLastPathCornerNre), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || pathFollowerFinalizer is null)
        {
            Debug.LogWarning("[T3MP] PathFollower NRE guard target was not found.");
        }
        else
        {
            var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                    method.Name == "ReachedLastPathCorner" &&
                    method.ReturnType == typeof(bool) &&
                    method.GetParameters().Length == 0 &&
                    !method.ContainsGenericParameters);
            if (targetMethod is null)
            {
                Debug.LogWarning("[T3MP] PathFollower.ReachedLastPathCorner() bool method was not found.");
            }
            else
            {
                var finalizerHarmonyMethod = Activator.CreateInstance(harmonyMethodType, pathFollowerFinalizer);
                patchMethod.Invoke(harmony, new object?[] { targetMethod, null, null, null, finalizerHarmonyMethod });
                patched++;
            }
        }

        var rotatorType = FindType("Timberborn.CharacterMovementSystem.CharacterRotator");
        var rotatorFinalizer = typeof(BenchmarkProbe).GetMethod(nameof(SuppressCharacterRotatorXRotationNre), BindingFlags.Static | BindingFlags.NonPublic);
        if (rotatorType is null || rotatorFinalizer is null)
        {
            Debug.LogWarning("[T3MP] CharacterRotator NRE guard target was not found.");
            return patched;
        }

        var xRotationMethod = rotatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "GetXRotation" &&
                method.ReturnType == typeof(float) &&
                method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(float) &&
                !method.ContainsGenericParameters);
        if (xRotationMethod is null)
        {
            Debug.LogWarning("[T3MP] CharacterRotator.GetXRotation(float) method was not found.");
            return patched;
        }

        var rotatorFinalizerHarmonyMethod = Activator.CreateInstance(harmonyMethodType, rotatorFinalizer);
        patchMethod.Invoke(harmony, new object?[] { xRotationMethod, null, null, null, rotatorFinalizerHarmonyMethod });
        patched++;

        var movementAnimatorType = FindType("Timberborn.CharacterMovementSystem.MovementAnimator");
        var movementAnimatorPrefix = typeof(BenchmarkProbe).GetMethod(nameof(SkipBadMovementAnimatorUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        var movementAnimatorFinalizer = typeof(BenchmarkProbe).GetMethod(nameof(SuppressMovementAnimatorUpdateNre), BindingFlags.Static | BindingFlags.NonPublic);
        if (movementAnimatorType is null || movementAnimatorPrefix is null || movementAnimatorFinalizer is null)
        {
            Debug.LogWarning("[T3MP] MovementAnimator NRE guard target was not found.");
            return patched;
        }

        var movementAnimatorPrefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, movementAnimatorPrefix);
        var movementAnimatorFinalizerHarmonyMethod = Activator.CreateInstance(harmonyMethodType, movementAnimatorFinalizer);
        foreach (var updateMethod in movementAnimatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method =>
                         method.Name == "Update" &&
                         method.ReturnType == typeof(void) &&
                         !method.ContainsGenericParameters))
        {
            patchMethod.Invoke(harmony, new object?[] { updateMethod, movementAnimatorPrefixHarmonyMethod, null, null, movementAnimatorFinalizerHarmonyMethod });
            patched++;
        }

        return patched;
    }

    private static int PatchAnimatedPathFollowerHorizontalOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableAnimatedPathFollowerHorizontalOptimizer)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseAnimatedPathFollowerHorizontalOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] AnimatedPathFollower horizontal optimizer patch method was not found.");
            return 0;
        }

        var targetType = FindType("Timberborn.CharacterMovementSystem.AnimatedPathFollower") ??
            TryLoadAssemblyAndFindType("Timberborn.CharacterMovementSystem", "Timberborn.CharacterMovementSystem.AnimatedPathFollower");
        if (targetType is null)
        {
            Debug.LogWarning("[T3MP] AnimatedPathFollower type was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "PlaceBetweenCorners" ||
                    method.ReturnType != typeof(void) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(AnimatedPathCorner) &&
                    parameters[1].ParameterType == typeof(AnimatedPathCorner) &&
                    parameters[2].ParameterType == typeof(float);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] AnimatedPathFollower.PlaceBetweenCorners method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchTickerUpdate(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.TickSystem.Ticker");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTickerUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Ticker update probe target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "Update" && method.GetParameters().Length == 1);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] Ticker.Update method was not found.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, null, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchFramesPerSecondCounter(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.Diagnostics.FramesPerSecondCounter");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTimberbornFpsCounter), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] FramesPerSecondCounter probe target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "UpdateSingleton" && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] FramesPerSecondCounter.UpdateSingleton method was not found.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, null, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchSpeedManagerProbe(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableAutoResumeGameSpeed && !BenchmarkSettings.EnableSpeedManagerProbe)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.TimeSystem.SpeedManager") ??
            TryLoadAssemblyAndFindType("Timberborn.TimeSystem", "Timberborn.TimeSystem.SpeedManager");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordSpeedManager), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] SpeedManager auto-resume target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "LateUpdateSingleton" && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] SpeedManager.LateUpdateSingleton method was not found.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, null, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchTimeSpeedButtonGroupProbe(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableTimeSpeedButtonGroupAutoResume && !BenchmarkSettings.EnableTimeSpeedButtonGroupProbe)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.TimeSpeedButtonSystem.TimeSpeedButtonGroup") ??
            TryLoadAssemblyAndFindType("Timberborn.TimeSpeedButtonSystem", "Timberborn.TimeSpeedButtonSystem.TimeSpeedButtonGroup");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTimeSpeedButtonGroup), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] TimeSpeedButtonGroup probe target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "UpdateSingleton" && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] TimeSpeedButtonGroup.UpdateSingleton method was not found.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, null, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchFullTick(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetType = FindType("Timberborn.TimeSystem.TickProgressService");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordFullTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Full tick probe target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "Tick" && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] TickProgressService.Tick method was not found.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        patchMethod.Invoke(harmony, new object?[] { targetMethod, null, postfixHarmonyMethod, null, null });
        return 1;
    }

    private static int PatchLoadProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadStageCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadStageReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Load profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameSaveRepositorySystem.GameSaveDeserializer", "Load");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameSaveRuntimeSystem.GameLoader", "Load");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameSceneLoading.GameSceneLoader", "StartSaveGame");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameSceneLoading.GameSceneLoader", "StartSaveGameInstantly");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SceneLoading.SceneLoader", "LoadScene");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SceneLoading.SceneLoader", "LoadSceneInstantly");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SceneLoading.SceneLoader", "LoadSceneInternal");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "LoadAll");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "LoadSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "LoadNonSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "PostLoadSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "PostLoadNonSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameScene.GameSceneSerializedWorldSupplier", "Load");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameScene.GameSceneSerializedWorldSupplier", "LoadGame");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameScene.GameSceneSerializedWorldSupplier", "PostLoadNonSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameStartup.GameInitializer", "InitializeGameFromSave");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameStartup.GameInitializer", "Initialize");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.GameStartup.GameStarter", "StartGameplay");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.MapSystem.MapLoader", "Load");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.TemplateCollectionSystem.TemplateCollectionService", "Load");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldSerialization.WorldSerializer", "ReadFromSaveEntryStream");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.WorldEntitiesLoader", "LoadNonSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.WorldEntitiesLoader", "PostLoadNonSingletons");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.WorldEntitiesLoader", "InstantiateEntities");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.EntitiesLoader", "LoadAndInitialize");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.EntitiesLoader", "Load");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.EntitiesLoader", "BatchLoad");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.EntitiesLoader", "PreInitialize");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.EntitiesLoader", "Initialize");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.WorldPersistence.EntitiesLoader", "PostInitialize");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.BlockAndTerrainLoadValidation.BlockAndTerrainBatchLoader", "BatchLoadEntities");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.BlockSystem.BlockObjectBatchLoader", "AddToServices");
        patched += PatchLoadProfilerMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, "Timberborn.TerrainPhysics.TerrainPhysicsPostLoader", "ValidateAll");
        return patched;
    }

    private static int PatchLoadProfilerMethod(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod,
        string typeName,
        string methodName)
    {
        var targetType = FindType(typeName);
        if (targetType is null)
        {
            Debug.LogWarning($"[T3MP] Load profiler target type was not found: {typeName}");
            return 0;
        }

        var methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName && !method.ContainsGenericParameters)
            .ToArray();
        if (typeName == "Timberborn.WorldPersistence.EntitiesLoader" && methodName == "Load")
        {
            methods = methods
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                        parameters[0].ParameterType.FullName is string fullName &&
                        fullName.StartsWith("System.Collections.Generic.ICollection", StringComparison.Ordinal);
                })
                .ToArray();
        }

        if (methods.Length == 0)
        {
            Debug.LogWarning($"[T3MP] Load profiler target method was not found: {typeName}.{methodName}");
            return 0;
        }

        foreach (var method in methods)
        {
            patchMethod.Invoke(harmony, new object?[] { method, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
        }

        return methods.Length;
    }

    private static int PatchMechanicalGraphLoadBatcher(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableMechanicalGraphLoadBatching)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.MechanicalSystem.MechanicalGraphFactory");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordMechanicalGraphJoinForLoadBatch), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Mechanical graph load batcher target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Join" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    return false;
                }

                var parameterType = parameters[0].ParameterType;
                return parameterType.IsGenericType &&
                    parameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] Mechanical graph load batcher Join method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchHaulCandidateOrderCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableHaulCandidateOrderCache)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Hauling.DistrictHaulCandidates") ??
            TryLoadAssemblyAndFindType("Timberborn.Hauling", "Timberborn.Hauling.DistrictHaulCandidates");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseCachedHaulCandidateOrder), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(StoreCachedHaulCandidateOrder), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Haul candidate order cache target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "GetWorkplaceBehaviorsOrdered" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    return false;
                }

                var parameterType = parameters[0].ParameterType;
                return parameterType.IsGenericType &&
                    parameterType.GetGenericTypeDefinition() == typeof(IList<>) &&
                    parameterType.GetGenericArguments()[0].FullName == "Timberborn.WorkSystem.WorkplaceBehavior";
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static int PatchHaulNoActionFrameCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableHaulNoActionFrameCache)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Hauling.HaulWorkplaceBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.Hauling", "Timberborn.Hauling.HaulWorkplaceBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseHaulNoActionFrameCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Haul no-action frame cache target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Decide" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(BehaviorAgent);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] HaulWorkplaceBehavior.Decide method was not found.");
            return 0;
        }

        HaulNoActionFrameCache.Initialize(targetType);
        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchInventoryStockDistanceCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableInventoryStockDistanceCache)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseInventoryStockDistanceCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] Inventory stock distance cache patch method was not found.");
            return 0;
        }

        var targetMethod = typeof(DistrictInventoryPicker).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "ClosestInventoryWithStock" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(Accessible) &&
                    parameters[1].ParameterType == typeof(string) &&
                    parameters[2].ParameterType == typeof(Predicate<Inventory>);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] DistrictInventoryPicker.ClosestInventoryWithStock(Accessible, string, Predicate<Inventory>) method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchInventoryNeedGoodOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableInventoryNeedGoodOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.InventoryNeedSystem.InventoryNeedBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.InventoryNeedSystem", "Timberborn.InventoryNeedSystem.InventoryNeedBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseInventoryNeedGoodOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] InventoryNeed good optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "FindMostOptimalGood" || method.ReturnType != typeof(GoodAmount) || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(Appraiser) &&
                    parameters[1].ParameterType == typeof(Inventory);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] InventoryNeedBehavior.FindMostOptimalGood(Appraiser, Inventory) was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchInventoryCapacityDistanceCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableInventoryCapacityDistanceCache)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseInventoryCapacityDistanceCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] Inventory capacity distance cache patch method was not found.");
            return 0;
        }

        var targetMethod = typeof(DistrictInventoryPicker).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "ClosestInventoryWithCapacity" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(Accessible) &&
                    parameters[1].ParameterType == typeof(GoodAmount) &&
                    parameters[2].ParameterType == typeof(Predicate<Inventory>) &&
                    parameters[3].ParameterType == typeof(float).MakeByRefType();
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] DistrictInventoryPicker.ClosestInventoryWithCapacity(Accessible, GoodAmount, Predicate<Inventory>, out float) method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchInventoryCapacityVectorProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableInventoryCapacityVectorProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordInventoryCapacityVectorCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordInventoryCapacityVectorReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Inventory capacity vector profiler patch methods were not found.");
            return 0;
        }

        var targetMethod = typeof(DistrictInventoryPicker).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "ClosestInventoryWithCapacity" || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                    parameters[0].ParameterType == typeof(Vector3) &&
                    parameters[1].ParameterType == typeof(GoodAmount) &&
                    parameters[2].ParameterType == typeof(float).MakeByRefType();
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] DistrictInventoryPicker.ClosestInventoryWithCapacity(Vector3, GoodAmount, out float) method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static int PatchWorkplaceNoActionFrameCache(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWorkplaceNoActionFrameCache)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseWorkplaceNoActionFrameCache), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordWorkplaceNoActionFrameCache), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Workplace no-action frame cache patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract ||
                type.ContainsGenericParameters ||
                type.FullName == "Timberborn.Hauling.HaulWorkplaceBehavior" ||
                !IsAssignableTo(type, "Timberborn.WorkSystem.WorkplaceBehavior"))
            {
                continue;
            }

            var targetMethod = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "Decide" ||
                        method.ReturnType != typeof(Decision) ||
                        method.ContainsGenericParameters)
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(BehaviorAgent);
                });
            if (targetMethod is not null &&
                patchedMethods.Add(targetMethod) &&
                TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod))
            {
                patched++;
            }
        }

        return patched;
    }

    private static int PatchWorkerRootMetricsBypass(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWorkerRootMetricsBypass)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WorkSystem.WorkerRootBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.WorkSystem", "Timberborn.WorkSystem.WorkerRootBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseWorkerRootMetricsBypass), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] WorkerRoot metrics bypass target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Decide" ||
                    method.ReturnType != typeof(Decision) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(BehaviorAgent);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] WorkerRootBehavior.Decide method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchFillInputWorkplaceOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableFillInputWorkplaceOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Workshops.FillInputWorkplaceBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.Workshops", "Timberborn.Workshops.FillInputWorkplaceBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseFillInputWorkplaceOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] FillInput optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Decide" ||
                    method.ReturnType != typeof(Decision) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(BehaviorAgent);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] FillInputWorkplaceBehavior.Decide method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchWaitInsideIdlyOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWaitInsideIdlyOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WorkSystem.WaitInsideIdlyWorkplaceBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.WorkSystem", "Timberborn.WorkSystem.WaitInsideIdlyWorkplaceBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseWaitInsideIdlyOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] WaitInsideIdly optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "Decide" ||
                    method.ReturnType != typeof(Decision) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(BehaviorAgent);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] WaitInsideIdlyWorkplaceBehavior.Decide method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchWorkerWorkingSpeedOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWorkerWorkingSpeedNoRepeatSet)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WorkSystem.Worker") ??
            TryLoadAssemblyAndFindType("Timberborn.WorkSystem", "Timberborn.WorkSystem.Worker");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseWorkerWorkingSpeedOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Worker working-speed optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "Tick" && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] Worker.Tick method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchBehaviorManagerProcessOptimizer(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableBehaviorManagerProcessOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.BehaviorSystem.BehaviorManager");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(UseBehaviorManagerProcessOptimizer), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] BehaviorManager process optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "ProcessBehaviors" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] BehaviorManager.ProcessBehaviors method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchExecutorTickProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableExecutorTickProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordExecutorTickProfilerCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordExecutorTickProfilerReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Executor tick profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patched = 0;
        var patchedMethods = new HashSet<MethodBase>();
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract ||
                type.ContainsGenericParameters ||
                type.FullName is null ||
                !type.FullName.StartsWith("Timberborn.", StringComparison.Ordinal))
            {
                continue;
            }

            var method = FindExecutorTickImplementation(type);
            if (method is not null &&
                patchedMethods.Add(method) &&
                TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
            {
                patched++;
            }
        }

        return patched;
    }

    private static int PatchDistrictResourceCounterThrottle(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableDistrictResourceCounterThrottle ||
            BenchmarkSettings.DistrictResourceCounterThrottleTicks <= 1)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.ResourceCountingSystem.DistrictResourceCounter") ??
            TryLoadAssemblyAndFindType("Timberborn.ResourceCountingSystem", "Timberborn.ResourceCountingSystem.DistrictResourceCounter");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunDistrictResourceCounterTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] DistrictResourceCounter throttle target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Tick" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] DistrictResourceCounter.Tick method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchWaterObjectServiceThrottle(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWaterObjectServiceThrottle ||
            BenchmarkSettings.WaterObjectServiceThrottleTicks <= 1)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WaterObjects.WaterObjectService") ??
            TryLoadAssemblyAndFindType("Timberborn.WaterObjects", "Timberborn.WaterObjects.WaterObjectService");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunWaterObjectServiceTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] WaterObjectService throttle target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Tick" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] WaterObjectService.Tick method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchNavMeshUpdateInvalidation(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableNavMeshEventTravelCacheInvalidation)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Navigation.NavMeshUpdateNotifier") ??
            TryLoadAssemblyAndFindType("Timberborn.Navigation", "Timberborn.Navigation.NavMeshUpdateNotifier");
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordRegularNavMeshUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] NavMeshUpdateNotifier was not found for travel cache invalidation.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "NotifyOfNavMeshUpdates" &&
                method.GetParameters().Length == 1 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] NotifyOfNavMeshUpdates method was not found for travel cache invalidation.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, null, postfixHarmonyMethod) ? 1 : 0;
    }

    private static void RecordRegularNavMeshUpdate()
    {
        NeedBehaviorTravelOptimizer.OnRegularNavMeshUpdate();
        RoadReachabilityCache.OnNavMeshUpdate();
    }

    private static int PatchRoadReachabilityCache(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableRoadReachabilityCache ||
            !BenchmarkSettings.EnableNavMeshEventTravelCacheInvalidation)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Navigation.RoadReachabilityService") ??
            TryLoadAssemblyAndFindType("Timberborn.Navigation", "Timberborn.Navigation.RoadReachabilityService");
        var targetMethod = targetType?.GetMethod("GetReachableNeighborsInRange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(FastRoadReachableNeighbors), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(StoreRoadReachableNeighbors), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] RoadReachabilityService.GetReachableNeighborsInRange was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static bool FastRoadReachableNeighbors(int startingNodeId, int range, List<int> reachableRoadNodes, out int __state)
    {
        __state = -1;
        if (!BenchmarkSettings.EnableRoadReachabilityCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (RoadReachabilityCache.TryGet(startingNodeId, range, reachableRoadNodes))
        {
            return false;
        }

        __state = reachableRoadNodes.Count;
        return true;
    }

    private static void StoreRoadReachableNeighbors(int startingNodeId, int range, List<int> reachableRoadNodes, int __state)
    {
        if (__state >= 0)
        {
            RoadReachabilityCache.Store(startingNodeId, range, reachableRoadNodes, __state);
        }
    }

    private static int PatchEmptyInventoriesFastPath(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableEmptyInventoriesFastPath)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.Emptying.EmptyInventoriesLaborBehavior") ??
            TryLoadAssemblyAndFindType("Timberborn.Emptying", "Timberborn.Emptying.EmptyInventoriesLaborBehavior");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeFastEmptyInventoriesDecide), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] EmptyInventories fast path target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Decide" &&
                method.GetParameters().Length == 1 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] EmptyInventoriesLaborBehavior.Decide method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static bool MaybeFastEmptyInventoriesDecide(object __instance, ref Timberborn.BehaviorSystem.Decision __result)
    {
        if (!BenchmarkSettings.EnableEmptyInventoriesFastPath ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        return !EmptyInventoriesFastPath.TryFastDecide(__instance, ref __result);
    }

    private static int PatchTickDispatchOptimizer(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableTickDispatchOptimizer)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.TickSystem.TickableEntityBucket") ??
            TryLoadAssemblyAndFindType("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntityBucket");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunTickDispatchFast), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Tick dispatch optimizer target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "TickAll" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] TickableEntityBucket.TickAll method was not found for the tick dispatch optimizer.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static bool MaybeRunTickDispatchFast(object __instance)
    {
        if (!BenchmarkSettings.EnableTickDispatchOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        return !TickDispatchOptimizer.TryTickBucket(__instance);
    }

    // ------------------------------------------------------------------
    // Flat tick dispatch hooks: keep the per-bucket flat component snapshot
    // (TickDispatchOptimizer) exact. EnableComponent/DisableComponent are the
    // only writers of BaseComponent.Enabled, so mirroring transitions into the
    // snapshot bitmask makes the sweep read the same value vanilla would read
    // at visit time. Bucket Add/Remove invalidates the affected snapshot.
    // ------------------------------------------------------------------
    private static int PatchFlatTickDispatchHooks(object harmonyInstance, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var patched = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var enableTarget = typeof(Timberborn.BaseComponentSystem.BaseComponent).GetMethod("EnableComponent", flags);
        var disableTarget = typeof(Timberborn.BaseComponentSystem.BaseComponent).GetMethod("DisableComponent", flags);
        var enablePrefix = typeof(BenchmarkProbe).GetMethod(nameof(OnBaseComponentEnabledPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        var disablePrefix = typeof(BenchmarkProbe).GetMethod(nameof(OnBaseComponentDisabledPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (enableTarget is not null && enablePrefix is not null)
        {
            patched += TryPatch(harmonyInstance, patchMethod, enableTarget, Activator.CreateInstance(harmonyMethodType, enablePrefix), null) ? 1 : 0;
        }

        if (disableTarget is not null && disablePrefix is not null)
        {
            patched += TryPatch(harmonyInstance, patchMethod, disableTarget, Activator.CreateInstance(harmonyMethodType, disablePrefix), null) ? 1 : 0;
        }

        var bucketType = FindType("Timberborn.TickSystem.TickableEntityBucket") ??
            TryLoadAssemblyAndFindType("Timberborn.TickSystem", "Timberborn.TickSystem.TickableEntityBucket");
        if (bucketType is not null)
        {
            var addTarget = bucketType.GetMethod("Add", flags);
            var removeTarget = bucketType.GetMethod("Remove", flags);
            var addPrefix = typeof(BenchmarkProbe).GetMethod(nameof(OnTickableBucketAddPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var removePrefix = typeof(BenchmarkProbe).GetMethod(nameof(OnTickableBucketRemovePrefix), BindingFlags.Static | BindingFlags.NonPublic);
            if (addTarget is not null && addPrefix is not null)
            {
                patched += TryPatch(harmonyInstance, patchMethod, addTarget, Activator.CreateInstance(harmonyMethodType, addPrefix), null) ? 1 : 0;
            }

            if (removeTarget is not null && removePrefix is not null)
            {
                patched += TryPatch(harmonyInstance, patchMethod, removeTarget, Activator.CreateInstance(harmonyMethodType, removePrefix), null) ? 1 : 0;
            }
        }

        // The flat dispatch path is only exact when ALL four hooks are live.
        TickDispatchOptimizer.FlatHooksInstalled = patched == 4;
        if (!TickDispatchOptimizer.FlatHooksInstalled)
        {
            Debug.LogWarning($"[T3MP] Flat tick dispatch hooks incomplete ({patched}/4); falling back to per-entity dispatch.");
        }

        return patched;
    }

    private static void OnBaseComponentEnabledPrefix(Timberborn.BaseComponentSystem.BaseComponent __instance)
    {
        if (!__instance.Enabled && __instance is Timberborn.TickSystem.TickableComponent tickable)
        {
            TickDispatchOptimizer.NotifyComponentEnabledChanged(tickable, true);
        }
    }

    private static void OnBaseComponentDisabledPrefix(Timberborn.BaseComponentSystem.BaseComponent __instance)
    {
        if (__instance.Enabled && __instance is Timberborn.TickSystem.TickableComponent tickable)
        {
            TickDispatchOptimizer.NotifyComponentEnabledChanged(tickable, false);
        }
    }

    private static void OnTickableBucketAddPrefix(object __instance, Timberborn.TickSystem.TickableEntity tickableEntity)
    {
        // Attach the activeInHierarchy sentinel HERE: during save-load this
        // spreads the ~26k AddComponent calls over the loading phase (a
        // lazily-attached burst in the first sweep froze the game ~10 s),
        // and afterwards it is one call per born/built entity.
        TickDispatchOptimizer.AttachSentinelForEntity(tickableEntity);
        TickDispatchOptimizer.NotifyBucketMembershipChanged(__instance, true);
    }

    private static void OnTickableBucketRemovePrefix(object __instance)
    {
        TickDispatchOptimizer.NotifyBucketMembershipChanged(__instance, false);
    }

    // ------------------------------------------------------------------
    // Smooth frame pacing: caps the game time the sim ticker consumes per
    // rendered frame in visible high-speed play (see BenchmarkModeController
    // for the rationale). Same drop-the-surplus semantics as the vanilla
    // maximumDeltaTime clamp, scoped to the ticker only.
    // ------------------------------------------------------------------
    private static int PatchSmoothFramePacing(object harmonyInstance, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var targetMethod = typeof(Timberborn.TickSystem.Ticker).GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(ClampTickerDeltaForSmoothPacing), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Smooth frame pacing target was not found.");
            return 0;
        }

        return TryPatch(harmonyInstance, patchMethod, targetMethod, Activator.CreateInstance(harmonyMethodType, prefix), null) ? 1 : 0;
    }

    private static void ClampTickerDeltaForSmoothPacing(ref float deltaTimeInSeconds)
    {
        if (!BenchmarkModeController.SmoothFramePacingActive)
        {
            return;
        }

        var cap = BenchmarkSettings.SmoothFramePacingMaxDeltaTime * Time.timeScale;
        if (deltaTimeInSeconds > cap)
        {
            deltaTimeInSeconds = cap;
        }
    }

    // ------------------------------------------------------------------
    // EventBus fast delegates: replace the reflective per-delivery closure
    // built by EventBus.RegisterMethod with a compiled delegate (see
    // EventBusFastDelegates for exactness notes).
    // ------------------------------------------------------------------
    private static int PatchEventBusFastDelegates(object harmonyInstance, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var eventBusType = FindType("Timberborn.SingletonSystem.EventBus");
        var targetMethod = eventBusType?.GetMethod("RegisterMethod", BindingFlags.Instance | BindingFlags.NonPublic);
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRegisterEventBusMethodFast), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] EventBus fast delegate target was not found.");
            return 0;
        }

        return TryPatch(harmonyInstance, patchMethod, targetMethod, Activator.CreateInstance(harmonyMethodType, prefix), null) ? 1 : 0;
    }

    private static bool MaybeRegisterEventBusMethodFast(object __instance, object subscriber, MethodInfo method)
    {
        return EventBusFastDelegates.TryRegisterMethod(__instance, subscriber, method);
    }

    // ------------------------------------------------------------------
    // Population-speed-throttle removal: vanilla GameSpeedThrottler scales
    // the requested game speed down (scale 1.0 at <=30 beavers -> 0.4 at
    // >=200). With the flag on, the scale is forced to 1 so the requested
    // speed applies raw. Deliberately behavior-CHANGING (it alters the
    // achievable speed cap, never the per-tick simulation); ships ON by
    // default and is disclosed in the store description.
    // ------------------------------------------------------------------
    private static int PatchGameSpeedThrottlerRemoval(object harmonyInstance, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var speedManagerType = FindType("Timberborn.TimeSystem.SpeedManager");
        var targetMethod = speedManagerType?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "ChangeSpeedScale" && method.GetParameters().Length == 1);
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(ForceUnthrottledSpeedScale), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Speed throttler removal target was not found.");
            return 0;
        }

        return TryPatch(harmonyInstance, patchMethod, targetMethod, Activator.CreateInstance(harmonyMethodType, prefix), null) ? 1 : 0;
    }

    private static void ForceUnthrottledSpeedScale(ref float speedScale)
    {
        speedScale = 1f;
    }

    // ------------------------------------------------------------------
    // Shift+P turbo cancellation on the in-game options menu (Esc). The
    // turbo request lives on a DontDestroyOnLoad controller, so it would
    // otherwise survive "exit to main menu" and stay active in the next
    // save that gets loaded. The Esc menu is the only route back to the
    // main menu, so cancelling on menu open closes that leak (and doubles
    // as an intuitive "menu = turbo off" behavior).
    // ------------------------------------------------------------------
    private static int PatchOptionsMenuBlackoutCancel(object harmonyInstance, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var optionsBoxType = FindType("Timberborn.OptionsGame.GameOptionsBox");
        var targetMethod = optionsBoxType?.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(CancelRenderBlackoutOnOptionsMenu), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] Options menu blackout-cancel target was not found.");
            return 0;
        }

        return TryPatch(harmonyInstance, patchMethod, targetMethod, Activator.CreateInstance(harmonyMethodType, prefix), null) ? 1 : 0;
    }

    // Stopwatch probes around the topology-UI hot paths identified in the
    // gear/path placement-lag investigation (see TopologyUiProbe).
    private static int PatchTopologyUiProbe(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableTopologyUiProbe)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTopologyUiCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTopologyUiReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Topology UI probe patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var sites = new (string TypeName, string MethodName)[]
        {
            ("Timberborn.Navigation.DistrictMap", "RecalculateRoadFlowFields"),
            ("Timberborn.Navigation.DistrictMap", "AssignDistrictToRoadMap"),
            ("Timberborn.Navigation.DistrictRoadFlowFieldGenerator", "FillFlowFieldUpToDistance"),
            ("Timberborn.Navigation.RoadSpillFlowFieldGenerator", "FillFlowFieldUpToDistance"),
            ("Timberborn.Navigation.RoadFlowFieldGenerator", "FillFlowField"),
            ("Timberborn.Navigation.NavMeshUpdater", "ApplyPreviewChanges"),
            ("Timberborn.MechanicalSystemHighlighting.MechanicalGraphIterator", "Iterate"),
            ("Timberborn.MechanicalSystemHighlighting.MechanicalGraphHighlightService", "RefreshHighlight"),
            ("Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer", "LateUpdate"),
            ("Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer", "UpdateAllNodes"),
            ("Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer", "UpdateDrawers"),
            ("Timberborn.BlockObjectTools.PreviewPlacer", "ShowPreviews"),
            // Attribution for the RoadFlowFieldGenerator.FillFlowField storms:
            // which caller actually triggers the full-district refills.
            ("Timberborn.Navigation.PathfindingService", "FindInstantRoadPath"),
            ("Timberborn.Navigation.PathfindingService", "FindRoadPathCached"),
            ("Timberborn.Navigation.PathfindingService", "TryFillRoadFlowField"),
            ("Timberborn.Navigation.RoadNavigationRangeService", "GetNodesInRange"),
            ("Timberborn.Navigation.RoadNavigationRangeService", "GetPreviewNodesInRange"),
            // Attribution INSIDE a full ShowPreviews run (up to 19ms in
            // manual play): navmesh recalcs, validation, model updates.
            ("Timberborn.BlockSystemNavigation.BlockObjectNavMesh", "RecalculateNavMeshObject"),
            ("Timberborn.BlockSystem.BlockObjectValidationService", "AreValid"),
            ("Timberborn.BlockObjectModelSystem.BlockObjectModelController", "UpdateModel"),
            // Overlay rebuild cost split: mesh construction vs the per-tile
            // connection-key loop (UpdateDrawers total minus Build).
            ("Timberborn.BuildingsNavigation.PathMeshDrawer", "Build")
        };

        var patched = 0;
        foreach (var (typeName, methodName) in sites)
        {
            var targetType = FindType(typeName);
            if (targetType is null)
            {
                Debug.LogWarning($"[T3MP] Topology UI probe target type was not found: {typeName}");
                continue;
            }

            var found = false;
            foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(method => method.Name == methodName && !method.IsAbstract && !method.ContainsGenericParameters))
            {
                found = true;
                if (TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }

            if (!found)
            {
                Debug.LogWarning($"[T3MP] Topology UI probe target method was not found: {typeName}.{methodName}");
            }
        }

        // Sampled WHO-calls-it attribution for the UpdateModel churn.
        var modelControllerType = FindType("Timberborn.BlockObjectModelSystem.BlockObjectModelController");
        var updateModelMethod = modelControllerType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "UpdateModel");
        var samplerPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordModelUpdateSource), BindingFlags.Static | BindingFlags.NonPublic);
        if (updateModelMethod is not null && samplerPrefix is not null &&
            TryPatch(harmony, patchMethod, updateModelMethod, Activator.CreateInstance(harmonyMethodType, samplerPrefix), null))
        {
            patched++;
        }

        return patched;
    }

    private static void RecordModelUpdateSource(object __instance)
    {
        TopologyUiProbe.SampleModelUpdateSource(__instance);
    }

    // Singleton/instance capture + driver installation for the automated
    // '-benchTopoUi' selection scenario (see TopologyUiScenario).
    private static int PatchTopologyUiScenario(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.BenchTopoUiRequested)
        {
            return 0;
        }

        var patched = 0;
        patched += PatchScenarioCapture(harmony, harmonyMethodType, patchMethod,
            "Timberborn.SelectionSystem.EntitySelectionService", "Load", nameof(RecordEntitySelectionServiceForScenario));
        patched += PatchScenarioCapture(harmony, harmonyMethodType, patchMethod,
            "Timberborn.MechanicalSystem.MechanicalGraphRegistry", "Load", nameof(RecordMechanicalGraphRegistryForScenario));
        patched += PatchScenarioCapture(harmony, harmonyMethodType, patchMethod,
            "Timberborn.BuildingsNavigation.PathRangeDrawer", "Awake", nameof(RecordPathRangeDrawerForScenario));

        if (patched > 0)
        {
            TopologyUiScenario.Install();
        }

        return patched;
    }

    private static int PatchScenarioCapture(
        object harmony,
        Type harmonyMethodType,
        MethodInfo patchMethod,
        string typeName,
        string methodName,
        string postfixName)
    {
        var targetType = FindType(typeName);
        var targetMethod = targetType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == 0);
        var postfix = typeof(BenchmarkProbe).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
        if (targetMethod is null || postfix is null)
        {
            Debug.LogWarning($"[T3MP] Topology UI scenario capture target was not found: {typeName}.{methodName}");
            return 0;
        }

        return TryPatch(harmony, patchMethod, targetMethod, null, Activator.CreateInstance(harmonyMethodType, postfix)) ? 1 : 0;
    }

    // UI-only topology optimizations (see TopologyUiOptimizer).
    private static int PatchTopologyUiOptimizers(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var patched = 0;

        if (BenchmarkSettings.EnableMechanicalHighlightDiff)
        {
            var serviceType = FindType("Timberborn.MechanicalSystemHighlighting.MechanicalGraphHighlightService");
            var refreshMethod = serviceType?.GetMethod("RefreshHighlight", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var refreshPrefix = typeof(BenchmarkProbe).GetMethod(nameof(SkipVanillaRefreshHighlight), BindingFlags.Static | BindingFlags.NonPublic);
            var highlighterType = FindType("Timberborn.SelectionSystem.Highlighter");
            var unhighlightAllMethod = highlighterType?.GetMethod("UnhighlightAllSecondary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var unhighlightAllPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordUnhighlightAllSecondary), BindingFlags.Static | BindingFlags.NonPublic);
            if (refreshMethod is null || refreshPrefix is null || unhighlightAllMethod is null || unhighlightAllPostfix is null)
            {
                Debug.LogWarning("[T3MP] Mechanical highlight diff targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, unhighlightAllMethod, null, Activator.CreateInstance(harmonyMethodType, unhighlightAllPostfix)) &&
                TryPatch(harmony, patchMethod, refreshMethod, Activator.CreateInstance(harmonyMethodType, refreshPrefix), null))
            {
                patched += 2;
            }
        }

        if (BenchmarkSettings.EnableMechanicalHighlightDiff)
        {
            var serviceType = FindType("Timberborn.MechanicalSystemHighlighting.MechanicalGraphHighlightService");
            var lateUpdateMethod = serviceType?.GetMethod("LateUpdateSingleton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var prefix = typeof(BenchmarkProbe).GetMethod(nameof(CoalesceHighlightLateUpdateCall), BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(BenchmarkProbe).GetMethod(nameof(CoalesceHighlightLateUpdateReturn), BindingFlags.Static | BindingFlags.NonPublic);
            if (lateUpdateMethod is null || prefix is null || postfix is null)
            {
                Debug.LogWarning("[T3MP] Mechanical highlight coalescing targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, lateUpdateMethod,
                Activator.CreateInstance(harmonyMethodType, prefix),
                Activator.CreateInstance(harmonyMethodType, postfix)))
            {
                patched++;
            }
        }

        if (BenchmarkSettings.EnablePathOverlayAmortizedRebuild)
        {
            var drawerType = FindType("Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer");
            var lateUpdateMethod = drawerType?.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var prefix = typeof(PathOverlayAmortizer).GetMethod(nameof(PathOverlayAmortizer.AmortizedLateUpdate), BindingFlags.Static | BindingFlags.NonPublic);
            if (lateUpdateMethod is null || prefix is null)
            {
                Debug.LogWarning("[T3MP] Path overlay amortizer targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, lateUpdateMethod, Activator.CreateInstance(harmonyMethodType, prefix), null))
            {
                patched++;
            }
        }
        else if (BenchmarkSettings.EnablePathOverlayRebuildThrottle)
        {
            var drawerType = FindType("Timberborn.BuildingsNavigation.DistrictPathNavRangeDrawer");
            var lateUpdateMethod = drawerType?.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var prefix = typeof(BenchmarkProbe).GetMethod(nameof(ThrottleDrawerLateUpdateCall), BindingFlags.Static | BindingFlags.NonPublic);
            var postfix = typeof(BenchmarkProbe).GetMethod(nameof(ThrottleDrawerLateUpdateReturn), BindingFlags.Static | BindingFlags.NonPublic);
            if (lateUpdateMethod is null || prefix is null || postfix is null)
            {
                Debug.LogWarning("[T3MP] Path overlay throttle targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, lateUpdateMethod,
                Activator.CreateInstance(harmonyMethodType, prefix),
                Activator.CreateInstance(harmonyMethodType, postfix)))
            {
                patched++;
            }
        }

        if (BenchmarkSettings.EnablePathOverlayInvalidationFilter)
        {
            var invalidatorType = FindType("Timberborn.BuildingsNavigation.PathNavRangeDrawerInvalidator");
            var targetMethod = invalidatorType?.GetMethod("OnInstantNavMeshUpdated", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var prefix = typeof(BenchmarkProbe).GetMethod(nameof(FilterOverlayInvalidationCall), BindingFlags.Static | BindingFlags.NonPublic);
            if (targetMethod is null || prefix is null)
            {
                Debug.LogWarning("[T3MP] Overlay invalidation filter targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, targetMethod, Activator.CreateInstance(harmonyMethodType, prefix), null))
            {
                patched++;
            }
        }

        if (BenchmarkSettings.EnableFlowFieldFastPath)
        {
            var generatorType = FindType("Timberborn.Navigation.RoadFlowFieldGenerator");
            var fillMethod = generatorType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method => method.Name == "FillFlowField");
            var prefix = typeof(FlowFieldFastPath).GetMethod(nameof(FlowFieldFastPath.FastFillFlowField), BindingFlags.Static | BindingFlags.NonPublic);
            if (fillMethod is null || prefix is null)
            {
                Debug.LogWarning("[T3MP] Flow field fast path targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, fillMethod, Activator.CreateInstance(harmonyMethodType, prefix), null))
            {
                patched++;
            }
        }

        if (BenchmarkSettings.EnableModelUpdateBatching)
        {
            var controllerType = FindType("Timberborn.BlockObjectModelSystem.BlockObjectModelController");
            var updateModelMethod = controllerType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method => method.Name == "UpdateModel");
            var prefix = typeof(BenchmarkProbe).GetMethod(nameof(DeferModelUpdateCall), BindingFlags.Static | BindingFlags.NonPublic);
            if (updateModelMethod is null || prefix is null)
            {
                Debug.LogWarning("[T3MP] Model update batching targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, updateModelMethod, Activator.CreateInstance(harmonyMethodType, prefix), null))
            {
                patched++;
            }
        }

        if (BenchmarkSettings.EnablePreviewPlacerSkip)
        {
            var placerType = FindType("Timberborn.BlockObjectTools.PreviewPlacer");
            var showMethod = placerType?.GetMethod("ShowPreviews", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var showPrefix = typeof(BenchmarkProbe).GetMethod(nameof(SkipUnchangedShowPreviews), BindingFlags.Static | BindingFlags.NonPublic);
            var invalidatePostfix = typeof(BenchmarkProbe).GetMethod(nameof(InvalidatePreviewPlacerCache), BindingFlags.Static | BindingFlags.NonPublic);
            var hideMethod = placerType?.GetMethod("HideAllPreviews", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            var buildableMethod = placerType?.GetMethod("GetBuildableCoordinates", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (showMethod is null || showPrefix is null || invalidatePostfix is null || hideMethod is null || buildableMethod is null)
            {
                Debug.LogWarning("[T3MP] Preview placer skip targets were not found.");
            }
            else if (TryPatch(harmony, patchMethod, hideMethod, null, Activator.CreateInstance(harmonyMethodType, invalidatePostfix)) &&
                TryPatch(harmony, patchMethod, buildableMethod, null, Activator.CreateInstance(harmonyMethodType, invalidatePostfix)) &&
                TryPatch(harmony, patchMethod, showMethod, Activator.CreateInstance(harmonyMethodType, showPrefix), null))
            {
                patched += 3;
            }
        }

        return patched;
    }

    private static bool SkipVanillaRefreshHighlight(object __instance)
    {
        return TopologyUiOptimizer.RefreshHighlightDiff(__instance);
    }

    private static void RecordUnhighlightAllSecondary()
    {
        TopologyUiOptimizer.OnUnhighlightAllSecondary();
    }

    private static void CoalesceHighlightLateUpdateCall(object __instance)
    {
        TopologyUiOptimizer.BeforeHighlightLateUpdate(__instance);
    }

    private static void CoalesceHighlightLateUpdateReturn(object __instance)
    {
        TopologyUiOptimizer.AfterHighlightLateUpdate(__instance);
    }

    private static void ThrottleDrawerLateUpdateCall(object __instance)
    {
        TopologyUiOptimizer.BeforeDrawerLateUpdate(__instance);
    }

    private static void ThrottleDrawerLateUpdateReturn(object __instance)
    {
        TopologyUiOptimizer.AfterDrawerLateUpdate(__instance);
    }

    private static bool DeferModelUpdateCall(object __instance)
    {
        return TopologyUiOptimizer.DeferModelUpdate(__instance);
    }

    private static bool FilterOverlayInvalidationCall(object __instance, Timberborn.Navigation.NavMeshUpdate navMeshUpdate)
    {
        return TopologyUiOptimizer.FilterOverlayInvalidation(__instance, navMeshUpdate);
    }

    private static bool SkipUnchangedShowPreviews(object __instance, IEnumerable<Timberborn.Coordinates.Placement> placements)
    {
        return TopologyUiOptimizer.ShouldRunShowPreviews(__instance, placements);
    }

    private static void InvalidatePreviewPlacerCache(object __instance)
    {
        TopologyUiOptimizer.OnHideAllPreviews(__instance);
    }

    private static void RecordTopologyUiCall(object __instance, MethodBase __originalMethod, out TopologyUiProbe.State __state)
    {
        TopologyUiProbe.Begin(__instance, __originalMethod, out __state);
    }

    private static void RecordTopologyUiReturn(TopologyUiProbe.State __state)
    {
        TopologyUiProbe.End(in __state);
    }

    private static void RecordEntitySelectionServiceForScenario(object __instance)
    {
        TopologyUiScenario.RecordEntitySelectionService(__instance);
    }

    private static void RecordMechanicalGraphRegistryForScenario(object __instance)
    {
        TopologyUiScenario.RecordMechanicalGraphRegistry(__instance);
    }

    private static void RecordPathRangeDrawerForScenario(object __instance)
    {
        TopologyUiScenario.RecordPathRangeDrawerOwner(__instance);
    }

    private static void CancelRenderBlackoutOnOptionsMenu()
    {
        BenchmarkModeController.CancelRenderBlackoutForMenu();
    }

    // ------------------------------------------------------------------
    // Invisible-animator pose skip (see InvisibleAnimatorPoseSkip).
    // ------------------------------------------------------------------
    private static int PatchInvisibleAnimatorPoseSkip(object harmonyInstance, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var animatorType = FindType("Timberborn.TimbermeshAnimations.TimbermeshAnimator");
        var poseMethod = animatorType?.GetMethod("UpdateAnimationUpdaters", BindingFlags.Instance | BindingFlags.NonPublic);
        var updateMethod = animatorType?.GetMethod("UpdateAnimation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        var posePrefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeApplyAnimatorPose), BindingFlags.Static | BindingFlags.NonPublic);
        var updatePrefix = typeof(BenchmarkProbe).GetMethod(nameof(RepairFinishedAnimatorPose), BindingFlags.Static | BindingFlags.NonPublic);
        var playingFinishedProperty = animatorType?.GetProperty("PlayingFinished", BindingFlags.Instance | BindingFlags.Public);
        if (poseMethod is null || updateMethod is null || posePrefix is null || updatePrefix is null || playingFinishedProperty is null)
        {
            Debug.LogWarning("[T3MP] Invisible-animator pose skip targets were not found.");
            return 0;
        }

        InvisibleAnimatorPoseSkip.Initialize(poseMethod, playingFinishedProperty);
        var patched = 0;
        patched += TryPatch(harmonyInstance, patchMethod, poseMethod, Activator.CreateInstance(harmonyMethodType, posePrefix), null) ? 1 : 0;
        patched += TryPatch(harmonyInstance, patchMethod, updateMethod, Activator.CreateInstance(harmonyMethodType, updatePrefix), null) ? 1 : 0;
        return patched;
    }

    private static bool MaybeApplyAnimatorPose(object __instance)
    {
        return InvisibleAnimatorPoseSkip.ShouldApplyPose((Component)__instance);
    }

    private static void RepairFinishedAnimatorPose(object __instance)
    {
        InvisibleAnimatorPoseSkip.RepairFinishedPose((Component)__instance);
    }

    private static int PatchWaterObjectServiceFastSkip(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableWaterObjectServiceFastSkip)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WaterObjects.WaterObjectService") ??
            TryLoadAssemblyAndFindType("Timberborn.WaterObjects", "Timberborn.WaterObjects.WaterObjectService");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunWaterObjectServiceFastSkip), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] WaterObjectService fast-skip target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Tick" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] WaterObjectService.Tick method was not found for fast-skip.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchThreadSafeWaterMapTickThrottle(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableThreadSafeWaterMapTickThrottle ||
            BenchmarkSettings.ThreadSafeWaterMapTickThrottleTicks <= 1)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunThreadSafeWaterMapTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] ThreadSafeWaterMap tick throttle patch method was not found.");
            return 0;
        }

        return PatchSingleNoArgumentVoidMethod(
            harmony,
            harmonyMethodType,
            patchMethod,
            "Timberborn.WaterSystem.ThreadSafeWaterMap",
            "Tick",
            prefix);
    }

    private static int PatchThreadSafeWaterFlowDirectionThrottle(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableThreadSafeWaterFlowDirectionThrottle ||
            BenchmarkSettings.ThreadSafeWaterFlowDirectionIntervalTicks <= 1)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.WaterSystem.ThreadSafeWaterMap") ??
            TryLoadAssemblyAndFindType("Timberborn.WaterSystem", "Timberborn.WaterSystem.ThreadSafeWaterMap");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeUpdateThreadSafeWaterFlowDirections), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] ThreadSafeWaterMap flow direction target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "UpdateWaterFlowDirections" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 2 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] ThreadSafeWaterMap.UpdateWaterFlowDirections method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchRangedEffectSubjectThrottle(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableRangedEffectSubjectThrottle ||
            BenchmarkSettings.RangedEffectSubjectThrottleTicks <= 1)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.RangedEffectSystem.RangedEffectSubject") ??
            TryLoadAssemblyAndFindType("Timberborn.RangedEffectSystem", "Timberborn.RangedEffectSystem.RangedEffectSubject");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunRangedEffectSubjectTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] RangedEffectSubject throttle target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Tick" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] RangedEffectSubject.Tick method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchContaminationApplierThrottle(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableContaminationApplierThrottle ||
            BenchmarkSettings.ContaminationApplierThrottleTicks <= 1)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.BeaverContaminationSystem.ContaminationApplier") ??
            TryLoadAssemblyAndFindType("Timberborn.BeaverContaminationSystem", "Timberborn.BeaverContaminationSystem.ContaminationApplier");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunContaminationApplierTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null)
        {
            Debug.LogWarning("[T3MP] ContaminationApplier throttle target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
                method.Name == "Tick" &&
                method.ReturnType == typeof(void) &&
                method.GetParameters().Length == 0 &&
                !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] ContaminationApplier.Tick method was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static int PatchLoadComponentProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableLoadComponentProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Load component profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract || type.ContainsGenericParameters || type.FullName is null ||
                !type.FullName.StartsWith("Timberborn.", StringComparison.Ordinal))
            {
                continue;
            }

            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.WorldPersistence.IPersistentEntity", "Load");
            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.EntitySystem.IPreInitializableEntity", "PreInitializeEntity");
            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.EntitySystem.IInitializableEntity", "InitializeEntity");
            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.EntitySystem.IPostInitializableEntity", "PostInitializeEntity");
            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.EntitySystem.IPostLoadableEntity", "PostLoadEntity");
            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.WorldPersistence.IEntityBatchLoader", "BatchLoadEntities");
            if (BenchmarkSettings.EnableLoadStateListenerProfiler)
            {
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.BlockSystem.IFinishedStateListener", "OnEnterFinishedState");
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.BlockSystem.IUnfinishedStateListener", "OnEnterUnfinishedState");
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.BlockSystem.IPreviewStateListener", "OnEnterPreviewState");
            }
        }

        return patched;
    }

    private static int PatchLoadSingletonProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableLoadSingletonProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Load singleton profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract || type.ContainsGenericParameters || type.FullName is null ||
                !type.FullName.StartsWith("Timberborn.", StringComparison.Ordinal))
            {
                continue;
            }

            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.SingletonSystem.ILoadableSingleton", "Load");
            patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.SingletonSystem.IPostLoadableSingleton", "PostLoad");
        }

        return patched;
    }

    private static int PatchLoadEventProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableLoadEventProfiler)
        {
            return 0;
        }

        var eventBusPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadEventBusCall), BindingFlags.Static | BindingFlags.NonPublic);
        var eventBusPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentReturn), BindingFlags.Static | BindingFlags.NonPublic);
        var eventHandlerPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadEventHandlerCall), BindingFlags.Static | BindingFlags.NonPublic);
        var eventHandlerPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (eventBusPrefix is null || eventBusPostfix is null || eventHandlerPrefix is null || eventHandlerPostfix is null)
        {
            Debug.LogWarning("[T3MP] Load event profiler patch methods were not found.");
            return 0;
        }

        var eventBusPrefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, eventBusPrefix);
        var eventBusPostfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, eventBusPostfix);
        var eventHandlerPrefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, eventHandlerPrefix);
        var eventHandlerPostfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, eventHandlerPostfix);
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;

        var eventBusType = FindType("Timberborn.SingletonSystem.EventBus");
        if (eventBusType is not null)
        {
            foreach (var method in eventBusType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(method => (method.Name == "Post" || method.Name == "PostNow") &&
                             method.ReturnType == typeof(void) &&
                             method.GetParameters().Length == 1 &&
                             method.GetParameters()[0].ParameterType == typeof(object) &&
                             !method.ContainsGenericParameters))
            {
                if (patchedMethods.Add(method) && TryPatch(harmony, patchMethod, method, eventBusPrefixHarmonyMethod, eventBusPostfixHarmonyMethod))
                {
                    patched++;
                }
            }
        }
        else
        {
            Debug.LogWarning("[T3MP] Load event profiler target type was not found: Timberborn.SingletonSystem.EventBus");
        }

        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract || type.ContainsGenericParameters || type.FullName is null ||
                !type.FullName.StartsWith("Timberborn.", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                         .Where(method =>
                             method.ReturnType == typeof(void) &&
                             method.GetParameters().Length == 1 &&
                             HasOnEventAttribute(method) &&
                             !method.IsAbstract &&
                             !method.ContainsGenericParameters))
            {
                if (patchedMethods.Add(method) && TryPatch(harmony, patchMethod, method, eventHandlerPrefixHarmonyMethod, eventHandlerPostfixHarmonyMethod))
                {
                    patched++;
                }
            }
        }

        return patched;
    }

    private static int PatchLoadHotspotProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableLoadComponentProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordLoadComponentReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Load hotspot profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalNode", "InitializeTransputs");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalNode", "AddOrRemoveFromGraph");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalNode", "AddToGraph");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalNode", "InitializeActuals");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalGraphManager", "AddNode");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalGraphFactory", "Join");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalGraph", "AddNode");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalGraphRegistry", "AddGraph");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.MechanicalGraphRegistry", "RemoveGraph");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.TransputMap", "AddNode");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.MechanicalSystem.TransputMap", "GetFacingTransput");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.RangedEffectSystem.RangedEffectBuilding", "UpdateRangedEffectApplierState");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.RangedEffectSystem.RangedEffectBuilding", "ToggleActiveStateInternal");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.RangedEffectSystem.RangedEffectApplier", "Enable");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.RangedEffectSystem.RangedEffectService", "SetApplier");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.RangedEffectSystem.RangedEffectService", "AddApplierToExistingEnterablesAt");
        patched += PatchLoadHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.RangedEffectSystem.RangedEffectService", "SetExistingAppliersToEnterable");
        return patched;
    }

    private static int PatchLoadHotspotMethod(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod,
        HashSet<MethodBase> patchedMethods,
        string typeName,
        string methodName)
    {
        var targetType = FindType(typeName);
        if (targetType is null)
        {
            Debug.LogWarning($"[T3MP] Load hotspot target was not found: {typeName}.{methodName}");
            return 0;
        }

        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => method.Name == methodName && !method.IsAbstract && !method.ContainsGenericParameters))
        {
            if (patchedMethods.Add(method) && TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
            {
                patched++;
            }
        }

        if (patched == 0)
        {
            Debug.LogWarning($"[T3MP] Load hotspot target method was not patched: {typeName}.{methodName}");
        }

        return patched;
    }

    private static int PatchStutterDetailProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            return 0;
        }

        var patched = 0;
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.RangedEffectSystem.RangedEffectBuilding",
            "Tick",
            nameof(RecordRangedEffectBuildingTickCall),
            nameof(RecordRangedEffectBuildingTickReturn));
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.RangedEffectSystem.RangedEffectApplier",
            "UpdateActiveState",
            nameof(RecordRangedEffectApplierUpdateCall),
            nameof(RecordRangedEffectApplierUpdateReturn));
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.StatusSystem.StatusAggregator",
            "UpdateSingleton",
            nameof(RecordStatusAggregatorUpdateCall),
            nameof(RecordStatusAggregatorUpdateReturn));
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.DwellingSystem.UnreachableHomeUnassigner",
            "Tick",
            nameof(RecordUnreachableHomeTickCall),
            nameof(RecordUnreachableHomeTickReturn));
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.Navigation.NavMeshListenerEntityRegistry",
            "NotifyAll",
            nameof(RecordNavMeshNotifyCall),
            nameof(RecordNavMeshNotifyReturn));
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.Navigation.NavMeshListenerEntityRegistry",
            "NotifyAllInstant",
            nameof(RecordNavMeshNotifyCall),
            nameof(RecordNavMeshNotifyReturn));
        return patched;
    }

    private static int PatchRangedEffectSubjectProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableRangedEffectSubjectProfiler)
        {
            return 0;
        }

        var patched = 0;
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.RangedEffectSystem.RangedEffectSubject",
            "Tick",
            nameof(RecordRangedEffectSubjectTickCall),
            nameof(RecordRangedEffectSubjectTickReturn));
        patched += PatchSpecificMethod(
            harmony,
            patchMethod,
            harmonyMethodType,
            "Timberborn.RangedEffectSystem.RangedEffectSubject",
            "GetAffectingEffects",
            nameof(RecordRangedEffectSubjectGetEffectsCall),
            nameof(RecordRangedEffectSubjectGetEffectsReturn));
        return patched;
    }

    private static int PatchSpecificMethod(
        object harmony,
        MethodInfo patchMethod,
        Type harmonyMethodType,
        string typeName,
        string methodName,
        string prefixName,
        string postfixName)
    {
        var targetType = FindType(typeName);
        var prefix = typeof(BenchmarkProbe).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null || postfix is null)
        {
            Debug.LogWarning($"[T3MP] Stutter detail target was not found: {typeName}.{methodName}");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == methodName && !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning($"[T3MP] Stutter detail target method was not found: {typeName}.{methodName}");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static int PatchRuntimeHotspotProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordRuntimeHotspotCall), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordRuntimeHotspotReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Runtime hotspot profiler patch methods were not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;

        patched += PatchRuntimeHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.DwellingSystem.Dweller", "UnassignFromHome");
        patched += PatchRuntimeHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.DwellingSystem.Dwelling", "UnassignDweller");
        patched += PatchRuntimeHotspotMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, "Timberborn.DwellingSystem.Dwelling", "UnassignAllDwellers");

        return patched;
    }

    private static int PatchAnimatorRegistry(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if ((!BenchmarkSettings.EnableAnimatorRegistryThrottle ||
             BenchmarkSettings.AnimatorRegistryThrottleFrames <= 1) &&
            !BenchmarkSettings.EnableAnimatorRegistryDetailProfiler &&
            !BenchmarkSettings.EnableDefaultMechanicalAnimatorRegistryReplacement)
        {
            return 0;
        }

        var targetType = FindType("Timberborn.TimbermeshAnimations.AnimatorRegistry");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunAnimatorRegistryUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordAnimatorRegistryUpdateReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] AnimatorRegistry throttle target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == "UpdateSingleton" && !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] AnimatorRegistry.UpdateSingleton was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static bool MaybeRunAnimatorRegistryUpdate(object __instance, out AnimatorRegistryProfiler.CallState __state)
    {
        __state = AnimatorRegistryProfiler.CallState.Inactive;
        // Gate on the mod's Optimized mode, NOT on TryGetSampleMode (which is a
        // measurement-only flag). Otherwise the animation thinning and
        // MechanicalDirectRotation optimization only run during dev benchmark
        // runs and are inert in the shipped build.
        var mode = BenchmarkModeController.CurrentMode;
        if (mode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (mode == BenchmarkMode.Optimized &&
            BenchmarkSettings.EnableUnattendedVisualSuppression &&
            BenchmarkModeController.RenderBlackoutActive)
        {
            return false;
        }

        MechanicalAnimationBatchProbe.MaybeSample(__instance, mode);

        if (mode == BenchmarkMode.Optimized &&
            !BenchmarkModeController.RenderBlackoutActive)
        {
            // Visible high-speed play (smooth frame pacing active): sample the
            // Timbermesh animations only every Nth rendered frame. Movement
            // stays per-frame smooth (MovementAnimator moves transforms); only
            // the skeletal pose updates at a lower rate, which is imperceptible
            // at the speeds where pacing engages. Normal speeds never reach
            // here with pacing active, so regular play keeps full-rate
            // animation.
            if (BenchmarkModeController.SmoothFramePacingActive &&
                BenchmarkSettings.SmoothPacingAnimationFrameStride > 1 &&
                Time.frameCount % BenchmarkSettings.SmoothPacingAnimationFrameStride != 0)
            {
                return false;
            }

            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                AnimatorRegistryProfiler.Begin(__instance, mode, true, out __state);
            }

            return true;
        }

        if (mode == BenchmarkMode.Optimized &&
            BenchmarkSettings.EnableMechanicalDirectRotationOptimizer)
        {
            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                AnimatorRegistryProfiler.Begin(__instance, mode, true, out __state);
            }

            if (MechanicalDirectRotationOptimizer.TryUpdateRegistry(__instance))
            {
                return false;
            }

            return true;
        }

        if (mode == BenchmarkMode.Optimized &&
            BenchmarkSettings.EnableDefaultMechanicalAnimatorRegistryReplacement &&
            BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle &&
            BenchmarkSettings.DefaultMechanicalAnimatorThrottleFrames > 1)
        {
            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                AnimatorRegistryProfiler.Begin(__instance, mode, true, out __state);
            }

            if (DefaultMechanicalAnimatorOptimizer.TryUpdateRegistry(__instance))
            {
                return false;
            }

            return true;
        }

        var shouldRun = mode != BenchmarkMode.Optimized ||
            !BenchmarkSettings.EnableAnimatorRegistryThrottle ||
            BenchmarkSettings.AnimatorRegistryThrottleFrames <= 1 ||
            Time.frameCount % BenchmarkSettings.AnimatorRegistryThrottleFrames == 0;
        if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
        {
            AnimatorRegistryProfiler.Begin(__instance, mode, shouldRun, out __state);
        }

        return shouldRun;
    }

    private static void RecordAnimatorRegistryUpdateReturn(AnimatorRegistryProfiler.CallState __state)
    {
        AnimatorRegistryProfiler.End(__state);
    }

    private static int PatchDefaultMechanicalAnimatorOptimizer(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableDefaultMechanicalAnimatorUpdatePatch ||
            (!BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle &&
             !BenchmarkSettings.EnableDefaultMechanicalAnimatorDetailProfiler))
        {
            return 0;
        }

        var targetType = FindType("Timberborn.TimbermeshAnimations.TimbermeshAnimator");
        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunDefaultMechanicalAnimatorUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordDefaultMechanicalAnimatorUpdateReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (targetType is null || prefix is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Default mechanical animator target was not found.");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method =>
            {
                if (method.Name != "UpdateAnimation" || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(float);
            });
        if (targetMethod is null)
        {
            Debug.LogWarning("[T3MP] TimbermeshAnimator.UpdateAnimation(float) was not found.");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static bool MaybeRunDefaultMechanicalAnimatorUpdate(
        object __instance,
        ref float deltaTime,
        out DefaultMechanicalAnimatorOptimizer.CallState __state)
    {
        return DefaultMechanicalAnimatorOptimizer.Begin(__instance, ref deltaTime, out __state);
    }

    private static void RecordDefaultMechanicalAnimatorUpdateReturn(DefaultMechanicalAnimatorOptimizer.CallState __state)
    {
        DefaultMechanicalAnimatorOptimizer.End(__state);
    }

    private static int PatchVisualUpdateThrottles(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        var patched = 0;
        if (BenchmarkSettings.EnableTubeVisitorUpdaterThrottle &&
            BenchmarkSettings.TubeVisitorUpdaterThrottleFrames > 1)
        {
            var targetType = FindType("Timberborn.TubeSystem.TubeVisitorUpdater");
            var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunTubeVisitorUpdaterUpdate), BindingFlags.Static | BindingFlags.NonPublic);
            if (targetType is null || prefix is null)
            {
                Debug.LogWarning("[T3MP] TubeVisitorUpdater throttle target was not found.");
            }
            else
            {
                var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(method => method.Name == "UpdateSingleton" && method.ReturnType == typeof(void) && !method.ContainsGenericParameters);
                if (targetMethod is null)
                {
                    Debug.LogWarning("[T3MP] TubeVisitorUpdater.UpdateSingleton was not found.");
                }
                else
                {
                    var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
                    if (TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null))
                    {
                        patched++;
                    }
                }
            }
        }

        if (BenchmarkSettings.EnableStatusIconPositionerThrottle &&
            BenchmarkSettings.StatusIconPositionerThrottleFrames > 1)
        {
            var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunStatusIconPositionerLateUpdate), BindingFlags.Static | BindingFlags.NonPublic);
            if (prefix is null)
            {
                Debug.LogWarning("[T3MP] StatusIconPositioner throttle patch method was not found.");
            }
            patched += PatchSingleNoArgumentVoidMethod(
                harmony,
                harmonyMethodType,
                patchMethod,
                "Timberborn.CharacterModelSystem.CharacterStatusIconCyclerPositioner",
                "LateUpdate",
                prefix!);
        }

        return patched;
    }

    private static bool MaybeRunTubeVisitorUpdaterUpdate()
    {
        if (!BenchmarkModeController.TryGetSampleMode(out var mode) ||
            mode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!BenchmarkModeController.RenderBlackoutActive)
        {
            return true;
        }

        if (BenchmarkSettings.EnableUnattendedVisualSuppression)
        {
            return false;
        }

        return Time.frameCount % BenchmarkSettings.TubeVisitorUpdaterThrottleFrames == 0;
    }

    private static bool MaybeRunStatusIconPositionerLateUpdate(object __instance)
    {
        if (BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!BenchmarkModeController.RenderBlackoutActive)
        {
            return true;
        }

        return ShouldRunStaggeredVisualUpdate(__instance, BenchmarkSettings.StatusIconPositionerThrottleFrames);
    }

    private static int PatchStatusAggregatorThrottle(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableStatusAggregatorThrottle ||
            BenchmarkSettings.StatusAggregatorThrottleFrames <= 1)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunStatusAggregatorUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] StatusAggregator throttle patch method was not found.");
            return 0;
        }

        return PatchSingleNoArgumentVoidMethod(
            harmony,
            harmonyMethodType,
            patchMethod,
            "Timberborn.StatusSystem.StatusAggregator",
            "UpdateSingleton",
            prefix);
    }

    private static bool MaybeRunStatusAggregatorUpdate(object __instance)
    {
        return StatusAggregatorThrottle.ShouldRunOriginal(__instance);
    }

    private static bool ShouldRunStaggeredVisualUpdate(object instance, int throttleFrames)
    {
        if (throttleFrames <= 1)
        {
            return true;
        }

        var hash = RuntimeHelpers.GetHashCode(instance) & int.MaxValue;
        return (Time.frameCount + hash) % throttleFrames == 0;
    }

    private static int PatchTickVisualSingletonThrottles(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableTickVisualSingletonThrottle)
        {
            return 0;
        }

        var waterRendererPrefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunWaterRendererTick), BindingFlags.Static | BindingFlags.NonPublic);
        var modularShaftPrefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunModularShaftAnimatorTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (waterRendererPrefix is null || modularShaftPrefix is null)
        {
            Debug.LogWarning("[T3MP] Tick visual throttle patch methods were not found.");
            return 0;
        }

        var patched = 0;
        if (BenchmarkSettings.WaterRendererThrottleTicks > 1)
        {
            patched += PatchSingleNoArgumentVoidMethod(
                harmony,
                harmonyMethodType,
                patchMethod,
                "Timberborn.WaterSystemRendering.WaterRenderer",
                "Tick",
                waterRendererPrefix);
        }

        if (BenchmarkSettings.ModularShaftAnimatorThrottleTicks > 1)
        {
            patched += PatchSingleNoArgumentVoidMethod(
                harmony,
                harmonyMethodType,
                patchMethod,
                "Timberborn.ModularShafts.ModularShaftAnimatorUpdater",
                "Tick",
                modularShaftPrefix);
        }

        return patched;
    }

    private static int PatchSingleNoArgumentVoidMethod(
        object harmony,
        Type harmonyMethodType,
        MethodInfo patchMethod,
        string typeName,
        string methodName,
        MethodInfo prefix)
    {
        var targetType = FindType(typeName);
        if (targetType is null)
        {
            Debug.LogWarning($"[T3MP] Tick visual throttle target was not found: {typeName}.{methodName}");
            return 0;
        }

        var targetMethod = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(method => method.Name == methodName && method.ReturnType == typeof(void) && method.GetParameters().Length == 0 && !method.ContainsGenericParameters);
        if (targetMethod is null)
        {
            Debug.LogWarning($"[T3MP] Tick visual throttle method was not found: {typeName}.{methodName}");
            return 0;
        }

        var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, prefix);
        return TryPatch(harmony, patchMethod, targetMethod, prefixHarmonyMethod, null) ? 1 : 0;
    }

    private static bool MaybeRunWaterRendererTick(object __instance)
    {
        if (BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!BenchmarkModeController.RenderBlackoutActive)
        {
            return true;
        }

        if (BenchmarkSettings.EnableUnattendedVisualSuppression)
        {
            return false;
        }

        return ShouldRunStaggeredVisualUpdate(__instance, BenchmarkSettings.WaterRendererThrottleTicks);
    }

    private static bool MaybeRunModularShaftAnimatorTick(object __instance)
    {
        if (BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!BenchmarkModeController.RenderBlackoutActive)
        {
            return true;
        }

        if (BenchmarkSettings.EnableUnattendedVisualSuppression)
        {
            return false;
        }

        return ShouldRunStaggeredVisualUpdate(__instance, BenchmarkSettings.ModularShaftAnimatorThrottleTicks);
    }

    private static int PatchUnattendedVisualSuppression(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableUnattendedVisualSuppression)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunUnattendedVisualUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        var tickPrefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunUnattendedVisualTick), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null || tickPrefix is null)
        {
            Debug.LogWarning("[T3MP] Unattended visual suppression patch method was not found.");
            return 0;
        }

        var patched = 0;
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.CharacterModelSystem.CharacterStatusIconCyclerPositioner", "LateUpdate", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.CameraSystem.FacingCamera", "LateUpdate", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.CoreSound.SoundListener", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.CoreSound.CameraHeightVolumeUpdater", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.GameSound.SoundListenerDebugger", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WalkingSystemUI.WalkerDebugger", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.CameraSystem.CameraService", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.CameraSystem.ShadowDistanceUpdater", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.SkySystem.Sun", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.AreaSelectionSystemUI.MeasurableAreaDrawer", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.Rendering.TickProgressPropertyUpdater", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TerrainSystemRendering.TerrainMaterialMap", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TerrainSystemRendering.TerrainMaterialMap", "Tick", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TerrainSystemRendering.TerrainMeshManager", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TerrainSystemRendering.TerrainHighlightingService", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WaterSourceRendering.WaterSourceRenderingService", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WaterSourceRendering.WaterSourceRenderingService", "Tick", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.ConstructionGuidelines.ConstructionGuidelinesRenderingService", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.DeconstructionSystem.DeconstructionParticleFactory", "LateUpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.SoundSystem.AudioSourceFader", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.StatusSystem.StatusAggregator", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.StatusSystem.DynamicStatusAggregator", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.StatusSystem.StatusIconCyclerUpdater", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.StatusSystem.StatusSlotsUpdater", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TopBarSystem.TopBarPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.SteamStoreSystem.SteamManager", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TooltipSystem.TooltipContainer", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TutorialSystemUI.TutorialPanels", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.DiagnosticsUI.FramesPerSecondPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WeatherSystemUI.WeatherPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WellbeingUI.BasicStatisticsPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.AlertPanelSystem.AlertPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.SkySystem.SkyboxPositioner", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TimeSystemUI.ClockPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.DemolishingUI.DemolishableMarkerService", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.BlockObjectToolsUI.BlockObjectToolWarningPanel", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.UILayoutSystem.DebugUIScaleChanger", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.TimeSystem.NonlinearAnimationManager", "UpdateSingleton", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WaterSystemRendering.WaterRenderer", "StartParallelTick", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.ModularShafts.ShaftSoundEmitter", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.SlotSystem.FixedSlotManager", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.BlockObstacles.LayeredBlockObstacleVisualizer", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.ActivatorSystem.ActivationProgressParticles", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WaterSourceSystem.WaterSourceRegulatorAnimationController", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.Terraforming.DrillScrewRotator", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.Terraforming.DrillHeadVisualizer", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.MechanicalSystem.MechanicalNodeTransformHeight", "Update", prefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.Buildings.FireIntensityController", "Update", prefix);
        // Tick-driven cosmetic components (sounds, worker hiding, workplace
        // lights, particles) use the peek-agnostic gate: their effects are
        // invisible during the blackout AND during the one-frame peek, so the
        // dozens of ticks a clamped peek frame carries should not run them.
        // Frame-driven updaters above stay on RenderBlackoutActive so the peek
        // frame itself renders fresh.
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WorkshopsEffects.WorkshopSounds", "Tick", tickPrefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WorkshopsEffects.WorkshopWorkerHider", "Tick", tickPrefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.SleepSystem.SleepSoundEmitter", "Tick", tickPrefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WaterBuildings.WaterMoverParticleController", "Tick", tickPrefix);
        patched += PatchSingleNoArgumentVoidMethod(harmony, harmonyMethodType, patchMethod, "Timberborn.WorkSystemUI.WorkplaceIlluminator", "Tick", tickPrefix);
        return patched;
    }

    private static bool MaybeRunUnattendedVisualUpdate()
    {
        return !BenchmarkSettings.EnableUnattendedVisualSuppression ||
            !BenchmarkModeController.RenderBlackoutActive;
    }

    private static bool MaybeRunUnattendedVisualTick()
    {
        return !BenchmarkSettings.EnableUnattendedVisualSuppression ||
            !BenchmarkModeController.BlackoutTickSuppressionActive;
    }

    private static int PatchSoundListenerStaticCameraOptimizer(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableSoundListenerStaticCameraOptimizer ||
            BenchmarkSettings.SoundListenerStaticCameraIntervalFrames <= 1)
        {
            return 0;
        }

        var prefix = typeof(BenchmarkProbe).GetMethod(nameof(MaybeRunSoundListenerLateUpdate), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            Debug.LogWarning("[T3MP] SoundListener static-camera optimizer patch method was not found.");
            return 0;
        }

        return PatchSingleNoArgumentVoidMethod(
            harmony,
            harmonyMethodType,
            patchMethod,
            "Timberborn.CoreSound.SoundListener",
            "LateUpdateSingleton",
            prefix);
    }

    private static bool MaybeRunSoundListenerLateUpdate(object __instance)
    {
        return SoundListenerStaticCameraOptimizer.ShouldRunOriginal(__instance);
    }

    private static int PatchRuntimeHotspotMethod(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod,
        HashSet<MethodBase> patchedMethods,
        string typeName,
        string methodName)
    {
        var targetType = FindType(typeName);
        if (targetType is null)
        {
            Debug.LogWarning($"[T3MP] Runtime hotspot target was not found: {typeName}.{methodName}");
            return 0;
        }

        var patched = 0;
        foreach (var method in targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(method => method.Name == methodName && !method.IsAbstract && !method.ContainsGenericParameters))
        {
            if (patchedMethods.Add(method) && TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
            {
                patched++;
            }
        }

        if (patched == 0)
        {
            Debug.LogWarning($"[T3MP] Runtime hotspot target method was not patched: {typeName}.{methodName}");
        }

        return patched;
    }

    private static int PatchMainLoopProfiler(object harmony, Type harmonyType, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableMainLoopProfiler)
        {
            return 0;
        }

        var stagePrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordMainLoopStageCall), BindingFlags.Static | BindingFlags.NonPublic);
        var stagePostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordMainLoopStageReturn), BindingFlags.Static | BindingFlags.NonPublic);
        var typePrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordMainLoopTypeCall), BindingFlags.Static | BindingFlags.NonPublic);
        var typePostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordMainLoopTypeReturn), BindingFlags.Static | BindingFlags.NonPublic);
        if (stagePrefix is null || stagePostfix is null || typePrefix is null || typePostfix is null)
        {
            Debug.LogWarning("[T3MP] Main loop profiler patch methods were not found.");
            return 0;
        }

        var stagePrefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, stagePrefix);
        var stagePostfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, stagePostfix);
        var typePrefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, typePrefix);
        var typePostfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, typePostfix);

        var patched = 0;
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.Ticker", "Update");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableBucketService", "TickBuckets");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableSingletonService", "TickAll");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableSingletonService", "FinishParallelTick");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableSingletonService", "TickSingletons");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableSingletonService", "StartParallelTick");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableEntityBucket", "TickAll");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.TickableEntity", "Tick");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.TickSystem.MeteredTickableComponent", "Tick");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "UpdateAll");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "UpdateSingletons");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "LateUpdateAll");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.SingletonSystem.SingletonLifecycleService", "LateUpdateSingletons");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.BaseComponentSystem.BaseComponentUpdateUnityAdapter", "Update");
        patched += PatchMainLoopStageMethod(harmony, patchMethod, stagePrefixHarmonyMethod, stagePostfixHarmonyMethod, "Timberborn.BaseComponentSystem.BaseComponentLateUpdateUnityAdapter", "LateUpdate");

        if (BenchmarkSettings.EnableMainLoopTypeProfiler || BenchmarkSettings.EnableMainLoopUpdateTypeProfiler)
        {
            patched += PatchMainLoopTypeMethods(harmony, patchMethod, typePrefixHarmonyMethod, typePostfixHarmonyMethod);
        }

        return patched;
    }

    private static int PatchMainLoopStageMethod(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod,
        string typeName,
        string methodName)
    {
        var targetType = FindType(typeName);
        if (targetType is null)
        {
            Debug.LogWarning($"[T3MP] Main loop profiler target type was not found: {typeName}");
            return 0;
        }

        var methods = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName && !method.ContainsGenericParameters)
            .ToArray();
        if (methods.Length == 0)
        {
            Debug.LogWarning($"[T3MP] Main loop profiler target method was not found: {typeName}.{methodName}");
            return 0;
        }

        var patched = 0;
        foreach (var method in methods)
        {
            if (TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
            {
                patched++;
            }
        }

        return patched;
    }

    private static int PatchMainLoopTypeMethods(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod)
    {
        var patchedMethods = new HashSet<MethodBase>();
        var patched = 0;
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            if (type.IsAbstract || type.ContainsGenericParameters || type.FullName is null ||
                !type.FullName.StartsWith("Timberborn.", StringComparison.Ordinal))
            {
                continue;
            }

            if (BenchmarkSettings.EnableMainLoopTypeProfiler)
            {
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.TickSystem.ITickableSingleton", "Tick");
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.TickSystem.IParallelTickableSingleton", "StartParallelTick");
            }

            if (BenchmarkSettings.EnableMainLoopUpdateTypeProfiler)
            {
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.SingletonSystem.IUpdatableSingleton", "UpdateSingleton");
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.SingletonSystem.ILateUpdatableSingleton", "LateUpdateSingleton");
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.BaseComponentSystem.IUpdatableComponent", "Update");
                patched += PatchMainLoopInterfaceMethod(harmony, patchMethod, prefixHarmonyMethod, postfixHarmonyMethod, patchedMethods, type, "Timberborn.BaseComponentSystem.ILateUpdatableComponent", "LateUpdate");
            }

            if (BenchmarkSettings.EnableMainLoopTypeProfiler && IsAssignableTo(type, "Timberborn.TickSystem.TickableComponent"))
            {
                var tickMethod = FindNoArgumentVoidMethod(type, "Tick");
                if (tickMethod is not null && patchedMethods.Add(tickMethod) &&
                    TryPatch(harmony, patchMethod, tickMethod, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }

            if (BenchmarkSettings.EnableMainLoopTypeProfiler && IsAssignableTo(type, "Timberborn.BehaviorSystem.Behavior"))
            {
                var decideMethod = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(method => method.Name == "Decide" && !method.ContainsGenericParameters);
                if (decideMethod is not null && patchedMethods.Add(decideMethod) &&
                    TryPatch(harmony, patchMethod, decideMethod, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }

            if (BenchmarkSettings.EnableMainLoopTypeProfiler && IsAssignableTo(type, "Timberborn.NeedBehaviorSystem.NeedBehavior"))
            {
                var actionPositionMethod = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "ActionPosition" || method.ContainsGenericParameters)
                        {
                            return false;
                        }

                        var parameters = method.GetParameters();
                        return parameters.Length == 1 &&
                            parameters[0].ParameterType.FullName == "Timberborn.NeedSystem.NeedManager";
                    });
                if (actionPositionMethod is not null && patchedMethods.Add(actionPositionMethod) &&
                    TryPatch(harmony, patchMethod, actionPositionMethod, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }
        }

        return patched;
    }

    private static int PatchMainLoopInterfaceMethod(
        object harmony,
        MethodInfo patchMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod,
        HashSet<MethodBase> patchedMethods,
        Type type,
        string interfaceFullName,
        string interfaceMethodName)
    {
        var method = FindInterfaceImplementation(type, interfaceFullName, interfaceMethodName);
        if (method is null || !patchedMethods.Add(method))
        {
            return 0;
        }

        return TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod) ? 1 : 0;
    }

    private static MethodInfo? FindInterfaceImplementation(Type type, string interfaceFullName, string interfaceMethodName)
    {
        var interfaceType = type.GetInterfaces().FirstOrDefault(candidate => candidate.FullName == interfaceFullName);
        if (interfaceType is null)
        {
            return null;
        }

        try
        {
            var map = type.GetInterfaceMap(interfaceType);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var targetMethod = map.TargetMethods[i];
                if (map.InterfaceMethods[i].Name == interfaceMethodName &&
                    targetMethod.ReturnType == typeof(void) &&
                    !targetMethod.IsAbstract &&
                    !targetMethod.ContainsGenericParameters)
                {
                    return targetMethod;
                }
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    private static MethodInfo? FindExecutorTickImplementation(Type type)
    {
        var interfaceType = type.GetInterfaces().FirstOrDefault(candidate => candidate.FullName == "Timberborn.BehaviorSystem.IExecutor");
        if (interfaceType is null)
        {
            return null;
        }

        try
        {
            var map = type.GetInterfaceMap(interfaceType);
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var targetMethod = map.TargetMethods[i];
                var parameters = targetMethod.GetParameters();
                if (map.InterfaceMethods[i].Name == "Tick" &&
                    targetMethod.ReturnType == typeof(ExecutorStatus) &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(float) &&
                    !targetMethod.IsAbstract &&
                    !targetMethod.ContainsGenericParameters)
                {
                    return targetMethod;
                }
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        return null;
    }

    private static MethodInfo? FindNoArgumentVoidMethod(Type type, string methodName)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == methodName &&
                method.GetParameters().Length == 0 &&
                method.ReturnType == typeof(void) &&
                !method.IsAbstract &&
                !method.ContainsGenericParameters);
    }

    private static int PatchDecideSplitProbe(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.BenchDecideRequested)
        {
            return 0;
        }

        var patched = 0;
        try
        {
            // Behavior subclasses live in many lazily-loaded assemblies; force
            // load every Timberborn.*.dll so the enumeration below is complete.
            var managedDir = System.IO.Path.GetDirectoryName(typeof(Behavior).Assembly.Location);
            if (managedDir is not null)
            {
                foreach (var dllPath in System.IO.Directory.GetFiles(managedDir, "Timberborn.*.dll"))
                {
                    try
                    {
                        Assembly.Load(AssemblyName.GetAssemblyName(dllPath));
                    }
                    catch (Exception)
                    {
                        // Non-loadable assemblies cannot contain reachable behaviors.
                    }
                }
            }

            var decidePrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordDecideSplitCall), BindingFlags.Static | BindingFlags.NonPublic);
            var decidePostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordDecideSplitReturn), BindingFlags.Static | BindingFlags.NonPublic);
            if (decidePrefix is null || decidePostfix is null)
            {
                Debug.LogWarning("[T3MP] DecideSplit probe patch methods were not found.");
                return 0;
            }

            // Run the timing prefix FIRST (Priority.First) so optimizer prefixes
            // that replace a Decide (return false) are still timed; the postfix
            // guards on __state == 0 in case another prefix suppressed ours.
            var decideMethods = new HashSet<MethodBase>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (type is null || !IsAssignableTo(type, "Timberborn.BehaviorSystem.Behavior"))
                    {
                        continue;
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (method.Name != "Decide" ||
                            method.IsAbstract ||
                            method.ContainsGenericParameters ||
                            method.ReturnType != typeof(Decision))
                        {
                            continue;
                        }

                        var parameters = method.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(BehaviorAgent))
                        {
                            decideMethods.Add(method);
                        }
                    }
                }
            }

            foreach (var method in decideMethods)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, decidePrefix);
                harmonyMethodType.GetField("priority")?.SetValue(prefixHarmonyMethod, 800);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, decidePostfix);
                if (TryPatch(harmony, patchMethod, method, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }

            var behaviorManagerType = FindType("Timberborn.BehaviorSystem.BehaviorManager");
            var processBehavior = behaviorManagerType?.GetMethod("ProcessBehavior", BindingFlags.Instance | BindingFlags.NonPublic);
            var processPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordProcessBehaviorCall), BindingFlags.Static | BindingFlags.NonPublic);
            var processPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordProcessBehaviorReturn), BindingFlags.Static | BindingFlags.NonPublic);
            if (processBehavior is not null && processPrefix is not null && processPostfix is not null)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, processPrefix);
                harmonyMethodType.GetField("priority")?.SetValue(prefixHarmonyMethod, 800);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, processPostfix);
                if (TryPatch(harmony, patchMethod, processBehavior, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }
            else
            {
                Debug.LogWarning("[T3MP] DecideSplit probe: BehaviorManager.ProcessBehavior was not found.");
            }

            var tickRunningExecutor = behaviorManagerType?.GetMethod("TickRunningExecutor", BindingFlags.Instance | BindingFlags.NonPublic);
            var executorPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTickExecutorCall), BindingFlags.Static | BindingFlags.NonPublic);
            var executorPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordTickExecutorReturn), BindingFlags.Static | BindingFlags.NonPublic);
            if (tickRunningExecutor is not null && executorPrefix is not null && executorPostfix is not null)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, executorPrefix);
                harmonyMethodType.GetField("priority")?.SetValue(prefixHarmonyMethod, 800);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, executorPostfix);
                if (TryPatch(harmony, patchMethod, tickRunningExecutor, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }
            else
            {
                Debug.LogWarning("[T3MP] DecideSplit probe: BehaviorManager.TickRunningExecutor was not found.");
            }

            Debug.Log($"[T3MP] DecideSplit probe patched {patched} methods ({decideMethods.Count} Decide overrides found).");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] DecideSplit probe installation failed: {exception}");
        }

        return patched;
    }

    private static int PatchSentinelTemplateInjection(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableActiveInHierarchyMirror || !BenchmarkSettings.EnableSentinelTemplateInjection)
        {
            return 0;
        }

        var templateInstantiatorType = FindType("Timberborn.TemplateInstantiation.TemplateInstantiator") ??
            TryLoadAssemblyAndFindType("Timberborn.TemplateInstantiation", "Timberborn.TemplateInstantiation.TemplateInstantiator");
        var getCachedTemplate = templateInstantiatorType?.GetMethod("GetCachedTemplate", BindingFlags.Instance | BindingFlags.NonPublic);
        var postfix = typeof(BenchmarkProbe).GetMethod(nameof(InjectSentinelIntoCachedTemplate), BindingFlags.Static | BindingFlags.NonPublic);
        if (getCachedTemplate is null || postfix is null)
        {
            Debug.LogWarning("[T3MP] Sentinel template injection target was not found.");
            return 0;
        }

        _cachedTemplatePrefabProperty = getCachedTemplate.ReturnType.GetProperty("Prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _cachedTemplatePrefabField = getCachedTemplate.ReturnType.GetField("_prefab", BindingFlags.Instance | BindingFlags.NonPublic) ??
            getCachedTemplate.ReturnType.GetField("Prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_cachedTemplatePrefabProperty is null && _cachedTemplatePrefabField is null)
        {
            Debug.LogWarning("[T3MP] CachedTemplate.Prefab accessor was not found; sentinel template injection disabled.");
            return 0;
        }

        var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postfix);
        return TryPatch(harmony, patchMethod, getCachedTemplate, null, postfixHarmonyMethod) ? 1 : 0;
    }

    private static PropertyInfo? _cachedTemplatePrefabProperty;
    private static FieldInfo? _cachedTemplatePrefabField;

    private static void InjectSentinelIntoCachedTemplate(object __result)
    {
        if (__result is null)
        {
            return;
        }

        var prefab = _cachedTemplatePrefabProperty?.GetValue(__result) ?? _cachedTemplatePrefabField?.GetValue(__result);
        if (prefab is GameObject templatePrefab)
        {
            TickDispatchOptimizer.InjectSentinelIntoTemplate(templatePrefab);
        }
    }

    private static int PatchRegistryFastRemoves(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.EnableEntityRegistryFastRemove && !BenchmarkSettings.EnableComponentRegistryFastRemove)
        {
            return 0;
        }

        var patched = 0;

        if (BenchmarkSettings.EnableEntityRegistryFastRemove)
        {
            var addEntity = typeof(Timberborn.EntitySystem.EntityRegistry).GetMethod("AddEntity", BindingFlags.Instance | BindingFlags.Public);
            var removeEntity = typeof(Timberborn.EntitySystem.EntityRegistry).GetMethod("RemoveEntity", BindingFlags.Instance | BindingFlags.Public);
            var stampPostfix = typeof(BenchmarkProbe).GetMethod(nameof(StampRegisteredEntity), BindingFlags.Static | BindingFlags.NonPublic);
            var removePrefix = typeof(BenchmarkProbe).GetMethod(nameof(FastEntityRegistryRemove), BindingFlags.Static | BindingFlags.NonPublic);
            if (addEntity is not null && removeEntity is not null && stampPostfix is not null && removePrefix is not null)
            {
                if (TryPatch(harmony, patchMethod, addEntity, null, Activator.CreateInstance(harmonyMethodType, stampPostfix)))
                {
                    patched++;
                }
                if (TryPatch(harmony, patchMethod, removeEntity, Activator.CreateInstance(harmonyMethodType, removePrefix), null))
                {
                    patched++;
                }
            }
            else
            {
                Debug.LogWarning("[T3MP] EntityRegistry fast remove targets were not found.");
            }
        }

        if (BenchmarkSettings.EnableComponentRegistryFastRemove)
        {
            var registryType = typeof(Timberborn.EntitySystem.EntityComponentRegistry);
            var registerAsType = registryType.GetMethod("RegisterAsType", BindingFlags.Instance | BindingFlags.NonPublic);
            var unregisterAsType = registryType.GetMethod("UnregisterAsType", BindingFlags.Instance | BindingFlags.NonPublic);
            var stampPostfix = typeof(BenchmarkProbe).GetMethod(nameof(StampRegisteredComponent), BindingFlags.Static | BindingFlags.NonPublic);
            var removePrefix = typeof(BenchmarkProbe).GetMethod(nameof(FastUnregisterAsType), BindingFlags.Static | BindingFlags.NonPublic);
            if (registerAsType is not null && unregisterAsType is not null && stampPostfix is not null && removePrefix is not null)
            {
                if (TryPatch(harmony, patchMethod, registerAsType, null, Activator.CreateInstance(harmonyMethodType, stampPostfix)))
                {
                    patched++;
                }
                if (TryPatch(harmony, patchMethod, unregisterAsType, Activator.CreateInstance(harmonyMethodType, removePrefix), null))
                {
                    patched++;
                }
            }
            else
            {
                Debug.LogWarning("[T3MP] EntityComponentRegistry fast remove targets were not found.");
            }
        }

        return patched;
    }

    private static void StampRegisteredEntity(Timberborn.EntitySystem.EntityComponent entityComponent)
    {
        OrderedListFastRemove.Stamp(entityComponent);
    }

    private static bool FastEntityRegistryRemove(
        Dictionary<Guid, Timberborn.EntitySystem.EntityComponent> ____entities,
        List<Timberborn.EntitySystem.EntityComponent> ____entitiesInInstantiationOrder,
        Timberborn.EntitySystem.EntityComponent entityComponent)
    {
        if (!BenchmarkSettings.EnableEntityRegistryFastRemove ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!OrderedListFastRemove.TryRemove(____entitiesInInstantiationOrder, entityComponent))
        {
            return true;
        }

        ____entities.Remove(entityComponent.EntityId);
        return false;
    }

    private static void StampRegisteredComponent(Timberborn.EntitySystem.IRegisteredComponent registeredComponent)
    {
        OrderedListFastRemove.Stamp(registeredComponent);
    }

    private static bool FastUnregisterAsType(
        Dictionary<Type, List<Timberborn.EntitySystem.IRegisteredComponent>> ____registeredComponents,
        Timberborn.EntitySystem.IRegisteredComponent registeredComponent,
        Type type)
    {
        if (!BenchmarkSettings.EnableComponentRegistryFastRemove ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        if (!____registeredComponents.TryGetValue(type, out var list))
        {
            // Vanilla would throw KeyNotFoundException here - run it.
            return true;
        }

        return !OrderedListFastRemove.TryRemove(list, registeredComponent);
    }

    private static int PatchSpawnSplitProbe(object harmony, Type harmonyMethodType, MethodInfo patchMethod)
    {
        if (!BenchmarkSettings.BenchSpawnRequested)
        {
            return 0;
        }

        var patched = 0;
        try
        {
            var sitePrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordSpawnSiteCall), BindingFlags.Static | BindingFlags.NonPublic);
            var sitePostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordSpawnSiteReturn), BindingFlags.Static | BindingFlags.NonPublic);
            var postPrefix = typeof(BenchmarkProbe).GetMethod(nameof(RecordPostNowCall), BindingFlags.Static | BindingFlags.NonPublic);
            var postPostfix = typeof(BenchmarkProbe).GetMethod(nameof(RecordPostNowReturn), BindingFlags.Static | BindingFlags.NonPublic);
            if (sitePrefix is null || sitePostfix is null || postPrefix is null || postPostfix is null)
            {
                Debug.LogWarning("[T3MP] SpawnSplit probe patch methods were not found.");
                return 0;
            }

            var siteTargets = new List<MethodBase>();

            var naturalResourceFactoryType = FindType("Timberborn.NaturalResources.NaturalResourceFactory") ??
                TryLoadAssemblyAndFindType("Timberborn.NaturalResources", "Timberborn.NaturalResources.NaturalResourceFactory");
            var plantNew = naturalResourceFactoryType?.GetMethod("PlantNew", BindingFlags.Instance | BindingFlags.Public);
            if (plantNew is not null)
            {
                siteTargets.Add(plantNew);
            }

            var templateInstantiatorType = FindType("Timberborn.TemplateInstantiation.TemplateInstantiator") ??
                TryLoadAssemblyAndFindType("Timberborn.TemplateInstantiation", "Timberborn.TemplateInstantiation.TemplateInstantiator");
            var templateInstantiate = templateInstantiatorType?.GetMethod("Instantiate", BindingFlags.Instance | BindingFlags.Public);
            if (templateInstantiate is not null)
            {
                siteTargets.Add(templateInstantiate);
            }

            var baseInstantiatorType = templateInstantiatorType?.GetField("_baseInstantiator", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType;
            var instantiateInactive = baseInstantiatorType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(method => method.Name == "InstantiateInactive" && !method.ContainsGenericParameters);
            if (instantiateInactive is not null)
            {
                siteTargets.Add(instantiateInactive);
            }

            var entityComponentType = FindType("Timberborn.EntitySystem.EntityComponent") ??
                TryLoadAssemblyAndFindType("Timberborn.EntitySystem", "Timberborn.EntitySystem.EntityComponent");
            foreach (var phaseName in new[] { "PreInitialize", "Initialize", "PostInitialize", "PostLoad" })
            {
                var phase = entityComponentType?.GetMethod(phaseName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (phase is not null)
                {
                    siteTargets.Add(phase);
                }
            }

            var entityServiceType = FindType("Timberborn.EntitySystem.EntityService") ??
                TryLoadAssemblyAndFindType("Timberborn.EntitySystem", "Timberborn.EntitySystem.EntityService");
            var deleteMethod = entityServiceType?.GetMethod("Delete", BindingFlags.Instance | BindingFlags.Public);
            if (deleteMethod is not null)
            {
                siteTargets.Add(deleteMethod);
            }

            // Sub-sites of the TickableEntityLifecycleManager spawn tax.
            var lifecycleManagerType = FindType("Timberborn.TickSystem.TickableEntityLifecycleManager");
            var addTickableEntity = lifecycleManagerType?.GetMethod("AddTickableEntity", BindingFlags.Instance | BindingFlags.NonPublic);
            if (addTickableEntity is not null)
            {
                siteTargets.Add(addTickableEntity);
            }

            var createMetered = lifecycleManagerType?.GetMethod("CreateMeteredComponent", BindingFlags.Instance | BindingFlags.NonPublic);
            if (createMetered is not null)
            {
                siteTargets.Add(createMetered);
            }

            var tickableEntityType = FindType("Timberborn.TickSystem.TickableEntity");
            var tickableEntityCtor = tickableEntityType?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault();
            if (tickableEntityCtor is not null)
            {
                siteTargets.Add(tickableEntityCtor);
            }

            var bucketType = FindType("Timberborn.TickSystem.TickableEntityBucket");
            var bucketAdd = bucketType?.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (bucketAdd is not null)
            {
                siteTargets.Add(bucketAdd);
            }

            // Delete-side sub-sites.
            var entityRegistryType = FindType("Timberborn.EntitySystem.EntityRegistry");
            var removeEntity = entityRegistryType?.GetMethod("RemoveEntity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (removeEntity is not null)
            {
                siteTargets.Add(removeEntity);
            }

            var entityComponentDelete = entityComponentType?.GetMethod("Delete", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (entityComponentDelete is not null)
            {
                siteTargets.Add(entityComponentDelete);
            }

            var componentRegistryUnregister = typeof(Timberborn.EntitySystem.EntityComponentRegistry)
                .GetMethod("Unregister", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Timberborn.EntitySystem.EntityComponent) }, null);
            if (componentRegistryUnregister is not null)
            {
                siteTargets.Add(componentRegistryUnregister);
            }

            var eventBusUnregisterNow = FindType("Timberborn.SingletonSystem.EventBus")
                ?.GetMethod("UnregisterNow", BindingFlags.Instance | BindingFlags.NonPublic);
            if (eventBusUnregisterNow is not null)
            {
                siteTargets.Add(eventBusUnregisterNow);
            }

            foreach (var target in siteTargets)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, sitePrefix);
                harmonyMethodType.GetField("priority")?.SetValue(prefixHarmonyMethod, 800);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, sitePostfix);
                if (TryPatch(harmony, patchMethod, target, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }

            var eventBusType = FindType("Timberborn.SingletonSystem.EventBus");
            var postNow = eventBusType?.GetMethod("PostNow", BindingFlags.Instance | BindingFlags.NonPublic);
            if (postNow is not null)
            {
                var prefixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postPrefix);
                harmonyMethodType.GetField("priority")?.SetValue(prefixHarmonyMethod, 800);
                var postfixHarmonyMethod = Activator.CreateInstance(harmonyMethodType, postPostfix);
                if (TryPatch(harmony, patchMethod, postNow, prefixHarmonyMethod, postfixHarmonyMethod))
                {
                    patched++;
                }
            }
            else
            {
                Debug.LogWarning("[T3MP] SpawnSplit probe: EventBus.PostNow was not found.");
            }

            Debug.Log($"[T3MP] SpawnSplit probe patched {patched} methods.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] SpawnSplit probe installation failed: {exception}");
        }

        return patched;
    }

    private static void RecordSpawnSiteCall(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void RecordSpawnSiteReturn(MethodBase __originalMethod, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        SpawnSplitProbe.RecordSite(
            $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}",
            Stopwatch.GetTimestamp() - __state);
    }

    private static void RecordPostNowCall(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void RecordPostNowReturn(object eventObject, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        SpawnSplitProbe.RecordEvent(eventObject.GetType(), Stopwatch.GetTimestamp() - __state);
    }

    private static void RecordDecideSplitCall(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void RecordDecideSplitReturn(MethodBase __originalMethod, Decision __result, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        DecideSplitProbe.RecordDecide(__originalMethod, Stopwatch.GetTimestamp() - __state, __result.ShouldReleaseNow);
    }

    private static void RecordProcessBehaviorCall(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static void RecordProcessBehaviorReturn(Behavior behavior, bool __result, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        DecideSplitProbe.RecordRoot(behavior.GetType(), Stopwatch.GetTimestamp() - __state, __result);
    }

    private static void RecordTickExecutorCall(object ____runningExecutor, out DecideSplitProbe.ExecState __state)
    {
        __state = new DecideSplitProbe.ExecState(Stopwatch.GetTimestamp(), ____runningExecutor?.GetType());
    }

    private static void RecordTickExecutorReturn(DecideSplitProbe.ExecState __state)
    {
        if (__state.Timestamp == 0)
        {
            return;
        }

        DecideSplitProbe.RecordExecutor(__state.ExecutorType, Stopwatch.GetTimestamp() - __state.Timestamp);
    }

    private static bool TryPatch(
        object harmony,
        MethodInfo patchMethod,
        MethodBase targetMethod,
        object? prefixHarmonyMethod,
        object? postfixHarmonyMethod)
    {
        try
        {
            patchMethod.Invoke(harmony, new object?[] { targetMethod, prefixHarmonyMethod, postfixHarmonyMethod, null, null });
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] Failed to patch {targetMethod.DeclaringType?.FullName}.{targetMethod.Name}: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static bool HasOnEventAttribute(MethodInfo method)
    {
        try
        {
            return method.GetCustomAttributes(inherit: false)
                .Any(attribute => attribute.GetType().FullName == "Timberborn.SingletonSystem.OnEventAttribute");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RecordYielderCall(
        YielderFinder __instance,
        MethodBase __originalMethod,
        Inventory receivingInventory,
        Accessible start,
        int liftingCapacity,
        IEnumerable<Yielder> yielders,
        ref YielderSearchResult __result,
        out TimedCallState __state)
    {
        _yielderFinderDepth++;
        if (!BenchmarkSettings.EnableDetailedBenchmarkTiming ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            __state = TimedCallState.Inactive;
        }
        else
        {
            __state = new TimedCallState(
                true,
                mode,
                Stopwatch.GetTimestamp(),
                TryGetCount(yielders));
        }

        if (__originalMethod.Name == "FindLivingYielderWithoutAccessible"
            && BenchmarkSettings.EnableFastYielderFinder
            && BenchmarkModeController.CurrentMode == BenchmarkMode.Optimized
            && FastYielderFinder.TryFindLivingYielderWithoutAccessible(__instance, receivingInventory, start, liftingCapacity, yielders, out var optimizedResult))
        {
            __result = optimizedResult;
            return false;
        }

        return true;
    }

    private static void RecordYielderReturn(TimedCallState __state, object? __result)
    {
        try
        {
            if (!__state.Active)
            {
                return;
            }

            BenchmarkMetrics.RecordYielderCall(
                __state.Mode,
                Stopwatch.GetTimestamp() - __state.StartTimestamp,
                __state.Count,
                __result);
        }
        finally
        {
            if (_yielderFinderDepth > 0)
            {
                _yielderFinderDepth--;
            }
        }
    }

    private static bool RecordFarmCall(
        HarvestStarter __instance,
        Inventory receivingInventory,
        InRangeYielders inRangeYielders,
        string prioritizedName,
        ref YielderSearchResult __result,
        out TimedCallState __state)
    {
        if (!BenchmarkSettings.EnableDetailedBenchmarkTiming ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            __state = TimedCallState.Inactive;
        }
        else
        {
            __state = new TimedCallState(true, mode, Stopwatch.GetTimestamp(), 0);
        }

        if (BenchmarkSettings.EnableFarmYielderSegmentTree
            && BenchmarkModeController.CurrentMode == BenchmarkMode.Optimized
            && FarmYielderOptimizer.TryFindYielder(__instance, receivingInventory, inRangeYielders, prioritizedName, out var optimizedResult, out var stats))
        {
            if (__state.Active)
            {
                BenchmarkMetrics.RecordFarmYielderOptimizer(__state.Mode, stats);
            }

            __result = optimizedResult;
            return false;
        }

        if (__state.Active && BenchmarkModeController.CurrentMode == BenchmarkMode.Optimized)
        {
            BenchmarkMetrics.RecordFarmYielderOptimizer(__state.Mode, FarmYielderOptimizer.FarmOptimizerStats.CreateFallback());
        }

        return true;
    }

    private static void RecordFarmReturn(TimedCallState __state, object? __result)
    {
        if (!__state.Active)
        {
            return;
        }

        BenchmarkMetrics.RecordFarmCall(
            __state.Mode,
            Stopwatch.GetTimestamp() - __state.StartTimestamp,
            __result);
    }

    private static bool UseFarmHouseBehaviorDirectOptimizer(object __instance, BehaviorAgent agent, ref Decision __result)
    {
        return FarmHouseBehaviorDirectOptimizer.TryDecide(__instance, agent, ref __result);
    }

    private static bool UsePlantingSpotFinderOptimizer(object __instance, Vector3 agentPosition, ref PlantingSpot? __result)
    {
        if (PlantingSpotFinderOptimizer.TryFindClosest(__instance, agentPosition, ref __result))
        {
            return false;
        }

        return true;
    }

    private static void RecordPlantingCoordinatesChanged()
    {
        PlantingSpotFinderOptimizer.InvalidateAll();
    }

    private static bool UseLumberjackYielderOptimizer(object __instance, int liftingCapacity, ref YielderSearchResult __result)
    {
        if (LumberjackYielderOptimizer.TryFindCuttable(__instance, liftingCapacity, out var optimizedResult))
        {
            __result = optimizedResult;
            return false;
        }

        return true;
    }

    private static void InvalidateLumberjackAreaCache(object __instance)
    {
        LumberjackYielderOptimizer.InvalidateArea(__instance);
    }

    private static bool UseGatherWorkplaceOptimizer(
        object __instance,
        Accessible start,
        int liftingCapacity,
        string templateName,
        ref YielderSearchResult __result)
    {
        if (GatherWorkplaceOptimizer.TryFindYielder(__instance, start, liftingCapacity, templateName, out var optimizedResult))
        {
            __result = optimizedResult;
            return false;
        }

        return true;
    }

    private static void RecordInRangeCall(out TimedCallState __state)
    {
        if (!BenchmarkSettings.EnableDetailedBenchmarkTiming ||
            !BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            __state = TimedCallState.Inactive;
            return;
        }

        __state = new TimedCallState(true, mode, Stopwatch.GetTimestamp(), 0);
    }

    // No object[] __args here: Harmony materializes it (allocation + boxing)
    // on EVERY call even though it was only read in dev detailed-timing mode.
    // The candidate count is reported as 0 in that dev-only log instead.
    private static void RecordInRangeReturn(TimedCallState __state, bool __result)
    {
        if (!__state.Active)
        {
            return;
        }

        BenchmarkMetrics.RecordInRangeYieldersCall(
            __state.Mode,
            Stopwatch.GetTimestamp() - __state.StartTimestamp,
            0,
            __result);
    }

    private static void RecordNavigationCall(MethodBase __originalMethod, out NavigationCallState __state)
    {
        if (!BenchmarkModeController.TryGetSampleMode(out var mode))
        {
            __state = new NavigationCallState(false, BenchmarkModeController.CurrentMode, 0, __originalMethod.Name, false);
            return;
        }

        __state = new NavigationCallState(
            true,
            mode,
            Stopwatch.GetTimestamp(),
            __originalMethod.Name,
            _yielderFinderDepth > 0);
    }

    private static void RecordNavigationReturn(NavigationCallState __state, object[] __args, bool __result)
    {
        WalkerDistanceCache.TryCaptureNavigationResult(__state.MethodName, __args, __result);

        if (!__state.Active)
        {
            return;
        }

        BenchmarkMetrics.RecordNavigationCall(
            __state.Mode,
            __state.MethodName,
            __state.InYielderFinder,
            Stopwatch.GetTimestamp() - __state.StartTimestamp);
        NavigationCallerSampler.TryRecord(__state.Mode, __state.MethodName);
    }

    private static bool RecordWalkerTravelTimeCall(
        object __instance,
        Vector3 start,
        Vector3 destination,
        ref float __result,
        out WalkerTravelTimeCallState __state)
    {
        NeedActionFlowFieldProbe.BeginTravelLeg(__instance, start, destination, out var flowFieldState);
        if (BenchmarkSettings.EnableWalkerDistanceCache &&
            WalkerDistanceCache.TryGetTravelTime(__instance, start, destination, out var cachedDistanceTravelTime))
        {
            __result = cachedDistanceTravelTime;
            __state = new WalkerTravelTimeCallState(flowFieldState);
            return false;
        }

        __state = new WalkerTravelTimeCallState(flowFieldState);
        WalkerDistanceCache.BeginCapture(start, destination);
        return true;
    }

    private static void RecordWalkerTravelTimeReturn(WalkerTravelTimeCallState __state, float __result)
    {
        NeedActionFlowFieldProbe.RecordTravelLegReturn(__state.FlowFieldState, __result);
        WalkerDistanceCache.EndCapture();
    }

    private static bool UseWalkerMoverDelegateCache(
        Enterer ____enterer,
        Walker ____walker,
        WalkerSpeedManager ____walkerSpeedManager,
        ITickService ____tickService)
    {
        return !WalkerMoverDelegateCacheOptimizer.TryMove(
            ____enterer,
            ____walker,
            ____walkerSpeedManager,
            ____tickService);
    }

    private static bool UsePathFollowerNoAnimationFastMove(
        PathFollower __instance,
        float tickDeltaTime,
        string animationName,
        Func<float> movementSpeedProvider)
    {
        return !PathFollowerNoAnimationFastMove.TryMove(
            __instance,
            tickDeltaTime,
            animationName,
            movementSpeedProvider);
    }

    private static bool UseCarryAmountCalculatorOptimizer(
        IGoodService ____goodService,
        int liftingCapacity,
        GoodAmount good,
        IAmountProvider input,
        ref GoodAmount __result)
    {
        return !CarryAmountCalculatorOptimizer.TryAmountToCarry(
            ____goodService,
            liftingCapacity,
            good,
            input,
            ref __result);
    }

    private static bool UseGoodCarrierLiftingCapacityCache(
        GoodCarrier __instance,
        ref int __result,
        out GoodCarrierLiftingCapacityCache.CallState __state)
    {
        return GoodCarrierLiftingCapacityCache.TryGet(__instance, ref __result, out __state);
    }

    private static void RecordGoodCarrierLiftingCapacityCache(
        GoodCarrierLiftingCapacityCache.CallState __state,
        int __result)
    {
        GoodCarrierLiftingCapacityCache.Store(__state, __result);
    }

    private static void RecordPathFollowerProfilerCall(MethodBase __originalMethod, out PathFollowerProfiler.CallState __state)
    {
        PathFollowerProfiler.Begin(__originalMethod, out __state);
    }

    private static void RecordPathFollowerProfilerReturn(PathFollowerProfiler.CallState __state)
    {
        PathFollowerProfiler.End(__state);
    }

    private static bool UseAnimatedPathFollowerHorizontalOptimizer(
        object __instance,
        AnimatedPathCorner previousCorner,
        AnimatedPathCorner nextCorner,
        float timeInSeconds)
    {
        return !AnimatedPathFollowerHorizontalOptimizer.TryPlaceBetweenCorners(__instance, previousCorner, nextCorner, timeInSeconds);
    }

    // NOTE: parameters are bound by NAME to the vanilla
    // ActionDurationCalculator.TravelTimeBetween(Vector3 actionPosition,
    // Vector3 position). Do NOT use object[] __args here: Harmony would
    // allocate an object[] and box both Vector3 on every call (~110 B), which
    // measured ~38 MB per 20 s of pure GC garbage on a large colony.
    private static bool RecordNeedBehaviorTravelEstimateCall(
        object __instance,
        Vector3 actionPosition,
        Vector3 position,
        ref float __result,
        out NeedBehaviorTravelOptimizer.TravelCallState __state)
    {
        if (BenchmarkSettings.EnableNeedBehaviorDecisionMetrics)
        {
            NeedBehaviorDecisionSampler.RecordTravelEstimateCall();
        }

        if (NeedBehaviorTravelOptimizer.TryHandleTravelTimeDistanceBased(__instance, actionPosition, position, ref __result))
        {
            __state = default;
            return false;
        }

        return NeedBehaviorTravelOptimizer.TryHandleTravelTime(actionPosition, position, ref __result, out __state);
    }

    private static void RecordNeedBehaviorTravelEstimateReturn(NeedBehaviorTravelOptimizer.TravelCallState __state, float __result)
    {
        NeedBehaviorTravelOptimizer.RecordTravelTimeReturn(__state, __result);
    }

    private static void RecordDurationWithReturnCall(
        object __instance,
        Vector3 actionPosition,
        Vector3 returnPosition,
        out NeedActionFlowFieldProbe.DurationProbeState __state)
    {
        NeedBehaviorTravelOptimizer.BeginDurationWithReturn(actionPosition, returnPosition);
        NeedActionFlowFieldProbe.BeginDuration(__instance, actionPosition, returnPosition, out __state);
    }

    private static void RecordDurationWithReturnReturn(NeedActionFlowFieldProbe.DurationProbeState __state, float __result)
    {
        try
        {
            NeedActionFlowFieldProbe.RecordDurationReturn(__state, __result);
        }
        finally
        {
            NeedActionFlowFieldProbe.EndDuration();
            NeedBehaviorTravelOptimizer.EndDurationWithReturn();
        }
    }

    // NOTE: parameters are bound by NAME to the vanilla
    // DistrictNeedBehaviorService.PickBestAction(NeedManager needManager,
    // Vector3 essentialActionPosition, float hoursLeftForNonEssentialActions,
    // NeedFilter needFilter). Do NOT use object[] __args (allocates + boxes
    // per call, see RecordNeedBehaviorTravelEstimateCall).
    private static bool RecordNeedBehaviorPickBestCall(
        object __instance,
        Timberborn.NeedSystem.NeedManager needManager,
        Vector3 essentialActionPosition,
        float hoursLeftForNonEssentialActions,
        NeedFilter needFilter,
        ref AppraisedAction? __result,
        out NeedBehaviorDecisionSampler.PickBestCallState __state)
    {
        if (BenchmarkSettings.EnableNeedBehaviorDecisionMetrics)
        {
            NeedBehaviorDecisionSampler.RecordPickBestCall(out __state);
        }
        else
        {
            __state = NeedBehaviorDecisionSampler.PickBestCallState.Inactive;
        }

        NeedBehaviorTravelOptimizer.BeginPickBest();
        return DistrictNeedBehaviorDirectOptimizer.TryPickBest(
            __instance,
            needManager,
            essentialActionPosition,
            hoursLeftForNonEssentialActions,
            needFilter,
            ref __result);
    }

    private static void RecordNeedBehaviorPickBestReturn(NeedBehaviorDecisionSampler.PickBestCallState __state, object? __result)
    {
        if (BenchmarkSettings.EnableNeedBehaviorDecisionMetrics)
        {
            NeedBehaviorDecisionSampler.RecordPickBestReturn(__state, __result);
        }

        NeedBehaviorTravelOptimizer.EndPickBest();
    }

    private static bool UseNeedManagerDirectCriticalState(object __instance, ref bool __result)
    {
        return NeedManagerDirectCriticalState.TryAnyNeedIsInCriticalState(__instance, ref __result);
    }

    private static bool UseNeedManagerFastTick(NeedManager __instance)
    {
        return !NeedManagerFastTick.TryTick(__instance);
    }

    private static bool RecordBeaverNeedDecisionFrequencyCall(
        object __instance,
        object[] __args,
        ref Behavior? __result,
        out NoActionCooldown.CallState __state)
    {
        BeaverNeedDecisionFrequencySampler.RecordCall(__instance);
        if (__args.Length < 1)
        {
            __state = NoActionCooldown.CallState.Inactive;
            return true;
        }

        var needFilter = __args[0];
        if (NoActionCooldown.TrySkip(__instance, needFilter, out var cooldownResult, out __state))
        {
            __result = cooldownResult;
            return false;
        }

        return true;
    }

    private static void RecordBeaverNeedDecisionFrequencyReturn(
        object __instance,
        object[] __args,
        Behavior? __result,
        NoActionCooldown.CallState __state)
    {
        if (__args.Length < 1)
        {
            return;
        }

        var needFilter = __args[0];
        NoActionCooldown.RecordReturn(__instance, needFilter, __result, __state);
    }

    private static void RecordReservableChanged(Reservable __instance)
    {
        FarmYielderOptimizer.OnReservableChanged(__instance);
    }

    private static void RecordTickBuckets(object __instance, int numberOfBucketsToTick)
    {
        RuntimePerformanceOverlay.RecordTickBuckets(__instance, numberOfBucketsToTick);
        SimulationProgressMetrics.RecordTickBuckets(__instance, numberOfBucketsToTick);
    }

    private static void RecordTickerUpdate(object __instance)
    {
        RuntimePerformanceOverlay.RecordTickerUpdate(__instance);
        SimulationProgressMetrics.RecordTickerUpdate(__instance);
    }

    private static void RecordTimberbornFpsCounter(object __instance)
    {
        RuntimePerformanceOverlay.RecordTimberbornFpsCounter(__instance);
    }

    private static void RecordSpeedManager(object __instance)
    {
        SpeedManagerProbe.Record(__instance);
        AutoRuntimeControl.TryResumeGameSpeed(__instance);
        TopologyUiScenario.RecordSpeedManager(__instance);
        SmoothTimeScaleGovernor.RecordSpeedManager(__instance);
    }

    private static void RecordTimeSpeedButtonGroup(object __instance)
    {
        TimeSpeedButtonGroupProbe.RecordAndMaybeSetFastest(__instance);
    }

    private static void RecordFullTick()
    {
        BenchmarkModeController.RecordFullTickForRenderPeek();
        RuntimePerformanceOverlay.RecordFullTick();
        SimulationProgressMetrics.RecordFullTick();
    }

    private static Exception? SuppressReachedLastPathCornerNre(Exception? __exception, ref bool __result)
    {
        if (__exception is null)
        {
            return null;
        }

        if (__exception is NullReferenceException)
        {
            __result = false;
            if (Interlocked.Increment(ref _pathFollowerNreGuardLogs) <= 3)
            {
                Debug.LogWarning("[T3MP] Suppressed PathFollower.ReachedLastPathCorner NullReferenceException during benchmark run.");
            }

            return null;
        }

        return __exception;
    }

    private static Exception? SuppressCharacterRotatorXRotationNre(Exception? __exception, ref float __result)
    {
        if (__exception is null)
        {
            return null;
        }

        if (__exception is NullReferenceException)
        {
            __result = 0f;
            if (Interlocked.Increment(ref _characterRotatorNreGuardLogs) <= 3)
            {
                Debug.LogWarning("[T3MP] Suppressed CharacterRotator.GetXRotation NullReferenceException during benchmark run.");
            }

            return null;
        }

        return __exception;
    }

    private static bool SkipBadMovementAnimatorUpdate(object __instance)
    {
        if (BenchmarkSettings.EnableUnattendedVisualSuppression &&
            BenchmarkModeController.RenderBlackoutActive)
        {
            return false;
        }

        if (BenchmarkSettings.EnableMovementAnimatorThrottle &&
            BenchmarkSettings.MovementAnimatorThrottleFrames > 1 &&
            BenchmarkModeController.RenderBlackoutActive &&
            !ShouldRunStaggeredVisualUpdate(__instance, BenchmarkSettings.MovementAnimatorThrottleFrames))
        {
            return false;
        }

        var key = RuntimeHelpers.GetHashCode(__instance);
        var snapshot = BadMovementAnimatorSnapshot;
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index] == key)
            {
                return false;
            }
        }

        return true;
    }

    private static Exception? SuppressMovementAnimatorUpdateNre(object __instance, Exception? __exception)
    {
        if (__exception is null)
        {
            return null;
        }

        if (__exception is NullReferenceException)
        {
            var key = RuntimeHelpers.GetHashCode(__instance);
            lock (MovementAnimatorGuardLock)
            {
                if (BadMovementAnimators.Add(key))
                {
                    BadMovementAnimatorSnapshot = BadMovementAnimators.ToArray();
                }
            }

            if (Interlocked.Increment(ref _movementAnimatorNreGuardLogs) <= 3)
            {
                Debug.LogWarning("[T3MP] Suppressed MovementAnimator.Update NullReferenceException during benchmark run; future updates for this animator will be skipped.");
            }

            return null;
        }

        return __exception;
    }

    private static void RecordLoadStageCall(MethodBase __originalMethod, out LoadProfiler.LoadStageState __state)
    {
        __state = LoadProfiler.BeginStage(__originalMethod);
        MechanicalGraphLoadBatcher.BeginStage(__state.Name);
    }

    private static void RecordLoadStageReturn(LoadProfiler.LoadStageState __state)
    {
        MechanicalGraphLoadBatcher.EndStage(__state.Name);
        LoadProfiler.EndStage(__state);
    }

    private static bool RecordMechanicalGraphJoinForLoadBatch(object __instance, object[] __args)
    {
        return __args.Length == 0 || !MechanicalGraphLoadBatcher.TryDeferJoin(__instance, __args[0]);
    }

    private static bool UseCachedHaulCandidateOrder(object __instance, IList<WorkplaceBehavior> workplaceBehaviors, out HaulCandidateOrderCache.CallState __state)
    {
        return HaulCandidateOrderCache.TryUse(__instance, workplaceBehaviors, out __state);
    }

    private static void StoreCachedHaulCandidateOrder(object __instance, IList<WorkplaceBehavior> workplaceBehaviors, HaulCandidateOrderCache.CallState __state)
    {
        HaulCandidateOrderCache.Store(__instance, workplaceBehaviors, __state);
    }

    private static bool UseHaulNoActionFrameCache(object __instance, BehaviorAgent agent, ref Decision __result)
    {
        if (HaulNoActionFrameCache.TryDecide(__instance, agent, out var decision))
        {
            __result = decision;
            return false;
        }

        return true;
    }

    private static bool UseWorkplaceNoActionFrameCache(object __instance, ref Decision __result, out WorkplaceNoActionFrameCache.CallState __state)
    {
        if (WorkplaceNoActionFrameCache.TrySkip(__instance, out var decision, out __state))
        {
            return true;
        }

        __result = decision;
        return false;
    }

    private static void RecordWorkplaceNoActionFrameCache(object __instance, Decision __result, WorkplaceNoActionFrameCache.CallState __state)
    {
        WorkplaceNoActionFrameCache.Record(__instance, __result, __state);
    }

    private static bool UseInventoryStockDistanceCache(
        DistrictInventoryPicker __instance,
        Accessible start,
        string goodId,
        Predicate<Inventory> inventoryFilter,
        ref Inventory __result)
    {
        if (InventoryStockDistanceCache.TryFind(__instance, start, goodId, inventoryFilter, out var inventory))
        {
            __result = inventory!;
            return false;
        }

        return true;
    }

    private static bool UseInventoryNeedGoodOptimizer(object __instance, Appraiser appraiser, Inventory inventory, ref GoodAmount __result)
    {
        if (InventoryNeedGoodOptimizer.TryFindMostOptimalGood(__instance, appraiser, inventory, ref __result))
        {
            return false;
        }

        return true;
    }

    private static bool UseInventoryCapacityDistanceCache(
        DistrictInventoryPicker __instance,
        Accessible start,
        GoodAmount goodAmount,
        Predicate<Inventory> inventoryFilter,
        ref float closestDistance,
        ref Inventory __result)
    {
        if (InventoryCapacityDistanceCache.TryFind(__instance, start, goodAmount, inventoryFilter, ref closestDistance, out var inventory))
        {
            __result = inventory!;
            return false;
        }

        return true;
    }

    private static void RecordInventoryCapacityVectorCall(
        Vector3 start,
        GoodAmount goodAmount,
        out InventoryCapacityVectorProfiler.CallState __state)
    {
        InventoryCapacityVectorProfiler.Begin(start, goodAmount, out __state);
    }

    private static void RecordInventoryCapacityVectorReturn(
        InventoryCapacityVectorProfiler.CallState __state,
        object[] __args,
        Inventory? __result)
    {
        var closestDistance = float.NaN;
        if (__args.Length > 2 && __args[2] is float distance)
        {
            closestDistance = distance;
        }

        InventoryCapacityVectorProfiler.End(__state, __result, closestDistance);
    }

    private static bool UseWorkerRootMetricsBypass(
        Worker ____worker,
        WorkerWorkingHours ____workerWorkingHours,
        WorkRefuser ____workRefuser,
        BehaviorAgent ____behaviorAgent,
        CommunityServiceBehavior ____communityServiceBehavior,
        BehaviorAgent agent,
        ref Decision __result)
    {
        if (WorkerRootMetricsBypass.TryDecide(
                ____worker,
                ____workerWorkingHours,
                ____workRefuser,
                ____behaviorAgent,
                ____communityServiceBehavior,
                agent,
                out var decision))
        {
            __result = decision;
            return false;
        }

        return true;
    }

    private static bool UseWorkerWorkingSpeedOptimizer(
        BonusManager ____bonusManager,
        CharacterAnimator ____characterAnimator,
        ref float ____workingSpeedMultiplier)
    {
        return WorkerWorkingSpeedOptimizer.TryTick(
            ____bonusManager,
            ____characterAnimator,
            ref ____workingSpeedMultiplier);
    }

    private static bool UseFillInputWorkplaceOptimizer(
        object __instance,
        BehaviorAgent agent,
        ref Decision __result)
    {
        return !FillInputWorkplaceOptimizer.TryDecide(__instance, agent, ref __result);
    }

    private static bool UseWaitInsideIdlyOptimizer(
        object __instance,
        BehaviorAgent agent,
        ref Decision __result)
    {
        return !WaitInsideIdlyOptimizer.TryDecide(__instance, agent, ref __result);
    }

    private static bool UseBehaviorManagerProcessOptimizer(object __instance)
    {
        return !BehaviorManagerProcessOptimizer.TryProcessBehaviors(__instance);
    }

    private static void RecordExecutorTickProfilerCall(
        object __instance,
        MethodBase __originalMethod,
        out ExecutorTickProfiler.CallState __state)
    {
        ExecutorTickProfiler.Begin(__instance, __originalMethod, out __state);
    }

    private static void RecordExecutorTickProfilerReturn(
        ExecutorTickProfiler.CallState __state,
        ExecutorStatus __result)
    {
        ExecutorTickProfiler.End(__state, __result);
    }


    private static bool MaybeRunDistrictResourceCounterTick(object __instance)
    {
        return DistrictResourceCounterThrottle.ShouldRunOriginal(__instance);
    }

    private static bool MaybeRunWaterObjectServiceTick(object __instance)
    {
        return WaterObjectServiceThrottle.ShouldRunOriginal(__instance);
    }

    private static bool MaybeRunWaterObjectServiceFastSkip(object __instance)
    {
        return WaterObjectServiceFastSkip.ShouldRunOriginal(__instance);
    }

    private static bool MaybeRunThreadSafeWaterMapTick()
    {
        return ThreadSafeWaterMapTickThrottle.ShouldRunOriginal();
    }

    private static bool MaybeUpdateThreadSafeWaterFlowDirections(object __instance)
    {
        return ThreadSafeWaterFlowDirectionThrottle.ShouldRunOriginal(__instance);
    }

    private static bool MaybeRunRangedEffectSubjectTick(object __instance)
    {
        return RangedEffectSubjectThrottle.ShouldRunOriginal(__instance);
    }

    private static bool MaybeRunContaminationApplierTick(object __instance)
    {
        return ContaminationApplierThrottle.ShouldRunOriginal(__instance);
    }


    private static void RecordLoadComponentCall(object __instance, MethodBase __originalMethod, out LoadProfiler.LoadComponentCallState __state)
    {
        LoadProfiler.BeginComponentCall(__instance, __originalMethod, out __state);
    }

    private static void RecordLoadComponentReturn(LoadProfiler.LoadComponentCallState __state)
    {
        LoadProfiler.EndComponentCall(__state);
    }

    private static void RecordLoadEventBusCall(MethodBase __originalMethod, object[] __args, out LoadProfiler.LoadComponentCallState __state)
    {
        LoadProfiler.BeginEventBusCall(__originalMethod, __args.Length > 0 ? __args[0] : null, out __state);
    }

    private static void RecordLoadEventHandlerCall(object __instance, MethodBase __originalMethod, object[] __args, out LoadProfiler.LoadComponentCallState __state)
    {
        LoadProfiler.BeginEventHandlerCall(__instance, __originalMethod, __args.Length > 0 ? __args[0] : null, out __state);
    }

    private static void RecordRangedEffectBuildingTickCall(object __instance, out StutterDetailProfiler.RangedEffectBuildingState __state)
    {
        StutterDetailProfiler.BeginRangedEffectBuildingTick(__instance, out __state);
    }

    private static void RecordRangedEffectBuildingTickReturn(object __instance, StutterDetailProfiler.RangedEffectBuildingState __state)
    {
        StutterDetailProfiler.EndRangedEffectBuildingTick(__instance, __state);
    }

    private static void RecordRangedEffectApplierUpdateCall(object __instance, bool active, out StutterDetailProfiler.RangedEffectApplierState __state)
    {
        StutterDetailProfiler.BeginRangedEffectApplierUpdate(__instance, active, out __state);
    }

    private static void RecordRangedEffectApplierUpdateReturn(object __instance, StutterDetailProfiler.RangedEffectApplierState __state)
    {
        StutterDetailProfiler.EndRangedEffectApplierUpdate(__instance, __state);
    }

    private static void RecordRangedEffectSubjectTickCall(out RangedEffectSubjectProfiler.CallState __state)
    {
        RangedEffectSubjectProfiler.BeginTick(out __state);
    }

    private static void RecordRangedEffectSubjectTickReturn(RangedEffectSubjectProfiler.CallState __state)
    {
        RangedEffectSubjectProfiler.EndTick(__state);
    }

    private static void RecordRangedEffectSubjectGetEffectsCall(out RangedEffectSubjectProfiler.CallState __state)
    {
        RangedEffectSubjectProfiler.BeginGetEffects(out __state);
    }

    private static void RecordRangedEffectSubjectGetEffectsReturn(RangedEffectSubjectProfiler.CallState __state, object? __result)
    {
        RangedEffectSubjectProfiler.EndGetEffects(__state, __result);
    }

    private static void RecordStatusAggregatorUpdateCall(object __instance, out StutterDetailProfiler.StatusAggregatorState __state)
    {
        StutterDetailProfiler.BeginStatusAggregatorUpdate(__instance, out __state);
    }

    private static void RecordStatusAggregatorUpdateReturn(object __instance, StutterDetailProfiler.StatusAggregatorState __state)
    {
        StutterDetailProfiler.EndStatusAggregatorUpdate(__instance, __state);
    }

    private static void RecordUnreachableHomeTickCall(object __instance, out StutterDetailProfiler.UnreachableHomeState __state)
    {
        StutterDetailProfiler.BeginUnreachableHomeTick(__instance, out __state);
    }

    private static void RecordUnreachableHomeTickReturn(object __instance, StutterDetailProfiler.UnreachableHomeState __state)
    {
        StutterDetailProfiler.EndUnreachableHomeTick(__instance, __state);
    }

    private static void RecordNavMeshNotifyCall(object __instance, MethodBase __originalMethod, object[] __args, out StutterDetailProfiler.NavMeshNotifyState __state)
    {
        StutterDetailProfiler.BeginNavMeshNotify(__instance, __originalMethod, __args.Length > 0 ? __args[0] : null, out __state);
    }

    private static void RecordNavMeshNotifyReturn(StutterDetailProfiler.NavMeshNotifyState __state)
    {
        StutterDetailProfiler.EndNavMeshNotify(__state);
    }

    private static void RecordRuntimeHotspotCall(object __instance, MethodBase __originalMethod, out StutterDetailProfiler.RuntimeHotspotState __state)
    {
        StutterDetailProfiler.BeginRuntimeHotspot(__instance, __originalMethod, null, out __state);
    }

    private static void RecordRuntimeEventHotspotCall(object __instance, MethodBase __originalMethod, object[] __args, out StutterDetailProfiler.RuntimeHotspotState __state)
    {
        StutterDetailProfiler.BeginRuntimeHotspot(__instance, __originalMethod, __args.Length > 0 ? __args[0] : null, out __state);
    }

    private static void RecordRuntimeHotspotReturn(StutterDetailProfiler.RuntimeHotspotState __state)
    {
        StutterDetailProfiler.EndRuntimeHotspot(__state);
    }

    private static void RecordMainLoopStageCall(MethodBase __originalMethod, out MainLoopProfiler.StageCallState __state)
    {
        MainLoopProfiler.BeginStage(__originalMethod, out __state);
    }

    private static void RecordMainLoopStageReturn(MainLoopProfiler.StageCallState __state)
    {
        MainLoopProfiler.EndStage(__state);
    }

    private static void RecordMainLoopTypeCall(object __instance, MethodBase __originalMethod, out MainLoopProfiler.TypeCallState __state)
    {
        MainLoopProfiler.BeginTypeCall(__instance, __originalMethod, out __state);
    }

    private static void RecordMainLoopTypeReturn(MainLoopProfiler.TypeCallState __state)
    {
        MainLoopProfiler.EndTypeCall(__state);
    }

    private static int TryGetCount(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is ICollection collection)
        {
            return collection.Count;
        }

        var countProperty = value.GetType().GetProperty("Count");
        if (countProperty?.GetValue(value) is int count)
        {
            return count;
        }

        return 0;
    }

    private static MethodInfo? FindPatchMethod(Type harmonyType, Type harmonyMethodType)
    {
        return harmonyType.GetMethods()
            .Where(method => method.Name == "Patch")
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length >= 2
                    && typeof(MethodBase).IsAssignableFrom(parameters[0].ParameterType)
                    && parameters[1].ParameterType == harmonyMethodType;
            });
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
        catch (Exception)
        {
            return Array.Empty<Type>();
        }
    }

    private static bool IsAssignableTo(Type type, string baseTypeFullName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.FullName == baseTypeFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private static Type? TryLoadAssemblyAndFindType(string assemblyName, string fullName)
    {
        try
        {
            return Assembly.Load(assemblyName).GetType(fullName, throwOnError: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly struct TimedCallState
    {
        public static readonly TimedCallState Inactive = new TimedCallState(false, BenchmarkMode.Vanilla, 0, 0);

        public TimedCallState(bool active, BenchmarkMode mode, long startTimestamp, int count)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            Count = count;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
        public int Count { get; }
    }

    private readonly struct NavigationCallState
    {
        public static readonly NavigationCallState Inactive = new NavigationCallState(false, BenchmarkMode.Vanilla, 0, string.Empty, false);

        public NavigationCallState(bool active, BenchmarkMode mode, long startTimestamp, string methodName, bool inYielderFinder)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
            MethodName = methodName;
            InYielderFinder = inYielderFinder;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
        public string MethodName { get; }
        public bool InYielderFinder { get; }
    }

    private readonly struct WalkerTravelTimeCallState
    {
        public WalkerTravelTimeCallState(NeedActionFlowFieldProbe.TravelLegProbeState flowFieldState)
        {
            FlowFieldState = flowFieldState;
        }

        public NeedActionFlowFieldProbe.TravelLegProbeState FlowFieldState { get; }
    }
}
