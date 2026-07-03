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

internal static class MechanicalDirectRotationOptimizer
{
    private static readonly object LockObject = new object();
    private static readonly List<DirectEntry> DirectEntries = new List<DirectEntry>(8192);
    private static readonly List<DirectEntry> VisibleDirectEntries = new List<DirectEntry>(4096);
    private static readonly List<DirectEntry> InvisibleDirectEntries = new List<DirectEntry>(4096);
    private static readonly List<object> VanillaEntries = new List<object>(2048);
    private static readonly Dictionary<RotationSampleKey, Quaternion> RotationSampleCache = new Dictionary<RotationSampleKey, Quaternion>(4096);
    private static readonly Dictionary<string, int> VanillaResidueCounts = new Dictionary<string, int>(StringComparer.Ordinal);

    private static FieldInfo? _registryAnimatorsField;
    private static FieldInfo? _animationUpdatersField;
    private static FieldInfo? _currentAnimationField;
    private static FieldInfo? _enabledField;
    private static FieldInfo? _timeField;
    private static FieldInfo? _repeatedTimeField;
    private static FieldInfo? _playingFinishedField;
    private static FieldInfo? _speedField;
    private static FieldInfo? _loopedField;
    private static FieldInfo? _playBackwardsField;
    private static FieldInfo? _nodeCurrentAnimationField;
    private static FieldInfo? _nodeSelfTransformField;
    private static FieldInfo? _nodePositionsField;
    private static FieldInfo? _nodeRotationsField;
    private static FieldInfo? _nodeScalesField;
    private static PropertyInfo? _animationNameProperty;
    private static PropertyInfo? _animationLengthProperty;
    private static PropertyInfo? _nodeFrameCountProperty;
    private static PropertyInfo? _nodeHasDifferentPositionsProperty;
    private static PropertyInfo? _nodeHasDifferentRotationsProperty;
    private static PropertyInfo? _nodeHasDifferentScalesProperty;
    private static Action<object, float>? _updateAnimation;
    private static FloatGetter? _getTime;
    private static FloatSetter? _setTime;
    private static FloatSetter? _setRepeatedTime;
    private static FloatGetter? _getSpeed;
    private static BoolGetter? _getEnabled;
    private static BoolGetter? _getPlayingFinished;
    private static BoolGetter? _getLooped;
    private static BoolGetter? _getPlayBackwards;
    private static ObjectGetter? _getCurrentAnimation;
    private static bool _membersInitialized;
    private static object? _cachedRegistry;
    private static int _cachedRegistryCount = -1;
    private static int _cachedRegistryFrame = -1000000;
    private static int _lastDirectEntryCount;
    private static int _lastVanillaEntryCount;
    private static int _lastDirectNodeCount;
    private static int _lastVisibleDirectEntryCount;
    private static int _lastInvisibleDirectEntryCount;
    private static int _lastInvisibleDirectNodeCount;
    private static int _lastGroupCount;
    private static int _lastUniqueRotationArrayCount;
    private static int _lastRotationCacheCount;
    private static int _lastVisibilityRefreshFrame = -1000000;
    private static int _warningCount;
    private static string _lastTopVanillaResidues = "<none>";

    private static long _registryCalls;
    private static long _handledCalls;
    private static long _fallbackCalls;
    private static long _skippedCalls;
    private static long _rebuilds;
    private static long _rebuildStopwatchTicks;
    private static long _directEntryVisits;
    private static long _directEntryUpdates;
    private static long _directInvalidFallbacks;
    private static long _vanillaRuns;
    private static long _visibleWrites;
    private static long _invisibleSkips;
    private static long _rotationCacheLookups;
    private static long _rotationCacheHits;
    private static long _rotationCacheMisses;
    private static long _updateStopwatchTicks;
    private static long _maxUpdateStopwatchTicks;
    private static float _accumulatedDeltaTime;
    private static float _accumulatedInvisibleDeltaTime;

    private delegate float FloatGetter(object instance);
    private delegate void FloatSetter(object instance, float value);
    private delegate bool BoolGetter(object instance);
    private delegate object? ObjectGetter(object instance);

