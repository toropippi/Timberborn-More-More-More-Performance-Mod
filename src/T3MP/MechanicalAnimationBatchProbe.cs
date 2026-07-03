using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class MechanicalAnimationBatchProbe
{
    private static readonly object LockObject = new object();

    private static FieldInfo? _registryAnimatorsField;
    private static FieldInfo? _animationUpdatersField;
    private static FieldInfo? _currentAnimationMetadataField;
    private static FieldInfo? _speedField;
    private static FieldInfo? _loopedField;
    private static FieldInfo? _playBackwardsField;
    private static FieldInfo? _nodeCurrentAnimationField;
    private static FieldInfo? _nodeSelfTransformField;
    private static FieldInfo? _nodeRotationsField;
    private static FieldInfo? _vertexCurrentAnimationField;
    private static PropertyInfo? _animationNameProperty;
    private static PropertyInfo? _animationLengthProperty;
    private static PropertyInfo? _enabledProperty;
    private static PropertyInfo? _playingFinishedProperty;
    private static PropertyInfo? _metadataNameProperty;
    private static PropertyInfo? _metadataLengthProperty;
    private static PropertyInfo? _nodeFrameCountProperty;
    private static PropertyInfo? _nodeHasDifferentPositionsProperty;
    private static PropertyInfo? _nodeHasDifferentRotationsProperty;
    private static PropertyInfo? _nodeHasDifferentScalesProperty;
    private static bool _membersInitialized;
    private static int _lastSampleFrame = -1000000;
    private static int _warningCount;

    private static long _snapshots;
    private static long _snapshotStopwatchTicks;
    private static long _snapshotMaxStopwatchTicks;
    private static Snapshot _lastSnapshot = Snapshot.Empty;

    public static void MaybeSample(object registry, BenchmarkMode mode)
    {
        if (!BenchmarkSettings.EnableMechanicalAnimationBatchProbe ||
            mode != BenchmarkMode.Optimized)
        {
            return;
        }

        var frame = Time.frameCount;
        if (frame - Volatile.Read(ref _lastSampleFrame) < BenchmarkSettings.MechanicalAnimationBatchProbeSampleFrames)
        {
            return;
        }

        Volatile.Write(ref _lastSampleFrame, frame);
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var snapshot = CaptureSnapshot(registry);
            var ticks = Stopwatch.GetTimestamp() - startTimestamp;
            lock (LockObject)
            {
                _lastSnapshot = snapshot;
            }

            Interlocked.Increment(ref _snapshots);
            Interlocked.Add(ref _snapshotStopwatchTicks, ticks);
            UpdateMax(ref _snapshotMaxStopwatchTicks, ticks);
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] MechanicalAnimationBatchProbe snapshot failed: {exception}");
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableMechanicalAnimationBatchProbe)
        {
            return;
        }

        var snapshots = Interlocked.Exchange(ref _snapshots, 0);
        var snapshotTicks = Interlocked.Exchange(ref _snapshotStopwatchTicks, 0);
        var snapshotMaxTicks = Interlocked.Exchange(ref _snapshotMaxStopwatchTicks, 0);
        if (snapshots == 0)
        {
            return;
        }

        Snapshot snapshot;
        lock (LockObject)
        {
            snapshot = _lastSnapshot;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] MechanicalBatchProbe aggregate={0}, snapshots={1}, snapshotMs={2:F2}, snapshotAvgMs={3:F3}, snapshotMaxMs={4:F3}, animators={5}, activeAnimators={6}, defaultMechanical={7}, eligibleDirectRotation={8}, nodeTransforms={9}, uniqueNodeAnimations={10}, uniqueRotationArrays={11}, batchKeys={12}, speedGroups={13}, lengthGroups={14}, frameCountGroups={15}, looped={16}, backwards={17}, rendererVisible={18}, rendererInvisible={19}, rendererMissing={20}, multiNode={21}, vertexAnimator={22}, positionOrScale={23}, unknownShape={24}, topBatchKeys={25}, topRoots={26}, topObjects={27}",
            aggregateId,
            snapshots,
            ToMilliseconds(snapshotTicks),
            snapshots > 0 ? ToMilliseconds(snapshotTicks) / snapshots : 0.0,
            ToMilliseconds(snapshotMaxTicks),
            snapshot.AnimatorCount,
            snapshot.ActiveAnimatorCount,
            snapshot.DefaultMechanicalCount,
            snapshot.EligibleDirectRotationCount,
            snapshot.NodeTransformCount,
            snapshot.UniqueNodeAnimationCount,
            snapshot.UniqueRotationArrayCount,
            snapshot.BatchKeyCount,
            snapshot.SpeedGroupCount,
            snapshot.LengthGroupCount,
            snapshot.FrameCountGroupCount,
            snapshot.LoopedCount,
            snapshot.BackwardsCount,
            snapshot.RendererVisibleCount,
            snapshot.RendererInvisibleCount,
            snapshot.RendererMissingCount,
            snapshot.MultiNodeAnimatorCount,
            snapshot.VertexAnimatorCount,
            snapshot.PositionOrScaleAnimatorCount,
            snapshot.UnknownShapeCount,
            FormatTop(snapshot.BatchKeyCounts),
            FormatTop(snapshot.RootCounts),
            FormatTop(snapshot.ObjectCounts)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _snapshots, 0);
        Interlocked.Exchange(ref _snapshotStopwatchTicks, 0);
        Interlocked.Exchange(ref _snapshotMaxStopwatchTicks, 0);
        Volatile.Write(ref _lastSampleFrame, -1000000);
    }

    private static Snapshot CaptureSnapshot(object registry)
    {
        if (!EnsureMembers(registry.GetType()) ||
            _registryAnimatorsField?.GetValue(registry) is not IEnumerable animators)
        {
            return Snapshot.Empty;
        }

        var animatorCount = 0;
        var activeAnimatorCount = 0;
        var defaultMechanicalCount = 0;
        var eligibleDirectRotationCount = 0;
        var nodeTransformCount = 0;
        var loopedCount = 0;
        var backwardsCount = 0;
        var rendererVisibleCount = 0;
        var rendererInvisibleCount = 0;
        var rendererMissingCount = 0;
        var multiNodeAnimatorCount = 0;
        var vertexAnimatorCount = 0;
        var positionOrScaleAnimatorCount = 0;
        var unknownShapeCount = 0;
        var uniqueNodeAnimations = new HashSet<int>();
        var uniqueRotationArrays = new HashSet<int>();
        var uniqueSpeeds = new HashSet<string>(StringComparer.Ordinal);
        var uniqueLengths = new HashSet<string>(StringComparer.Ordinal);
        var uniqueFrameCounts = new HashSet<int>();
        var batchKeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rootCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var objectCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var animator in animators)
        {
            if (animator is null)
            {
                continue;
            }

            animatorCount++;
            var component = animator as Component;
            if (component is Behaviour behaviour && behaviour.isActiveAndEnabled)
            {
                activeAnimatorCount++;
            }

            var objectName = component is not null ? CleanName(component.gameObject.name) : "<unknown>";
            var rootName = component is not null ? CleanName(component.transform.root.gameObject.name) : "<unknown>";
            var animationName = TryGetString(_animationNameProperty, animator);
            if (!string.Equals(animationName, "Default", StringComparison.Ordinal) ||
                !IsMechanicalName(objectName) && !IsMechanicalName(rootName))
            {
                continue;
            }

            defaultMechanicalCount++;
            AddCount(rootCounts, rootName);
            AddCount(objectCounts, objectName);

            var activeForAnimation = IsActiveForAnimation(animator, component);
            var updaters = _animationUpdatersField?.GetValue(animator) as IEnumerable;
            if (updaters is null)
            {
                unknownShapeCount++;
                continue;
            }

            var nodeUpdaters = 0;
            var rotationOnlyNodes = 0;
            var positionOrScaleNodes = 0;
            var vertexUpdaters = 0;
            var unknownNodes = 0;
            var firstNodeAnimationId = 0;
            var firstRotationArrayId = 0;
            var firstFrameCount = 0;
            var firstTransformVisibility = VisibilityState.MissingRenderer;

            foreach (var updater in updaters)
            {
                if (updater is null)
                {
                    continue;
                }

                var typeName = updater.GetType().Name;
                if (typeName == "NodeAnimationUpdater")
                {
                    nodeUpdaters++;
                    var nodeAnimation = _nodeCurrentAnimationField?.GetValue(updater);
                    if (nodeAnimation is null)
                    {
                        unknownNodes++;
                        continue;
                    }

                    var frameCount = TryGetInt(_nodeFrameCountProperty, nodeAnimation);
                    var hasPosition = TryGetBool(_nodeHasDifferentPositionsProperty, nodeAnimation);
                    var hasRotation = TryGetBool(_nodeHasDifferentRotationsProperty, nodeAnimation);
                    var hasScale = TryGetBool(_nodeHasDifferentScalesProperty, nodeAnimation);
                    var nodeAnimationId = RuntimeHelpers.GetHashCode(nodeAnimation);
                    var rotations = _nodeRotationsField?.GetValue(nodeAnimation);
                    var rotationArrayId = rotations is null ? 0 : RuntimeHelpers.GetHashCode(rotations);

                    uniqueNodeAnimations.Add(nodeAnimationId);
                    if (rotationArrayId != 0)
                    {
                        uniqueRotationArrays.Add(rotationArrayId);
                    }

                    uniqueFrameCounts.Add(frameCount);
                    nodeTransformCount++;
                    firstNodeAnimationId = firstNodeAnimationId == 0 ? nodeAnimationId : firstNodeAnimationId;
                    firstRotationArrayId = firstRotationArrayId == 0 ? rotationArrayId : firstRotationArrayId;
                    firstFrameCount = firstFrameCount == 0 ? frameCount : firstFrameCount;
                    firstTransformVisibility = CountRendererVisibility(updater);
                    if (hasRotation && !hasPosition && !hasScale)
                    {
                        rotationOnlyNodes++;
                    }
                    else if (hasPosition || hasScale)
                    {
                        positionOrScaleNodes++;
                    }
                    else
                    {
                        unknownNodes++;
                    }
                }
                else if (typeName == "VertexAnimationUpdater")
                {
                    vertexUpdaters++;
                    if (_vertexCurrentAnimationField?.GetValue(updater) is null)
                    {
                        unknownNodes++;
                    }
                }
            }

            if (nodeUpdaters > 1)
            {
                multiNodeAnimatorCount++;
            }

            if (vertexUpdaters > 0)
            {
                vertexAnimatorCount++;
            }

            if (positionOrScaleNodes > 0)
            {
                positionOrScaleAnimatorCount++;
            }

            if (unknownNodes > 0)
            {
                unknownShapeCount++;
            }

            if (firstTransformVisibility == VisibilityState.Visible)
            {
                rendererVisibleCount++;
            }
            else if (firstTransformVisibility == VisibilityState.Invisible)
            {
                rendererInvisibleCount++;
            }
            else
            {
                rendererMissingCount++;
            }

            var speed = TryGetFloat(_speedField, animator, 1f);
            var length = TryGetFloat(_animationLengthProperty, animator, TryGetMetadataLength(animator));
            var looped = TryGetBool(_loopedField, animator);
            var backwards = TryGetBool(_playBackwardsField, animator);
            if (looped)
            {
                loopedCount++;
            }

            if (backwards)
            {
                backwardsCount++;
            }

            var speedKey = speed.ToString("0.###", CultureInfo.InvariantCulture);
            var lengthKey = length.ToString("0.###", CultureInfo.InvariantCulture);
            uniqueSpeeds.Add(speedKey);
            uniqueLengths.Add(lengthKey);

            var eligible = activeForAnimation &&
                nodeUpdaters > 0 &&
                rotationOnlyNodes == nodeUpdaters &&
                vertexUpdaters == 0 &&
                positionOrScaleNodes == 0 &&
                unknownNodes == 0;
            if (!eligible)
            {
                continue;
            }

            eligibleDirectRotationCount++;
            var batchKey = string.Concat(
                "anim=", firstNodeAnimationId,
                "/rot=", firstRotationArrayId,
                "/frames=", firstFrameCount,
                "/len=", lengthKey,
                "/speed=", speedKey,
                "/loop=", looped ? "1" : "0",
                "/back=", backwards ? "1" : "0");
            AddCount(batchKeyCounts, batchKey);
        }

        return new Snapshot(
            animatorCount,
            activeAnimatorCount,
            defaultMechanicalCount,
            eligibleDirectRotationCount,
            nodeTransformCount,
            uniqueNodeAnimations.Count,
            uniqueRotationArrays.Count,
            batchKeyCounts.Count,
            uniqueSpeeds.Count,
            uniqueLengths.Count,
            uniqueFrameCounts.Count,
            loopedCount,
            backwardsCount,
            rendererVisibleCount,
            rendererInvisibleCount,
            rendererMissingCount,
            multiNodeAnimatorCount,
            vertexAnimatorCount,
            positionOrScaleAnimatorCount,
            unknownShapeCount,
            batchKeyCounts,
            rootCounts,
            objectCounts);
    }

    private static bool EnsureMembers(Type registryType)
    {
        if (_membersInitialized)
        {
            return _registryAnimatorsField is not null;
        }

        lock (LockObject)
        {
            if (_membersInitialized)
            {
                return _registryAnimatorsField is not null;
            }

            _registryAnimatorsField = registryType.GetField("_animators", BindingFlags.Instance | BindingFlags.NonPublic);
            var animatorType = _registryAnimatorsField?.FieldType.IsGenericType == true
                ? _registryAnimatorsField.FieldType.GetGenericArguments().FirstOrDefault()
                : null;
            if (animatorType is not null)
            {
                _animationUpdatersField = animatorType.GetField("_animationUpdaters", BindingFlags.Instance | BindingFlags.NonPublic);
                _currentAnimationMetadataField = animatorType.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
                _speedField = animatorType.GetField("_speed", BindingFlags.Instance | BindingFlags.NonPublic);
                _loopedField = animatorType.GetField("_looped", BindingFlags.Instance | BindingFlags.NonPublic);
                _playBackwardsField = animatorType.GetField("_playBackwards", BindingFlags.Instance | BindingFlags.NonPublic);
                _animationNameProperty = animatorType.GetProperty("AnimationName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _animationLengthProperty = animatorType.GetProperty("AnimationLength", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _enabledProperty = animatorType.GetProperty("Enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _playingFinishedProperty = animatorType.GetProperty("PlayingFinished", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            var assembly = registryType.Assembly;
            var animationMetadataType = assembly.GetType("Timberborn.TimbermeshAnimations.AnimationMetadata");
            _metadataNameProperty = animationMetadataType?.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _metadataLengthProperty = animationMetadataType?.GetProperty("Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var nodeUpdaterType = assembly.GetType("Timberborn.TimbermeshAnimations.NodeAnimationUpdater");
            _nodeCurrentAnimationField = nodeUpdaterType?.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
            _nodeSelfTransformField = nodeUpdaterType?.GetField("_selfTransform", BindingFlags.Instance | BindingFlags.NonPublic);

            var vertexUpdaterType = assembly.GetType("Timberborn.TimbermeshAnimations.VertexAnimationUpdater");
            _vertexCurrentAnimationField = vertexUpdaterType?.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);

            var nodeAnimationType = assembly.GetType("Timberborn.TimbermeshAnimations.NodeAnimation");
            _nodeRotationsField = nodeAnimationType?.GetField("_rotations", BindingFlags.Instance | BindingFlags.NonPublic);
            _nodeFrameCountProperty = nodeAnimationType?.GetProperty("FrameCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _nodeHasDifferentPositionsProperty = nodeAnimationType?.GetProperty("HasDifferentPositions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _nodeHasDifferentRotationsProperty = nodeAnimationType?.GetProperty("HasDifferentRotations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _nodeHasDifferentScalesProperty = nodeAnimationType?.GetProperty("HasDifferentScales", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _membersInitialized = true;
            return _registryAnimatorsField is not null;
        }
    }

    private static bool IsActiveForAnimation(object animator, Component? component)
    {
        if (component is not Behaviour behaviour || !behaviour.isActiveAndEnabled)
        {
            return false;
        }

        if (!TryGetBool(_enabledProperty, animator))
        {
            return false;
        }

        if (Math.Abs(TryGetFloat(_speedField, animator, 1f)) <= 0.0001f)
        {
            return false;
        }

        return !TryGetBool(_playingFinishedProperty, animator);
    }

    private static VisibilityState CountRendererVisibility(object updater)
    {
        var transform = _nodeSelfTransformField?.GetValue(updater) as Transform;
        var component = updater as Component;
        var renderer = transform is not null
            ? transform.GetComponent<Renderer>()
            : component?.GetComponent<Renderer>();
        if (renderer is null)
        {
            return VisibilityState.MissingRenderer;
        }

        return renderer.isVisible ? VisibilityState.Visible : VisibilityState.Invisible;
    }

    private static string TryGetString(PropertyInfo? property, object instance)
    {
        try
        {
            return property?.GetValue(instance) is string value ? CleanName(value) : string.Empty;
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

    private static bool TryGetBool(FieldInfo? field, object instance)
    {
        try
        {
            return field?.GetValue(instance) is bool value && value;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static float TryGetFloat(FieldInfo? field, object instance, float fallback)
    {
        try
        {
            return field?.GetValue(instance) is float value ? value : fallback;
        }
        catch (Exception)
        {
            return fallback;
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

    private static float TryGetMetadataLength(object animator)
    {
        try
        {
            var metadata = _currentAnimationMetadataField?.GetValue(animator);
            return metadata is not null && _metadataLengthProperty?.GetValue(metadata) is float value ? value : 0f;
        }
        catch (Exception)
        {
            return 0f;
        }
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
            .Take(BenchmarkSettings.MechanicalAnimationBatchProbeTopEntries)
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

    private static void AddCount(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var current);
        counts[key] = current + 1;
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

    private enum VisibilityState
    {
        MissingRenderer,
        Visible,
        Invisible
    }

    private readonly struct Snapshot
    {
        public static readonly Snapshot Empty = new Snapshot(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal));

        public Snapshot(
            int animatorCount,
            int activeAnimatorCount,
            int defaultMechanicalCount,
            int eligibleDirectRotationCount,
            int nodeTransformCount,
            int uniqueNodeAnimationCount,
            int uniqueRotationArrayCount,
            int batchKeyCount,
            int speedGroupCount,
            int lengthGroupCount,
            int frameCountGroupCount,
            int loopedCount,
            int backwardsCount,
            int rendererVisibleCount,
            int rendererInvisibleCount,
            int rendererMissingCount,
            int multiNodeAnimatorCount,
            int vertexAnimatorCount,
            int positionOrScaleAnimatorCount,
            int unknownShapeCount,
            Dictionary<string, int> batchKeyCounts,
            Dictionary<string, int> rootCounts,
            Dictionary<string, int> objectCounts)
        {
            AnimatorCount = animatorCount;
            ActiveAnimatorCount = activeAnimatorCount;
            DefaultMechanicalCount = defaultMechanicalCount;
            EligibleDirectRotationCount = eligibleDirectRotationCount;
            NodeTransformCount = nodeTransformCount;
            UniqueNodeAnimationCount = uniqueNodeAnimationCount;
            UniqueRotationArrayCount = uniqueRotationArrayCount;
            BatchKeyCount = batchKeyCount;
            SpeedGroupCount = speedGroupCount;
            LengthGroupCount = lengthGroupCount;
            FrameCountGroupCount = frameCountGroupCount;
            LoopedCount = loopedCount;
            BackwardsCount = backwardsCount;
            RendererVisibleCount = rendererVisibleCount;
            RendererInvisibleCount = rendererInvisibleCount;
            RendererMissingCount = rendererMissingCount;
            MultiNodeAnimatorCount = multiNodeAnimatorCount;
            VertexAnimatorCount = vertexAnimatorCount;
            PositionOrScaleAnimatorCount = positionOrScaleAnimatorCount;
            UnknownShapeCount = unknownShapeCount;
            BatchKeyCounts = batchKeyCounts;
            RootCounts = rootCounts;
            ObjectCounts = objectCounts;
        }

        public int AnimatorCount { get; }
        public int ActiveAnimatorCount { get; }
        public int DefaultMechanicalCount { get; }
        public int EligibleDirectRotationCount { get; }
        public int NodeTransformCount { get; }
        public int UniqueNodeAnimationCount { get; }
        public int UniqueRotationArrayCount { get; }
        public int BatchKeyCount { get; }
        public int SpeedGroupCount { get; }
        public int LengthGroupCount { get; }
        public int FrameCountGroupCount { get; }
        public int LoopedCount { get; }
        public int BackwardsCount { get; }
        public int RendererVisibleCount { get; }
        public int RendererInvisibleCount { get; }
        public int RendererMissingCount { get; }
        public int MultiNodeAnimatorCount { get; }
        public int VertexAnimatorCount { get; }
        public int PositionOrScaleAnimatorCount { get; }
        public int UnknownShapeCount { get; }
        public Dictionary<string, int> BatchKeyCounts { get; }
        public Dictionary<string, int> RootCounts { get; }
        public Dictionary<string, int> ObjectCounts { get; }
    }
}
