namespace T3MP;

internal static class BenchmarkSettings
{
    // Automated benchmark runs pass '-benchAutoUltra' on the Timberborn command
    // line. Only then does the mod auto-apply Optimized ultra speed with render
    // blackout after load. Normal play keeps manual 1/2/3/4 speed key behavior.
    public static readonly bool BenchAutoUltraRequested =
        HasCommandLineFlag("-benchAutoUltra");

    // Automated topology-UI investigation runs pass '-benchTopoUi'. The mod
    // then installs the selection scenario driver (TopologyUiScenario):
    // forced speed x50, rendered (no blackout), alternating gear-network /
    // path-network selections picked deterministically from the save.
    public static readonly bool BenchTopoUiRequested =
        HasCommandLineFlag("-benchTopoUi");

    // Automated verification of the Shift+O smooth mode: start with the
    // governor already enabled (equivalent to pressing Shift+O once).
    public static readonly bool BenchSmoothModeRequested =
        HasCommandLineFlag("-benchSmoothMode");

    private static bool HasCommandLineFlag(string flag)
    {
        var arguments = System.Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], flag, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Master switch: installs the optimization patches and the runtime
    // controller. Keep true for the mod to do anything.
    public static readonly bool EnableBenchmark = true;
    public static readonly bool EnableBenchmarkController = true;
    // Development-only A/B measurement: Vanilla<->Optimized cycling, frame
    // sampling, and SimProgress/aggregate log output. Off in the distributed
    // build so the mod runs purely in Optimized mode with no log spam.
    public static readonly bool EnableBenchmarkMeasurement = false;
    // Temporary measurement helper: run the save in VANILLA (optimizations off)
    // at ultra speed so it can be compared against Optimized at the same speed.
    // Keep false in the shipped build.
    public static readonly bool MeasureVanillaBaseline = false;
    // Temporary A/B helper: run at ultra speed while the controller cycles
    // Vanilla<->Optimized, so both are measured at the same 50x in one run.
    // Keep false in the shipped build.
    public static readonly bool MeasureAbAtUltra = false;
    // Temporary helper: optimizations ON but render blackout OFF (visible
    // 1/2/3 mode) at ultra speed, i.e. this mod loaded while a DIFFERENT mod
    // drives 50x. Keep false in the shipped build.
    public static readonly bool MeasureOptimizedVisibleAtUltra = false;
    public static readonly bool EnableBenchmarkDetailedMetrics = false;
    public static readonly bool EnableDetailedBenchmarkTiming = false;
    public static readonly bool EnableRuntimeProbes = true;
    public static readonly bool EnableNavigationProfiler = false;
    public static readonly bool EnableFastYielderFinder = true;
    public static readonly bool EnableFastYielderNoCapacityPrecheck = false;
    public static readonly bool EnableFarmYielderSegmentTree = true;
    public static readonly bool EnableFarmHouseBehaviorDirectOptimizer = true;
    public static readonly bool EnablePlantingSpotFinderOptimizer = true;
    public static readonly bool EnableLumberjackYielderOptimizer = true;
    public static readonly bool EnableLumberjackNoCapacityFastEmpty = true;
    public static readonly bool EnableLumberjackScanStats = false;
    public static readonly bool EnableGatherWorkplaceOptimizer = true;
    public static readonly bool EnableFarmEventDrivenUpdates = true;
    public static readonly bool EnableFarmSafetyRefreshAudit = false;
    public static readonly bool EnableWalkerDistanceCache = false;
    public static readonly bool EnableWalkerMoverDelegateCacheOptimizer = true;
    public static readonly bool EnablePathFollowerNoAnimationFastMove = true;
    public static readonly bool EnablePathFollowerFastMoveStopAnimation = true;
    public static readonly bool EnablePathFollowerProfiler = false;
    public static readonly bool EnableAnimatedPathFollowerHorizontalOptimizer = false;
    public static readonly bool EnableCarryAmountCalculatorOptimizer = true;
    public static readonly bool EnableGoodCarrierLiftingCapacityFrameCache = false;
    public static readonly bool EnablePickBestTravelCache = false;
    public static readonly bool EnableGlobalNeedTravelCache = true;
    public static readonly bool EnableGlobalNeedTravelShadowCache = true;
    public static readonly bool EnableGlobalNeedTravelShadowCacheLocking = false;
    public static readonly bool EnableGlobalNeedTravelShadowCacheRefreshOnHit = false;
    public static readonly bool EnableNeedTravelCacheMetrics = false;
    public static readonly bool EnableNeedBehaviorDecisionMetrics = false;
    public static readonly bool EnableBeaverDecisionFrequencySampler = false;
    public static readonly bool EnableHotOptimizerMetrics = false;
    public static readonly bool EnableNeedManagerDirectCriticalState = false;
    public static readonly bool EnableNeedManagerFastTick = false;
    public static readonly bool EnableHaulCandidateOrderCache = false;
    public static readonly bool EnableHaulNoActionFrameCache = true;
    public static readonly bool EnableHaulNoActionOrderedCache = true;
    public static readonly bool EnableWorkplaceNoActionFrameCache = false;
    public static readonly bool EnableWorkplaceNoActionFrameCacheTiming = false;
    public static readonly bool EnableInventoryStockDistanceCache = true;
    public static readonly bool EnableInventoryNeedGoodOptimizer = true;
    public static readonly bool EnableInventoryCapacityDistanceCache = false;
    public static readonly bool EnableInventoryCapacityVectorProfiler = false;
    public static readonly bool EnableWorkerRootMetricsBypass = true;
    public static readonly bool EnableWorkerWorkingSpeedNoRepeatSet = true;
    public static readonly bool EnableFillInputWorkplaceOptimizer = false;
    public static readonly bool EnableWaitInsideIdlyOptimizer = false;
    public static readonly bool EnableBehaviorManagerProcessOptimizer = false;
    public static readonly bool EnableExecutorTickProfiler = false;
    public static readonly bool EnableNeedActionPositionSampling = false;
    public static readonly bool EnableNeedActionFlowFieldProbe = false;
    public static readonly bool EnableDistrictNeedBehaviorDirectOptimizer = true;
    public static readonly bool EnableDistrictNeedAppraisalCache = true;
    // Permanently disabled after the guard fired on real data even with the
    // speed-normalized distance cache: game blueprints define navmesh edges
    // with arbitrary spec costs including 0.25 (tubes) and 0.0 (entrance
    // links), some spanning horizontal tiles, so NO positive geometric factor
    // is a sound lower bound. With the distance cache in place, per-candidate
    // evaluation is a sub-microsecond lookup anyway, so pruning's remaining
    // value does not justify the risk. Keep the code + guard for reference.
    public static readonly bool EnableDistrictNeedBoundPruning = false;
    // Replaces the hours-based shadow cache for Walker.CalculateTravelTimeInHours:
    // cache PATH DISTANCES (speed-independent, navmesh-version-invalidated) and
    // convert to hours per query with the walker's own current base speed.
    // More vanilla-faithful than sharing hours across differently-fast walkers.
    public static readonly bool EnableTravelDistanceCache = true;
    public const int TravelDistanceCacheMaxEntries = 262144;
    // Invalidate the global need-travel shadow cache whenever the regular
    // navmesh actually changes (batched, at most once per tick) instead of
    // relying on the frame TTL. Makes the cache exact and lets entries live
    // for GlobalNeedTravelShadowCacheEventModeTtlFrames.
    public static readonly bool EnableNavMeshEventTravelCacheInvalidation = true;
    public const int GlobalNeedTravelShadowCacheEventModeTtlFrames = 18000;
    public static readonly bool EnableDistrictNeedDirectDetailedStats = false;
    public static readonly bool EnableEmptyInventoriesFastPath = true;
    // Sound lower bound on nav path distance. Default edges cost the plain
    // horizontal euclidean length (NavMeshEdge.CreateDefault), but zipline
    // cable edges cost CableUnitCost=0.4 per 3D length unit (game 1.1.0.2
    // spec), so any path costs at least 0.4 * horizontal euclidean minus
    // start/end node snap slack. The runtime guard in
    // DistrictNeedBehaviorDirectOptimizer disables pruning permanently if a
    // measured duration ever undercuts this bound.
    public const float NavDistanceLowerBoundFactor = 0.4f;
    // Raw distance units removed from the euclidean estimate BEFORE the
    // minimum edge-cost factor is applied. Covers node snap error (~1.4
    // horizontal units) plus distance-cache key quantization aliasing (~1.4
    // units per endpoint at quantize step 1.0): 1.4 + 2 * 1.4 = 4.2, rounded up.
    public const float NavDistanceLowerBoundSlack = 4.5f;
    public const float BoundPruningGuardEpsilonHours = 0.001f;
    public static readonly bool EnableDistrictResourceCounterThrottle = true;
    public static readonly bool EnableWaterObjectServiceThrottle = false;
    public static readonly bool EnableWaterObjectServiceFastSkip = true;
    public static readonly bool EnableThreadSafeWaterMapTickThrottle = false;
    public static readonly bool EnableThreadSafeWaterFlowDirectionThrottle = false;
    public static readonly bool EnableRangedEffectSubjectThrottle = false;
    public static readonly bool EnableContaminationApplierThrottle = false;
    public static readonly bool EnableNoActionCooldown = false;
    public static readonly bool EnableMechanicalGraphLoadBatching = true;
    public static readonly bool EnableTickDispatchOptimizer = true;

