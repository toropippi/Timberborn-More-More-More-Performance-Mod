using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class AnimatorRegistryProfiler
{
    private static readonly object LockObject = new object();
    private static FieldInfo? _animatorsField;
    private static FieldInfo? _animationUpdatersField;
    private static FieldInfo? _currentAnimationField;
    private static PropertyInfo? _enabledProperty;
    private static PropertyInfo? _animationNameProperty;
    private static int _lastSnapshotFrame = -1000000;
    private static int _warningCount;

    private static readonly ModeStats[] Stats =
    {
        new ModeStats(BenchmarkMode.Vanilla),
        new ModeStats(BenchmarkMode.Optimized)
    };

    public static void Begin(object registry, BenchmarkMode mode, bool willRun, out CallState state)
    {
        state = CallState.Inactive;
        if (!BenchmarkSettings.EnableAnimatorRegistryDetailProfiler)
        {
            return;
        }

        var stats = Stats[(int)mode];
        Interlocked.Increment(ref stats.Calls);
        if (!willRun)
        {
            Interlocked.Increment(ref stats.Skipped);
            return;
        }

        Interlocked.Increment(ref stats.Runs);
        var shouldSnapshot = ShouldSnapshot();
        if (shouldSnapshot)
        {
            TryRecordSnapshot(registry, mode);
        }

        state = new CallState(true, mode, Stopwatch.GetTimestamp());
    }

    public static void End(CallState state)
    {
        if (!state.Active)
        {
            return;
        }

        var stopwatchTicks = Stopwatch.GetTimestamp() - state.StartTimestamp;
        var stats = Stats[(int)state.Mode];
        Interlocked.Add(ref stats.StopwatchTicks, stopwatchTicks);
        UpdateMax(ref stats.MaxStopwatchTicks, stopwatchTicks);
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableAnimatorRegistryDetailProfiler)
        {
            return;
        }

        LogMode(aggregateId, Stats[(int)BenchmarkMode.Vanilla].SnapshotAndReset());
        LogMode(aggregateId, Stats[(int)BenchmarkMode.Optimized].SnapshotAndReset());
    }

    public static void Reset()
    {
        Stats[(int)BenchmarkMode.Vanilla].SnapshotAndReset();
        Stats[(int)BenchmarkMode.Optimized].SnapshotAndReset();
    }

    private static bool ShouldSnapshot()
    {
        var frame = Time.frameCount;
        if (frame - Volatile.Read(ref _lastSnapshotFrame) < BenchmarkSettings.AnimatorRegistryDetailSampleFrames)
        {
            return false;
        }

        return Interlocked.Exchange(ref _lastSnapshotFrame, frame) != frame;
    }

    private static void TryRecordSnapshot(object registry, BenchmarkMode mode)
    {
        try
        {
            EnsureMembers(registry.GetType());
            if (_animatorsField?.GetValue(registry) is not IEnumerable animators)
            {
                return;
            }

            var snapshot = Snapshot.Create(animators);
            lock (LockObject)
            {
                Stats[(int)mode].RecordSnapshot(snapshot);
            }
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] AnimatorRegistryDetail snapshot failed: {exception}");
            }
        }
    }

    private static void EnsureMembers(Type registryType)
    {
        if (_animatorsField is not null)
        {
            return;
        }

        _animatorsField = registryType.GetField("_animators", BindingFlags.Instance | BindingFlags.NonPublic);
        var animatorType = registryType.Assembly.GetType("Timberborn.TimbermeshAnimations.TimbermeshAnimator");
        if (animatorType is null)
        {
            return;
        }

        _animationUpdatersField = animatorType.GetField("_animationUpdaters", BindingFlags.Instance | BindingFlags.NonPublic);
        _currentAnimationField = animatorType.GetField("_currentAnimation", BindingFlags.Instance | BindingFlags.NonPublic);
        _enabledProperty = animatorType.GetProperty("Enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _animationNameProperty = animatorType.GetProperty("AnimationName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static Snapshot InspectAnimator(object animator)
    {
        var enabled = false;
        try
        {
            enabled = _enabledProperty?.GetValue(animator) is bool value && value;
        }
        catch (Exception)
        {
            // Keep the profiler diagnostic-only.
        }

        var updaterSlots = 0;
        var nodeSlots = 0;
        var vertexSlots = 0;
        var otherSlots = 0;
        try
        {
            if (_animationUpdatersField?.GetValue(animator) is IEnumerable updaters)
            {
                foreach (var updater in updaters)
                {
                    if (updater is null)
                    {
                        continue;
                    }

                    updaterSlots++;
                    var typeName = updater.GetType().Name;
                    if (typeName == "NodeAnimationUpdater")
                    {
                        nodeSlots++;
                    }
                    else if (typeName == "VertexAnimationUpdater")
                    {
                        vertexSlots++;
                    }
                    else
                    {
                        otherSlots++;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Keep the profiler diagnostic-only.
        }

        var objectName = "<unknown>";
        var rootName = "<unknown>";
        if (animator is Component component)
        {
            objectName = CleanName(component.gameObject.name);
            rootName = CleanName(component.transform.root.gameObject.name);
        }

        var animationName = TryGetAnimationName(animator);
        return new Snapshot(
            totalAnimators: 1,
            enabledAnimators: enabled ? 1 : 0,
            updaterSlots: updaterSlots,
            nodeSlots: nodeSlots,
            vertexSlots: vertexSlots,
            otherSlots: otherSlots,
            animatorsWithNode: nodeSlots > 0 ? 1 : 0,
            animatorsWithVertex: vertexSlots > 0 ? 1 : 0,
            objectCounts: new Dictionary<string, int>(StringComparer.Ordinal) { [objectName] = 1 },
            rootCounts: new Dictionary<string, int>(StringComparer.Ordinal) { [rootName] = 1 },
            animationCounts: animationName.Length == 0
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(StringComparer.Ordinal) { [animationName] = 1 });
    }

    private static string TryGetAnimationName(object animator)
    {
        try
        {
            if (_animationNameProperty?.GetValue(animator) is string propertyName && propertyName.Length > 0)
            {
                return CleanName(propertyName);
            }

            var currentAnimation = _currentAnimationField?.GetValue(animator);
            if (currentAnimation is null)
            {
                return string.Empty;
            }

            var nameProperty = currentAnimation.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return nameProperty?.GetValue(currentAnimation) is string fieldName ? CleanName(fieldName) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void LogMode(long aggregateId, ModeSnapshot snapshot)
    {
        if (snapshot.Calls == 0 && snapshot.Runs == 0 && snapshot.Skipped == 0)
        {
            return;
        }

        var totalMilliseconds = ToMilliseconds(snapshot.StopwatchTicks);
        var millisecondsPerFrame = snapshot.Calls > 0 ? totalMilliseconds / snapshot.Calls : 0.0;
        var millisecondsPerRun = snapshot.Runs > 0 ? totalMilliseconds / snapshot.Runs : 0.0;
        var skipRate = snapshot.Calls > 0 ? (double)snapshot.Skipped / snapshot.Calls : 0.0;
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] AnimatorRegistryDetail mode={0}, aggregate={1}, calls={2}, runs={3}, skipped={4}, skipRate={5:F3}, ms={6:F2}, msPerFrame={7:F3}, msPerRun={8:F3}, maxMs={9:F3}, snapshots={10}, animatorsLast={11}, enabledLast={12}, disabledLast={13}, updaterSlotsLast={14}, nodeSlotsLast={15}, vertexSlotsLast={16}, otherSlotsLast={17}, animatorsWithNodeLast={18}, animatorsWithVertexLast={19}, topObjects={20}, topRoots={21}, topAnimations={22}",
            snapshot.Mode,
            aggregateId,
            snapshot.Calls,
            snapshot.Runs,
            snapshot.Skipped,
            skipRate,
            totalMilliseconds,
            millisecondsPerFrame,
            millisecondsPerRun,
            ToMilliseconds(snapshot.MaxStopwatchTicks),
            snapshot.Snapshots,
            snapshot.Last.TotalAnimators,
            snapshot.Last.EnabledAnimators,
            snapshot.Last.DisabledAnimators,
            snapshot.Last.UpdaterSlots,
            snapshot.Last.NodeSlots,
            snapshot.Last.VertexSlots,
            snapshot.Last.OtherSlots,
            snapshot.Last.AnimatorsWithNode,
            snapshot.Last.AnimatorsWithVertex,
            FormatTop(snapshot.Last.ObjectCounts),
            FormatTop(snapshot.Last.RootCounts),
            FormatTop(snapshot.Last.AnimationCounts)));
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
        public static readonly CallState Inactive = new CallState(false, BenchmarkMode.Vanilla, 0);

        public CallState(bool active, BenchmarkMode mode, long startTimestamp)
        {
            Active = active;
            Mode = mode;
            StartTimestamp = startTimestamp;
        }

        public bool Active { get; }
        public BenchmarkMode Mode { get; }
        public long StartTimestamp { get; }
    }

    private sealed class ModeStats
    {
        private Snapshot _lastSnapshot = Snapshot.Empty;

        public ModeStats(BenchmarkMode mode)
        {
            Mode = mode;
        }

        public BenchmarkMode Mode { get; }
        public long Calls;
        public long Runs;
        public long Skipped;
        public long StopwatchTicks;
        public long MaxStopwatchTicks;
        public long Snapshots;

        public void RecordSnapshot(Snapshot snapshot)
        {
            _lastSnapshot = snapshot;
            Snapshots++;
        }

        public ModeSnapshot SnapshotAndReset()
        {
            lock (LockObject)
            {
                var snapshot = new ModeSnapshot(
                    Mode,
                    Interlocked.Exchange(ref Calls, 0),
                    Interlocked.Exchange(ref Runs, 0),
                    Interlocked.Exchange(ref Skipped, 0),
                    Interlocked.Exchange(ref StopwatchTicks, 0),
                    Interlocked.Exchange(ref MaxStopwatchTicks, 0),
                    Interlocked.Exchange(ref Snapshots, 0),
                    _lastSnapshot);
                _lastSnapshot = Snapshot.Empty;
                return snapshot;
            }
        }
    }

    private readonly struct ModeSnapshot
    {
        public ModeSnapshot(
            BenchmarkMode mode,
            long calls,
            long runs,
            long skipped,
            long stopwatchTicks,
            long maxStopwatchTicks,
            long snapshots,
            Snapshot last)
        {
            Mode = mode;
            Calls = calls;
            Runs = runs;
            Skipped = skipped;
            StopwatchTicks = stopwatchTicks;
            MaxStopwatchTicks = maxStopwatchTicks;
            Snapshots = snapshots;
            Last = last;
        }

        public BenchmarkMode Mode { get; }
        public long Calls { get; }
        public long Runs { get; }
        public long Skipped { get; }
        public long StopwatchTicks { get; }
        public long MaxStopwatchTicks { get; }
        public long Snapshots { get; }
        public Snapshot Last { get; }
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
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal));

        public Snapshot(
            int totalAnimators,
            int enabledAnimators,
            int updaterSlots,
            int nodeSlots,
            int vertexSlots,
            int otherSlots,
            int animatorsWithNode,
            int animatorsWithVertex,
            Dictionary<string, int> objectCounts,
            Dictionary<string, int> rootCounts,
            Dictionary<string, int> animationCounts)
        {
            TotalAnimators = totalAnimators;
            EnabledAnimators = enabledAnimators;
            UpdaterSlots = updaterSlots;
            NodeSlots = nodeSlots;
            VertexSlots = vertexSlots;
            OtherSlots = otherSlots;
            AnimatorsWithNode = animatorsWithNode;
            AnimatorsWithVertex = animatorsWithVertex;
            ObjectCounts = objectCounts;
            RootCounts = rootCounts;
            AnimationCounts = animationCounts;
        }

        public int TotalAnimators { get; }
        public int EnabledAnimators { get; }
        public int DisabledAnimators => TotalAnimators - EnabledAnimators;
        public int UpdaterSlots { get; }
        public int NodeSlots { get; }
        public int VertexSlots { get; }
        public int OtherSlots { get; }
        public int AnimatorsWithNode { get; }
        public int AnimatorsWithVertex { get; }
        public Dictionary<string, int> ObjectCounts { get; }
        public Dictionary<string, int> RootCounts { get; }
        public Dictionary<string, int> AnimationCounts { get; }

        public static Snapshot Create(IEnumerable animators)
        {
            var result = Empty.MutableCopy();
            foreach (var animator in animators)
            {
                if (animator is null)
                {
                    continue;
                }

                result.Add(InspectAnimator(animator));
            }

            return result.ToSnapshot();
        }

        private MutableSnapshot MutableCopy()
        {
            return new MutableSnapshot();
        }
    }

    private sealed class MutableSnapshot
    {
        private readonly Dictionary<string, int> _objectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _rootCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _animationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        private int _totalAnimators;
        private int _enabledAnimators;
        private int _updaterSlots;
        private int _nodeSlots;
        private int _vertexSlots;
        private int _otherSlots;
        private int _animatorsWithNode;
        private int _animatorsWithVertex;

        public void Add(Snapshot snapshot)
        {
            _totalAnimators += snapshot.TotalAnimators;
            _enabledAnimators += snapshot.EnabledAnimators;
            _updaterSlots += snapshot.UpdaterSlots;
            _nodeSlots += snapshot.NodeSlots;
            _vertexSlots += snapshot.VertexSlots;
            _otherSlots += snapshot.OtherSlots;
            _animatorsWithNode += snapshot.AnimatorsWithNode;
            _animatorsWithVertex += snapshot.AnimatorsWithVertex;
            AddCounts(_objectCounts, snapshot.ObjectCounts);
            AddCounts(_rootCounts, snapshot.RootCounts);
            AddCounts(_animationCounts, snapshot.AnimationCounts);
        }

        public Snapshot ToSnapshot()
        {
            return new Snapshot(
                _totalAnimators,
                _enabledAnimators,
                _updaterSlots,
                _nodeSlots,
                _vertexSlots,
                _otherSlots,
                _animatorsWithNode,
                _animatorsWithVertex,
                new Dictionary<string, int>(_objectCounts, StringComparer.Ordinal),
                new Dictionary<string, int>(_rootCounts, StringComparer.Ordinal),
                new Dictionary<string, int>(_animationCounts, StringComparer.Ordinal));
        }

        private static void AddCounts(Dictionary<string, int> target, IReadOnlyDictionary<string, int> source)
        {
            foreach (var pair in source)
            {
                target.TryGetValue(pair.Key, out var current);
                target[pair.Key] = current + pair.Value;
            }
        }
    }
}
