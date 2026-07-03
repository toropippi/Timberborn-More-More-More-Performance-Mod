using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class WalkerDistanceCache
{
    private const int MaxEntries = 65536;
    private const int PruneIntervalFrames = 30;

    private static readonly object CacheLock = new object();
    private static readonly Dictionary<DistanceKey, CacheEntry> Cache = new Dictionary<DistanceKey, CacheEntry>();
    private static readonly FieldInfo? WalkerSpeedManagerField = FindType("Timberborn.WalkingSystem.Walker")?.GetField("_walkerSpeedManager", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? DayNightCycleField = FindType("Timberborn.WalkingSystem.Walker")?.GetField("_dayNightCycle", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? GetWalkerBaseSpeedMethod = FindType("Timberborn.WalkingSystem.WalkerSpeedManager")?.GetMethod("GetWalkerBaseSpeed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? SecondsToHoursMethod = FindType("Timberborn.TimeSystem.IDayNightCycle")?.GetMethod("SecondsToHours", BindingFlags.Instance | BindingFlags.Public);

    [ThreadStatic]
    private static CaptureContext _captureContext;

    private static int _lastPruneFrame = -1;
    private static long _attempts;
    private static long _hits;
    private static long _misses;
    private static long _stores;
    private static long _expiredRemovals;
    private static long _evictions;
    private static long _computeFailures;
    private static long _captures;

    public static bool TryGetTravelTime(object walker, Vector3 start, Vector3 destination, out float travelTime)
    {
        travelTime = default;
        if (!BenchmarkSettings.EnableWalkerDistanceCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _attempts);
        var frame = Time.frameCount;
        var key = DistanceKey.Create(start, destination);
        float distance;
        lock (CacheLock)
        {
            PruneExpiredIfNeeded(frame);
            if (!Cache.TryGetValue(key, out var entry) || !IsFresh(frame, entry.Frame))
            {
                if (Cache.Remove(key))
                {
                    Interlocked.Increment(ref _expiredRemovals);
                }

                Interlocked.Increment(ref _misses);
                return false;
            }

            distance = entry.Distance;
        }

        if (!TryConvertDistanceToTravelTime(walker, distance, out travelTime))
        {
            Interlocked.Increment(ref _computeFailures);
            Interlocked.Increment(ref _misses);
            return false;
        }

        Interlocked.Increment(ref _hits);
        return true;
    }

    public static void BeginCapture(Vector3 start, Vector3 destination)
    {
        if (!BenchmarkSettings.EnableWalkerDistanceCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            _captureContext = CaptureContext.Inactive;
            return;
        }

        _captureContext = new CaptureContext(true, start, destination);
    }

    public static void EndCapture()
    {
        _captureContext = CaptureContext.Inactive;
    }

    public static void TryCaptureNavigationResult(string methodName, object[] args, bool success)
    {
        if (!_captureContext.Active ||
            !success ||
            methodName != "FindPathUnlimitedRange" ||
            args.Length < 4 ||
            args[0] is not Vector3 start ||
            args[1] is not Vector3 destination ||
            args[args.Length - 1] is not float distance)
        {
            return;
        }

        if (!SamePoint(start, _captureContext.Start) || !SamePoint(destination, _captureContext.Destination))
        {
            return;
        }

        if (distance <= 0f && !SamePoint(start, destination))
        {
            Interlocked.Increment(ref _computeFailures);
            return;
        }

        Store(start, destination, distance);
        _captureContext = _captureContext.WithCaptured();
        Interlocked.Increment(ref _captures);
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var hits = Interlocked.Exchange(ref _hits, 0);
        var misses = Interlocked.Exchange(ref _misses, 0);
        var stores = Interlocked.Exchange(ref _stores, 0);
        var expiredRemovals = Interlocked.Exchange(ref _expiredRemovals, 0);
        var evictions = Interlocked.Exchange(ref _evictions, 0);
        var computeFailures = Interlocked.Exchange(ref _computeFailures, 0);
        var captures = Interlocked.Exchange(ref _captures, 0);
        if (attempts == 0 && hits == 0 && misses == 0 && stores == 0 && captures == 0)
        {
            return;
        }

        int entries;
        lock (CacheLock)
        {
            entries = Cache.Count;
        }

        var hitRate = attempts > 0 ? (double)hits / attempts : 0;
        Debug.Log(
            $"[T3MP] WalkerDistanceCache aggregate={aggregateId}, ttlFrames={BenchmarkSettings.WalkerDistanceCacheTtlFrames}, quantizeStep={BenchmarkSettings.WalkerDistanceCacheQuantizeStep:F2}, attempts={attempts}, hits={hits}, misses={misses}, hitRate={hitRate:F3}, stores={stores}, captures={captures}, computeFailures={computeFailures}, expiredRemovals={expiredRemovals}, evictions={evictions}, entries={entries}");
    }

    private static void Store(Vector3 start, Vector3 destination, float distance)
    {
        var frame = Time.frameCount;
        var key = DistanceKey.Create(start, destination);
        lock (CacheLock)
        {
            PruneExpiredIfNeeded(frame);
            if (Cache.Count >= MaxEntries)
            {
                PruneExpired(frame);
                if (Cache.Count >= MaxEntries)
                {
                    Cache.Clear();
                    Interlocked.Increment(ref _evictions);
                }
            }

            Cache[key] = new CacheEntry(frame, distance);
            Interlocked.Increment(ref _stores);
        }
    }

    private static bool TryConvertDistanceToTravelTime(object walker, float distance, out float travelTime)
    {
        travelTime = default;
        if (WalkerSpeedManagerField?.GetValue(walker) is not { } walkerSpeedManager ||
            DayNightCycleField?.GetValue(walker) is not { } dayNightCycle ||
            GetWalkerBaseSpeedMethod?.Invoke(walkerSpeedManager, Array.Empty<object>()) is not float baseSpeed ||
            baseSpeed <= 0 ||
            SecondsToHoursMethod?.Invoke(dayNightCycle, new object[] { distance / baseSpeed }) is not float hours)
        {
            return false;
        }

        travelTime = hours;
        return true;
    }

    private static void PruneExpiredIfNeeded(int frame)
    {
        if (_lastPruneFrame >= 0 && frame - _lastPruneFrame < PruneIntervalFrames)
        {
            return;
        }

        PruneExpired(frame);
        _lastPruneFrame = frame;
    }

    private static void PruneExpired(int frame)
    {
        var expiredKeys = new List<DistanceKey>();
        foreach (var pair in Cache)
        {
            if (!IsFresh(frame, pair.Value.Frame))
            {
                expiredKeys.Add(pair.Key);
            }
        }

        foreach (var key in expiredKeys)
        {
            Cache.Remove(key);
        }

        if (expiredKeys.Count > 0)
        {
            Interlocked.Add(ref _expiredRemovals, expiredKeys.Count);
        }
    }

    private static bool IsFresh(int currentFrame, int entryFrame)
    {
        var age = currentFrame - entryFrame;
        return age >= 0 && age <= BenchmarkSettings.WalkerDistanceCacheTtlFrames;
    }

    private static bool SamePoint(Vector3 left, Vector3 right)
    {
        return Quantize(left.x) == Quantize(right.x) &&
            Quantize(left.y) == Quantize(right.y) &&
            Quantize(left.z) == Quantize(right.z);
    }

    private static int Quantize(float value)
    {
        return RoundHalfAwayFromZero(value / BenchmarkSettings.WalkerDistanceCacheQuantizeStep);
    }

    private static int RoundHalfAwayFromZero(float value)
    {
        return value >= 0f
            ? Mathf.FloorToInt(value + 0.5f)
            : Mathf.CeilToInt(value - 0.5f);
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

    private readonly struct CacheEntry
    {
        public CacheEntry(int frame, float distance)
        {
            Frame = frame;
            Distance = distance;
        }

        public int Frame { get; }
        public float Distance { get; }
    }

    private readonly struct DistanceKey : IEquatable<DistanceKey>
    {
        private DistanceKey(int startX, int startY, int startZ, int destinationX, int destinationY, int destinationZ)
        {
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            DestinationX = destinationX;
            DestinationY = destinationY;
            DestinationZ = destinationZ;
        }

        private int StartX { get; }
        private int StartY { get; }
        private int StartZ { get; }
        private int DestinationX { get; }
        private int DestinationY { get; }
        private int DestinationZ { get; }

        public static DistanceKey Create(Vector3 start, Vector3 destination)
        {
            return new DistanceKey(
                Quantize(start.x),
                Quantize(start.y),
                Quantize(start.z),
                Quantize(destination.x),
                Quantize(destination.y),
                Quantize(destination.z));
        }

        public bool Equals(DistanceKey other)
        {
            return StartX == other.StartX &&
                StartY == other.StartY &&
                StartZ == other.StartZ &&
                DestinationX == other.DestinationX &&
                DestinationY == other.DestinationY &&
                DestinationZ == other.DestinationZ;
        }

        public override bool Equals(object? obj)
        {
            return obj is DistanceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StartX;
                hash = (hash * 397) ^ StartY;
                hash = (hash * 397) ^ StartZ;
                hash = (hash * 397) ^ DestinationX;
                hash = (hash * 397) ^ DestinationY;
                hash = (hash * 397) ^ DestinationZ;
                return hash;
            }
        }
    }

    private readonly struct CaptureContext
    {
        public static readonly CaptureContext Inactive = new CaptureContext(false, default, default);

        public CaptureContext(bool active, Vector3 start, Vector3 destination, bool captured = false)
        {
            Active = active;
            Start = start;
            Destination = destination;
            Captured = captured;
        }

        public bool Active { get; }
        public Vector3 Start { get; }
        public Vector3 Destination { get; }
        public bool Captured { get; }

        public CaptureContext WithCaptured()
        {
            return new CaptureContext(Active, Start, Destination, true);
        }
    }
}