    // Measurement-only stopwatch probes around the road/mechanical topology
    // UI hot paths (district flow-field recomputes, road overlay rebuild,
    // mechanical network DFS + rehighlight, preview placer). One of the
    // patched methods (DistrictMap.RecalculateRoadFlowFields) is also on the
    // sim pathfinding hot path, so SET FALSE FOR SHIPPED BUILDS and never
    // compare absolute ticks/s from probe-enabled runs.
    public static readonly bool EnableTopologyUiProbe = true;
    public const float TopoUiReportWindowSeconds = 5f;
    // UI-only fixes for the topology hot paths (see TopologyUiOptimizer):
    // diff-based mechanical network highlight instead of unhighlight-all +
    // re-highlight-all on every refresh (46ms -> DFS-only on a 1643-node
    // network); rate-limited district path overlay rebuilds (33ms per rebuild,
    // vanilla re-fires it on ANY instant-navmesh change while selected);
    // preview placer skip when the placement list did not change since the
    // last recent full pass (vanilla re-adds previews to the preview navmesh
    // EVERY frame while a tool is held, even with the cursor still).
    // Placement validation itself is untouched - only visuals refresh less.
    public static readonly bool EnableMechanicalHighlightDiff = true;
    public static readonly bool EnablePathOverlayRebuildThrottle = true;
    public static readonly bool EnablePreviewPlacerSkip = true;
    public const float TopoPathOverlayMinRebuildIntervalSeconds = 0.35f;
    public const float TopoPreviewRefreshIntervalSeconds = 0.25f;
    // Coalesce mechanical highlight refreshes: dragging a gear preview flips
    // the network between connected/disconnected, repainting ~all nodes per
    // flip even with the diff. One refresh per interval bounds that cost;
    // deferred dirt is re-armed so the final state always gets painted.
    public const float TopoHighlightMinRefreshIntervalSeconds = 0.25f;
    // Budgeted highlight painting: at most this many highlight/unhighlight
    // material operations per frame (~12us each => ~5ms/frame worst case).
    // A full 1600-node repaint becomes a 4-frame sweep instead of one hitch.
    public const int TopoHighlightOpsPerFrame = 400;
    // Only re-render the district path overlay when a navmesh change touches
    // (or neighbors) the drawn area, instead of on every change map-wide.
    public static readonly bool EnablePathOverlayInvalidationFilter = true;
    // Defer + dedupe BlockObjectModelController.UpdateModel into one flush
    // per frame (BuildingModelUpdater re-fires it per changed coordinate,
    // multi-tile objects once PER TILE; measured 276k calls / 366ms in a 5s
    // burst). Rendering sees only end-of-frame state, so visually identical.
    public static readonly bool EnableModelUpdateBatching = true;
    // While dragging (placements changing every tile), still cap full
    // ShowPreviews runs: the ghost trails the cursor by at most this long.
    // Clicking always validates fresh placements, so nothing can be
    // mis-placed - only the ghost visuals update at this rate.
    public const float TopoPreviewDragMinIntervalSeconds = 0.1f;

