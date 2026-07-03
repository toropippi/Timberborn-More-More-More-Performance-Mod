using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NeedActionFlowFieldProbe
{
    private const float MatchToleranceHours = 0.02f;

    private static readonly Type? ActionDurationCalculatorType = FindType("Timberborn.NeedBehaviorSystem.ActionDurationCalculator");
    private static readonly Type? WalkerType = FindType("Timberborn.WalkingSystem.Walker");
    private static readonly Type? NavigationServiceType = FindType("Timberborn.Navigation.NavigationService");
    private static readonly Type? PathfindingServiceType = FindType("Timberborn.Navigation.PathfindingService");
    private static readonly Type? NodeIdServiceType = FindType("Timberborn.Navigation.NodeIdService");
    private static readonly Type? AccessFlowFieldType = FindType("Timberborn.Navigation.AccessFlowField");
    private static readonly Type? WalkerSpeedManagerType = FindType("Timberborn.WalkingSystem.WalkerSpeedManager");
    private static readonly Type? DayNightCycleType = FindType("Timberborn.TimeSystem.IDayNightCycle");

    private static readonly FieldInfo? WalkerField = ActionDurationCalculatorType?.GetField("_walker", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo? TransformProperty = FindProperty(ActionDurationCalculatorType, "Transform");
    private static readonly FieldInfo? NavigationServiceField = WalkerType?.GetField("_navigationService", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? WalkerSpeedManagerField = WalkerType?.GetField("_walkerSpeedManager", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? DayNightCycleField = WalkerType?.GetField("_dayNightCycle", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PathfindingServiceField = NavigationServiceType?.GetField("_pathfindingService", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? RoadFlowFieldCacheField = PathfindingServiceType?.GetField("_roadFlowFieldCache", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? TerrainFlowFieldCacheField = PathfindingServiceType?.GetField("_terrainFlowFieldCache", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? NodeIdServiceField = PathfindingServiceType?.GetField("_nodeIdService", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? WorldToIdMethod = NodeIdServiceType?.GetMethod("WorldToId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? IdToWorldMethod = NodeIdServiceType?.GetMethod("IdToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? NumberOfNodesProperty = NodeIdServiceType?.GetProperty("NumberOfNodes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? RoadTryGetFlowFieldMethod = FindTryGetFlowFieldMethod(FindType("Timberborn.Navigation.RoadFlowFieldCache"));
    private static readonly MethodInfo? TerrainTryGetFlowFieldMethod = FindTryGetFlowFieldMethod(FindType("Timberborn.Navigation.TerrainFlowFieldCache"));
    private static readonly PropertyInfo? FlowFieldIsFilledProperty = AccessFlowFieldType?.GetProperty("IsFilled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo? FlowFieldNumberOfNodesProperty = AccessFlowFieldType?.GetProperty("NumberOfNodes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? FlowFieldFoundPathMethod = AccessFlowFieldType?.GetMethod("FoundPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? FlowFieldGetDistanceMethod = AccessFlowFieldType?.GetMethod("GetDistance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? FindPathUncachedMethod = FindVectorPathMethod(PathfindingServiceType, "FindPathUncached");
    private static readonly MethodInfo? GetWalkerBaseSpeedMethod = WalkerSpeedManagerType?.GetMethod("GetWalkerBaseSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? SecondsToHoursMethod = DayNightCycleType?.GetMethod("SecondsToHours", BindingFlags.Instance | BindingFlags.Public);

    private static int _callCounter;
    private static int _samplesThisAggregate;
    private static int _warningLogged;
    private static long _scheduledSamples;
    private static long _recordedSamples;
    private static long _setupFailures;
    private static long _nodeCountSamples;
    private static long _nodeCountSum;
    private static int _nodeCountMax;
    private static long _roadCacheHits;
    private static long _roadFilled;
    private static long _roadCurrentHits;
    private static long _roadReturnHits;
    private static long _roadBothHits;
    private static long _roadNodeCountSum;
    private static int _roadNodeCountMax;
    private static long _roadMatches;
    private static double _roadBestErrorSum;
    private static double _roadBestErrorMax;
    private static long _terrainCacheHits;
    private static long _terrainFilled;
    private static long _terrainCurrentHits;
    private static long _terrainReturnHits;
    private static long _terrainBothHits;
    private static long _terrainNodeCountSum;
    private static int _terrainNodeCountMax;
    private static long _terrainMatches;
    private static double _terrainBestErrorSum;
    private static double _terrainBestErrorMax;
    private static long _eitherBothHits;
    private static long _bestMatches;
    private static double _bestErrorSum;
    private static double _bestErrorMax;

    [ThreadStatic]
    private static DurationContext _durationContext;

    private static readonly object TravelLegLock = new object();
    private static readonly TravelLegStats[] TravelLegStatsByKind =
    {
        new TravelLegStats("currentToAction"),
        new TravelLegStats("actionToReturn"),
        new TravelLegStats("other")
    };

    private static int _travelLegCallCounter;
    private static int _travelLegSamplesThisAggregate;

    public static void BeginDuration(
        object actionDurationCalculator,
        Vector3 actionPosition,
        Vector3 returnPosition,
        out DurationProbeState state)
    {
        state = DurationProbeState.Inactive;
        if (!BenchmarkSettings.EnableNeedActionFlowFieldProbe)
        {
            return;
        }

        if (!TryEnterDurationContext(actionDurationCalculator, actionPosition, returnPosition))
        {
            return;
        }

        var call = Interlocked.Increment(ref _callCounter);
        if (call % BenchmarkSettings.NeedActionFlowFieldProbeSampleRate != 0)
        {
            return;
        }

        if (Interlocked.Increment(ref _samplesThisAggregate) > BenchmarkSettings.NeedActionFlowFieldProbeMaxSamplesPerAggregate)
        {
            return;
        }

        try
        {
            state = new DurationProbeState(true, actionDurationCalculator, _durationContext.CurrentPosition, actionPosition, returnPosition);
            Interlocked.Increment(ref _scheduledSamples);
        }
        catch (Exception exception)
        {
            LogWarningOnce(exception);
            Interlocked.Increment(ref _setupFailures);
        }
    }

    public static void RecordDurationReturn(DurationProbeState state, float actualDuration)
    {
        if (!state.Active)
        {
            return;
        }

        try
        {
            if (!TryGetProbeContext(state.ActionDurationCalculator, out var context))
            {
                Interlocked.Increment(ref _setupFailures);
                return;
            }

            var currentNode = context.WorldToId(state.CurrentPosition);
            var actionNode = context.WorldToId(state.ActionPosition);
            var returnNode = context.WorldToId(state.ReturnPosition);
            RecordNodeCount(context.NodeCount);

            var road = ProbeFlowField(
                context.RoadFlowFieldCache,
                RoadTryGetFlowFieldMethod,
                actionNode,
                currentNode,
                returnNode,
                context);
            var terrain = ProbeFlowField(
                context.TerrainFlowFieldCache,
                TerrainTryGetFlowFieldMethod,
                actionNode,
                currentNode,
                returnNode,
                context);

            RecordFlowStats(road, isRoad: true);
            RecordFlowStats(terrain, isRoad: false);
            RecordEstimateStats(road, terrain, actualDuration);
            Interlocked.Increment(ref _recordedSamples);
        }
        catch (Exception exception)
        {
            LogWarningOnce(exception);
            Interlocked.Increment(ref _setupFailures);
        }
    }

    public static void EndDuration()
    {
        _durationContext = DurationContext.Inactive;
    }

    public static void BeginTravelLeg(
        object walker,
        Vector3 start,
        Vector3 destination,
        out TravelLegProbeState state)
    {
        state = TravelLegProbeState.Inactive;
        if (!BenchmarkSettings.EnableNeedActionFlowFieldProbe)
        {
            return;
        }

        var call = Interlocked.Increment(ref _travelLegCallCounter);
        if (call % BenchmarkSettings.NeedActionFlowFieldProbeSampleRate != 0)
        {
            return;
        }

        if (Interlocked.Increment(ref _travelLegSamplesThisAggregate) > BenchmarkSettings.NeedActionFlowFieldProbeMaxSamplesPerAggregate)
        {
            return;
        }

        var kind = ClassifyTravelLeg(start, destination);
        state = new TravelLegProbeState(true, kind, walker, start, destination);
        lock (TravelLegLock)
        {
            TravelLegStatsByKind[(int)kind].Scheduled++;
        }
    }

    public static void RecordTravelLegReturn(TravelLegProbeState state, float actualHours)
    {
        if (!state.Active)
        {
            return;
        }

        lock (TravelLegLock)
        {
            var stats = TravelLegStatsByKind[(int)state.Kind];
            try
            {
                if (!TryGetProbeContextFromWalker(state.Walker, out var context))
                {
                    stats.SetupFailures++;
                    return;
                }

                var startNode = context.WorldToId(state.Start);
                var destinationNode = context.WorldToId(state.Destination);
                var startValid = context.IsValidNode(startNode);
                var destinationValid = context.IsValidNode(destinationNode);
                stats.RecordPosition(state.Start, startNode, destinationNode, startValid, destinationValid, context);

                var roadFromStart = ProbeFlowFieldDistance(
                    context.RoadFlowFieldCache,
                    RoadTryGetFlowFieldMethod,
                    startNode,
                    destinationNode,
                    context);
                var roadFromDestination = ProbeFlowFieldDistance(
                    context.RoadFlowFieldCache,
                    RoadTryGetFlowFieldMethod,
                    destinationNode,
                    startNode,
                    context);
                var terrainFromStart = ProbeFlowFieldDistance(
                    context.TerrainFlowFieldCache,
                    TerrainTryGetFlowFieldMethod,
                    startNode,
                    destinationNode,
                    context);
                var terrainFromDestination = ProbeFlowFieldDistance(
                    context.TerrainFlowFieldCache,
                    TerrainTryGetFlowFieldMethod,
                    destinationNode,
                    startNode,
                    context);

                stats.RecordFlow(0, roadFromStart, actualHours);
                stats.RecordFlow(1, roadFromDestination, actualHours);
                stats.RecordFlow(2, terrainFromStart, actualHours);
                stats.RecordFlow(3, terrainFromDestination, actualHours);
                if (state.Kind == TravelLegKind.CurrentToAction)
                {
                    stats.RecordRoundedCurrentDistance(context, state.Start, state.Destination, actualHours);
                }

                stats.Recorded++;
            }
            catch (Exception exception)
            {
                LogWarningOnce(exception);
                stats.SetupFailures++;
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var scheduledSamples = Interlocked.Exchange(ref _scheduledSamples, 0);
        var recordedSamples = Interlocked.Exchange(ref _recordedSamples, 0);
        var setupFailures = Interlocked.Exchange(ref _setupFailures, 0);
        var nodeCountSamples = Interlocked.Exchange(ref _nodeCountSamples, 0);
        var nodeCountSum = Interlocked.Exchange(ref _nodeCountSum, 0);
        var nodeCountMax = Interlocked.Exchange(ref _nodeCountMax, 0);
        var roadCacheHits = Interlocked.Exchange(ref _roadCacheHits, 0);
        var roadFilled = Interlocked.Exchange(ref _roadFilled, 0);
        var roadCurrentHits = Interlocked.Exchange(ref _roadCurrentHits, 0);
        var roadReturnHits = Interlocked.Exchange(ref _roadReturnHits, 0);
        var roadBothHits = Interlocked.Exchange(ref _roadBothHits, 0);
        var roadNodeCountSum = Interlocked.Exchange(ref _roadNodeCountSum, 0);
        var roadNodeCountMax = Interlocked.Exchange(ref _roadNodeCountMax, 0);
        var roadMatches = Interlocked.Exchange(ref _roadMatches, 0);
        var roadBestErrorSum = ExchangeDouble(ref _roadBestErrorSum, 0);
        var roadBestErrorMax = ExchangeDouble(ref _roadBestErrorMax, 0);
        var terrainCacheHits = Interlocked.Exchange(ref _terrainCacheHits, 0);
        var terrainFilled = Interlocked.Exchange(ref _terrainFilled, 0);
        var terrainCurrentHits = Interlocked.Exchange(ref _terrainCurrentHits, 0);
        var terrainReturnHits = Interlocked.Exchange(ref _terrainReturnHits, 0);
        var terrainBothHits = Interlocked.Exchange(ref _terrainBothHits, 0);
        var terrainNodeCountSum = Interlocked.Exchange(ref _terrainNodeCountSum, 0);
        var terrainNodeCountMax = Interlocked.Exchange(ref _terrainNodeCountMax, 0);
        var terrainMatches = Interlocked.Exchange(ref _terrainMatches, 0);
        var terrainBestErrorSum = ExchangeDouble(ref _terrainBestErrorSum, 0);
        var terrainBestErrorMax = ExchangeDouble(ref _terrainBestErrorMax, 0);
        var eitherBothHits = Interlocked.Exchange(ref _eitherBothHits, 0);
        var bestMatches = Interlocked.Exchange(ref _bestMatches, 0);
        var bestErrorSum = ExchangeDouble(ref _bestErrorSum, 0);
        var bestErrorMax = ExchangeDouble(ref _bestErrorMax, 0);
        Interlocked.Exchange(ref _samplesThisAggregate, 0);

        if (scheduledSamples == 0 && recordedSamples == 0 && setupFailures == 0)
        {
            return;
        }

        var nodeCountAvg = nodeCountSamples > 0 ? (double)nodeCountSum / nodeCountSamples : 0;
        var roadNodeAvg = roadCacheHits > 0 ? (double)roadNodeCountSum / roadCacheHits : 0;
        var terrainNodeAvg = terrainCacheHits > 0 ? (double)terrainNodeCountSum / terrainCacheHits : 0;
        var roadErrorAvg = roadBothHits > 0 ? roadBestErrorSum / roadBothHits : 0;
        var terrainErrorAvg = terrainBothHits > 0 ? terrainBestErrorSum / terrainBothHits : 0;
        var bestErrorAvg = eitherBothHits > 0 ? bestErrorSum / eitherBothHits : 0;
        Debug.Log(
            $"[T3MP] NeedActionFlowFields aggregate={aggregateId}, sampleRate={BenchmarkSettings.NeedActionFlowFieldProbeSampleRate}, scheduled={scheduledSamples}, recorded={recordedSamples}, setupFailures={setupFailures}, nodeCountAvg={nodeCountAvg:F0}, nodeCountMax={nodeCountMax}, roadCacheHits={roadCacheHits}, roadFilled={roadFilled}, roadCurrentHits={roadCurrentHits}, roadReturnHits={roadReturnHits}, roadBothHits={roadBothHits}, roadNodeAvg={roadNodeAvg:F0}, roadNodeMax={roadNodeCountMax}, roadMatches={roadMatches}, roadErrorAvgHours={roadErrorAvg:F4}, roadErrorMaxHours={roadBestErrorMax:F4}, terrainCacheHits={terrainCacheHits}, terrainFilled={terrainFilled}, terrainCurrentHits={terrainCurrentHits}, terrainReturnHits={terrainReturnHits}, terrainBothHits={terrainBothHits}, terrainNodeAvg={terrainNodeAvg:F0}, terrainNodeMax={terrainNodeCountMax}, terrainMatches={terrainMatches}, terrainErrorAvgHours={terrainErrorAvg:F4}, terrainErrorMaxHours={terrainBestErrorMax:F4}, eitherBothHits={eitherBothHits}, bestMatches={bestMatches}, bestErrorAvgHours={bestErrorAvg:F4}, bestErrorMaxHours={bestErrorMax:F4}");

        LogAndResetTravelLegStats(aggregateId);
    }

    private static bool TryEnterDurationContext(object actionDurationCalculator, Vector3 actionPosition, Vector3 returnPosition)
    {
        try
        {
            if (!TryGetCurrentPosition(actionDurationCalculator, out var currentPosition))
            {
                return false;
            }

            _durationContext = new DurationContext(true, currentPosition, actionPosition, returnPosition);
            return true;
        }
        catch (Exception exception)
        {
            LogWarningOnce(exception);
            return false;
        }
    }

    private static bool TryGetCurrentPosition(object actionDurationCalculator, out Vector3 position)
    {
        position = default;
        if (TransformProperty?.GetValue(actionDurationCalculator) is not Transform transform)
        {
            return false;
        }

        position = transform.position;
        return true;
    }

    private static bool TryGetProbeContext(object actionDurationCalculator, out ProbeContext context)
    {
        context = default;
        if (WalkerField?.GetValue(actionDurationCalculator) is not { } walker ||
            NavigationServiceField?.GetValue(walker) is not { } navigationService ||
            PathfindingServiceField?.GetValue(navigationService) is not { } pathfindingService ||
            RoadFlowFieldCacheField?.GetValue(pathfindingService) is not { } roadFlowFieldCache ||
            TerrainFlowFieldCacheField?.GetValue(pathfindingService) is not { } terrainFlowFieldCache ||
            NodeIdServiceField?.GetValue(pathfindingService) is not { } nodeIdService ||
            WalkerSpeedManagerField?.GetValue(walker) is not { } walkerSpeedManager ||
            DayNightCycleField?.GetValue(walker) is not { } dayNightCycle)
        {
            return false;
        }

        context = new ProbeContext(
            pathfindingService,
            roadFlowFieldCache,
            terrainFlowFieldCache,
            nodeIdService,
            walkerSpeedManager,
            dayNightCycle);
        return true;
    }

    private static bool TryGetProbeContextFromWalker(object walker, out ProbeContext context)
    {
        context = default;
        if (NavigationServiceField?.GetValue(walker) is not { } navigationService ||
            PathfindingServiceField?.GetValue(navigationService) is not { } pathfindingService ||
            RoadFlowFieldCacheField?.GetValue(pathfindingService) is not { } roadFlowFieldCache ||
            TerrainFlowFieldCacheField?.GetValue(pathfindingService) is not { } terrainFlowFieldCache ||
            NodeIdServiceField?.GetValue(pathfindingService) is not { } nodeIdService ||
            WalkerSpeedManagerField?.GetValue(walker) is not { } walkerSpeedManager ||
            DayNightCycleField?.GetValue(walker) is not { } dayNightCycle)
        {
            return false;
        }

        context = new ProbeContext(
            pathfindingService,
            roadFlowFieldCache,
            terrainFlowFieldCache,
            nodeIdService,
            walkerSpeedManager,
            dayNightCycle);
        return true;
    }

    private static FlowProbeResult ProbeFlowField(
        object flowFieldCache,
        MethodInfo? tryGetFlowFieldMethod,
        int actionNode,
        int currentNode,
        int returnNode,
        ProbeContext context)
    {
        if (tryGetFlowFieldMethod is null ||
            FlowFieldIsFilledProperty is null ||
            FlowFieldNumberOfNodesProperty is null ||
            FlowFieldFoundPathMethod is null ||
            FlowFieldGetDistanceMethod is null)
        {
            return FlowProbeResult.Missing;
        }

        var args = new object?[] { actionNode, null };
        if (tryGetFlowFieldMethod.Invoke(flowFieldCache, args) is not true || args[1] is not { } flowField)
        {
            return FlowProbeResult.Missing;
        }

        var isFilled = FlowFieldIsFilledProperty.GetValue(flowField) is true;
        var nodeCount = FlowFieldNumberOfNodesProperty.GetValue(flowField) is int count ? count : 0;
        var currentHit = FlowFieldFoundPathMethod.Invoke(flowField, new object[] { currentNode }) is true;
        var returnHit = FlowFieldFoundPathMethod.Invoke(flowField, new object[] { returnNode }) is true;
        var currentDistance = currentHit && FlowFieldGetDistanceMethod.Invoke(flowField, new object[] { currentNode }) is float current
            ? current
            : float.NaN;
        var returnDistance = returnHit && FlowFieldGetDistanceMethod.Invoke(flowField, new object[] { returnNode }) is float returnValue
            ? returnValue
            : float.NaN;
        var estimatedDuration = currentHit && returnHit &&
            context.TryDistanceToHours(currentDistance + returnDistance, out var hours)
            ? hours + 0.3f
            : float.NaN;

        return new FlowProbeResult(
            true,
            isFilled,
            nodeCount,
            currentHit,
            returnHit,
            estimatedDuration);
    }

    private static FlowLegProbeResult ProbeFlowFieldDistance(
        object flowFieldCache,
        MethodInfo? tryGetFlowFieldMethod,
        int originNode,
        int targetNode,
        ProbeContext context)
    {
        if (tryGetFlowFieldMethod is null ||
            FlowFieldIsFilledProperty is null ||
            FlowFieldNumberOfNodesProperty is null ||
            FlowFieldFoundPathMethod is null ||
            FlowFieldGetDistanceMethod is null ||
            !context.IsValidNode(originNode) ||
            !context.IsValidNode(targetNode))
        {
            return FlowLegProbeResult.Missing;
        }

        var args = new object?[] { originNode, null };
        if (tryGetFlowFieldMethod.Invoke(flowFieldCache, args) is not true || args[1] is not { } flowField)
        {
            return FlowLegProbeResult.Missing;
        }

        var isFilled = FlowFieldIsFilledProperty.GetValue(flowField) is true;
        var nodeCount = FlowFieldNumberOfNodesProperty.GetValue(flowField) is int count ? count : 0;
        var targetHit = FlowFieldFoundPathMethod.Invoke(flowField, new object[] { targetNode }) is true;
        var distance = targetHit && FlowFieldGetDistanceMethod.Invoke(flowField, new object[] { targetNode }) is float value
            ? value
            : float.NaN;
        var estimatedHours = targetHit && context.TryDistanceToHours(distance, out var hours)
            ? hours
            : float.NaN;

        return new FlowLegProbeResult(true, isFilled, nodeCount, targetHit, estimatedHours);
    }

    private static void RecordFlowStats(FlowProbeResult result, bool isRoad)
    {
        if (!result.CacheHit)
        {
            return;
        }

        if (isRoad)
        {
            Interlocked.Increment(ref _roadCacheHits);
            if (result.IsFilled)
            {
                Interlocked.Increment(ref _roadFilled);
            }
            if (result.CurrentHit)
            {
                Interlocked.Increment(ref _roadCurrentHits);
            }
            if (result.ReturnHit)
            {
                Interlocked.Increment(ref _roadReturnHits);
            }
            if (result.BothHit)
            {
                Interlocked.Increment(ref _roadBothHits);
            }
            Interlocked.Add(ref _roadNodeCountSum, result.NodeCount);
            SetMax(ref _roadNodeCountMax, result.NodeCount);
            return;
        }

        Interlocked.Increment(ref _terrainCacheHits);
        if (result.IsFilled)
        {
            Interlocked.Increment(ref _terrainFilled);
        }
        if (result.CurrentHit)
        {
            Interlocked.Increment(ref _terrainCurrentHits);
        }
        if (result.ReturnHit)
        {
            Interlocked.Increment(ref _terrainReturnHits);
        }
        if (result.BothHit)
        {
            Interlocked.Increment(ref _terrainBothHits);
        }
        Interlocked.Add(ref _terrainNodeCountSum, result.NodeCount);
        SetMax(ref _terrainNodeCountMax, result.NodeCount);
    }

    private static void RecordEstimateStats(FlowProbeResult road, FlowProbeResult terrain, float actualDuration)
    {
        var bestError = double.PositiveInfinity;
        if (road.BothHit && !float.IsNaN(road.EstimatedDuration))
        {
            var error = Math.Abs(road.EstimatedDuration - actualDuration);
            AddDouble(ref _roadBestErrorSum, error);
            SetMax(ref _roadBestErrorMax, error);
            if (error <= MatchToleranceHours)
            {
                Interlocked.Increment(ref _roadMatches);
            }
            bestError = Math.Min(bestError, error);
        }

        if (terrain.BothHit && !float.IsNaN(terrain.EstimatedDuration))
        {
            var error = Math.Abs(terrain.EstimatedDuration - actualDuration);
            AddDouble(ref _terrainBestErrorSum, error);
            SetMax(ref _terrainBestErrorMax, error);
            if (error <= MatchToleranceHours)
            {
                Interlocked.Increment(ref _terrainMatches);
            }
            bestError = Math.Min(bestError, error);
        }

        if (!double.IsPositiveInfinity(bestError))
        {
            Interlocked.Increment(ref _eitherBothHits);
            AddDouble(ref _bestErrorSum, bestError);
            SetMax(ref _bestErrorMax, bestError);
            if (bestError <= MatchToleranceHours)
            {
                Interlocked.Increment(ref _bestMatches);
            }
        }
    }

    private static void RecordNodeCount(int nodeCount)
    {
        if (nodeCount <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _nodeCountSamples);
        Interlocked.Add(ref _nodeCountSum, nodeCount);
        SetMax(ref _nodeCountMax, nodeCount);
    }

    private static void LogAndResetTravelLegStats(long aggregateId)
    {
        TravelLegStats[] snapshots;
        lock (TravelLegLock)
        {
            snapshots = new TravelLegStats[TravelLegStatsByKind.Length];
            for (var i = 0; i < TravelLegStatsByKind.Length; i++)
            {
                snapshots[i] = TravelLegStatsByKind[i].Clone();
                TravelLegStatsByKind[i].Reset();
            }
        }

        Interlocked.Exchange(ref _travelLegSamplesThisAggregate, 0);
        foreach (var stats in snapshots)
        {
            if (stats.Scheduled == 0 && stats.Recorded == 0 && stats.SetupFailures == 0)
            {
                continue;
            }

            Debug.Log(
                $"[T3MP] NeedTravelLegFlowFields aggregate={aggregateId}, kind={stats.Name}, sampleRate={BenchmarkSettings.NeedActionFlowFieldProbeSampleRate}, scheduled={stats.Scheduled}, recorded={stats.Recorded}, setupFailures={stats.SetupFailures}, startNodeValid={stats.StartNodeValid}, destinationNodeValid={stats.DestinationNodeValid}, uniqueStartNodes={stats.UniqueStartNodes}, cumulativeStartNodes={stats.CumulativeStartNodes}, uniqueDestinationNodes={stats.UniqueDestinationNodes}, cumulativeDestinationNodes={stats.CumulativeDestinationNodes}, uniqueStartDestinationPairs={stats.UniqueStartDestinationPairs}, cumulativeStartDestinationPairs={stats.CumulativeStartDestinationPairs}, startXzIntegerRate={stats.StartXzIntegerRate:F3}, startXzHalfOrIntegerRate={stats.StartXzHalfOrIntegerRate:F3}, startNodeOffsetAvg={stats.StartNodeOffsetAvg:F3}, startNodeOffsetMax={stats.StartNodeOffsetMax:F3}, roundedCurrent={stats.RoundedCurrent}, roadFromStart={stats.Flows[0]}, roadFromDestination={stats.Flows[1]}, terrainFromStart={stats.Flows[2]}, terrainFromDestination={stats.Flows[3]}");
        }
    }

    private static TravelLegKind ClassifyTravelLeg(Vector3 start, Vector3 destination)
    {
        if (!_durationContext.Active)
        {
            return TravelLegKind.Other;
        }

        if (SamePoint(start, _durationContext.CurrentPosition) && SamePoint(destination, _durationContext.ActionPosition))
        {
            return TravelLegKind.CurrentToAction;
        }

        if (SamePoint(start, _durationContext.ActionPosition) && SamePoint(destination, _durationContext.ReturnPosition))
        {
            return TravelLegKind.ActionToReturn;
        }

        return TravelLegKind.Other;
    }

    private static bool SamePoint(Vector3 left, Vector3 right)
    {
        return Mathf.Abs(left.x - right.x) <= 0.001f &&
            Mathf.Abs(left.y - right.y) <= 0.001f &&
            Mathf.Abs(left.z - right.z) <= 0.001f;
    }

    private static bool IsNearInteger(float value)
    {
        return Mathf.Abs(value - Mathf.Round(value)) <= 0.001f;
    }

    private static bool IsNearHalfOrInteger(float value)
    {
        var doubled = value * 2f;
        return Mathf.Abs(doubled - Mathf.Round(doubled)) <= 0.001f;
    }

    private static MethodInfo? FindTryGetFlowFieldMethod(Type? type)
    {
        return type?.GetMethod("TryGetFlowFieldAtNode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static MethodInfo? FindVectorPathMethod(Type? type, string name)
    {
        if (type is null)
        {
            return null;
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.Name != name)
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 4 &&
                parameters[0].ParameterType == typeof(Vector3) &&
                parameters[1].ParameterType == typeof(Vector3) &&
                parameters[2].ParameterType == typeof(float).MakeByRefType())
            {
                return method;
            }
        }

        return null;
    }

    private static PropertyInfo? FindProperty(Type? type, string name)
    {
        var current = type;
        while (current != null)
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property != null)
            {
                return property;
            }

            current = current.BaseType;
        }

        return null;
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

    private static void SetMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static void SetMax(ref double target, double value)
    {
        lock (ActionDoubleLock.Instance)
        {
            if (value > target)
            {
                target = value;
            }
        }
    }

    private static void AddDouble(ref double target, double value)
    {
        lock (ActionDoubleLock.Instance)
        {
            target += value;
        }
    }

    private static double ExchangeDouble(ref double target, double value)
    {
        lock (ActionDoubleLock.Instance)
        {
            var old = target;
            target = value;
            return old;
        }
    }

    private static void LogWarningOnce(Exception exception)
    {
        if (Interlocked.Exchange(ref _warningLogged, 1) == 0)
        {
            Debug.LogWarning($"[T3MP] NeedActionFlowFieldProbe failed once. {exception.GetType().Name}: {exception.Message}");
        }
    }

    public readonly struct DurationProbeState
    {
        public static readonly DurationProbeState Inactive = new DurationProbeState(false, null!, default, default, default);

        public DurationProbeState(
            bool active,
            object actionDurationCalculator,
            Vector3 currentPosition,
            Vector3 actionPosition,
            Vector3 returnPosition)
        {
            Active = active;
            ActionDurationCalculator = actionDurationCalculator;
            CurrentPosition = currentPosition;
            ActionPosition = actionPosition;
            ReturnPosition = returnPosition;
        }

        public bool Active { get; }
        public object ActionDurationCalculator { get; }
        public Vector3 CurrentPosition { get; }
        public Vector3 ActionPosition { get; }
        public Vector3 ReturnPosition { get; }
    }

    private readonly struct ProbeContext
    {
        public ProbeContext(
            object pathfindingService,
            object roadFlowFieldCache,
            object terrainFlowFieldCache,
            object nodeIdService,
            object walkerSpeedManager,
            object dayNightCycle)
        {
            PathfindingService = pathfindingService;
            RoadFlowFieldCache = roadFlowFieldCache;
            TerrainFlowFieldCache = terrainFlowFieldCache;
            NodeIdService = nodeIdService;
            WalkerSpeedManager = walkerSpeedManager;
            DayNightCycle = dayNightCycle;
        }

        private object PathfindingService { get; }
        public object RoadFlowFieldCache { get; }
        public object TerrainFlowFieldCache { get; }
        private object NodeIdService { get; }
        private object WalkerSpeedManager { get; }
        private object DayNightCycle { get; }
        public int NodeCount => NumberOfNodesProperty?.GetValue(NodeIdService) is int value ? value : 0;

        public int WorldToId(Vector3 position)
        {
            return WorldToIdMethod?.Invoke(NodeIdService, new object[] { position }) is int nodeId ? nodeId : -1;
        }

        public bool TryIdToWorld(int nodeId, out Vector3 position)
        {
            position = default;
            if (!IsValidNode(nodeId) ||
                IdToWorldMethod?.Invoke(NodeIdService, new object[] { nodeId }) is not Vector3 value)
            {
                return false;
            }

            position = value;
            return true;
        }

        public bool IsValidNode(int nodeId)
        {
            return nodeId >= 0 && nodeId < NodeCount;
        }

        public bool TryCalculateTravelTimeFromNodeCenter(
            int startNode,
            Vector3 destination,
            out float hours,
            out float distance,
            out bool pathFound)
        {
            hours = default;
            distance = default;
            pathFound = false;
            if (!TryIdToWorld(startNode, out var roundedStart))
            {
                return false;
            }

            if (FindPathUncachedMethod is not null)
            {
                var args = new object?[] { roundedStart, destination, 0f, null };
                if (FindPathUncachedMethod.Invoke(PathfindingService, args) is true &&
                    args[2] is float pathDistance)
                {
                    pathFound = true;
                    distance = pathDistance;
                    return TryDistanceToHours(distance, out hours);
                }
            }

            distance = Vector3.Distance(roundedStart, destination);
            return TryDistanceToHours(distance, out hours);
        }

        public bool TryFindUncachedPath(
            Vector3 start,
            Vector3 destination,
            out float distance,
            out List<PathCorner> pathCorners)
        {
            distance = default;
            pathCorners = new List<PathCorner>(64);
            if (FindPathUncachedMethod is null)
            {
                return false;
            }

            var args = new object?[] { start, destination, 0f, pathCorners };
            if (FindPathUncachedMethod.Invoke(PathfindingService, args) is true &&
                args[2] is float pathDistance)
            {
                distance = pathDistance;
                return true;
            }

            pathCorners.Clear();
            return false;
        }

        public bool TryGetHoursPerDistance(out float hoursPerDistance)
        {
            hoursPerDistance = default;
            return TryDistanceToHours(1f, out hoursPerDistance);
        }

        public bool TryHoursToDistance(float hours, out float distance)
        {
            distance = default;
            if (!TryGetHoursPerDistance(out var hoursPerDistance) ||
                hoursPerDistance <= 0f)
            {
                return false;
            }

            distance = hours / hoursPerDistance;
            return true;
        }

        public bool TryDistanceToHours(float distance, out float hours)
        {
            hours = default;
            if (GetWalkerBaseSpeedMethod?.Invoke(WalkerSpeedManager, Array.Empty<object>()) is not float baseSpeed ||
                baseSpeed <= 0 ||
                SecondsToHoursMethod?.Invoke(DayNightCycle, new object[] { distance / baseSpeed }) is not float converted)
            {
                return false;
            }

            hours = converted;
            return true;
        }
    }

    private readonly struct FlowProbeResult
    {
        public static readonly FlowProbeResult Missing = new FlowProbeResult(false, false, 0, false, false, float.NaN);

        public FlowProbeResult(
            bool cacheHit,
            bool isFilled,
            int nodeCount,
            bool currentHit,
            bool returnHit,
            float estimatedDuration)
        {
            CacheHit = cacheHit;
            IsFilled = isFilled;
            NodeCount = nodeCount;
            CurrentHit = currentHit;
            ReturnHit = returnHit;
            EstimatedDuration = estimatedDuration;
        }

        public bool CacheHit { get; }
        public bool IsFilled { get; }
        public int NodeCount { get; }
        public bool CurrentHit { get; }
        public bool ReturnHit { get; }
        public bool BothHit => CurrentHit && ReturnHit;
        public float EstimatedDuration { get; }
    }

    public readonly struct TravelLegProbeState
    {
        public static readonly TravelLegProbeState Inactive = new TravelLegProbeState(false, TravelLegKind.Other, null!, default, default);

        public TravelLegProbeState(
            bool active,
            TravelLegKind kind,
            object walker,
            Vector3 start,
            Vector3 destination)
        {
            Active = active;
            Kind = kind;
            Walker = walker;
            Start = start;
            Destination = destination;
        }

        public bool Active { get; }
        public TravelLegKind Kind { get; }
        public object Walker { get; }
        public Vector3 Start { get; }
        public Vector3 Destination { get; }
    }

    public enum TravelLegKind
    {
        CurrentToAction = 0,
        ActionToReturn = 1,
        Other = 2
    }

    private readonly struct DurationContext
    {
        public static readonly DurationContext Inactive = new DurationContext(false, default, default, default);

        public DurationContext(bool active, Vector3 currentPosition, Vector3 actionPosition, Vector3 returnPosition)
        {
            Active = active;
            CurrentPosition = currentPosition;
            ActionPosition = actionPosition;
            ReturnPosition = returnPosition;
        }

        public bool Active { get; }
        public Vector3 CurrentPosition { get; }
        public Vector3 ActionPosition { get; }
        public Vector3 ReturnPosition { get; }
    }

    private readonly struct FlowLegProbeResult
    {
        public static readonly FlowLegProbeResult Missing = new FlowLegProbeResult(false, false, 0, false, float.NaN);

        public FlowLegProbeResult(bool cacheHit, bool isFilled, int nodeCount, bool targetHit, float estimatedHours)
        {
            CacheHit = cacheHit;
            IsFilled = isFilled;
            NodeCount = nodeCount;
            TargetHit = targetHit;
            EstimatedHours = estimatedHours;
        }

        public bool CacheHit { get; }
        public bool IsFilled { get; }
        public int NodeCount { get; }
        public bool TargetHit { get; }
        public float EstimatedHours { get; }
    }

    private sealed class TravelLegStats
    {
        public TravelLegStats(string name)
        {
            Name = name;
            Flows = new[]
            {
                new TravelLegFlowStats("roadStart"),
                new TravelLegFlowStats("roadDest"),
                new TravelLegFlowStats("terrainStart"),
                new TravelLegFlowStats("terrainDest")
            };
        }

        public string Name { get; }
        public TravelLegFlowStats[] Flows { get; }
        public RoundedCurrentStats RoundedCurrent { get; private set; } = new RoundedCurrentStats();
        public long Scheduled;
        public long Recorded;
        public long SetupFailures;
        public long StartNodeValid;
        public long DestinationNodeValid;
        public long StartXzInteger;
        public long StartXzHalfOrInteger;
        public long StartNodeOffsetSamples;
        public double StartNodeOffsetSum;
        public double StartNodeOffsetMax;
        private readonly HashSet<int> StartNodes = new HashSet<int>();
        private readonly HashSet<int> DestinationNodes = new HashSet<int>();
        private readonly HashSet<NodePairKey> StartDestinationPairs = new HashSet<NodePairKey>();
        private readonly HashSet<int> CumulativeStartNodeSet = new HashSet<int>();
        private readonly HashSet<int> CumulativeDestinationNodeSet = new HashSet<int>();
        private readonly HashSet<NodePairKey> CumulativeStartDestinationPairSet = new HashSet<NodePairKey>();
        private int UniqueStartNodesSnapshot = -1;
        private int UniqueDestinationNodesSnapshot = -1;
        private int UniqueStartDestinationPairsSnapshot = -1;
        private int CumulativeStartNodesSnapshot = -1;
        private int CumulativeDestinationNodesSnapshot = -1;
        private int CumulativeStartDestinationPairsSnapshot = -1;

        public double StartXzIntegerRate => Recorded > 0 ? (double)StartXzInteger / Recorded : 0;
        public double StartXzHalfOrIntegerRate => Recorded > 0 ? (double)StartXzHalfOrInteger / Recorded : 0;
        public double StartNodeOffsetAvg => StartNodeOffsetSamples > 0 ? StartNodeOffsetSum / StartNodeOffsetSamples : 0;
        public int UniqueStartNodes => UniqueStartNodesSnapshot >= 0 ? UniqueStartNodesSnapshot : StartNodes.Count;
        public int UniqueDestinationNodes => UniqueDestinationNodesSnapshot >= 0 ? UniqueDestinationNodesSnapshot : DestinationNodes.Count;
        public int UniqueStartDestinationPairs => UniqueStartDestinationPairsSnapshot >= 0 ? UniqueStartDestinationPairsSnapshot : StartDestinationPairs.Count;
        public int CumulativeStartNodes => CumulativeStartNodesSnapshot >= 0 ? CumulativeStartNodesSnapshot : CumulativeStartNodeSet.Count;
        public int CumulativeDestinationNodes => CumulativeDestinationNodesSnapshot >= 0 ? CumulativeDestinationNodesSnapshot : CumulativeDestinationNodeSet.Count;
        public int CumulativeStartDestinationPairs => CumulativeStartDestinationPairsSnapshot >= 0 ? CumulativeStartDestinationPairsSnapshot : CumulativeStartDestinationPairSet.Count;

        public void RecordPosition(Vector3 start, int startNode, int destinationNode, bool startValid, bool destinationValid, ProbeContext context)
        {
            if (startValid)
            {
                StartNodeValid++;
                StartNodes.Add(startNode);
                CumulativeStartNodeSet.Add(startNode);
                if (context.TryIdToWorld(startNode, out var nodeWorld))
                {
                    var dx = start.x - nodeWorld.x;
                    var dz = start.z - nodeWorld.z;
                    var offset = Math.Sqrt(dx * dx + dz * dz);
                    StartNodeOffsetSamples++;
                    StartNodeOffsetSum += offset;
                    if (offset > StartNodeOffsetMax)
                    {
                        StartNodeOffsetMax = offset;
                    }
                }
            }

            if (destinationValid)
            {
                DestinationNodeValid++;
                DestinationNodes.Add(destinationNode);
                CumulativeDestinationNodeSet.Add(destinationNode);
            }

            if (startValid && destinationValid)
            {
                var pair = new NodePairKey(startNode, destinationNode);
                StartDestinationPairs.Add(pair);
                CumulativeStartDestinationPairSet.Add(pair);
            }

            if (IsNearInteger(start.x) && IsNearInteger(start.z))
            {
                StartXzInteger++;
            }

            if (IsNearHalfOrInteger(start.x) && IsNearHalfOrInteger(start.z))
            {
                StartXzHalfOrInteger++;
            }
        }

        public void RecordFlow(int index, FlowLegProbeResult result, float actualHours)
        {
            Flows[index].Record(result, actualHours);
        }

        public void RecordRoundedCurrentDistance(ProbeContext context, Vector3 start, Vector3 destination, float actualHours)
        {
            var startNode = context.WorldToId(start);
            if (!context.IsValidNode(startNode))
            {
                RoundedCurrent.RecordSetupFailure();
                return;
            }

            if (!context.TryCalculateTravelTimeFromNodeCenter(startNode, destination, out var roundedHours, out var roundedDistance, out var pathFound) ||
                !context.TryHoursToDistance(actualHours, out var actualDistance) ||
                !context.TryGetHoursPerDistance(out var hoursPerDistance))
            {
                RoundedCurrent.RecordSetupFailure();
                return;
            }

            RoundedCurrent.Record(roundedHours, actualHours, roundedDistance, actualDistance, hoursPerDistance, pathFound);
            if (!context.TryIdToWorld(startNode, out var roundedStart))
            {
                RoundedCurrent.RecordPathShapeSetupFailure();
                return;
            }

            var originalFound = context.TryFindUncachedPath(start, destination, out var originalDistance, out var originalCorners);
            var roundedFound = context.TryFindUncachedPath(roundedStart, destination, out var roundedDirectDistance, out var roundedCorners);
            RoundedCurrent.RecordPathShape(
                context,
                originalFound,
                originalDistance,
                originalCorners,
                roundedFound,
                roundedDirectDistance,
                roundedCorners,
                actualDistance,
                roundedDistance);
        }

        public TravelLegStats Clone()
        {
            var clone = new TravelLegStats(Name)
            {
                Scheduled = Scheduled,
                Recorded = Recorded,
                SetupFailures = SetupFailures,
                StartNodeValid = StartNodeValid,
                DestinationNodeValid = DestinationNodeValid,
                StartXzInteger = StartXzInteger,
                StartXzHalfOrInteger = StartXzHalfOrInteger,
                StartNodeOffsetSamples = StartNodeOffsetSamples,
                StartNodeOffsetSum = StartNodeOffsetSum,
                StartNodeOffsetMax = StartNodeOffsetMax,
                RoundedCurrent = RoundedCurrent.Clone(),
                UniqueStartNodesSnapshot = StartNodes.Count,
                UniqueDestinationNodesSnapshot = DestinationNodes.Count,
                UniqueStartDestinationPairsSnapshot = StartDestinationPairs.Count,
                CumulativeStartNodesSnapshot = CumulativeStartNodeSet.Count,
                CumulativeDestinationNodesSnapshot = CumulativeDestinationNodeSet.Count,
                CumulativeStartDestinationPairsSnapshot = CumulativeStartDestinationPairSet.Count
            };

            for (var i = 0; i < Flows.Length; i++)
            {
                clone.Flows[i] = Flows[i].Clone();
            }

            return clone;
        }

        public void Reset()
        {
            Scheduled = 0;
            Recorded = 0;
            SetupFailures = 0;
            StartNodeValid = 0;
            DestinationNodeValid = 0;
            StartXzInteger = 0;
            StartXzHalfOrInteger = 0;
            StartNodeOffsetSamples = 0;
            StartNodeOffsetSum = 0;
            StartNodeOffsetMax = 0;
            StartNodes.Clear();
            DestinationNodes.Clear();
            StartDestinationPairs.Clear();
            RoundedCurrent.Reset();
            foreach (var flow in Flows)
            {
                flow.Reset();
            }
        }
    }

    private sealed class TravelLegFlowStats
    {
        private readonly string _name;

        public TravelLegFlowStats(string name)
        {
            _name = name;
        }

        private long CacheHits;
        private long Filled;
        private long TargetHits;
        private long Matches;
        private long NodeCountSum;
        private int NodeCountMax;
        private double ErrorSum;
        private double ErrorMax;

        public void Record(FlowLegProbeResult result, float actualHours)
        {
            if (!result.CacheHit)
            {
                return;
            }

            CacheHits++;
            if (result.IsFilled)
            {
                Filled++;
            }

            NodeCountSum += result.NodeCount;
            if (result.NodeCount > NodeCountMax)
            {
                NodeCountMax = result.NodeCount;
            }

            if (!result.TargetHit || float.IsNaN(result.EstimatedHours))
            {
                return;
            }

            TargetHits++;
            var error = Math.Abs(result.EstimatedHours - actualHours);
            ErrorSum += error;
            if (error > ErrorMax)
            {
                ErrorMax = error;
            }

            if (error <= MatchToleranceHours)
            {
                Matches++;
            }
        }

        public TravelLegFlowStats Clone()
        {
            return new TravelLegFlowStats(_name)
            {
                CacheHits = CacheHits,
                Filled = Filled,
                TargetHits = TargetHits,
                Matches = Matches,
                NodeCountSum = NodeCountSum,
                NodeCountMax = NodeCountMax,
                ErrorSum = ErrorSum,
                ErrorMax = ErrorMax
            };
        }

        public void Reset()
        {
            CacheHits = 0;
            Filled = 0;
            TargetHits = 0;
            Matches = 0;
            NodeCountSum = 0;
            NodeCountMax = 0;
            ErrorSum = 0;
            ErrorMax = 0;
        }

        public override string ToString()
        {
            var nodeAvg = CacheHits > 0 ? (double)NodeCountSum / CacheHits : 0;
            var errorAvg = TargetHits > 0 ? ErrorSum / TargetHits : 0;
            return $"{_name}:cache={CacheHits},filled={Filled},target={TargetHits},matches={Matches},nodeAvg={nodeAvg:F0},nodeMax={NodeCountMax},errAvg={errorAvg:F4},errMax={ErrorMax:F4}";
        }
    }

    private sealed class RoundedCurrentStats
    {
        private long Samples;
        private long PathFound;
        private long HeuristicFallbacks;
        private long Matches;
        private long SetupFailures;
        private double ErrorSum;
        private double ErrorMax;
        private double DistanceErrorSum;
        private double DistanceErrorMax;
        private double HoursPerDistanceSum;
        private long HoursPerDistanceSamples;
        private long PathShapeSamples;
        private long PathShapeSetupFailures;
        private long OriginalPathFound;
        private long RoundedPathFound;
        private long BothPathsFound;
        private long SameFullPath;
        private long SameTail80;
        private long SameTail50;
        private double OriginalCornerCountSum;
        private double RoundedCornerCountSum;
        private double CornerCountDeltaAbsSum;
        private double CommonSuffixRatioSum;
        private double CommonSuffixRatioMin = double.PositiveInfinity;
        private long DirectDistanceSamples;
        private double DirectOriginalDistanceSum;
        private double DirectRoundedDistanceSum;
        private double DirectDistanceErrorSum;
        private double DirectDistanceErrorMax;
        private double ActualVsDirectOriginalDistanceErrorSum;
        private double ActualVsDirectOriginalDistanceErrorMax;
        private double RoundedCalcVsDirectDistanceErrorSum;
        private double RoundedCalcVsDirectDistanceErrorMax;

        public void Record(
            float roundedHours,
            float actualHours,
            float roundedDistance,
            float actualDistance,
            float hoursPerDistance,
            bool pathFound)
        {
            Samples++;
            if (pathFound)
            {
                PathFound++;
            }
            else
            {
                HeuristicFallbacks++;
            }

            var error = Math.Abs(roundedHours - actualHours);
            ErrorSum += error;
            if (error > ErrorMax)
            {
                ErrorMax = error;
            }

            var distanceError = Math.Abs(roundedDistance - actualDistance);
            DistanceErrorSum += distanceError;
            if (distanceError > DistanceErrorMax)
            {
                DistanceErrorMax = distanceError;
            }

            if (hoursPerDistance > 0f)
            {
                HoursPerDistanceSum += hoursPerDistance;
                HoursPerDistanceSamples++;
            }

            if (error <= MatchToleranceHours)
            {
                Matches++;
            }
        }

        public void RecordSetupFailure()
        {
            SetupFailures++;
        }

        public void RecordPathShapeSetupFailure()
        {
            PathShapeSetupFailures++;
        }

        public void RecordPathShape(
            ProbeContext context,
            bool originalFound,
            float originalDistance,
            List<PathCorner> originalCorners,
            bool roundedFound,
            float roundedDistance,
            List<PathCorner> roundedCorners,
            float actualDistance,
            float roundedCalculatedDistance)
        {
            PathShapeSamples++;
            if (originalFound)
            {
                OriginalPathFound++;
            }

            if (roundedFound)
            {
                RoundedPathFound++;
            }

            if (!originalFound || !roundedFound)
            {
                return;
            }

            BothPathsFound++;
            DirectDistanceSamples++;
            DirectOriginalDistanceSum += originalDistance;
            DirectRoundedDistanceSum += roundedDistance;
            var directDistanceError = Math.Abs(roundedDistance - originalDistance);
            DirectDistanceErrorSum += directDistanceError;
            if (directDistanceError > DirectDistanceErrorMax)
            {
                DirectDistanceErrorMax = directDistanceError;
            }

            var actualVsDirectError = Math.Abs(actualDistance - originalDistance);
            ActualVsDirectOriginalDistanceErrorSum += actualVsDirectError;
            if (actualVsDirectError > ActualVsDirectOriginalDistanceErrorMax)
            {
                ActualVsDirectOriginalDistanceErrorMax = actualVsDirectError;
            }

            var roundedCalcVsDirectError = Math.Abs(roundedCalculatedDistance - roundedDistance);
            RoundedCalcVsDirectDistanceErrorSum += roundedCalcVsDirectError;
            if (roundedCalcVsDirectError > RoundedCalcVsDirectDistanceErrorMax)
            {
                RoundedCalcVsDirectDistanceErrorMax = roundedCalcVsDirectError;
            }

            OriginalCornerCountSum += originalCorners.Count;
            RoundedCornerCountSum += roundedCorners.Count;
            CornerCountDeltaAbsSum += Math.Abs(originalCorners.Count - roundedCorners.Count);

            var sameFull = SameFullPathShape(context, originalCorners, roundedCorners);
            if (sameFull)
            {
                SameFullPath++;
            }

            var suffixRatio = CommonSuffixRatio(context, originalCorners, roundedCorners);
            CommonSuffixRatioSum += suffixRatio;
            if (suffixRatio < CommonSuffixRatioMin)
            {
                CommonSuffixRatioMin = suffixRatio;
            }

            if (suffixRatio >= 0.8)
            {
                SameTail80++;
            }

            if (suffixRatio >= 0.5)
            {
                SameTail50++;
            }
        }

        public RoundedCurrentStats Clone()
        {
            return new RoundedCurrentStats
            {
                Samples = Samples,
                PathFound = PathFound,
                HeuristicFallbacks = HeuristicFallbacks,
                Matches = Matches,
                SetupFailures = SetupFailures,
                ErrorSum = ErrorSum,
                ErrorMax = ErrorMax,
                DistanceErrorSum = DistanceErrorSum,
                DistanceErrorMax = DistanceErrorMax,
                HoursPerDistanceSum = HoursPerDistanceSum,
                HoursPerDistanceSamples = HoursPerDistanceSamples,
                PathShapeSamples = PathShapeSamples,
                PathShapeSetupFailures = PathShapeSetupFailures,
                OriginalPathFound = OriginalPathFound,
                RoundedPathFound = RoundedPathFound,
                BothPathsFound = BothPathsFound,
                SameFullPath = SameFullPath,
                SameTail80 = SameTail80,
                SameTail50 = SameTail50,
                OriginalCornerCountSum = OriginalCornerCountSum,
                RoundedCornerCountSum = RoundedCornerCountSum,
                CornerCountDeltaAbsSum = CornerCountDeltaAbsSum,
                CommonSuffixRatioSum = CommonSuffixRatioSum,
                CommonSuffixRatioMin = CommonSuffixRatioMin,
                DirectDistanceSamples = DirectDistanceSamples,
                DirectOriginalDistanceSum = DirectOriginalDistanceSum,
                DirectRoundedDistanceSum = DirectRoundedDistanceSum,
                DirectDistanceErrorSum = DirectDistanceErrorSum,
                DirectDistanceErrorMax = DirectDistanceErrorMax,
                ActualVsDirectOriginalDistanceErrorSum = ActualVsDirectOriginalDistanceErrorSum,
                ActualVsDirectOriginalDistanceErrorMax = ActualVsDirectOriginalDistanceErrorMax,
                RoundedCalcVsDirectDistanceErrorSum = RoundedCalcVsDirectDistanceErrorSum,
                RoundedCalcVsDirectDistanceErrorMax = RoundedCalcVsDirectDistanceErrorMax
            };
        }

        public void Reset()
        {
            Samples = 0;
            PathFound = 0;
            HeuristicFallbacks = 0;
            Matches = 0;
            SetupFailures = 0;
            ErrorSum = 0;
            ErrorMax = 0;
            DistanceErrorSum = 0;
            DistanceErrorMax = 0;
            HoursPerDistanceSum = 0;
            HoursPerDistanceSamples = 0;
            PathShapeSamples = 0;
            PathShapeSetupFailures = 0;
            OriginalPathFound = 0;
            RoundedPathFound = 0;
            BothPathsFound = 0;
            SameFullPath = 0;
            SameTail80 = 0;
            SameTail50 = 0;
            OriginalCornerCountSum = 0;
            RoundedCornerCountSum = 0;
            CornerCountDeltaAbsSum = 0;
            CommonSuffixRatioSum = 0;
            CommonSuffixRatioMin = double.PositiveInfinity;
            DirectDistanceSamples = 0;
            DirectOriginalDistanceSum = 0;
            DirectRoundedDistanceSum = 0;
            DirectDistanceErrorSum = 0;
            DirectDistanceErrorMax = 0;
            ActualVsDirectOriginalDistanceErrorSum = 0;
            ActualVsDirectOriginalDistanceErrorMax = 0;
            RoundedCalcVsDirectDistanceErrorSum = 0;
            RoundedCalcVsDirectDistanceErrorMax = 0;
        }

        public override string ToString()
        {
            var errorAvg = Samples > 0 ? ErrorSum / Samples : 0;
            var distanceErrorAvg = Samples > 0 ? DistanceErrorSum / Samples : 0;
            var hoursPerDistanceAvg = HoursPerDistanceSamples > 0 ? HoursPerDistanceSum / HoursPerDistanceSamples : 0;
            var originalCornersAvg = BothPathsFound > 0 ? OriginalCornerCountSum / BothPathsFound : 0;
            var roundedCornersAvg = BothPathsFound > 0 ? RoundedCornerCountSum / BothPathsFound : 0;
            var cornerDeltaAvg = BothPathsFound > 0 ? CornerCountDeltaAbsSum / BothPathsFound : 0;
            var suffixRatioAvg = BothPathsFound > 0 ? CommonSuffixRatioSum / BothPathsFound : 0;
            var suffixRatioMin = BothPathsFound > 0 ? CommonSuffixRatioMin : 0;
            var directOriginalAvg = DirectDistanceSamples > 0 ? DirectOriginalDistanceSum / DirectDistanceSamples : 0;
            var directRoundedAvg = DirectDistanceSamples > 0 ? DirectRoundedDistanceSum / DirectDistanceSamples : 0;
            var directErrorAvg = DirectDistanceSamples > 0 ? DirectDistanceErrorSum / DirectDistanceSamples : 0;
            var actualVsDirectAvg = DirectDistanceSamples > 0 ? ActualVsDirectOriginalDistanceErrorSum / DirectDistanceSamples : 0;
            var roundedCalcVsDirectAvg = DirectDistanceSamples > 0 ? RoundedCalcVsDirectDistanceErrorSum / DirectDistanceSamples : 0;
            return $"samples={Samples},pathFound={PathFound},heuristicFallbacks={HeuristicFallbacks},matches={Matches},setupFailures={SetupFailures},hoursPerDistanceAvg={hoursPerDistanceAvg:F5},errAvgHours={errorAvg:F4},errMaxHours={ErrorMax:F4},errAvgDistance={distanceErrorAvg:F2},errMaxDistance={DistanceErrorMax:F2},pathShapeSamples={PathShapeSamples},pathShapeSetupFailures={PathShapeSetupFailures},originalPathFound={OriginalPathFound},roundedPathFound={RoundedPathFound},bothPathsFound={BothPathsFound},sameFullPath={SameFullPath},sameTail80={SameTail80},sameTail50={SameTail50},originalCornersAvg={originalCornersAvg:F1},roundedCornersAvg={roundedCornersAvg:F1},cornerDeltaAvg={cornerDeltaAvg:F1},commonSuffixAvg={suffixRatioAvg:F3},commonSuffixMin={suffixRatioMin:F3},directOriginalAvgDistance={directOriginalAvg:F2},directRoundedAvgDistance={directRoundedAvg:F2},directErrAvgDistance={directErrorAvg:F2},directErrMaxDistance={DirectDistanceErrorMax:F2},actualVsDirectAvgDistance={actualVsDirectAvg:F2},actualVsDirectMaxDistance={ActualVsDirectOriginalDistanceErrorMax:F2},roundedCalcVsDirectAvgDistance={roundedCalcVsDirectAvg:F2},roundedCalcVsDirectMaxDistance={RoundedCalcVsDirectDistanceErrorMax:F2}";
        }

        private static bool SameFullPathShape(ProbeContext context, List<PathCorner> originalCorners, List<PathCorner> roundedCorners)
        {
            if (originalCorners.Count != roundedCorners.Count)
            {
                return false;
            }

            for (var i = 0; i < originalCorners.Count; i++)
            {
                if (!SameCorner(context, originalCorners[i], roundedCorners[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static double CommonSuffixRatio(ProbeContext context, List<PathCorner> originalCorners, List<PathCorner> roundedCorners)
        {
            var minCount = Math.Min(originalCorners.Count, roundedCorners.Count);
            if (minCount == 0)
            {
                return originalCorners.Count == roundedCorners.Count ? 1d : 0d;
            }

            var common = 0;
            while (common < minCount &&
                SameCorner(
                    context,
                    originalCorners[originalCorners.Count - 1 - common],
                    roundedCorners[roundedCorners.Count - 1 - common]))
            {
                common++;
            }

            return (double)common / minCount;
        }

        private static bool SameCorner(ProbeContext context, PathCorner left, PathCorner right)
        {
            return left.GroupId == right.GroupId &&
                Mathf.Abs(left.Speed - right.Speed) <= 0.001f &&
                context.WorldToId(left.Position) == context.WorldToId(right.Position);
        }
    }

    private readonly struct NodePairKey : IEquatable<NodePairKey>
    {
        public NodePairKey(int startNode, int destinationNode)
        {
            StartNode = startNode;
            DestinationNode = destinationNode;
        }

        private int StartNode { get; }
        private int DestinationNode { get; }

        public bool Equals(NodePairKey other)
        {
            return StartNode == other.StartNode && DestinationNode == other.DestinationNode;
        }

        public override bool Equals(object? obj)
        {
            return obj is NodePairKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (StartNode * 397) ^ DestinationNode;
            }
        }
    }

    private static class ActionDoubleLock
    {
        public static readonly object Instance = new object();
    }
}
