using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Effects;
using Timberborn.NeedBehaviorSystem;
using Timberborn.NeedSystem;
using Timberborn.TimeSystem;
using Timberborn.WalkingSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class DistrictNeedBehaviorDirectOptimizer
{
    [ThreadStatic]
    private static List<AppraisedGroup>? _appraisedGroups;

    [ThreadStatic]
    private static List<Candidate>? _candidates;

    private static FieldInfo? _needBehaviorsField;
    private static PropertyInfo? _needsProperty;
    private static PropertyInfo? _effectsProperty;
    private static PropertyInfo? _needBehaviorsProperty;
    private static GetNeedBehaviorsByKeyDelegate? _getNeedBehaviorsByKey;
    private static GetNeedsDelegate? _getNeeds;
    private static GetEffectsDelegate? _getEffects;
    private static GetNeedBehaviorsDelegate? _getNeedBehaviors;
    private static FieldInfo? _needManagerNeedsField;
    private static FieldInfo? _needManagerNullNeedField;
    private static FieldInfo? _walkerDayNightCycleField;
    private static FieldInfo? _walkerSpeedManagerField;
    private static int _reflectionInitialized;
    private static int _exceptionLogs;
    private static bool _beaverReflectionReady;
    private static bool _pruningDisabledByGuard;
    private static int _pruningGuardLogs;

    private static readonly ConditionalWeakTable<NeedManager, BeaverContext> BeaverContexts =
        new ConditionalWeakTable<NeedManager, BeaverContext>();

    private static long _calls;
    private static long _handled;
    private static long _fallbacks;
    private static long _groupsScanned;
    private static long _groupsPositive;
    private static long _actionsScanned;
    private static long _actionPositions;
    private static long _durationCalls;
    private static long _boundSkips;
    private static long _results;
    private static long _nullResults;

    public static bool TryPickBest(
        object instance,
        NeedManager needManager,
        Vector3 essentialActionPosition,
        float hoursLeftForNonEssentialActions,
        NeedFilter needFilter,
        ref AppraisedAction? result)
    {
        if (!BenchmarkSettings.EnableDistrictNeedBehaviorDirectOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _calls);
        }

        if (!TryInitializeReflection(instance.GetType()))
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            return true;
        }

        try
        {
            var stats = default(PickStats);
            result = PickBestAction(instance, needManager, essentialActionPosition, hoursLeftForNonEssentialActions, needFilter, ref stats);
            if (BenchmarkSettings.EnableDistrictNeedDirectDetailedStats)
            {
                RecordStats(stats);
            }
            if (recordMetrics)
            {
                Interlocked.Increment(ref _handled);
                if (result.HasValue)
                {
                    Interlocked.Increment(ref _results);
                }
                else
                {
                    Interlocked.Increment(ref _nullResults);
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            if (Interlocked.Increment(ref _exceptionLogs) <= 3)
            {
                Debug.LogWarning("[T3MP] DistrictNeed direct optimizer fallback: " + exception);
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

        var calls = Interlocked.Exchange(ref _calls, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var groupsScanned = Interlocked.Exchange(ref _groupsScanned, 0);
        var groupsPositive = Interlocked.Exchange(ref _groupsPositive, 0);
        var actionsScanned = Interlocked.Exchange(ref _actionsScanned, 0);
        var actionPositions = Interlocked.Exchange(ref _actionPositions, 0);
        var durationCalls = Interlocked.Exchange(ref _durationCalls, 0);
        var boundSkips = Interlocked.Exchange(ref _boundSkips, 0);
        var results = Interlocked.Exchange(ref _results, 0);
        var nullResults = Interlocked.Exchange(ref _nullResults, 0);
        if (calls == 0 && handled == 0 && fallbacks == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] DistrictNeedDirect aggregate={0}, calls={1}, handled={2}, fallbacks={3}, groupsScanned={4}, groupsPositive={5}, actionsScanned={6}, actionPositions={7}, durationCalls={8}, boundSkips={9}, pruningDisabled={10}, results={11}, nullResults={12}",
            aggregateId,
            calls,
            handled,
            fallbacks,
            groupsScanned,
            groupsPositive,
            actionsScanned,
            actionPositions,
            durationCalls,
            boundSkips,
            _pruningDisabledByGuard,
            results,
            nullResults));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _groupsScanned, 0);
        Interlocked.Exchange(ref _groupsPositive, 0);
        Interlocked.Exchange(ref _actionsScanned, 0);
        Interlocked.Exchange(ref _actionPositions, 0);
        Interlocked.Exchange(ref _durationCalls, 0);
        Interlocked.Exchange(ref _boundSkips, 0);
        Interlocked.Exchange(ref _results, 0);
        Interlocked.Exchange(ref _nullResults, 0);
    }

    private static AppraisedAction? PickBestAction(
        object instance,
        NeedManager needManager,
        Vector3 essentialActionPosition,
        float hoursLeftForNonEssentialActions,
        NeedFilter needFilter,
        ref PickStats stats)
    {
        var beaverContext = GetBeaverContext(needManager);
        var appraiser = beaverContext?.Appraiser ?? needManager.GetComponent<Appraiser>();
        var actionDurationCalculator = beaverContext?.DurationCalculator ?? needManager.GetComponent<ActionDurationCalculator>();
        var groups = _appraisedGroups ??= new List<AppraisedGroup>(16);
        groups.Clear();

        if (_getNeedBehaviorsByKey?.Invoke(instance) is not IDictionary needBehaviorsByKey)
        {
            return null;
        }

        var order = 0;
        var recordStats = BenchmarkSettings.EnableDistrictNeedDirectDetailedStats;
        foreach (var group in needBehaviorsByKey.Values)
        {
            if (recordStats)
            {
                stats.GroupsScanned++;
            }
            if (group is null)
            {
                continue;
            }

            if (_getEffects is null)
            {
                continue;
            }

            float points;
            var groupAppraisal = beaverContext is not null
                ? GetGroupAppraisal(beaverContext, group)
                : null;
            if (groupAppraisal is not null)
            {
                points = AppraiseEffectsCached(groupAppraisal, needFilter);
            }
            else
            {
                points = appraiser.AppraiseEffects(_getEffects(group), needFilter);
            }

            if (points > 0f)
            {
                if (recordStats)
                {
                    stats.GroupsPositive++;
                }
                groups.Add(new AppraisedGroup(group, points, order));
            }

            order++;
        }

        // Vanilla stores groups in a SortedSet whose comparer never returns 0;
        // items with equal points are placed left of existing equals, so the
        // in-order enumeration visits equal-point groups in REVERSE insertion
        // order. Replicate that exactly: points descending, then order
        // descending.
        groups.Sort(static (left, right) =>
        {
            var points = right.Points.CompareTo(left.Points);
            return points != 0 ? points : right.Order.CompareTo(left.Order);
        });

        for (var index = 0; index < groups.Count; index++)
        {
            var appraisedGroup = groups[index];
            var group = appraisedGroup.Group;
            if (_getNeeds is null || _getNeedBehaviors is null)
            {
                continue;
            }

            var needs = _getNeeds(group);
            var needBehaviors = _getNeedBehaviors(group);
            if (needBehaviors is null)
            {
                continue;
            }

            var usePruning = BenchmarkSettings.EnableDistrictNeedBoundPruning &&
                !_pruningDisabledByGuard &&
                beaverContext is not null &&
                beaverContext.CanComputeBounds;
            if (usePruning)
            {
                var status = TryPickShortestWithPruning(
                    beaverContext!,
                    needManager,
                    needBehaviors,
                    needs,
                    appraisedGroup.Points,
                    essentialActionPosition,
                    hoursLeftForNonEssentialActions,
                    needFilter,
                    actionDurationCalculator,
                    ref stats,
                    out var prunedAction);
                if (status == PruningStatus.GuardTripped)
                {
                    // The lower bound was violated; fall back to the exact
                    // vanilla-shaped scan for this and all future calls.
                    usePruning = false;
                }
                else if (status == PruningStatus.Found)
                {
                    groups.Clear();
                    return prunedAction;
                }
                else
                {
                    continue;
                }
            }

            if (!usePruning)
            {
                AppraisedAction? appraisedAction = null;
                var shortestDuration = float.MaxValue;
                for (var behaviorIndex = 0; behaviorIndex < needBehaviors.Count; behaviorIndex++)
                {
                    if (recordStats)
                    {
                        stats.ActionsScanned++;
                    }
                    var needBehavior = needBehaviors[behaviorIndex];
                    var actionPosition = needBehavior.ActionPosition(needManager);
                    if (!actionPosition.HasValue)
                    {
                        continue;
                    }

                    if (recordStats)
                    {
                        stats.ActionPositions++;
                    }
                    var duration = actionDurationCalculator.DurationWithReturnInHours(actionPosition.Value, essentialActionPosition);
                    if (recordStats)
                    {
                        stats.DurationCalls++;
                    }
                    if ((hoursLeftForNonEssentialActions > duration || needFilter.OnlyCriticalStateNeeds) &&
                        duration < shortestDuration)
                    {
                        appraisedAction = new AppraisedAction(needBehavior, needs, appraisedGroup.Points);
                        shortestDuration = duration;
                    }
                }

                if (appraisedAction.HasValue)
                {
                    groups.Clear();
                    return appraisedAction.Value;
                }
            }
        }

        groups.Clear();
        return null;
    }

    private enum PruningStatus
    {
        NotFound,
        Found,
        GuardTripped
    }

    private static PruningStatus TryPickShortestWithPruning(
        BeaverContext beaverContext,
        NeedManager needManager,
        List<NeedBehavior> needBehaviors,
        ImmutableArray<string> needs,
        float points,
        Vector3 essentialActionPosition,
        float hoursLeftForNonEssentialActions,
        NeedFilter needFilter,
        ActionDurationCalculator actionDurationCalculator,
        ref PickStats stats,
        out AppraisedAction? result)
    {
        result = null;
        var recordStats = BenchmarkSettings.EnableDistrictNeedDirectDetailedStats;
        var candidates = _candidates ??= new List<Candidate>(64);
        candidates.Clear();

        // Gather ActionPosition for every behavior in vanilla index order.
        // ActionPosition is called for ALL candidates, exactly like vanilla, so
        // implementations with side effects stay correct; only the duration
        // queries are pruned.
        for (var behaviorIndex = 0; behaviorIndex < needBehaviors.Count; behaviorIndex++)
        {
            if (recordStats)
            {
                stats.ActionsScanned++;
            }
            var needBehavior = needBehaviors[behaviorIndex];
            var actionPosition = needBehavior.ActionPosition(needManager);
            if (!actionPosition.HasValue)
            {
                continue;
            }

            if (recordStats)
            {
                stats.ActionPositions++;
            }
            candidates.Add(new Candidate(behaviorIndex, needBehavior, actionPosition.GetValueOrDefault()));
        }

        if (candidates.Count == 0)
        {
            return PruningStatus.NotFound;
        }

        // durationHours = pathDistance / walkerBaseSpeed converted to hours,
        // plus a 0.3h constant. The same walker speed divides every candidate,
        // so a lower bound on path distance gives a lower bound on duration.
        var hoursPerDistance = beaverContext.DayNightCycle!.SecondsToHours(1f) /
            beaverContext.SpeedManager!.GetWalkerBaseSpeed();
        if (!(hoursPerDistance > 0f) || float.IsInfinity(hoursPerDistance))
        {
            return RunPruningFallbackScan(
                candidates,
                needs,
                points,
                essentialActionPosition,
                hoursLeftForNonEssentialActions,
                needFilter,
                actionDurationCalculator,
                ref stats,
                out result);
        }

        var startPosition = beaverContext.CalculatorTransform!.position;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var bound = (LowerBoundNavDistance(startPosition, candidate.Position) +
                LowerBoundNavDistance(candidate.Position, essentialActionPosition)) * hoursPerDistance + 0.3f;
            candidates[i] = candidate.WithBound(bound);
        }

        candidates.Sort(static (left, right) =>
        {
            var bound = left.Bound.CompareTo(right.Bound);
            return bound != 0 ? bound : left.Index.CompareTo(right.Index);
        });

        NeedBehavior? bestBehavior = null;
        var bestDuration = float.MaxValue;
        var bestIndex = int.MaxValue;
        var onlyCritical = needFilter.OnlyCriticalStateNeeds;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.Bound > bestDuration)
            {
                if (recordStats)
                {
                    stats.BoundSkips += candidates.Count - i;
                }
                break;
            }

            var duration = actionDurationCalculator.DurationWithReturnInHours(candidate.Position, essentialActionPosition);
            if (recordStats)
            {
                stats.DurationCalls++;
            }

            if (duration < candidate.Bound - BenchmarkSettings.BoundPruningGuardEpsilonHours)
            {
                _pruningDisabledByGuard = true;
                if (Interlocked.Increment(ref _pruningGuardLogs) <= 3)
                {
                    var dx1 = startPosition.x - candidate.Position.x;
                    var dz1 = startPosition.z - candidate.Position.z;
                    var dx2 = candidate.Position.x - essentialActionPosition.x;
                    var dz2 = candidate.Position.z - essentialActionPosition.z;
                    var euclid1 = Mathf.Sqrt(dx1 * dx1 + dz1 * dz1);
                    var euclid2 = Mathf.Sqrt(dx2 * dx2 + dz2 * dz2);
                    // Implied total distance the measured hours correspond to,
                    // using the same hours-per-distance factor as the bound.
                    var impliedDistance = hoursPerDistance > 0f ? (duration - 0.3f) / hoursPerDistance : -1f;
                    Debug.LogWarning(string.Format(
                        CultureInfo.InvariantCulture,
                        "[T3MP] DistrictNeed bound pruning disabled: measured={0:F4}h bound={1:F4}h start=({2:F1},{3:F1},{4:F1}) action=({5:F1},{6:F1},{7:F1}) essential=({8:F1},{9:F1},{10:F1}) euclid1={11:F2} euclid2={12:F2} impliedDistance={13:F2} hoursPerDistance={14:F6}",
                        duration,
                        candidate.Bound,
                        startPosition.x, startPosition.y, startPosition.z,
                        candidate.Position.x, candidate.Position.y, candidate.Position.z,
                        essentialActionPosition.x, essentialActionPosition.y, essentialActionPosition.z,
                        euclid1,
                        euclid2,
                        impliedDistance,
                        hoursPerDistance));
                }

                return PruningStatus.GuardTripped;
            }

            if ((hoursLeftForNonEssentialActions > duration || onlyCritical) &&
                (duration < bestDuration || (duration == bestDuration && candidate.Index < bestIndex)))
            {
                bestDuration = duration;
                bestIndex = candidate.Index;
                bestBehavior = candidate.Behavior;
            }
        }

        if (bestBehavior is null)
        {
            return PruningStatus.NotFound;
        }

        result = new AppraisedAction(bestBehavior, needs, points);
        return PruningStatus.Found;
    }

    private static PruningStatus RunPruningFallbackScan(
        List<Candidate> candidates,
        ImmutableArray<string> needs,
        float points,
        Vector3 essentialActionPosition,
        float hoursLeftForNonEssentialActions,
        NeedFilter needFilter,
        ActionDurationCalculator actionDurationCalculator,
        ref PickStats stats,
        out AppraisedAction? result)
    {
        // Evaluate every gathered candidate in vanilla index order (candidates
        // are gathered in index order and not yet sorted here).
        result = null;
        var recordStats = BenchmarkSettings.EnableDistrictNeedDirectDetailedStats;
        NeedBehavior? bestBehavior = null;
        var bestDuration = float.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var duration = actionDurationCalculator.DurationWithReturnInHours(candidate.Position, essentialActionPosition);
            if (recordStats)
            {
                stats.DurationCalls++;
            }
            if ((hoursLeftForNonEssentialActions > duration || needFilter.OnlyCriticalStateNeeds) &&
                duration < bestDuration)
            {
                bestBehavior = candidate.Behavior;
                bestDuration = duration;
            }
        }

        if (bestBehavior is null)
        {
            return PruningStatus.NotFound;
        }

        result = new AppraisedAction(bestBehavior, needs, points);
        return PruningStatus.Found;
    }

    private static float LowerBoundNavDistance(Vector3 a, Vector3 b)
    {
        var dx = a.x - b.x;
        var dz = a.z - b.z;
        var euclidean = Mathf.Sqrt(dx * dx + dz * dz);
        // Slack is in raw distance units (node snap + cache key quantization
        // aliasing) and must be removed before the minimum edge-cost factor.
        var slackedDistance = euclidean - BenchmarkSettings.NavDistanceLowerBoundSlack;
        if (slackedDistance <= 0f)
        {
            return 0f;
        }

        return BenchmarkSettings.NavDistanceLowerBoundFactor * slackedDistance;
    }

    private static float AppraiseEffectsCached(GroupAppraisal groupAppraisal, NeedFilter needFilter)
    {
        // Exact clone of Appraiser.AppraiseEffects(ImmutableArray<InstantEffect>,
        // NeedFilter) with the needId -> Need dictionary lookups and
        // Effect.From conversions resolved once per (beaver, group).
        var needsArray = groupAppraisal.Needs;
        var effects = groupAppraisal.Effects;
        var needIds = groupAppraisal.NeedIds;
        var total = 0f;
        var anyFiltered = false;
        for (var i = 0; i < effects.Length; i++)
        {
            if (!needsArray[i].TryAppraise(effects[i], out var points))
            {
                return 0f;
            }

            if (needFilter.Filter(needIds[i]) && points > 0f)
            {
                anyFiltered = true;
            }

            total += points;
        }

        if (!anyFiltered)
        {
            return 0f;
        }

        return total;
    }

    private static BeaverContext? GetBeaverContext(NeedManager needManager)
    {
        if (!BenchmarkSettings.EnableDistrictNeedAppraisalCache || !_beaverReflectionReady)
        {
            return null;
        }

        if (BeaverContexts.TryGetValue(needManager, out var context))
        {
            return context.Valid ? context : null;
        }

        context = CreateBeaverContext(needManager);
        BeaverContexts.Add(needManager, context);
        return context.Valid ? context : null;
    }

    private static BeaverContext CreateBeaverContext(NeedManager needManager)
    {
        try
        {
            var appraiser = needManager.GetComponent<Appraiser>();
            var durationCalculator = needManager.GetComponent<ActionDurationCalculator>();
            if (appraiser is null || durationCalculator is null)
            {
                return BeaverContext.Invalid;
            }

            var needs = _needManagerNeedsField?.GetValue(needManager) as Needs;
            var nullNeed = _needManagerNullNeedField?.GetValue(needManager) as Need;
            if (needs is null || nullNeed is null)
            {
                return BeaverContext.Invalid;
            }

            var context = new BeaverContext(appraiser, durationCalculator, needs, nullNeed);
            var walker = needManager.GetComponent<Walker>();
            if (walker is not null &&
                _walkerDayNightCycleField?.GetValue(walker) is IDayNightCycle dayNightCycle &&
                _walkerSpeedManagerField?.GetValue(walker) is WalkerSpeedManager speedManager)
            {
                context.DayNightCycle = dayNightCycle;
                context.SpeedManager = speedManager;
                context.CalculatorTransform = durationCalculator.Transform;
            }

            return context;
        }
        catch (Exception)
        {
            return BeaverContext.Invalid;
        }
    }

    private static GroupAppraisal? GetGroupAppraisal(BeaverContext beaverContext, object group)
    {
        if (beaverContext.GroupAppraisals.TryGetValue(group, out var appraisal))
        {
            return appraisal;
        }

        appraisal = CreateGroupAppraisal(beaverContext, group);
        beaverContext.GroupAppraisals[group] = appraisal;
        return appraisal;
    }

    private static GroupAppraisal? CreateGroupAppraisal(BeaverContext beaverContext, object group)
    {
        try
        {
            if (_getEffects is null)
            {
                return null;
            }

            var instantEffects = _getEffects(group);
            var needsArray = new Need[instantEffects.Length];
            var effects = new Effect[instantEffects.Length];
            var needIds = new string[instantEffects.Length];
            for (var i = 0; i < instantEffects.Length; i++)
            {
                var instantEffect = instantEffects[i];
                needIds[i] = instantEffect.NeedId;
                effects[i] = Effect.From(instantEffect);
                // Vanilla NeedManager.GetNeed(needId) falls back to _nullNeed
                // for needs this character does not have; keep that behavior.
                needsArray[i] = beaverContext.NeedsCollection.TryGetNeed(instantEffect.NeedId, out var need)
                    ? need
                    : beaverContext.NullNeed;
            }

            return new GroupAppraisal(needsArray, effects, needIds);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryInitializeReflection(Type districtNeedBehaviorServiceType)
    {
        if (Volatile.Read(ref _reflectionInitialized) == 1)
        {
            return _needBehaviorsField is not null &&
                _needsProperty is not null &&
                _effectsProperty is not null &&
                _needBehaviorsProperty is not null;
        }

        _needBehaviorsField = districtNeedBehaviorServiceType.GetField("_needBehaviors", BindingFlags.Instance | BindingFlags.NonPublic);
        var groupType = _needBehaviorsField?.FieldType.GetGenericArguments()[1];
        if (groupType is not null)
        {
            _needsProperty = groupType.GetProperty("Needs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _effectsProperty = groupType.GetProperty("Effects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _needBehaviorsProperty = groupType.GetProperty("NeedBehaviors", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _getNeedBehaviorsByKey = CreateFieldGetter<GetNeedBehaviorsByKeyDelegate>(_needBehaviorsField!, typeof(IDictionary), districtNeedBehaviorServiceType);
            _getNeeds = CreatePropertyGetter<GetNeedsDelegate>(_needsProperty, typeof(ImmutableArray<string>), groupType);
            _getEffects = CreatePropertyGetter<GetEffectsDelegate>(_effectsProperty, typeof(ImmutableArray<InstantEffect>), groupType);
            _getNeedBehaviors = CreatePropertyGetter<GetNeedBehaviorsDelegate>(_needBehaviorsProperty, typeof(List<NeedBehavior>), groupType);
        }

        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _needManagerNeedsField = typeof(NeedManager).GetField("_needs", instanceFlags);
        _needManagerNullNeedField = typeof(NeedManager).GetField("_nullNeed", instanceFlags);
        _walkerDayNightCycleField = typeof(Walker).GetField("_dayNightCycle", instanceFlags);
        _walkerSpeedManagerField = typeof(Walker).GetField("_walkerSpeedManager", instanceFlags);
        _beaverReflectionReady = _needManagerNeedsField is not null && _needManagerNullNeedField is not null;
        if (!_beaverReflectionReady)
        {
            Debug.LogWarning("[T3MP] DistrictNeed appraisal cache fields were not found; cache disabled.");
        }

        Volatile.Write(ref _reflectionInitialized, 1);
        if (_needBehaviorsField is null ||
            _needsProperty is null ||
            _effectsProperty is null ||
            _needBehaviorsProperty is null ||
            _getNeedBehaviorsByKey is null ||
            _getNeeds is null ||
            _getEffects is null ||
            _getNeedBehaviors is null)
        {
            Debug.LogWarning("[T3MP] DistrictNeed direct optimizer could not find expected fields.");
            return false;
        }

        return true;
    }

    private static void RecordStats(PickStats stats)
    {
        Interlocked.Add(ref _groupsScanned, stats.GroupsScanned);
        Interlocked.Add(ref _groupsPositive, stats.GroupsPositive);
        Interlocked.Add(ref _actionsScanned, stats.ActionsScanned);
        Interlocked.Add(ref _actionPositions, stats.ActionPositions);
        Interlocked.Add(ref _durationCalls, stats.DurationCalls);
        Interlocked.Add(ref _boundSkips, stats.BoundSkips);
    }

    private static TDelegate? CreateFieldGetter<TDelegate>(FieldInfo field, Type returnType, Type declaringType)
        where TDelegate : Delegate
    {
        try
        {
            var method = new DynamicMethod(
                string.Concat("T3MP_Get_", field.Name),
                returnType,
                new[] { typeof(object) },
                typeof(DistrictNeedBehaviorDirectOptimizer).Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType != returnType)
            {
                il.Emit(OpCodes.Castclass, returnType);
            }
            il.Emit(OpCodes.Ret);
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to create DistrictNeed field getter: " + exception.Message);
            return null;
        }
    }

    private static TDelegate? CreatePropertyGetter<TDelegate>(PropertyInfo? property, Type returnType, Type declaringType)
        where TDelegate : Delegate
    {
        var getter = property?.GetGetMethod(nonPublic: true);
        if (getter is null)
        {
            return null;
        }

        try
        {
            var method = new DynamicMethod(
                string.Concat("T3MP_Get_", property!.Name),
                returnType,
                new[] { typeof(object) },
                typeof(DistrictNeedBehaviorDirectOptimizer).Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, declaringType);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to create DistrictNeed property getter: " + exception.Message);
            return null;
        }
    }

    private delegate IDictionary GetNeedBehaviorsByKeyDelegate(object instance);

    private delegate ImmutableArray<string> GetNeedsDelegate(object group);

    private delegate ImmutableArray<InstantEffect> GetEffectsDelegate(object group);

    private delegate List<NeedBehavior> GetNeedBehaviorsDelegate(object group);

    private struct PickStats
    {
        public long GroupsScanned;
        public long GroupsPositive;
        public long ActionsScanned;
        public long ActionPositions;
        public long DurationCalls;
        public long BoundSkips;
    }

    private readonly struct AppraisedGroup
    {
        public AppraisedGroup(object group, float points, int order)
        {
            Group = group;
            Points = points;
            Order = order;
        }

        public object Group { get; }
        public float Points { get; }
        public int Order { get; }
    }

    private readonly struct Candidate
    {
        public Candidate(int index, NeedBehavior behavior, Vector3 position)
        {
            Index = index;
            Behavior = behavior;
            Position = position;
            Bound = 0f;
        }

        private Candidate(int index, NeedBehavior behavior, Vector3 position, float bound)
        {
            Index = index;
            Behavior = behavior;
            Position = position;
            Bound = bound;
        }

        public int Index { get; }
        public NeedBehavior Behavior { get; }
        public Vector3 Position { get; }
        public float Bound { get; }

        public Candidate WithBound(float bound)
        {
            return new Candidate(Index, Behavior, Position, bound);
        }
    }

    private sealed class BeaverContext
    {
        public static readonly BeaverContext Invalid = new BeaverContext();

        public readonly bool Valid;
        public readonly Appraiser Appraiser;
        public readonly ActionDurationCalculator DurationCalculator;
        public readonly Needs NeedsCollection;
        public readonly Need NullNeed;
        public readonly Dictionary<object, GroupAppraisal?> GroupAppraisals = new Dictionary<object, GroupAppraisal?>();
        public IDayNightCycle? DayNightCycle;
        public WalkerSpeedManager? SpeedManager;
        public Transform? CalculatorTransform;

        public bool CanComputeBounds => DayNightCycle is not null &&
            SpeedManager is not null &&
            CalculatorTransform != null;

        private BeaverContext()
        {
            Valid = false;
            Appraiser = null!;
            DurationCalculator = null!;
            NeedsCollection = null!;
            NullNeed = null!;
        }

        public BeaverContext(Appraiser appraiser, ActionDurationCalculator durationCalculator, Needs needs, Need nullNeed)
        {
            Valid = true;
            Appraiser = appraiser;
            DurationCalculator = durationCalculator;
            NeedsCollection = needs;
            NullNeed = nullNeed;
        }
    }

    private sealed class GroupAppraisal
    {
        public readonly Need[] Needs;
        public readonly Effect[] Effects;
        public readonly string[] NeedIds;

        public GroupAppraisal(Need[] needs, Effect[] effects, string[] needIds)
        {
            Needs = needs;
            Effects = effects;
            NeedIds = needIds;
        }
    }
}