    // Smooth pacing v2 (user decision 2026-07-05: opt-in toggle, Shift+O).
    // Governs Time.timeScale toward a target frame rate at high speeds -
    // trading some achieved speed for smoothness, bounded below by
    // max(1, requested * GovernorMinScaleFraction) so a render-bound colony
    // never collapses to x1. All scaled clocks stay consistent (unlike the
    // retired v1 delta-time cap), so sim results equal vanilla at the speed
    // actually achieved. OFF unless the user presses Shift+O.
    public static readonly bool EnableSmoothTimeScaleGovernor = true;
    public const float GovernorTargetFps = 30f;
    public const float GovernorMinScaleFraction = 0.15f;
    // Absolute floor: never governs below min(requested, this), so pressing
    // a modest speed like x7 is shaved to x3 at worst, not toward x1.
    public const float GovernorAbsoluteMinSpeed = 3f;
    public const float GovernorAdjustDownFactor = 0.97f;
    public const float GovernorAdjustUpFactor = 1.03f;
    // Scenario pacing (see TopologyUiScenario). Settle covers post-load
    // stabilization before the first selection; hold keeps each selection
    // active long enough to catch event-driven re-highlights at speed.
    public const float TopoUiScenarioSettleSeconds = 20f;
    public const float TopoUiScenarioHoldSeconds = 5f;
    public const float TopoUiScenarioIdleSeconds = 2f;
    public const int TopoUiScenarioCycles = 5;
    // x50: with the population throttle removed this is comfortably past the
    // CPU ceiling on the test save (user rule: x7 is NOT CPU-bound, x50 is).
    public const float TopoUiTargetSpeed = 50f;

