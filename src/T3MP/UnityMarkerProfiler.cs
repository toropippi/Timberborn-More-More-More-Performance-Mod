using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine.Profiling;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class UnityMarkerProfiler
{
    private static readonly object LockObject = new object();

    private static readonly string[] MarkerNames =
    {
        "PlayerLoop",
        "Initialization.PlayerUpdateTime",
        "EarlyUpdate.ScriptRunDelayedStartupFrame",
        "FixedUpdate.ScriptRunBehaviourFixedUpdate",
        "Update.ScriptRunBehaviourUpdate",
        "PreLateUpdate.ScriptRunBehaviourLateUpdate",
        "PostLateUpdate.PlayerSendFrameStarted",
        "PostLateUpdate.PlayerSendFrameComplete",
        "PostLateUpdate.UpdateResolution",
        "Camera.Render",
        "RenderLoop.Draw",
        "BatchRenderer.Flush",
        "Canvas.BuildBatch",
        "UIElements.UpdatePanels",
        "Animation.Update",
        "DirectorUpdateAnimationBegin",
        "Physics.Simulate",
        "Loading.UpdatePreloading",
        "AsyncUploadManager.AsyncBufferResized",
        "AsyncReadManager.Update",
        "GC.Collect",
        "GarbageCollectAssetsProfile",
        "WaitForTargetFPS",
        "Gfx.WaitForPresentOnGfxThread",
        "Gfx.PresentFrame",
        "Semaphore.WaitForSignal"
    };

    private static readonly List<MarkerRecorder> Recorders = new List<MarkerRecorder>();
    private static readonly Dictionary<StatsKey, MarkerStats> StatsByKey = new Dictionary<StatsKey, MarkerStats>();
    private static readonly long[] SampleFramesByMode = new long[2];
    private static bool _initialized;

    public static void RecordFrame(BenchmarkMode mode)
    {
        if (!BenchmarkSettings.EnableUnityMarkerProfiler)
        {
            return;
        }

        EnsureInitialized();
        if (Recorders.Count == 0)
        {
            return;
        }

        lock (LockObject)
        {
            SampleFramesByMode[(int)mode]++;
            foreach (var marker in Recorders)
            {
                long elapsedNanoseconds;
                int sampleBlockCount;
                try
                {
                    elapsedNanoseconds = marker.Recorder.elapsedNanoseconds;
                    sampleBlockCount = marker.Recorder.sampleBlockCount;
                }
                catch (Exception)
                {
                    continue;
                }

                if (elapsedNanoseconds <= 0 && sampleBlockCount <= 0)
                {
                    continue;
                }

                var key = new StatsKey(mode, marker.Name);
                if (!StatsByKey.TryGetValue(key, out var stats))
                {
                    stats = new MarkerStats(mode, marker.Name);
                    StatsByKey.Add(key, stats);
                }

                stats.Record(elapsedNanoseconds, sampleBlockCount);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableUnityMarkerProfiler)
        {
            return;
        }

        EnsureInitialized();
        Dictionary<StatsKey, MarkerSnapshot> snapshots;
        long vanillaFrames;
        long optimizedFrames;
        lock (LockObject)
        {
            snapshots = StatsByKey.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot());
            StatsByKey.Clear();
            vanillaFrames = SampleFramesByMode[(int)BenchmarkMode.Vanilla];
            optimizedFrames = SampleFramesByMode[(int)BenchmarkMode.Optimized];
            SampleFramesByMode[(int)BenchmarkMode.Vanilla] = 0;
            SampleFramesByMode[(int)BenchmarkMode.Optimized] = 0;
        }

        LogMode(aggregateId, BenchmarkMode.Vanilla, vanillaFrames, snapshots.Values);
        LogMode(aggregateId, BenchmarkMode.Optimized, optimizedFrames, snapshots.Values);
    }

    public static void Reset()
    {
        lock (LockObject)
        {
            StatsByKey.Clear();
            SampleFramesByMode[(int)BenchmarkMode.Vanilla] = 0;
            SampleFramesByMode[(int)BenchmarkMode.Optimized] = 0;
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (LockObject)
        {
            if (_initialized)
            {
                return;
            }

            foreach (var name in MarkerNames)
            {
                try
                {
                    var recorder = Recorder.Get(name);
                    if (!recorder.isValid)
                    {
                        continue;
                    }

                    recorder.CollectFromAllThreads();
                    recorder.enabled = true;
                    Recorders.Add(new MarkerRecorder(name, recorder));
                }
                catch (Exception)
                {
                    // Marker names vary across Unity versions. Invalid markers are not useful for this probe.
                }
            }

            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] UnityMarkerProfiler initialized. enabled={0}, validMarkers={1}",
                BenchmarkSettings.EnableUnityMarkerProfiler,
                Recorders.Count > 0 ? string.Join(",", Recorders.Select(recorder => recorder.Name)) : "<none>"));
            _initialized = true;
        }
    }

    private static void LogMode(long aggregateId, BenchmarkMode mode, long sampleFrames, IEnumerable<MarkerSnapshot> snapshots)
    {
        var modeSnapshots = snapshots
            .Where(snapshot => snapshot.Mode == mode && snapshot.Hits > 0)
            .OrderByDescending(snapshot => snapshot.Nanoseconds)
            .Take(BenchmarkSettings.MainLoopProfilerTopEntries)
            .ToArray();
        if (modeSnapshots.Length == 0)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] UnityMarkerTop mode={0}, aggregate={1}, sampleFrames={2}, noSamples=true",
                mode,
                aggregateId,
                sampleFrames));
            return;
        }

        var top = string.Join("; ", modeSnapshots.Select(snapshot => FormatSnapshot(snapshot, sampleFrames)));
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] UnityMarkerTop mode={0}, aggregate={1}, sampleFrames={2}, top={3}",
            mode,
            aggregateId,
            sampleFrames,
            top));
    }

    private static string FormatSnapshot(MarkerSnapshot snapshot, long sampleFrames)
    {
        var totalMilliseconds = snapshot.Nanoseconds / 1000000.0;
        var maxMilliseconds = snapshot.MaxNanoseconds / 1000000.0;
        var millisecondsPerFrame = sampleFrames > 0 ? totalMilliseconds / sampleFrames : 0.0;
        var averageMicroseconds = snapshot.Hits > 0 ? snapshot.Nanoseconds / 1000.0 / snapshot.Hits : 0.0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}:hits={1},blocks={2},ms={3:F2},msPerFrame={4:F3},avgUs={5:F2},maxMs={6:F3}",
            snapshot.Name,
            snapshot.Hits,
            snapshot.SampleBlocks,
            totalMilliseconds,
            millisecondsPerFrame,
            averageMicroseconds,
            maxMilliseconds);
    }

    private sealed class MarkerRecorder
    {
        public MarkerRecorder(string name, Recorder recorder)
        {
            Name = name;
            Recorder = recorder;
        }

        public string Name { get; }
        public Recorder Recorder { get; }
    }

    private sealed class MarkerStats
    {
        public MarkerStats(BenchmarkMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        private BenchmarkMode Mode { get; }
        private string Name { get; }
        private long Hits { get; set; }
        private long SampleBlocks { get; set; }
        private long Nanoseconds { get; set; }
        private long MaxNanoseconds { get; set; }

        public void Record(long nanoseconds, int sampleBlocks)
        {
            Hits++;
            SampleBlocks += sampleBlocks;
            Nanoseconds += nanoseconds;
            if (nanoseconds > MaxNanoseconds)
            {
                MaxNanoseconds = nanoseconds;
            }
        }

        public MarkerSnapshot Snapshot()
        {
            return new MarkerSnapshot(Mode, Name, Hits, SampleBlocks, Nanoseconds, MaxNanoseconds);
        }
    }

    private readonly struct MarkerSnapshot
    {
        public MarkerSnapshot(BenchmarkMode mode, string name, long hits, long sampleBlocks, long nanoseconds, long maxNanoseconds)
        {
            Mode = mode;
            Name = name;
            Hits = hits;
            SampleBlocks = sampleBlocks;
            Nanoseconds = nanoseconds;
            MaxNanoseconds = maxNanoseconds;
        }

        public BenchmarkMode Mode { get; }
        public string Name { get; }
        public long Hits { get; }
        public long SampleBlocks { get; }
        public long Nanoseconds { get; }
        public long MaxNanoseconds { get; }
    }

    private readonly struct StatsKey : IEquatable<StatsKey>
    {
        public StatsKey(BenchmarkMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        private BenchmarkMode Mode { get; }
        private string Name { get; }

        public bool Equals(StatsKey other)
        {
            return Mode == other.Mode &&
                   StringComparer.Ordinal.Equals(Name, other.Name);
        }

        public override bool Equals(object? obj)
        {
            return obj is StatsKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ StringComparer.Ordinal.GetHashCode(Name);
            }
        }
    }
}