    public static bool TryUpdateRegistry(object registry)
    {
        if (!BenchmarkSettings.EnableMechanicalDirectRotationOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _registryCalls);
        var startTimestamp = BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        try
        {
            var frame = Time.frameCount;
            var deltaTime = Time.deltaTime;
            var runAnimation = !BenchmarkSettings.EnableAnimatorRegistryThrottle ||
                BenchmarkSettings.AnimatorRegistryThrottleFrames <= 1 ||
                frame % BenchmarkSettings.AnimatorRegistryThrottleFrames == 0;

            _accumulatedDeltaTime += deltaTime;
            if (!runAnimation)
            {
                Interlocked.Increment(ref _handledCalls);
                Interlocked.Increment(ref _skippedCalls);
                if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
                {
                    var skippedTicks = Stopwatch.GetTimestamp() - startTimestamp;
                    Interlocked.Add(ref _updateStopwatchTicks, skippedTicks);
                    UpdateMax(ref _maxUpdateStopwatchTicks, skippedTicks);
                }

                return true;
            }

            var effectiveDeltaTime = _accumulatedDeltaTime > 0f && _accumulatedDeltaTime < 5f
                ? _accumulatedDeltaTime
                : deltaTime;
            _accumulatedDeltaTime = 0f;

            if (!EnsureMembers(registry.GetType()) ||
                _registryAnimatorsField?.GetValue(registry) is not IEnumerable animators ||
                _updateAnimation is null)
            {
                Interlocked.Increment(ref _fallbackCalls);
                return false;
            }

            EnsureCache(registry, animators);

            RefreshVisibilityListsIfNeeded(frame);

            var directVisits = 0L;
            var directUpdates = 0L;
            var invalidFallbacks = 0L;
            var vanillaRuns = 0L;
            var visibleWrites = 0L;
            var invisibleSkips = 0L;
            var rotationCacheLookups = 0L;
            var rotationCacheHits = 0L;
            var rotationCacheMisses = 0L;

            _accumulatedInvisibleDeltaTime += effectiveDeltaTime;
            if (BenchmarkSettings.EnableMechanicalDirectRotationSampleCache)
            {
                RotationSampleCache.Clear();
            }

            foreach (var entry in VisibleDirectEntries)
            {
                directVisits++;
                var validateShape = frame % 120 == 0;
                if (!entry.TryUpdate(effectiveDeltaTime, frame, validateShape, ref visibleWrites, ref invisibleSkips, ref rotationCacheLookups, ref rotationCacheHits, ref rotationCacheMisses))
                {
                    invalidFallbacks++;
                    _updateAnimation(entry.Animator, effectiveDeltaTime);
                    vanillaRuns++;

                    continue;
                }

                directUpdates++;
            }

            invisibleSkips += Volatile.Read(ref _lastInvisibleDirectNodeCount);

            foreach (var animator in VanillaEntries)
            {
                if (animator is Behaviour behaviour &&
                    behaviour &&
                    behaviour.isActiveAndEnabled)
                {
                    _updateAnimation(animator, effectiveDeltaTime);
                    vanillaRuns++;
                }
            }

            Interlocked.Increment(ref _handledCalls);
            Interlocked.Add(ref _directEntryVisits, directVisits);
            Interlocked.Add(ref _directEntryUpdates, directUpdates);
            Interlocked.Add(ref _directInvalidFallbacks, invalidFallbacks);
            Interlocked.Add(ref _vanillaRuns, vanillaRuns);
            Interlocked.Add(ref _visibleWrites, visibleWrites);
            Interlocked.Add(ref _invisibleSkips, invisibleSkips);
            Interlocked.Add(ref _rotationCacheLookups, rotationCacheLookups);
            Interlocked.Add(ref _rotationCacheHits, rotationCacheHits);
            Interlocked.Add(ref _rotationCacheMisses, rotationCacheMisses);
            Volatile.Write(ref _lastRotationCacheCount, RotationSampleCache.Count);
            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                var ticks = Stopwatch.GetTimestamp() - startTimestamp;
                Interlocked.Add(ref _updateStopwatchTicks, ticks);
                UpdateMax(ref _maxUpdateStopwatchTicks, ticks);
            }

            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbackCalls);
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] MechanicalDirectRotation failed: {exception}");
            }

            return false;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableMechanicalDirectRotationOptimizer)
        {
            return;
        }

        var registryCalls = Interlocked.Exchange(ref _registryCalls, 0);
        var handledCalls = Interlocked.Exchange(ref _handledCalls, 0);
        var fallbackCalls = Interlocked.Exchange(ref _fallbackCalls, 0);
        var skippedCalls = Interlocked.Exchange(ref _skippedCalls, 0);
        var rebuilds = Interlocked.Exchange(ref _rebuilds, 0);
        var rebuildTicks = Interlocked.Exchange(ref _rebuildStopwatchTicks, 0);
        var directEntryVisits = Interlocked.Exchange(ref _directEntryVisits, 0);
        var directEntryUpdates = Interlocked.Exchange(ref _directEntryUpdates, 0);
        var directInvalidFallbacks = Interlocked.Exchange(ref _directInvalidFallbacks, 0);
        var vanillaRuns = Interlocked.Exchange(ref _vanillaRuns, 0);
        var visibleWrites = Interlocked.Exchange(ref _visibleWrites, 0);
        var invisibleSkips = Interlocked.Exchange(ref _invisibleSkips, 0);
        var rotationCacheLookups = Interlocked.Exchange(ref _rotationCacheLookups, 0);
        var rotationCacheHits = Interlocked.Exchange(ref _rotationCacheHits, 0);
        var rotationCacheMisses = Interlocked.Exchange(ref _rotationCacheMisses, 0);
        var updateTicks = Interlocked.Exchange(ref _updateStopwatchTicks, 0);
        var maxUpdateTicks = Interlocked.Exchange(ref _maxUpdateStopwatchTicks, 0);
        if (registryCalls == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] MechanicalDirectRotation aggregate={0}, registryCalls={1}, handled={2}, skipped={3}, fallbackCalls={4}, rebuilds={5}, rebuildMs={6:F2}, directEntriesLast={7}, visibleDirectLast={8}, invisibleDirectLast={9}, vanillaEntriesLast={10}, directNodesLast={11}, invisibleNodesLast={12}, groupsLast={13}, uniqueRotationArraysLast={14}, directVisits={15}, directUpdates={16}, invalidFallbacks={17}, vanillaRuns={18}, visibleWrites={19}, invisibleSkips={20}, rotationLookups={21}, rotationHits={22}, rotationMisses={23}, rotationHitRate={24:F1}, rotationCacheLast={25}, updateMs={26:F2}, avgMs={27:F3}, maxMs={28:F3}, vanillaResidues={29}",
            aggregateId,
            registryCalls,
            handledCalls,
            skippedCalls,
            fallbackCalls,
            rebuilds,
            ToMilliseconds(rebuildTicks),
            Volatile.Read(ref _lastDirectEntryCount),
            Volatile.Read(ref _lastVisibleDirectEntryCount),
            Volatile.Read(ref _lastInvisibleDirectEntryCount),
            Volatile.Read(ref _lastVanillaEntryCount),
            Volatile.Read(ref _lastDirectNodeCount),
            Volatile.Read(ref _lastInvisibleDirectNodeCount),
            Volatile.Read(ref _lastGroupCount),
            Volatile.Read(ref _lastUniqueRotationArrayCount),
            directEntryVisits,
            directEntryUpdates,
            directInvalidFallbacks,
            vanillaRuns,
            visibleWrites,
            invisibleSkips,
            rotationCacheLookups,
            rotationCacheHits,
            rotationCacheMisses,
            rotationCacheLookups > 0 ? rotationCacheHits * 100.0 / rotationCacheLookups : 0.0,
            Volatile.Read(ref _lastRotationCacheCount),
            ToMilliseconds(updateTicks),
            handledCalls > 0 ? ToMilliseconds(updateTicks) / handledCalls : 0.0,
            ToMilliseconds(maxUpdateTicks),
            _lastTopVanillaResidues));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _registryCalls, 0);
        Interlocked.Exchange(ref _handledCalls, 0);
        Interlocked.Exchange(ref _fallbackCalls, 0);
        Interlocked.Exchange(ref _skippedCalls, 0);
        Interlocked.Exchange(ref _rebuilds, 0);
        Interlocked.Exchange(ref _rebuildStopwatchTicks, 0);
        Interlocked.Exchange(ref _directEntryVisits, 0);
        Interlocked.Exchange(ref _directEntryUpdates, 0);
        Interlocked.Exchange(ref _directInvalidFallbacks, 0);
        Interlocked.Exchange(ref _vanillaRuns, 0);
        Interlocked.Exchange(ref _visibleWrites, 0);
        Interlocked.Exchange(ref _invisibleSkips, 0);
        Interlocked.Exchange(ref _rotationCacheLookups, 0);
        Interlocked.Exchange(ref _rotationCacheHits, 0);
        Interlocked.Exchange(ref _rotationCacheMisses, 0);
        Interlocked.Exchange(ref _updateStopwatchTicks, 0);
        Interlocked.Exchange(ref _maxUpdateStopwatchTicks, 0);
    }

    private static void EnsureCache(object registry, IEnumerable animators)
    {
        var frame = Time.frameCount;
        var count = animators is ICollection collection ? collection.Count : -1;
        if (ReferenceEquals(_cachedRegistry, registry) &&
            count == _cachedRegistryCount &&
            frame - _cachedRegistryFrame < 600)
        {
            return;
        }

        var startTimestamp = BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        DirectEntries.Clear();
        VisibleDirectEntries.Clear();
        InvisibleDirectEntries.Clear();
        VanillaEntries.Clear();
        VanillaResidueCounts.Clear();
        var directNodeCount = 0;
        var groupKeys = new HashSet<string>(StringComparer.Ordinal);
        var rotationArrays = new HashSet<int>();
        var rebuiltCount = 0;

        foreach (var animator in animators)
        {
            if (animator is null)
            {
                continue;
            }

            rebuiltCount++;
            if (TryCreateDirectEntry(animator, out var directEntry, out var groupKey, out var vanillaResidueKey))
            {
                DirectEntries.Add(directEntry);
                directNodeCount += directEntry.NodeCount;
                groupKeys.Add(groupKey);
                foreach (var rotationArrayId in directEntry.RotationArrayIds)
                {
                    rotationArrays.Add(rotationArrayId);
                }
            }
            else
            {
                VanillaEntries.Add(animator);
                AddCount(VanillaResidueCounts, vanillaResidueKey);
            }
        }

        _cachedRegistry = registry;
        _cachedRegistryCount = count >= 0 ? count : rebuiltCount;
        _cachedRegistryFrame = frame;
        Volatile.Write(ref _lastDirectEntryCount, DirectEntries.Count);
        Volatile.Write(ref _lastVanillaEntryCount, VanillaEntries.Count);
        Volatile.Write(ref _lastDirectNodeCount, directNodeCount);
        Volatile.Write(ref _lastGroupCount, groupKeys.Count);
        Volatile.Write(ref _lastUniqueRotationArrayCount, rotationArrays.Count);
        _lastTopVanillaResidues = FormatTop(VanillaResidueCounts, 8);
        _accumulatedInvisibleDeltaTime = 0f;
        RefreshVisibilityLists(frame);
        Interlocked.Increment(ref _rebuilds);
        if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
        {
            Interlocked.Add(ref _rebuildStopwatchTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }
    }

    private static void RefreshVisibilityListsIfNeeded(int frame)
    {
        if (frame - _lastVisibilityRefreshFrame < BenchmarkSettings.MechanicalDirectVisibilityRefreshFrames)
        {
            return;
        }

        var pendingDelta = _accumulatedInvisibleDeltaTime;
        _accumulatedInvisibleDeltaTime = 0f;
        for (var i = 0; i < InvisibleDirectEntries.Count; i++)
        {
            InvisibleDirectEntries[i].CatchUpInvisibleTime(pendingDelta);
        }

        RefreshVisibilityLists(frame);
    }

    private static void RefreshVisibilityLists(int frame)
    {
        VisibleDirectEntries.Clear();
        InvisibleDirectEntries.Clear();
        var invisibleNodes = 0;
        for (var i = 0; i < DirectEntries.Count; i++)
        {
            var entry = DirectEntries[i];
            if (entry.IsVisibleNow())
            {
                VisibleDirectEntries.Add(entry);
            }
            else
            {
                InvisibleDirectEntries.Add(entry);
                invisibleNodes += entry.NodeCount;
            }
        }

        _lastVisibilityRefreshFrame = frame;
        Volatile.Write(ref _lastVisibleDirectEntryCount, VisibleDirectEntries.Count);
        Volatile.Write(ref _lastInvisibleDirectEntryCount, InvisibleDirectEntries.Count);
        Volatile.Write(ref _lastInvisibleDirectNodeCount, invisibleNodes);
    }

    private static bool TryCreateDirectEntry(object animator, out DirectEntry directEntry, out string groupKey, out string vanillaResidueKey)
    {
        directEntry = DirectEntry.Empty;
        groupKey = string.Empty;
        vanillaResidueKey = BuildResidueKey(animator, "unknown");
        if (animator is not Behaviour behaviour)
        {
            vanillaResidueKey = BuildResidueKey(animator, "nonBehaviour");
            return false;
        }

        var objectName = CleanName(behaviour.gameObject.name);
        var rootName = CleanName(behaviour.transform.root.gameObject.name);
        var hasMechanicalName = IsMechanicalName(objectName) || IsMechanicalName(rootName);
        if (!hasMechanicalName &&
            !BenchmarkSettings.EnableDefaultRotationOnlyDirectOptimizer)
        {
            vanillaResidueKey = BuildResidueKey(animator, "nonMechanicalName");
            return false;
        }

        var animationName = TryGetAnimationName(animator);
        if (!string.Equals(animationName, "Default", StringComparison.Ordinal))
        {
            vanillaResidueKey = BuildResidueKey(animator, "nonDefaultAnimation");
            return false;
        }

        var currentAnimation = _getCurrentAnimation?.Invoke(animator);
        if (currentAnimation is null)
        {
            vanillaResidueKey = BuildResidueKey(animator, "noCurrentAnimation");
            return false;
        }

        var length = TryGetFloat(_animationLengthProperty, animator, 0f);
        if (length <= 0f)
        {
            vanillaResidueKey = BuildResidueKey(animator, "invalidLength");
            return false;
        }

        if (_getLooped?.Invoke(animator) != true)
        {
            vanillaResidueKey = BuildResidueKey(animator, "nonLooped");
            return false;
        }

        if (_animationUpdatersField?.GetValue(animator) is not IEnumerable updaters)
        {
            vanillaResidueKey = BuildResidueKey(animator, "noUpdaters");
            return false;
        }

        var nodes = new List<NodeRotationEntry>(2);
        var rotationArrayIds = new List<int>(2);
        var nodeShapeKeys = new List<string>(2);
        var commonFrameCount = 0;
        foreach (var updater in updaters)
        {
            if (updater is null)
            {
                continue;
            }

            var typeName = updater.GetType().Name;
            if (typeName != "NodeAnimationUpdater")
            {
                vanillaResidueKey = BuildResidueKey(animator, string.Concat("nonNodeUpdater-", CleanName(typeName)));
                return false;
            }

            var nodeAnimation = _nodeCurrentAnimationField?.GetValue(updater);
            if (nodeAnimation is null)
            {
                vanillaResidueKey = BuildResidueKey(animator, "nullNodeAnimation");
                return false;
            }

            var frameCount = TryGetInt(_nodeFrameCountProperty, nodeAnimation);
            var hasRotation = TryGetBool(_nodeHasDifferentRotationsProperty, nodeAnimation);
            var hasPosition = TryGetBool(_nodeHasDifferentPositionsProperty, nodeAnimation);
            var hasScale = TryGetBool(_nodeHasDifferentScalesProperty, nodeAnimation);
            if (frameCount <= 1 ||
                (!hasPosition && !hasRotation && !hasScale) ||
                (!BenchmarkSettings.EnableNodeTransformDirectOptimizer &&
                 (!hasRotation || hasPosition || hasScale)))
            {
                vanillaResidueKey = BuildResidueKey(
                    animator,
                    string.Concat(
                        "nodeShape-frame",
                        frameCount.ToString(CultureInfo.InvariantCulture),
                        "-rot",
                        hasRotation ? "1" : "0",
                        "-pos",
                        hasPosition ? "1" : "0",
                        "-scale",
                        hasScale ? "1" : "0"));
                return false;
            }

            var positionArrayId = 0;
            Vector3[]? positions = null;
            if (hasPosition)
            {
                if (_nodePositionsField?.GetValue(nodeAnimation) is not Vector3[] positionValues ||
                    positionValues.Length < frameCount)
                {
                    vanillaResidueKey = BuildResidueKey(animator, "noPositions");
                    return false;
                }

                positions = positionValues;
                positionArrayId = RuntimeHelpers.GetHashCode(positionValues);
            }

            var rotationArrayId = 0;
            Quaternion[]? rotations = null;
            if (hasRotation)
            {
                if (_nodeRotationsField?.GetValue(nodeAnimation) is not Quaternion[] rotationValues ||
                    rotationValues.Length < frameCount)
                {
                    vanillaResidueKey = BuildResidueKey(animator, "noRotations");
                    return false;
                }

                rotations = rotationValues;
                rotationArrayId = RuntimeHelpers.GetHashCode(rotationValues);
                rotationArrayIds.Add(rotationArrayId);
            }

            var scaleArrayId = 0;
            Vector3[]? scales = null;
            if (hasScale)
            {
                if (_nodeScalesField?.GetValue(nodeAnimation) is not Vector3[] scaleValues ||
                    scaleValues.Length < frameCount)
                {
                    vanillaResidueKey = BuildResidueKey(animator, "noScales");
                    return false;
                }

                scales = scaleValues;
                scaleArrayId = RuntimeHelpers.GetHashCode(scaleValues);
            }

            if (_nodeSelfTransformField?.GetValue(updater) is not Transform transform)
            {
                vanillaResidueKey = BuildResidueKey(animator, "noTransform");
                return false;
            }

            var renderer = transform.GetComponent<Renderer>();
            if (commonFrameCount == 0)
            {
                commonFrameCount = frameCount;
            }
            else if (commonFrameCount != frameCount)
            {
                commonFrameCount = -1;
            }

            nodeShapeKeys.Add(string.Concat(
                frameCount.ToString(CultureInfo.InvariantCulture),
                hasPosition ? ":p" + positionArrayId.ToString(CultureInfo.InvariantCulture) : string.Empty,
                hasRotation ? ":r" + rotationArrayId.ToString(CultureInfo.InvariantCulture) : string.Empty,
                hasScale ? ":s" + scaleArrayId.ToString(CultureInfo.InvariantCulture) : string.Empty));
            nodes.Add(new NodeRotationEntry(transform, renderer, positions, rotations, scales, positionArrayId, rotationArrayId, scaleArrayId, frameCount, hasPosition, hasRotation, hasScale));
        }

        if (nodes.Count == 0)
        {
            vanillaResidueKey = BuildResidueKey(animator, "noNodes");
            return false;
        }

        var speed = _getSpeed?.Invoke(animator) ?? 1f;
        var backwards = _getPlayBackwards?.Invoke(animator) == true;
        var initialTime = _getTime?.Invoke(animator) ?? 0f;
        groupKey = string.Concat(
            "nodes=", nodes.Count,
            "/len=", length.ToString("0.###", CultureInfo.InvariantCulture),
            "/speed=", speed.ToString("0.###", CultureInfo.InvariantCulture),
            "/back=", backwards ? "1" : "0",
            "/nodeshape=", string.Join("+", nodeShapeKeys));
        directEntry = new DirectEntry(animator, behaviour, currentAnimation, length, initialTime, backwards, nodes.ToArray(), rotationArrayIds.ToArray(), commonFrameCount);
        vanillaResidueKey = string.Empty;
        return true;
    }

    private static string BuildResidueKey(object animator, string reason)
    {
        var component = animator as Component;
        var objectName = component is not null ? CleanName(component.gameObject.name) : "<unknown>";
        var rootName = component is not null ? CleanName(component.transform.root.gameObject.name) : "<unknown>";
        var animationName = TryGetAnimationName(animator);
        return string.Concat(reason, "|anim=", animationName, "|root=", rootName, "|obj=", objectName);
    }

    private static void AddCount(Dictionary<string, int> counts, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }

    private static string FormatTop(Dictionary<string, int> counts, int take)
    {
        if (counts.Count == 0)
        {
            return "<none>";
        }

        return string.Join(
            ";",
            counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(take)
                .Select(pair => string.Concat(pair.Key, "=", pair.Value.ToString(CultureInfo.InvariantCulture))));
    }

    private static Quaternion GetRotationSample(Quaternion[] rotations, int rotationArrayId, int frameCount, int fromFrame, int toFrame, float weight, ref long lookups, ref long hits, ref long misses)
    {
        if (!BenchmarkSettings.EnableMechanicalDirectRotationSampleCache)
        {
            return Quaternion.Lerp(rotations[fromFrame], rotations[toFrame], weight);
        }

        lookups++;
        var key = new RotationSampleKey(rotationArrayId, frameCount, fromFrame, toFrame, weight);
        if (RotationSampleCache.TryGetValue(key, out var rotation))
        {
            hits++;
            return rotation;
        }

        misses++;
        rotation = Quaternion.Lerp(rotations[fromFrame], rotations[toFrame], weight);
        RotationSampleCache[key] = rotation;
        return rotation;
    }

    private static Vector3 GetVectorSample(Vector3[]? values, int arrayId, int frameCount, int fromFrame, int toFrame, float weight)
    {
        if (values is null)
        {
            return Vector3.zero;
        }

        return Vector3.Lerp(values[fromFrame], values[toFrame], weight);
    }

    private static bool EnsureMembers(Type registryType)
    {
        if (_membersInitialized)
        {
            return _registryAnimatorsField is not null && _updateAnimation is not null;
        }

        lock (LockObject)
        {
            if (_membersInitialized)
            {
                return _registryAnimatorsField is not null && _updateAnimation is not null;
            }

            _registryAnimatorsField = registryType.GetField("_animators", BindingFlags.Instance | BindingFlags.NonPublic);
            var animatorType = _registryAnimatorsField?.FieldType.IsGenericType == true
                ? _registryAnimatorsField.FieldType.GetGenericArguments().FirstOrDefault()
                : null;
            if (animatorType is not null)
            {
                _animationUpdatersField = animatorType.GetField("_animationUpdaters", BindingFlags.Instance | BindingFlags.NonPublic);
                _currentAnimationField = animatorType.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                _enabledField = animatorType.GetField("<Enabled>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                _timeField = animatorType.GetField("<Time>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                _repeatedTimeField = animatorType.GetField("<RepeatedTime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                _playingFinishedField = animatorType.GetField("<PlayingFinished>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                _speedField = animatorType.GetField("_speed", BindingFlags.Instance | BindingFlags.NonPublic);
                _loopedField = animatorType.GetField("_looped", BindingFlags.Instance | BindingFlags.NonPublic);
                _playBackwardsField = animatorType.GetField("_playBackwards", BindingFlags.Instance | BindingFlags.NonPublic);
                _animationNameProperty = animatorType.GetProperty("AnimationName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _animationLengthProperty = animatorType.GetProperty("AnimationLength", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var updateAnimationMethod = animatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method =>
                    {
                        if (method.Name != "UpdateAnimation" || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                        {
                            return false;
                        }

                        var parameters = method.GetParameters();
                        return parameters.Length == 1 && parameters[0].ParameterType == typeof(float);
                    });
                if (updateAnimationMethod is not null)
                {
                    _updateAnimation = CreateUpdateAnimationDelegate(animatorType, updateAnimationMethod);
                }

                _getTime = CreateFloatGetter(animatorType, _timeField);
                _setTime = CreateFloatSetter(animatorType, _timeField);
                _setRepeatedTime = CreateFloatSetter(animatorType, _repeatedTimeField);
                _getSpeed = CreateFloatGetter(animatorType, _speedField);
                _getEnabled = CreateBoolGetter(animatorType, _enabledField);
                _getPlayingFinished = CreateBoolGetter(animatorType, _playingFinishedField);
                _getLooped = CreateBoolGetter(animatorType, _loopedField);
                _getPlayBackwards = CreateBoolGetter(animatorType, _playBackwardsField);
                _getCurrentAnimation = CreateObjectGetter(animatorType, _currentAnimationField);
            }

            var assembly = registryType.Assembly;
            var nodeUpdaterType = assembly.GetType("Timberborn.TimbermeshAnimations.NodeAnimationUpdater");
            _nodeCurrentAnimationField = nodeUpdaterType?.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
            _nodeSelfTransformField = nodeUpdaterType?.GetField("_selfTransform", BindingFlags.Instance | BindingFlags.NonPublic);

            var nodeAnimationType = assembly.GetType("Timberborn.TimbermeshAnimations.NodeAnimation");
            _nodePositionsField = nodeAnimationType?.GetField("_positions", BindingFlags.Instance | BindingFlags.NonPublic);
            _nodeRotationsField = nodeAnimationType?.GetField("_rotations", BindingFlags.Instance | BindingFlags.NonPublic);
            _nodeScalesField = nodeAnimationType?.GetField("_scales", BindingFlags.Instance | BindingFlags.NonPublic);
            _nodeFrameCountProperty = nodeAnimationType?.GetProperty("FrameCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _nodeHasDifferentPositionsProperty = nodeAnimationType?.GetProperty("HasDifferentPositions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _nodeHasDifferentRotationsProperty = nodeAnimationType?.GetProperty("HasDifferentRotations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _nodeHasDifferentScalesProperty = nodeAnimationType?.GetProperty("HasDifferentScales", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _membersInitialized = true;
            return _registryAnimatorsField is not null && _updateAnimation is not null;
        }
    }

    private static Action<object, float> CreateUpdateAnimationDelegate(Type animatorType, MethodInfo updateAnimationMethod)
    {
        var dynamicMethod = new DynamicMethod(
            "T3MP_MechanicalDirect_UpdateAnimation",
            typeof(void),
            new[] { typeof(object), typeof(float) },
            typeof(MechanicalDirectRotationOptimizer).Module,
            true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, animatorType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, updateAnimationMethod);
        il.Emit(OpCodes.Ret);
        return (Action<object, float>)dynamicMethod.CreateDelegate(typeof(Action<object, float>));
    }

    private static FloatGetter? CreateFloatGetter(Type ownerType, FieldInfo? field)
    {
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod("T3MP_GetFloat", typeof(float), new[] { typeof(object) }, typeof(MechanicalDirectRotationOptimizer).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ownerType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (FloatGetter)method.CreateDelegate(typeof(FloatGetter));
    }

    private static FloatSetter? CreateFloatSetter(Type ownerType, FieldInfo? field)
    {
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod("T3MP_SetFloat", typeof(void), new[] { typeof(object), typeof(float) }, typeof(MechanicalDirectRotationOptimizer).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ownerType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return (FloatSetter)method.CreateDelegate(typeof(FloatSetter));
    }

    private static BoolGetter? CreateBoolGetter(Type ownerType, FieldInfo? field)
    {
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod("T3MP_GetBool", typeof(bool), new[] { typeof(object) }, typeof(MechanicalDirectRotationOptimizer).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ownerType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (BoolGetter)method.CreateDelegate(typeof(BoolGetter));
    }

    private static ObjectGetter? CreateObjectGetter(Type ownerType, FieldInfo? field)
    {
        if (field is null)
        {
            return null;
        }

        var method = new DynamicMethod("T3MP_GetObject", typeof(object), new[] { typeof(object) }, typeof(MechanicalDirectRotationOptimizer).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ownerType);
        il.Emit(OpCodes.Ldfld, field);
        if (field.FieldType.IsValueType)
        {
            il.Emit(OpCodes.Box, field.FieldType);
        }

        il.Emit(OpCodes.Ret);
        return (ObjectGetter)method.CreateDelegate(typeof(ObjectGetter));
    }

    private static string TryGetAnimationName(object animator)
    {
        try
        {
            return _animationNameProperty?.GetValue(animator) is string value ? CleanName(value) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
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

    private static float TryGetFloat(PropertyInfo? property, object instance, float fallback)
    {
        try
        {
            return property?.GetValue(instance) is float value ? value : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static bool IsMechanicalName(string name)
    {
        return name.IndexOf("Gear", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Axle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Shaft", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("WaterWheel", StringComparison.OrdinalIgnoreCase) >= 0;
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

    private sealed class DirectEntry
    {
        public static readonly DirectEntry Empty = new DirectEntry(new object(), null, new object(), 1f, 0f, false, Array.Empty<NodeRotationEntry>(), Array.Empty<int>(), 0);

        private readonly Behaviour? _behaviour;
        private readonly object _currentAnimation;
        private readonly float _length;
        private readonly bool _backwards;
        private readonly NodeRotationEntry[] _nodes;
        private readonly int _commonFrameCount;
        private float _time;

        public DirectEntry(object animator, Behaviour? behaviour, object currentAnimation, float length, float time, bool backwards, NodeRotationEntry[] nodes, int[] rotationArrayIds, int commonFrameCount)
        {
            Animator = animator;
            _behaviour = behaviour;
            _currentAnimation = currentAnimation;
            _length = length;
            _time = time;
            _backwards = backwards;
            _nodes = nodes;
            _commonFrameCount = commonFrameCount;
            RotationArrayIds = rotationArrayIds;
        }

        public object Animator { get; }
        public int NodeCount => _nodes.Length;
        public int[] RotationArrayIds { get; }

        public bool TryUpdate(float deltaTime, int frame, bool validateShape, ref long visibleWrites, ref long invisibleSkips, ref long rotationCacheLookups, ref long rotationCacheHits, ref long rotationCacheMisses)
        {
            if (_behaviour is null ||
                !_behaviour ||
                !_behaviour.isActiveAndEnabled ||
                _getEnabled?.Invoke(Animator) != true ||
                _getPlayingFinished?.Invoke(Animator) == true)
            {
                return true;
            }

            if (validateShape &&
                (_getCurrentAnimation?.Invoke(Animator) != _currentAnimation ||
                 _getLooped?.Invoke(Animator) != true))
            {
                return false;
            }

            var speed = _getSpeed?.Invoke(Animator) ?? 1f;
            if (Math.Abs(speed) <= 0.0001f)
            {
                return true;
            }

            _time += deltaTime * speed;
            var repeatedTime = Mathf.Repeat(_time, _length);
            _setTime?.Invoke(Animator, _time);
            _setRepeatedTime?.Invoke(Animator, repeatedTime);

            var normalized = Mathf.Clamp01(repeatedTime / _length);
            if (_backwards)
            {
                normalized = 1f - normalized;
            }

            if (BenchmarkSettings.EnableMechanicalDirectCommonFrameSampling &&
                _commonFrameCount > 1)
            {
                var rawFrame = normalized * _commonFrameCount;
                var fromFrame = Mathf.FloorToInt(rawFrame) % _commonFrameCount;
                var toFrame = (fromFrame + 1) % _commonFrameCount;
                var weight = rawFrame % 1f;
                for (var i = 0; i < _nodes.Length; i++)
                {
                    _nodes[i].UpdateKnownSample(fromFrame, toFrame, weight, ref visibleWrites, ref invisibleSkips, ref rotationCacheLookups, ref rotationCacheHits, ref rotationCacheMisses);
                }

                return true;
            }

            for (var i = 0; i < _nodes.Length; i++)
            {
                _nodes[i].Update(normalized, ref visibleWrites, ref invisibleSkips, ref rotationCacheLookups, ref rotationCacheHits, ref rotationCacheMisses);
            }

            return true;
        }

        public void CatchUpInvisibleTime(float deltaTime)
        {
            if (deltaTime <= 0f || deltaTime >= 5f)
            {
                return;
            }

            if (_behaviour is null ||
                !_behaviour ||
                !_behaviour.isActiveAndEnabled ||
                _getEnabled?.Invoke(Animator) != true ||
                _getPlayingFinished?.Invoke(Animator) == true)
            {
                return;
            }

            var speed = _getSpeed?.Invoke(Animator) ?? 1f;
            if (Math.Abs(speed) <= 0.0001f)
            {
                return;
            }

            _time += deltaTime * speed;
            var repeatedTime = Mathf.Repeat(_time, _length);
            _setTime?.Invoke(Animator, _time);
            _setRepeatedTime?.Invoke(Animator, repeatedTime);
        }

        public bool IsVisibleNow()
        {
            for (var i = 0; i < _nodes.Length; i++)
            {
                if (_nodes[i].IsVisible())
                {
                    return true;
                }
            }

            return false;
        }
    }

    private readonly struct NodeRotationEntry
    {
        private readonly Transform _transform;
        private readonly Renderer? _renderer;
        private readonly Vector3[]? _positions;
        private readonly Quaternion[]? _rotations;
        private readonly Vector3[]? _scales;
        private readonly int _positionArrayId;
        private readonly int _rotationArrayId;
        private readonly int _scaleArrayId;
        private readonly int _frameCount;
        private readonly bool _hasPosition;
        private readonly bool _hasRotation;
        private readonly bool _hasScale;

        public NodeRotationEntry(
            Transform transform,
            Renderer? renderer,
            Vector3[]? positions,
            Quaternion[]? rotations,
            Vector3[]? scales,
            int positionArrayId,
            int rotationArrayId,
            int scaleArrayId,
            int frameCount,
            bool hasPosition,
            bool hasRotation,
            bool hasScale)
        {
            _transform = transform;
            _renderer = renderer;
            _positions = positions;
            _rotations = rotations;
            _scales = scales;
            _positionArrayId = positionArrayId;
            _rotationArrayId = rotationArrayId;
            _scaleArrayId = scaleArrayId;
            _frameCount = frameCount;
            _hasPosition = hasPosition;
            _hasRotation = hasRotation;
            _hasScale = hasScale;
        }

        public void Update(float normalizedTime, ref long visibleWrites, ref long invisibleSkips, ref long rotationCacheLookups, ref long rotationCacheHits, ref long rotationCacheMisses)
        {
            var rawFrame = normalizedTime * _frameCount;
            var fromFrame = Mathf.FloorToInt(rawFrame) % _frameCount;
            var toFrame = (fromFrame + 1) % _frameCount;
            var weight = rawFrame % 1f;
            UpdateKnownSample(fromFrame, toFrame, weight, ref visibleWrites, ref invisibleSkips, ref rotationCacheLookups, ref rotationCacheHits, ref rotationCacheMisses);
        }

        public void UpdateKnownSample(int fromFrame, int toFrame, float weight, ref long visibleWrites, ref long invisibleSkips, ref long rotationCacheLookups, ref long rotationCacheHits, ref long rotationCacheMisses)
        {
            if (_hasPosition && _hasRotation)
            {
                _transform.SetLocalPositionAndRotation(
                    GetVectorSample(_positions, _positionArrayId, _frameCount, fromFrame, toFrame, weight),
                    GetRotationSample(_rotations!, _rotationArrayId, _frameCount, fromFrame, toFrame, weight, ref rotationCacheLookups, ref rotationCacheHits, ref rotationCacheMisses));
            }
            else if (_hasPosition)
            {
                _transform.localPosition = GetVectorSample(_positions, _positionArrayId, _frameCount, fromFrame, toFrame, weight);
            }
            else if (_hasRotation)
            {
                _transform.localRotation = GetRotationSample(_rotations!, _rotationArrayId, _frameCount, fromFrame, toFrame, weight, ref rotationCacheLookups, ref rotationCacheHits, ref rotationCacheMisses);
            }

            if (_hasScale)
            {
                _transform.localScale = GetVectorSample(_scales, _scaleArrayId, _frameCount, fromFrame, toFrame, weight);
            }

            visibleWrites++;
        }

        public bool IsVisible()
        {
            return _renderer is null || _renderer.isVisible;
        }
    }

    private readonly struct RotationSampleKey : IEquatable<RotationSampleKey>
    {
        private readonly int _rotationArrayId;
        private readonly int _frameCount;
        private readonly int _fromFrame;
        private readonly int _toFrame;
        private readonly float _weight;

        public RotationSampleKey(int rotationArrayId, int frameCount, int fromFrame, int toFrame, float weight)
        {
            _rotationArrayId = rotationArrayId;
            _frameCount = frameCount;
            _fromFrame = fromFrame;
            _toFrame = toFrame;
            _weight = weight;
        }

        public bool Equals(RotationSampleKey other)
        {
            return _rotationArrayId == other._rotationArrayId &&
                _frameCount == other._frameCount &&
                _fromFrame == other._fromFrame &&
                _toFrame == other._toFrame &&
                _weight.Equals(other._weight);
        }

        public override bool Equals(object? obj)
        {
            return obj is RotationSampleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = _rotationArrayId;
                hash = (hash * 397) ^ _frameCount;
                hash = (hash * 397) ^ _fromFrame;
                hash = (hash * 397) ^ _toFrame;
                hash = (hash * 397) ^ _weight.GetHashCode();
                return hash;
            }
        }
    }
}