    // Deliberately behavior-CHANGING (user decision 2026-07-04: ships ON):
    // remove the vanilla population speed throttle (GameSpeedThrottler scales
    // requested speed down to 40% between 30 and 200 beavers). Forces the
    // scale to 1 so the requested speed applies raw - pressing x3 gives x3 on
    // any colony size. Only changes the achievable speed cap, never the
    // per-tick simulation. Disclosed in the store description / README.
    public static readonly bool EnableGameSpeedThrottlerRemoval = true;

    // Skip applying Timbermesh animation POSES for animators whose renderers
    // are not visible to any camera (Renderer.isVisible includes shadow
    // rendering, so this is visually lossless). Animation time and
    // PlayingFinished keep advancing normally; a dirty flag re-applies the
    // pose when the renderer becomes visible again.
    public static readonly bool EnableInvisibleAnimatorPoseSkip = true;

    // Replace the reflective closure EventBus.RegisterMethod builds per
    // [OnEvent] handler (method.Invoke + new object[1] on EVERY delivery)
    // with a compiled delegate. Same handlers, same order, same exceptions;
    // large win on entity spawns and save-load (EntityInitializedEvent is
    // ~26k posts x ~680 handlers).
    public static readonly bool EnableEventBusFastDelegates = true;

    // Mirror GameObject.activeInHierarchy into a dense bitmask via an
    // ActiveInHierarchySentinel MonoBehaviour on each tickable entity (Unity's
    // synchronous OnEnable/OnDisable callbacks keep the bit exact at visit
    // time). Replaces ~18M native activeInHierarchy calls per 20s in the flat
    // sweep; measured ~+8% ticks at the compute ceiling.
    public static readonly bool EnableActiveInHierarchyMirror = true;

