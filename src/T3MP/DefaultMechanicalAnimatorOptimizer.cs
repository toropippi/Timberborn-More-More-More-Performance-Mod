using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class DefaultMechanicalAnimatorOptimizer
{
    private static readonly object LockObject = new object();
    private static readonly Dictionary<int, AnimatorInfo> AnimatorInfoById = new Dictionary<int, AnimatorInfo>();

    private static FieldInfo? _animationUpdatersField;
    private static FieldInfo? _registryAnimatorsField;
    private static FieldInfo? _nodeCurrentAnimationField;
    private static FieldInfo? _vertexCurrentAnimationField;
    private static FieldInfo? _vertexAnimationsMapField;
    private static PropertyInfo? _animationNameProperty;
    private static PropertyInfo? _nodeNameProperty;
    private static PropertyInfo? _nodeFrameCountProperty;
    private static PropertyInfo? _nodeHasDifferentPositionsProperty;
    private static PropertyInfo? _nodeHasDifferentRotationsProperty;
    private static PropertyInfo? _nodeHasDifferentScalesProperty;
    private static PropertyInfo? _vertexNameProperty;
    private static PropertyInfo? _vertexFrameCountProperty;
    private static PropertyInfo? _vertexAnimatedVertexCountProperty;
    private static Action<object, float>? _updateAnimation;
    private static MethodInfo? _updateAnimationMethod;
    private static bool _registryMembersInitialized;
    private static int _warningCount;

    private static object? _cachedRegistry;
    private static int _cachedRegistryCount = -1;
    private static int _cachedRegistryFrame = -1;
    private static int _cachedRegistryNonMechanicalDefaultCount;
    private static int _cachedRegistryNonDefaultCount;
    private static readonly List<AnimatorEntry> MechanicalDefaultEntries = new List<AnimatorEntry>(8192);
    private static readonly List<AnimatorEntry> NonTargetEntries = new List<AnimatorEntry>(2048);

    private static long _registryCalls;
    private static long _registryHandled;
    private static long _registryFallbacks;
    private static long _registryRebuilds;
    private static long _registryRebuildStopwatchTicks;
    private static long _closedDelegateFailures;
    private static long _registryVisitedAnimators;
    private static long _registryNonTargetRuns;
    private static long _registryStopwatchTicks;
    private static long _registryMaxStopwatchTicks;
    private static long _calls;
    private static long _optimizedCalls;
    private static long _mechanicalDefaultCalls;
    private static long _mechanicalDefaultRuns;
    private static long _mechanicalDefaultSkips;
    private static long _mechanicalDefaultUnknowns;
    private static long _nonMechanicalDefaultCalls;
    private static long _nonDefaultCalls;
    private static long _newClassifications;
    private static long _accumulatedDeltaApplications;
    private static long _stopwatchTicks;
    private static long _maxStopwatchTicks;

    public static bool TryUpdateRegistry(object registry)
    {
        if (!BenchmarkSettings.EnableDefaultMechanicalAnimatorRegistryReplacement ||
            !BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle ||
            BenchmarkSettings.DefaultMechanicalAnimatorThrottleFrames <= 1 ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _registryCalls);
        var startTimestamp = Stopwatch.GetTimestamp();
        var localVisited = 0L;
        var localCalls = 0L;
        var localOptimizedCalls = 0L;
        var localMechanicalDefaultCalls = 0L;
        var localMechanicalDefaultRuns = 0L;
        var localMechanicalDefaultSkips = 0L;
        var localMechanicalDefaultUnknowns = 0L;
        var localNonMechanicalDefaultCalls = 0L;
        var localNonDefaultCalls = 0L;
        var localAccumulatedDeltaApplications = 0L;
        var localNonTargetRuns = 0L;
        var localRunTicks = 0L;
        var localMaxRunTicks = 0L;

        try
        {
            if (!EnsureRegistryMembers(registry.GetType()) ||
                _registryAnimatorsField?.GetValue(registry) is not IEnumerable animators ||
                _updateAnimation is null)
            {
                Interlocked.Increment(ref _registryFallbacks);
                return false;
            }

            EnsureRegistryCache(registry, animators);

            var deltaTime = Time.deltaTime;
            localVisited = MechanicalDefaultEntries.Count + NonTargetEntries.Count;
            localCalls = localVisited;
            localOptimizedCalls = localVisited;
            localMechanicalDefaultCalls = MechanicalDefaultEntries.Count;
            localNonMechanicalDefaultCalls = _cachedRegistryNonMechanicalDefaultCount;
            localNonDefaultCalls = _cachedRegistryNonDefaultCount;
            localNonTargetRuns = NonTargetEntries.Count;

            var runStartTimestamp = Stopwatch.GetTimestamp();
            foreach (var entry in NonTargetEntries)
            {
                entry.Update(deltaTime);
            }

            foreach (var entry in MechanicalDefaultEntries)
            {
                var info = entry.Info;
                if (!info.HasKnownAnimationShape)
                {
                    localMechanicalDefaultUnknowns++;
                }

                if (!ShouldRun(info.Id, BenchmarkSettings.DefaultMechanicalAnimatorThrottleFrames))
                {
                    AddAccumulatedDelta(info, deltaTime);
                    localMechanicalDefaultSkips++;
                    continue;
                }

                var runDeltaTime = deltaTime;
                var accumulatedDelta = TakeAccumulatedDelta(info);
                if (accumulatedDelta > 0f && accumulatedDelta < 5f)
                {
                    runDeltaTime += accumulatedDelta;
                    localAccumulatedDeltaApplications++;
                }

                localMechanicalDefaultRuns++;
                entry.Update(runDeltaTime);
            }

            localRunTicks = Stopwatch.GetTimestamp() - runStartTimestamp;
            localMaxRunTicks = localRunTicks;

            AddCounters(
                localCalls,
                localOptimizedCalls,
                localMechanicalDefaultCalls,
                localMechanicalDefaultRuns,
                localMechanicalDefaultSkips,
                localMechanicalDefaultUnknowns,
                localNonMechanicalDefaultCalls,
                localNonDefaultCalls,
                localAccumulatedDeltaApplications,
                localRunTicks,
                localMaxRunTicks);
            Interlocked.Increment(ref _registryHandled);
            Interlocked.Add(ref _registryVisitedAnimators, localVisited);
            Interlocked.Add(ref _registryNonTargetRuns, localNonTargetRuns);
            var registryTicks = Stopwatch.GetTimestamp() - startTimestamp;
            Interlocked.Add(ref _registryStopwatchTicks, registryTicks);
            UpdateMax(ref _registryMaxStopwatchTicks, registryTicks);
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _registryFallbacks);
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] DefaultMechanicalAnimator registry replacement failed: {exception}");
            }

            return false;
        }
    }

    public static bool Begin(object animator, ref float deltaTime, out CallState state)
    {
        state = CallState.Inactive;
        if (!BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle &&
            !BenchmarkSettings.EnableDefaultMechanicalAnimatorDetailProfiler)
        {
            return true;
        }

        Interlocked.Increment(ref _calls);
        if (BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        Interlocked.Increment(ref _optimizedCalls);
        var info = GetAnimatorInfo(animator);
        if (!info.IsDefaultAnimation)
        {
            Interlocked.Increment(ref _nonDefaultCalls);
            return true;
        }

        if (!info.IsMechanical)
        {
            Interlocked.Increment(ref _nonMechanicalDefaultCalls);
            return true;
        }

        Interlocked.Increment(ref _mechanicalDefaultCalls);
        if (!info.HasKnownAnimationShape)
        {
            Interlocked.Increment(ref _mechanicalDefaultUnknowns);
        }

        if (!BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle ||
            BenchmarkSettings.DefaultMechanicalAnimatorThrottleFrames <= 1)
        {
            Interlocked.Increment(ref _mechanicalDefaultRuns);
            state = new CallState(true, Stopwatch.GetTimestamp());
            return true;
        }

        if (!ShouldRun(animator, BenchmarkSettings.DefaultMechanicalAnimatorThrottleFrames))
        {
            AddAccumulatedDelta(info, deltaTime);
            Interlocked.Increment(ref _mechanicalDefaultSkips);
            return false;
        }

        var accumulatedDelta = TakeAccumulatedDelta(info);
        if (accumulatedDelta > 0f && accumulatedDelta < 5f)
        {
            deltaTime += accumulatedDelta;
            Interlocked.Increment(ref _accumulatedDeltaApplications);
        }

        Interlocked.Increment(ref _mechanicalDefaultRuns);
        state = new CallState(true, Stopwatch.GetTimestamp());
        return true;
    }

    public static void End(CallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var stopwatchTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        Interlocked.Add(ref _stopwatchTicks, stopwatchTicks);
        UpdateMax(ref _maxStopwatchTicks, stopwatchTicks);
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle &&
            !BenchmarkSettings.EnableDefaultMechanicalAnimatorDetailProfiler)
        {
            return;
        }

        var calls = Interlocked.Exchange(ref _calls, 0);
        var optimizedCalls = Interlocked.Exchange(ref _optimizedCalls, 0);
        var mechanicalDefaultCalls = Interlocked.Exchange(ref _mechanicalDefaultCalls, 0);
        var mechanicalDefaultRuns = Interlocked.Exchange(ref _mechanicalDefaultRuns, 0);
        var mechanicalDefaultSkips = Interlocked.Exchange(ref _mechanicalDefaultSkips, 0);
        var mechanicalDefaultUnknowns = Interlocked.Exchange(ref _mechanicalDefaultUnknowns, 0);
        var nonMechanicalDefaultCalls = Interlocked.Exchange(ref _nonMechanicalDefaultCalls, 0);
        var nonDefaultCalls = Interlocked.Exchange(ref _nonDefaultCalls, 0);
        var newClassifications = Interlocked.Exchange(ref _newClassifications, 0);
        var accumulatedDeltaApplications = Interlocked.Exchange(ref _accumulatedDeltaApplications, 0);
        var stopwatchTicks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        var maxStopwatchTicks = Interlocked.Exchange(ref _maxStopwatchTicks, 0);
        var registryCalls = Interlocked.Exchange(ref _registryCalls, 0);
        var registryHandled = Interlocked.Exchange(ref _registryHandled, 0);
        var registryFallbacks = Interlocked.Exchange(ref _registryFallbacks, 0);
        var registryRebuilds = Interlocked.Exchange(ref _registryRebuilds, 0);
        var registryRebuildStopwatchTicks = Interlocked.Exchange(ref _registryRebuildStopwatchTicks, 0);
        var closedDelegateFailures = Interlocked.Exchange(ref _closedDelegateFailures, 0);
        var registryVisitedAnimators = Interlocked.Exchange(ref _registryVisitedAnimators, 0);
        var registryNonTargetRuns = Interlocked.Exchange(ref _registryNonTargetRuns, 0);
        var registryStopwatchTicks = Interlocked.Exchange(ref _registryStopwatchTicks, 0);
        var registryMaxStopwatchTicks = Interlocked.Exchange(ref _registryMaxStopwatchTicks, 0);
        if (calls == 0 && optimizedCalls == 0 && registryCalls == 0)
        {
            return;
        }

        AnimatorSummary summary;
        lock (LockObject)
        {
            summary = AnimatorSummary.Create(AnimatorInfoById.Values);
        }

        var skipRate = mechanicalDefaultCalls > 0 ? (double)mechanicalDefaultSkips / mechanicalDefaultCalls : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] DefaultMechanicalAnimator aggregate={0}, enabled={1}, registryReplacement={2}, throttleFrames={3}, registryCalls={4}, registryHandled={5}, registryFallbacks={6}, registryRebuilds={7}, registryRebuildMs={8:F2}, closedDelegateFailures={9}, registryVisitedAnimators={10}, registryNonTargetRuns={11}, registryMs={12:F2}, registryAvgMs={13:F3}, registryMaxMs={14:F3}, calls={15}, optimizedCalls={16}, mechanicalDefaultCalls={17}, runs={18}, skips={19}, skipRate={20:F3}, nonMechanicalDefaultCalls={21}, nonDefaultCalls={22}, unknownShapeCalls={23}, newClassifications={24}, accumulatedDeltaApplications={25}, runMs={26:F2}, runAvgUs={27:F2}, runMaxMs={28:F3}, cached={29}, cachedMechanical={30}, cachedDefaultMechanical={31}, cachedRotationOnlyNodeAnimators={32}, cachedPositionNodeAnimators={33}, cachedScaleNodeAnimators={34}, cachedVertexAnimators={35}, cachedUnknownShape={36}, topObjects={37}, topRoots={38}, topAnimations={39}",
            aggregateId,
            BenchmarkSettings.EnableDefaultMechanicalAnimatorThrottle,
            BenchmarkSettings.EnableDefaultMechanicalAnimatorRegistryReplacement,
            BenchmarkSettings.DefaultMechanicalAnimatorThrottleFrames,
            registryCalls,
            registryHandled,
            registryFallbacks,
            registryRebuilds,
            ToMilliseconds(registryRebuildStopwatchTicks),
            closedDelegateFailures,
            registryVisitedAnimators,
            registryNonTargetRuns,
            ToMilliseconds(registryStopwatchTicks),
            registryHandled > 0 ? ToMilliseconds(registryStopwatchTicks) / registryHandled : 0.0,
            ToMilliseconds(registryMaxStopwatchTicks),
            calls,
            optimizedCalls,
            mechanicalDefaultCalls,
            mechanicalDefaultRuns,
            mechanicalDefaultSkips,
            skipRate,
            nonMechanicalDefaultCalls,
            nonDefaultCalls,
            mechanicalDefaultUnknowns,
            newClassifications,
            accumulatedDeltaApplications,
            ToMilliseconds(stopwatchTicks),
            mechanicalDefaultRuns > 0 ? ToMicroseconds(stopwatchTicks) / mechanicalDefaultRuns : 0.0,
            ToMilliseconds(maxStopwatchTicks),
            summary.Total,
            summary.Mechanical,
            summary.DefaultMechanical,
            summary.RotationOnlyNodeAnimators,
            summary.PositionNodeAnimators,
            summary.ScaleNodeAnimators,
            summary.VertexAnimators,
            summary.UnknownShape,
            FormatTop(summary.ObjectCounts),
            FormatTop(summary.RootCounts),
            FormatTop(summary.AnimationCounts)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _registryCalls, 0);
        Interlocked.Exchange(ref _registryHandled, 0);
        Interlocked.Exchange(ref _registryFallbacks, 0);
        Interlocked.Exchange(ref _registryRebuilds, 0);
        Interlocked.Exchange(ref _registryRebuildStopwatchTicks, 0);
        Interlocked.Exchange(ref _closedDelegateFailures, 0);
        Interlocked.Exchange(ref _registryVisitedAnimators, 0);
        Interlocked.Exchange(ref _registryNonTargetRuns, 0);
        Interlocked.Exchange(ref _registryStopwatchTicks, 0);
        Interlocked.Exchange(ref _registryMaxStopwatchTicks, 0);
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _optimizedCalls, 0);
        Interlocked.Exchange(ref _mechanicalDefaultCalls, 0);
        Interlocked.Exchange(ref _mechanicalDefaultRuns, 0);
        Interlocked.Exchange(ref _mechanicalDefaultSkips, 0);
        Interlocked.Exchange(ref _mechanicalDefaultUnknowns, 0);
        Interlocked.Exchange(ref _nonMechanicalDefaultCalls, 0);
        Interlocked.Exchange(ref _nonDefaultCalls, 0);
        Interlocked.Exchange(ref _newClassifications, 0);
        Interlocked.Exchange(ref _accumulatedDeltaApplications, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
        Interlocked.Exchange(ref _maxStopwatchTicks, 0);
    }

    private static AnimatorInfo GetAnimatorInfo(object animator)
    {
        var id = RuntimeHelpers.GetHashCode(animator);
        if (AnimatorInfoById.TryGetValue(id, out var cached))
        {
            return cached;
        }

        lock (LockObject)
        {
            if (AnimatorInfoById.TryGetValue(id, out var cachedInsideLock))
            {
                return cachedInsideLock;
            }

            var created = CreateAnimatorInfo(animator, id);
            AnimatorInfoById[id] = created;
            Interlocked.Increment(ref _newClassifications);
            return created;
        }
    }

    private static bool EnsureRegistryMembers(Type registryType)
    {
        if (_registryMembersInitialized)
        {
            return _registryAnimatorsField is not null && _updateAnimation is not null;
        }

        lock (LockObject)
        {
            if (_registryMembersInitialized)
            {
                return _registryAnimatorsField is not null && _updateAnimation is not null;
            }

            _registryAnimatorsField = registryType.GetField("_animators", BindingFlags.Instance | BindingFlags.NonPublic);
            var animatorType = _registryAnimatorsField?.FieldType.IsGenericType == true
                ? _registryAnimatorsField.FieldType.GetGenericArguments().FirstOrDefault()
                : null;
            var updateAnimationMethod = animatorType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "UpdateAnimation" || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(float);
                });

            if (animatorType is not null && updateAnimationMethod is not null)
            {
                _updateAnimationMethod = updateAnimationMethod;
                _updateAnimation = CreateUpdateAnimationDelegate(animatorType, updateAnimationMethod);
            }

            _registryMembersInitialized = true;
            return _registryAnimatorsField is not null && _updateAnimation is not null;
        }
    }

    private static Action<object, float> CreateUpdateAnimationDelegate(Type animatorType, MethodInfo updateAnimationMethod)
    {
        var dynamicMethod = new DynamicMethod(
            "T3MP_UpdateTimbermeshAnimation",
            typeof(void),
            new[] { typeof(object), typeof(float) },
            typeof(DefaultMechanicalAnimatorOptimizer).Module,
            true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, animatorType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, updateAnimationMethod);
        il.Emit(OpCodes.Ret);
        return (Action<object, float>)dynamicMethod.CreateDelegate(typeof(Action<object, float>));
    }

    private static void EnsureRegistryCache(object registry, IEnumerable animators)
    {
        var frame = Time.frameCount;
        var count = animators is ICollection collection ? collection.Count : -1;
        if (ReferenceEquals(_cachedRegistry, registry) &&
            count == _cachedRegistryCount &&
            frame - _cachedRegistryFrame < 600)
        {
            return;
        }

        var rebuildStart = Stopwatch.GetTimestamp();
        MechanicalDefaultEntries.Clear();
        NonTargetEntries.Clear();
        _cachedRegistryNonMechanicalDefaultCount = 0;
        _cachedRegistryNonDefaultCount = 0;

        var rebuiltCount = 0;
        foreach (var animator in animators)
        {
            if (animator is null)
            {
                continue;
            }

            rebuiltCount++;
            var info = GetAnimatorInfo(animator);
            var entry = new AnimatorEntry(animator, info, CreateClosedUpdateDelegate(animator));
            if (info.IsDefaultAnimation && info.IsMechanical)
            {
                MechanicalDefaultEntries.Add(entry);
            }
            else
            {
                if (info.IsDefaultAnimation)
                {
                    _cachedRegistryNonMechanicalDefaultCount++;
                }
                else
                {
                    _cachedRegistryNonDefaultCount++;
                }

                NonTargetEntries.Add(entry);
            }
        }

        _cachedRegistry = registry;
        _cachedRegistryCount = count >= 0 ? count : rebuiltCount;
        _cachedRegistryFrame = frame;
        Interlocked.Increment(ref _registryRebuilds);
        Interlocked.Add(ref _registryRebuildStopwatchTicks, Stopwatch.GetTimestamp() - rebuildStart);
    }

    private static Action<float>? CreateClosedUpdateDelegate(object animator)
    {
        try
        {
            return _updateAnimationMethod is null
                ? null
                : (Action<float>)_updateAnimationMethod.CreateDelegate(typeof(Action<float>), animator);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _closedDelegateFailures);
            return null;
        }
    }

    private static void AddCounters(
        long calls,
        long optimizedCalls,
        long mechanicalDefaultCalls,
        long mechanicalDefaultRuns,
        long mechanicalDefaultSkips,
        long mechanicalDefaultUnknowns,
        long nonMechanicalDefaultCalls,
        long nonDefaultCalls,
        long accumulatedDeltaApplications,
        long stopwatchTicks,
        long maxStopwatchTicks)
    {
        Interlocked.Add(ref _calls, calls);
        Interlocked.Add(ref _optimizedCalls, optimizedCalls);
        Interlocked.Add(ref _mechanicalDefaultCalls, mechanicalDefaultCalls);
        Interlocked.Add(ref _mechanicalDefaultRuns, mechanicalDefaultRuns);
        Interlocked.Add(ref _mechanicalDefaultSkips, mechanicalDefaultSkips);
        Interlocked.Add(ref _mechanicalDefaultUnknowns, mechanicalDefaultUnknowns);
        Interlocked.Add(ref _nonMechanicalDefaultCalls, nonMechanicalDefaultCalls);
        Interlocked.Add(ref _nonDefaultCalls, nonDefaultCalls);
        Interlocked.Add(ref _accumulatedDeltaApplications, accumulatedDeltaApplications);
        Interlocked.Add(ref _stopwatchTicks, stopwatchTicks);
        UpdateMax(ref _maxStopwatchTicks, maxStopwatchTicks);
    }

    private static AnimatorInfo CreateAnimatorInfo(object animator, int id)
    {
        try
        {
            EnsureMembers(animator.GetType());
            var objectName = "<unknown>";
            var rootName = "<unknown>";
            if (animator is Component component)
            {
                objectName = CleanName(component.gameObject.name);
                rootName = CleanName(component.transform.root.gameObject.name);
            }

            var animationName = TryGetAnimationName(animator);
            var isMechanical = IsMechanicalName(objectName) || IsMechanicalName(rootName);
            var animationShape = InspectAnimationShape(animator);
            return new AnimatorInfo(
                id,
                objectName,
                rootName,
                animationName,
                isMechanical,
                string.Equals(animationName, "Default", StringComparison.Ordinal),
                animationShape);
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] DefaultMechanicalAnimator classification failed: {exception}");
            }

            return AnimatorInfo.Unknown(id);
        }
    }

    private static void EnsureMembers(Type animatorType)
    {
        if (_animationUpdatersField is not null)
        {
            return;
        }

        _animationUpdatersField = animatorType.GetField("_animationUpdaters", BindingFlags.Instance | BindingFlags.NonPublic);
        _animationNameProperty = animatorType.GetProperty("AnimationName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var assembly = animatorType.Assembly;
        var nodeUpdaterType = assembly.GetType("Timberborn.TimbermeshAnimations.NodeAnimationUpdater");
        var vertexUpdaterType = assembly.GetType("Timberborn.TimbermeshAnimations.VertexAnimationUpdater");
        _nodeCurrentAnimationField = nodeUpdaterType?.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
        _vertexCurrentAnimationField = vertexUpdaterType?.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
        _vertexAnimationsMapField = vertexUpdaterType?.GetField("_animationsMap", BindingFlags.Instance | BindingFlags.NonPublic);

        var nodeAnimationType = assembly.GetType("Timberborn.TimbermeshAnimations.NodeAnimation");
        _nodeNameProperty = nodeAnimationType?.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _nodeFrameCountProperty = nodeAnimationType?.GetProperty("FrameCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _nodeHasDifferentPositionsProperty = nodeAnimationType?.GetProperty("HasDifferentPositions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _nodeHasDifferentRotationsProperty = nodeAnimationType?.GetProperty("HasDifferentRotations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _nodeHasDifferentScalesProperty = nodeAnimationType?.GetProperty("HasDifferentScales", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var vertexAnimationType = assembly.GetType("Timberborn.TimbermeshAnimations.VertexAnimation");
        _vertexNameProperty = vertexAnimationType?.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _vertexFrameCountProperty = vertexAnimationType?.GetProperty("FrameCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _vertexAnimatedVertexCountProperty = vertexAnimationType?.GetProperty("AnimatedVertexCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static string TryGetAnimationName(object animator)
    {
        try
        {
            return _animationNameProperty?.GetValue(animator) is string name ? CleanName(name) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static AnimationShape InspectAnimationShape(object animator)
    {
        var shape = AnimationShape.Empty;
        if (_animationUpdatersField?.GetValue(animator) is not IEnumerable updaters)
        {
            return shape with { UnknownShape = true };
        }

        foreach (var updater in updaters)
        {
            if (updater is null)
            {
                continue;
            }

            var updaterTypeName = updater.GetType().Name;
            if (updaterTypeName == "NodeAnimationUpdater")
            {
                shape = shape.AddNode(InspectNodeAnimation(updater));
            }
            else if (updaterTypeName == "VertexAnimationUpdater")
            {
                shape = shape.AddVertex(InspectVertexAnimation(updater));
            }
            else
            {
                shape = shape with { OtherUpdaters = shape.OtherUpdaters + 1 };
            }
        }

        return shape;
    }

    private static NodeAnimationShape InspectNodeAnimation(object updater)
    {
        try
        {
            var animation = _nodeCurrentAnimationField?.GetValue(updater);
            if (animation is null)
            {
                return NodeAnimationShape.UnknownValue;
            }

            var name = _nodeNameProperty?.GetValue(animation) as string ?? string.Empty;
            return new NodeAnimationShape(
                CleanName(name),
                TryGetInt(_nodeFrameCountProperty, animation),
                TryGetBool(_nodeHasDifferentPositionsProperty, animation),
                TryGetBool(_nodeHasDifferentRotationsProperty, animation),
                TryGetBool(_nodeHasDifferentScalesProperty, animation),
                false);
        }
        catch (Exception)
        {
            return NodeAnimationShape.UnknownValue;
        }
    }

    private static VertexAnimationShape InspectVertexAnimation(object updater)
    {
        try
        {
            var animation = _vertexCurrentAnimationField?.GetValue(updater) ?? TryGetDefaultVertexAnimation(updater);
            if (animation is null)
            {
                return VertexAnimationShape.UnknownValue;
            }

            var name = _vertexNameProperty?.GetValue(animation) as string ?? string.Empty;
            return new VertexAnimationShape(
                CleanName(name),
                TryGetInt(_vertexFrameCountProperty, animation),
                TryGetInt(_vertexAnimatedVertexCountProperty, animation),
                false);
        }
        catch (Exception)
        {
            return VertexAnimationShape.UnknownValue;
        }
    }

    private static object? TryGetDefaultVertexAnimation(object updater)
    {
        if (_vertexAnimationsMapField?.GetValue(updater) is not IDictionary map)
        {
            return null;
        }

        return map.Contains("Default") ? map["Default"] : null;
    }

    private static int TryGetInt(PropertyInfo? property, object instance)
    {
        try
        {
            return property?.GetValue(instance) is int value ? value : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool TryGetBool(PropertyInfo? property, object instance)
    {
        try
        {
            return property?.GetValue(instance) is bool value && value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ShouldRun(object animator, int throttleFrames)
    {
        return ShouldRun(RuntimeHelpers.GetHashCode(animator), throttleFrames);
    }

    private static bool ShouldRun(int animatorHash, int throttleFrames)
    {
        var hash = animatorHash & int.MaxValue;
        var baseFrame = BenchmarkSettings.AnimatorRegistryThrottleFrames > 1
            ? Time.frameCount / BenchmarkSettings.AnimatorRegistryThrottleFrames
            : Time.frameCount;
        return (baseFrame + hash) % throttleFrames == 0;
    }

    private static void AddAccumulatedDelta(AnimatorInfo info, float deltaTime)
    {
        if (deltaTime <= 0f || deltaTime > 1f)
        {
            return;
        }

        info.AccumulatedDelta += deltaTime;
    }

    private static float TakeAccumulatedDelta(AnimatorInfo info)
    {
        var accumulated = info.AccumulatedDelta;
        info.AccumulatedDelta = 0f;
        return accumulated;
    }

    private static bool IsMechanicalName(string name)
    {
        return name.IndexOf("Gear", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Axle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Shaft", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("WaterWheel", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FormatTop(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "<none>";
        }

        return string.Join("|", counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(BenchmarkSettings.AnimatorRegistryDetailTopEntries)
            .Select(pair => pair.Key + ":" + pair.Value));
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "<empty>";
        }

        return name.Replace(';', '_').Replace('|', '_').Replace(',', '_').Trim();
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static double ToMicroseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000000.0 / Stopwatch.Frequency;
    }

    private static void UpdateMax(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (value <= current)
            {
                return;
            }

            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }
        }
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, 0);

        public CallState(bool active, long startTimestamp)
        {
            Active = active;
            StartTimestamp = startTimestamp;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
    }

    private sealed class AnimatorInfo
    {
        public AnimatorInfo(
            int id,
            string objectName,
            string rootName,
            string animationName,
            bool isMechanical,
            bool isDefaultAnimation,
            AnimationShape shape)
        {
            Id = id;
            ObjectName = objectName;
            RootName = rootName;
            AnimationName = animationName;
            IsMechanical = isMechanical;
            IsDefaultAnimation = isDefaultAnimation;
            Shape = shape;
        }

        public int Id { get; }
        public string ObjectName { get; }
        public string RootName { get; }
        public string AnimationName { get; }
        public bool IsMechanical { get; }
        public bool IsDefaultAnimation { get; }
        public bool HasKnownAnimationShape => !Shape.UnknownShape;
        public AnimationShape Shape { get; }
        public float AccumulatedDelta { get; set; }

        public static AnimatorInfo Unknown(int id)
        {
            return new AnimatorInfo(id, "<unknown>", "<unknown>", string.Empty, false, false, AnimationShape.Empty with { UnknownShape = true });
        }
    }

    private sealed class AnimatorEntry
    {
        private readonly object _animator;
        private readonly Action<float>? _closedUpdateAnimation;

        public AnimatorEntry(object animator, AnimatorInfo info, Action<float>? closedUpdateAnimation)
        {
            _animator = animator;
            Info = info;
            _closedUpdateAnimation = closedUpdateAnimation;
        }

        public AnimatorInfo Info { get; }

        public void Update(float deltaTime)
        {
            if (_closedUpdateAnimation is not null)
            {
                _closedUpdateAnimation(deltaTime);
                return;
            }

            _updateAnimation?.Invoke(_animator, deltaTime);
        }
    }

    private readonly struct AnimationShape
    {
        public static readonly AnimationShape Empty = new AnimationShape(0, 0, 0, 0, 0, 0, 0, 0, false);

        public AnimationShape(
            int nodeUpdaters,
            int vertexUpdaters,
            int otherUpdaters,
            int rotationNodes,
            int positionNodes,
            int scaleNodes,
            int rotationOnlyNodes,
            int vertexAnimatedVertices,
            bool unknownShape)
        {
            NodeUpdaters = nodeUpdaters;
            VertexUpdaters = vertexUpdaters;
            OtherUpdaters = otherUpdaters;
            RotationNodes = rotationNodes;
            PositionNodes = positionNodes;
            ScaleNodes = scaleNodes;
            RotationOnlyNodes = rotationOnlyNodes;
            VertexAnimatedVertices = vertexAnimatedVertices;
            UnknownShape = unknownShape;
        }

        public int NodeUpdaters { get; init; }
        public int VertexUpdaters { get; init; }
        public int OtherUpdaters { get; init; }
        public int RotationNodes { get; init; }
        public int PositionNodes { get; init; }
        public int ScaleNodes { get; init; }
        public int RotationOnlyNodes { get; init; }
        public int VertexAnimatedVertices { get; init; }
        public bool UnknownShape { get; init; }

        public AnimationShape AddNode(NodeAnimationShape node)
        {
            return this with
            {
                NodeUpdaters = NodeUpdaters + 1,
                RotationNodes = RotationNodes + (node.HasDifferentRotations ? 1 : 0),
                PositionNodes = PositionNodes + (node.HasDifferentPositions ? 1 : 0),
                ScaleNodes = ScaleNodes + (node.HasDifferentScales ? 1 : 0),
                RotationOnlyNodes = RotationOnlyNodes + (node.HasDifferentRotations && !node.HasDifferentPositions && !node.HasDifferentScales ? 1 : 0),
                UnknownShape = UnknownShape || node.Unknown
            };
        }

        public AnimationShape AddVertex(VertexAnimationShape vertex)
        {
            return this with
            {
                VertexUpdaters = VertexUpdaters + 1,
                VertexAnimatedVertices = VertexAnimatedVertices + vertex.AnimatedVertexCount,
                UnknownShape = UnknownShape || vertex.Unknown
            };
        }
    }

    private readonly struct NodeAnimationShape
    {
        public static readonly NodeAnimationShape UnknownValue = new NodeAnimationShape(string.Empty, 0, false, false, false, true);

        public NodeAnimationShape(string name, int frameCount, bool hasDifferentPositions, bool hasDifferentRotations, bool hasDifferentScales, bool unknown)
        {
            Name = name;
            FrameCount = frameCount;
            HasDifferentPositions = hasDifferentPositions;
            HasDifferentRotations = hasDifferentRotations;
            HasDifferentScales = hasDifferentScales;
            Unknown = unknown;
        }

        public string Name { get; }
        public int FrameCount { get; }
        public bool HasDifferentPositions { get; }
        public bool HasDifferentRotations { get; }
        public bool HasDifferentScales { get; }
        public bool Unknown { get; }
    }

    private readonly struct VertexAnimationShape
    {
        public static readonly VertexAnimationShape UnknownValue = new VertexAnimationShape(string.Empty, 0, 0, true);

        public VertexAnimationShape(string name, int frameCount, int animatedVertexCount, bool unknown)
        {
            Name = name;
            FrameCount = frameCount;
            AnimatedVertexCount = animatedVertexCount;
            Unknown = unknown;
        }

        public string Name { get; }
        public int FrameCount { get; }
        public int AnimatedVertexCount { get; }
        public bool Unknown { get; }
    }

    private readonly struct AnimatorSummary
    {
        private AnimatorSummary(
            int total,
            int mechanical,
            int defaultMechanical,
            int rotationOnlyNodeAnimators,
            int positionNodeAnimators,
            int scaleNodeAnimators,
            int vertexAnimators,
            int unknownShape,
            Dictionary<string, int> objectCounts,
            Dictionary<string, int> rootCounts,
            Dictionary<string, int> animationCounts)
        {
            Total = total;
            Mechanical = mechanical;
            DefaultMechanical = defaultMechanical;
            RotationOnlyNodeAnimators = rotationOnlyNodeAnimators;
            PositionNodeAnimators = positionNodeAnimators;
            ScaleNodeAnimators = scaleNodeAnimators;
            VertexAnimators = vertexAnimators;
            UnknownShape = unknownShape;
            ObjectCounts = objectCounts;
            RootCounts = rootCounts;
            AnimationCounts = animationCounts;
        }

        public int Total { get; }
        public int Mechanical { get; }
        public int DefaultMechanical { get; }
        public int RotationOnlyNodeAnimators { get; }
        public int PositionNodeAnimators { get; }
        public int ScaleNodeAnimators { get; }
        public int VertexAnimators { get; }
        public int UnknownShape { get; }
        public Dictionary<string, int> ObjectCounts { get; }
        public Dictionary<string, int> RootCounts { get; }
        public Dictionary<string, int> AnimationCounts { get; }

        public static AnimatorSummary Create(IEnumerable<AnimatorInfo> infos)
        {
            var total = 0;
            var mechanical = 0;
            var defaultMechanical = 0;
            var rotationOnlyNodeAnimators = 0;
            var positionNodeAnimators = 0;
            var scaleNodeAnimators = 0;
            var vertexAnimators = 0;
            var unknownShape = 0;
            var objectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var rootCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var animationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var info in infos)
            {
                total++;
                if (info.IsMechanical)
                {
                    mechanical++;
                }

                if (info.IsMechanical && info.IsDefaultAnimation)
                {
                    defaultMechanical++;
                    if (info.Shape.NodeUpdaters > 0 && info.Shape.RotationOnlyNodes == info.Shape.NodeUpdaters)
                    {
                        rotationOnlyNodeAnimators++;
                    }

                    if (info.Shape.PositionNodes > 0)
                    {
                        positionNodeAnimators++;
                    }

                    if (info.Shape.ScaleNodes > 0)
                    {
                        scaleNodeAnimators++;
                    }

                    if (info.Shape.VertexUpdaters > 0)
                    {
                        vertexAnimators++;
                    }

                    if (info.Shape.UnknownShape)
                    {
                        unknownShape++;
                    }

                    AddCount(objectCounts, info.ObjectName);
                    AddCount(rootCounts, info.RootName);
                    AddCount(animationCounts, info.AnimationName);
                }
            }

            return new AnimatorSummary(
                total,
                mechanical,
                defaultMechanical,
                rotationOnlyNodeAnimators,
                positionNodeAnimators,
                scaleNodeAnimators,
                vertexAnimators,
                unknownShape,
                objectCounts,
                rootCounts,
                animationCounts);
        }

        private static void AddCount(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }
    }
}