    // Flat-array bucket dispatch (v2 of the tick dispatch optimizer): per bucket,
    // all tickable components are cached in one flat array swept front-to-back,
    // with enabled state mirrored in a bitmask updated from
    // EnableComponent/DisableComponent hooks. Falls back to the per-entity
    // cached path when false (kept for A/B ablation).
    public static readonly bool EnableFlatTickDispatch = true;
    public static readonly bool EnableMainLoopProfiler = false;
    public static readonly bool EnableMainLoopTypeProfiler = false;
    public static readonly bool EnableMainLoopUpdateTypeProfiler = false;
    public static readonly bool EnableUnityMarkerProfiler = false;
    public static readonly bool EnableAnimatorRegistryThrottle = true;
    public static readonly bool EnableAnimatorRegistryDetailProfiler = false;
    public static readonly bool EnableMechanicalAnimationBatchProbe = false;
    public static readonly bool EnableMechanicalDirectRotationOptimizer = true;
    public static readonly bool EnableMechanicalDirectRotationSampleCache = true;
    public static readonly bool EnableMechanicalDirectCommonFrameSampling = false;
    public static readonly bool EnableNodeTransformDirectOptimizer = false;
    public static readonly bool EnableDefaultRotationOnlyDirectOptimizer = true;
    public static readonly bool EnableDefaultMechanicalAnimatorThrottle = false;
    public static readonly bool EnableDefaultMechanicalAnimatorDetailProfiler = false;
    public static readonly bool EnableDefaultMechanicalAnimatorRegistryReplacement = false;
    public static readonly bool EnableDefaultMechanicalAnimatorUpdatePatch = false;
    public static readonly bool EnableMovementAnimatorThrottle = true;
    public static readonly bool EnableStatusIconPositionerThrottle = false;
    public static readonly bool EnableSoundListenerStaticCameraOptimizer = false;
    // Keep false: TubeVisitorUpdater drives stateful tube enter/exit each frame
    // and is cheap; never throttle it. (The tube lingering-light bug was
    // actually caused by CharacterModel.LateUpdate suppression freezing the
    // model position TubeVisitor reads - see BenchmarkProbe - not by this.)
    public static readonly bool EnableTubeVisitorUpdaterThrottle = false;
    public static readonly bool EnableStatusAggregatorThrottle = false;
    public static readonly bool EnableTickVisualSingletonThrottle = true;
    public static readonly bool EnableUnattendedVisualSuppression = true;
    public static readonly bool EnableStutterDetailProfiler = false;
    public static readonly bool EnableRangedEffectSubjectProfiler = false;
    public static readonly bool EnableLoadComponentProfiler = false;
    public static readonly bool EnableLoadSingletonProfiler = false;
    public static readonly bool EnableLoadStateListenerProfiler = false;
    public static readonly bool EnableLoadEventProfiler = false;
    public static readonly bool EnableRuntimeOverlay = false;
    public static readonly bool EnableHitchLogging = false;
    public static readonly bool EnableOptimizedRenderBlackout = true;
    // During render blackout, lift the blackout for exactly one frame every
    // RenderPeekIntervalFullTicks full ticks so the screen shows a fresh
    // snapshot of the colony. All suppression paths key off
    // RenderBlackoutActive, so the peek frame runs the normal visual updates.
    public static readonly bool EnableBlackoutRenderPeek = true;
    public const int RenderPeekIntervalFullTicks = 100;
    // On-screen live tick-rate meter (bottom-right) while the mod is active.
    // Doubles as a liveness indicator during the Shift+P blackout: the number
    // keeps updating while the simulation runs.
    public static readonly bool EnableSpeedupOverlay = true;
    // Experimental: render the meter through the game's UI Toolkit layer
    // (game font) instead of IMGUI. Off by default because it attaches to a
    // discovered UIDocument that may not be the on-screen HUD; the IMGUI meter
    // is the reliable default.
    public static readonly bool EnableSpeedupOverlayGameUi = false;
    // Shift+P toggles the render blackout + animation thinning. Disable to
    // unbind it. The mod does not change game speed; speed is left to the base
    // game (and composes with any speed mod).
    public static readonly bool EnableRenderBlackoutToggleKey = true;
    // Timberborn's fixed simulation tick advances this many in-game seconds at
    // 1x speed (Configurations/TickTime.blueprint: TickIntervalInSeconds), so
    // realtime multiplier = simulation ticks per real second * this value.
    public const float GameTickIntervalSeconds = 0.6f;
    public static readonly bool EnablePathFollowerNreGuard = true;
    public static readonly bool EnableSpeedManagerProbe = true;
    public static readonly bool EnableSpeedManagerLogging = false;
    public static readonly bool EnableTimeSpeedButtonGroupAutoResume = false;
    public static readonly bool EnableTimeSpeedButtonGroupProbe = false;
    public static readonly bool EnableOptimizedFrameRateUncap = true;
    public static readonly bool EnableOptimizedUltraSpeed = true;
    public static readonly bool EnableAutoForceOptimizedAfterLoad = true;
    public static readonly bool EnableStartupGcBeforeAutoResume = true;
    public static readonly bool ForceOptimizedByDefault = false;
    public static readonly bool EnableAutoResumeGameSpeed = false;
    public const int WalkerDistanceCacheTtlFrames = 120;
    public const int GlobalNeedTravelShadowCacheTtlFrames = 600;
    public const int GlobalNeedTravelShadowCachePruneIntervalFrames = 120;
    public const int NoActionCooldownFrames = 60;
    public const int FastYielderDistanceCacheMaxEntries = 1024;
    public const int FastYielderDistanceCacheMaxAgeFrames = 600;
    public const int InventoryStockDistanceCacheTtlFrames = 1800;
    public const int InventoryCapacityDistanceCacheTtlFrames = 1800;
    public const int FarmEventDrivenSafetyRefreshFrames = 600;
    public const int DistrictResourceCounterThrottleTicks = 2;
    public const int WaterObjectServiceThrottleTicks = 2;
    public const int ThreadSafeWaterMapTickThrottleTicks = 2;
    public const int ThreadSafeWaterFlowDirectionIntervalTicks = 10;
    public const int RangedEffectSubjectThrottleTicks = 2;
    public const int ContaminationApplierThrottleTicks = 2;
    public const int PlantingSpotFinderCacheTtlFrames = 600;
    public const int NeedActionFlowFieldProbeSampleRate = 32;
    public const int NeedActionFlowFieldProbeMaxSamplesPerAggregate = 1024;
    public const int MainLoopProfilerTopEntries = 12;
    public const int ExecutorTickProfilerTopEntries = 12;
    public const int AnimatorRegistryThrottleFrames = 2;
    public const int AnimatorRegistryDetailSampleFrames = 120;
    public const int AnimatorRegistryDetailTopEntries = 12;
    public const int MechanicalAnimationBatchProbeSampleFrames = 300;
    public const int MechanicalAnimationBatchProbeTopEntries = 10;
    public const int MechanicalDirectVisibilityRefreshFrames = 30;
    public const int DefaultMechanicalAnimatorThrottleFrames = 3;
    public const int MovementAnimatorThrottleFrames = 2;
    public const int StatusIconPositionerThrottleFrames = 2;
    public const int SoundListenerStaticCameraIntervalFrames = 4;
    public const int TubeVisitorUpdaterThrottleFrames = 2;
    public const int StatusAggregatorThrottleFrames = 2;
    public const int WaterRendererThrottleTicks = 2;
    public const int ModularShaftAnimatorThrottleTicks = 2;
    public const int LoadProfilerTopEntries = 20;
    public const float HitchLogThresholdSeconds = 0.25f;
    public const float LoadSlowCallThresholdMilliseconds = 250f;
    public const float MainLoopSlowStageThresholdMilliseconds = 250f;
    public const float MainLoopSlowTypeThresholdMilliseconds = 250f;
    public const float RuntimeHotspotSlowThresholdMilliseconds = 10f;
    public const float RangedEffectSlowThresholdMilliseconds = 10f;
    public const float StatusAggregatorSlowThresholdMilliseconds = 10f;
    public const float UnreachableHomeSlowThresholdMilliseconds = 10f;
    public const float NavMeshNotifySlowThresholdMilliseconds = 10f;
    public const float RuntimeOverlayWindowSeconds = 3f;
    public const float AutoResumeGameAfterSeconds = 3f;
    public const float AutoResumeGameIntervalSeconds = 2f;
    // Disabled by default. Fastest-speed benchmarks must use TimeSpeedButtonGroup
    // and confirm SpeedManager currentSpeed=7 in logs.
    public const float AutoResumeTargetSpeed = 7f;
    // Smooth frame pacing v1: cap the game time the sim ticker consumes per
    // rendered frame in visible high-speed play (measured fps 0.8 -> ~8).
    // DEFAULT OFF: v1 breaks the frame-time == sim-time invariant, so
    // per-frame systems (MovementAnimator) receive more game time than the
    // sim advanced and character models can run ahead of their path and
    // visibly "walk in place" (user-reported at x99; the sim state itself
    // stays correct - verified with a stuck-walker probe). A v2 should govern
    // Time.timeScale itself down to the achievable speed instead, which keeps
    // every clock consistent by construction.
    public static readonly bool EnableSmoothFramePacing = false;
    public const float SmoothFramePacingMinTimeScale = 5f;
    public const float SmoothFramePacingMaxDeltaTime = 0.05f;
    // While smooth frame pacing is active, sample Timbermesh animations only
    // every Nth rendered frame (1 = vanilla full rate). Movement stays
    // per-frame smooth; only the skeletal pose rate drops.
    public const int SmoothPacingAnimationFrameStride = 2;
    // Benchmark-only (-benchAutoUltra) requested speed. NOTE: the vanilla
    // GameSpeedThrottler rescales by population (~660 beavers on n10c =>
    // factor 0.4): requested 50 => effective 20.6 => ideal 34.33 ticks/s,
    // which the optimized build now REACHES (speed-limited, not CPU-limited).
    // 99 gives effective 40.2 => ideal 67 ticks/s so benchmarks stay CPU-bound.
    public const float OptimizedUltraSpeed = 99f;
    public const float AutoForceOptimizedAfterLoadSeconds = 0f;
    public const float WalkerDistanceCacheQuantizeStep = 1f;
    public const float PickBestTravelCacheQuantizeStep = 1f;
    public static readonly string OptimizedImplementationName = "release-v1";
    public const float ModeSegmentSeconds = 2f;
    public const float AggregateSeconds = 20f;
    public const int WarmupFramesAfterSwitch = 5;
}
