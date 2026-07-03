using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Coordinates;
using Timberborn.Planting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class PlantingSpotFinderOptimizer
{
    private static readonly object LockObject = new object();
    private static readonly ConditionalWeakTable<object, FinderCache> Caches = new ConditionalWeakTable<object, FinderCache>();
    private static FieldInfo? _plantablePrioritizerField;
    private static FieldInfo? _blockObjectCenterField;
    private static FieldInfo? _inRangePlantingCoordinatesField;
    private static FieldInfo? _plantingServiceField;
    private static PropertyInfo? _prioritizedPlantableSpecProperty;
    private static PropertyInfo? _worldCenterGroundedProperty;
    private static MethodInfo? _getCoordinatesMethod;
    private static CanPlantAtDelegate? _canPlantAt;
    private static AreCoordinatesInRangeDelegate? _areCoordinatesInRange;
    private static bool _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _cacheBuilds;
    private static long _coordinatesBuilt;
    private static long _neighborChecks;
    private static long _orderedChecks;
    private static long _liveMisses;
    private static long _invalidRejects;
    private static long _foundNeighbor;
    private static long _foundOrdered;
    private static long _notFoundHandled;
    private static long _invalidations;
    private static long _stopwatchTicks;
    private static int _globalGeneration;

    private delegate bool CanPlantAtDelegate(object finder, PlantingSpot plantingSpot, object? prioritizedPlantableSpec);
    private delegate bool AreCoordinatesInRangeDelegate(object inRangePlantingCoordinates, Vector3Int coordinates);

    public static void Initialize(Type finderType)
    {
        EnsureInitialized(finderType);
    }

    public static bool TryFindClosest(object finder, Vector3 agentPosition, ref PlantingSpot? result)
    {
        result = null;
        if (!BenchmarkSettings.EnablePlantingSpotFinderOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _attempts);
        var startTimestamp = BenchmarkSettings.EnableDetailedBenchmarkTiming ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (!EnsureInitialized(finder.GetType()) ||
                _canPlantAt is null ||
                _areCoordinatesInRange is null ||
                _getCoordinatesMethod is null ||
                _plantablePrioritizerField is null ||
                _inRangePlantingCoordinatesField is null ||
                _plantingServiceField is null)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            if (_inRangePlantingCoordinatesField.GetValue(finder) is not { } inRange ||
                _plantingServiceField.GetValue(finder) is not PlantingService plantingService)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            var center = GetWorldCenterGrounded(finder);
            var prioritized = GetPrioritizedPlantableSpec(finder);
            var prioritizedOrderedSearched = false;
            var fallbackOrderedSearched = false;
            if (TryFindNeighbor(finder, inRange, plantingService, agentPosition, center, prioritized, out result) ||
                TryFindOrdered(finder, plantingService, center, prioritized, out result, out prioritizedOrderedSearched))
            {
                Interlocked.Increment(ref _handled);
                return true;
            }

            if (prioritized is not null &&
                (TryFindNeighbor(finder, inRange, plantingService, agentPosition, center, null, out result) ||
                 TryFindOrdered(finder, plantingService, center, null, out result, out fallbackOrderedSearched)))
            {
                Interlocked.Increment(ref _handled);
                return true;
            }

            if (prioritized is not null ? !fallbackOrderedSearched : !prioritizedOrderedSearched)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            Interlocked.Increment(ref _notFoundHandled);
            Interlocked.Increment(ref _handled);
            result = null;
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] PlantingSpotFinderOptimizer fallback: {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
        finally
        {
            if (BenchmarkSettings.EnableDetailedBenchmarkTiming)
            {
                Interlocked.Add(ref _stopwatchTicks, Stopwatch.GetTimestamp() - startTimestamp);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var cacheBuilds = Interlocked.Exchange(ref _cacheBuilds, 0);
        var coordinatesBuilt = Interlocked.Exchange(ref _coordinatesBuilt, 0);
        var neighborChecks = Interlocked.Exchange(ref _neighborChecks, 0);
        var orderedChecks = Interlocked.Exchange(ref _orderedChecks, 0);
        var liveMisses = Interlocked.Exchange(ref _liveMisses, 0);
        var invalidRejects = Interlocked.Exchange(ref _invalidRejects, 0);
        var foundNeighbor = Interlocked.Exchange(ref _foundNeighbor, 0);
        var foundOrdered = Interlocked.Exchange(ref _foundOrdered, 0);
        var notFoundHandled = Interlocked.Exchange(ref _notFoundHandled, 0);
        var invalidations = Interlocked.Exchange(ref _invalidations, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        var handledRate = attempts > 0 ? (double)handled / attempts : 0.0;
        var avgBuilt = cacheBuilds > 0 ? (double)coordinatesBuilt / cacheBuilds : 0.0;
        var orderedChecksPerHandled = handled > 0 ? (double)orderedChecks / handled : 0.0;
        Debug.Log(
            $"[T3MP] PlantingSpotFinderOptimizer aggregate={aggregateId}, enabled={BenchmarkSettings.EnablePlantingSpotFinderOptimizer}, attempts={attempts}, handled={handled}, handledRate={handledRate:F3}, fallbacks={fallbacks}, cacheBuilds={cacheBuilds}, avgBuilt={avgBuilt:F1}, invalidations={invalidations}, neighborChecks={neighborChecks}, orderedChecks={orderedChecks}, orderedChecksPerHandled={orderedChecksPerHandled:F2}, liveMisses={liveMisses}, invalidRejects={invalidRejects}, foundNeighbor={foundNeighbor}, foundOrdered={foundOrdered}, notFoundHandled={notFoundHandled}, ms={ToMilliseconds(ticks):F2}");
    }

    public static void InvalidateAll()
    {
        Interlocked.Increment(ref _invalidations);
        Interlocked.Increment(ref _globalGeneration);
    }

    private static bool TryFindNeighbor(
        object finder,
        object inRange,
        PlantingService plantingService,
        Vector3 agentPosition,
        Vector3 center,
        object? prioritized,
        out PlantingSpot? result)
    {
        var agentCoordinates = CoordinateSystem.WorldToGridInt(agentPosition);
        var bestDistance = float.PositiveInfinity;
        PlantingSpot? best = null;
        for (var index = 0; index < Deltas.Neighbors8Vector3IntOrdered.Length; index++)
        {
            var coordinates = agentCoordinates + Deltas.Neighbors8Vector3IntOrdered[index];
            Interlocked.Increment(ref _neighborChecks);
            if (_areCoordinatesInRange?.Invoke(inRange, coordinates) != true)
            {
                continue;
            }

            var spot = plantingService.GetSpotAt(coordinates);
            if (!spot.HasValue)
            {
                Interlocked.Increment(ref _liveMisses);
                continue;
            }

            if (!CanPlantAt(finder, spot.Value, prioritized))
            {
                Interlocked.Increment(ref _invalidRejects);
                continue;
            }

            var distance = Vector3.Distance(center, CoordinateSystem.GridToWorldCentered(spot.Value.Coordinates));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = spot;
            }
        }

        if (best.HasValue)
        {
            Interlocked.Increment(ref _foundNeighbor);
            result = best;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryFindOrdered(
        object finder,
        PlantingService plantingService,
        Vector3 center,
        object? prioritized,
        out PlantingSpot? result,
        out bool searched)
    {
        searched = false;
        var cache = Caches.GetValue(finder, _ => new FinderCache());
        var coordinates = cache.GetOrderedCoordinates(finder, plantingService, center);
        if (coordinates is null)
        {
            result = null;
            return false;
        }

        searched = true;
        for (var index = 0; index < coordinates.Length; index++)
        {
            Interlocked.Increment(ref _orderedChecks);
            var spot = plantingService.GetSpotAt(coordinates[index]);
            if (!spot.HasValue)
            {
                Interlocked.Increment(ref _liveMisses);
                continue;
            }

            if (!CanPlantAt(finder, spot.Value, prioritized))
            {
                Interlocked.Increment(ref _invalidRejects);
                continue;
            }

            Interlocked.Increment(ref _foundOrdered);
            result = spot;
            return true;
        }

        result = null;
        return false;
    }

    private static bool CanPlantAt(object finder, PlantingSpot plantingSpot, object? prioritized)
    {
        return _canPlantAt?.Invoke(finder, plantingSpot, prioritized) == true;
    }

    private static object? GetPrioritizedPlantableSpec(object finder)
    {
        var prioritizer = _plantablePrioritizerField?.GetValue(finder);
        return prioritizer is null ? null : _prioritizedPlantableSpecProperty?.GetValue(prioritizer);
    }

    private static Vector3 GetWorldCenterGrounded(object finder)
    {
        var center = _blockObjectCenterField?.GetValue(finder);
        return center is null || _worldCenterGroundedProperty?.GetValue(center) is not Vector3 value
            ? Vector3.zero
            : value;
    }

    private static bool EnsureInitialized(Type finderType)
    {
        if (_initialized)
        {
            return _canPlantAt is not null && _getCoordinatesMethod is not null && _areCoordinatesInRange is not null;
        }

        lock (LockObject)
        {
            if (_initialized)
            {
                return _canPlantAt is not null && _getCoordinatesMethod is not null && _areCoordinatesInRange is not null;
            }

            _plantablePrioritizerField = finderType.GetField("_plantablePrioritizer", BindingFlags.Instance | BindingFlags.NonPublic);
            _blockObjectCenterField = finderType.GetField("_blockObjectCenter", BindingFlags.Instance | BindingFlags.NonPublic);
            _inRangePlantingCoordinatesField = finderType.GetField("_inRangePlantingCoordinates", BindingFlags.Instance | BindingFlags.NonPublic);
            _plantingServiceField = finderType.GetField("_plantingService", BindingFlags.Instance | BindingFlags.NonPublic);
            _prioritizedPlantableSpecProperty = _plantablePrioritizerField?.FieldType.GetProperty("PrioritizedPlantableSpec", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _worldCenterGroundedProperty = _blockObjectCenterField?.FieldType.GetProperty("WorldCenterGrounded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var canPlantAt = finderType.GetMethod("CanPlantAt", BindingFlags.Instance | BindingFlags.NonPublic);
            if (canPlantAt is not null)
            {
                _canPlantAt = CreateCanPlantAtDelegate(finderType, canPlantAt);
            }

            var inRangeType = _inRangePlantingCoordinatesField?.FieldType;
            var getCoordinates = inRangeType?.GetMethod("GetCoordinates", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var areCoordinatesInRange = inRangeType?.GetMethod("AreCoordinatesInRange", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (inRangeType is not null && getCoordinates is not null && areCoordinatesInRange is not null)
            {
                _getCoordinatesMethod = getCoordinates;
                _areCoordinatesInRange = CreateAreCoordinatesInRangeDelegate(inRangeType, areCoordinatesInRange);
            }

            _initialized = true;
            return _canPlantAt is not null && _getCoordinatesMethod is not null && _areCoordinatesInRange is not null;
        }
    }

    private static CanPlantAtDelegate CreateCanPlantAtDelegate(Type finderType, MethodInfo method)
    {
        var parameters = method.GetParameters();
        var plantableSpecType = parameters[1].ParameterType;
        var dynamicMethod = new DynamicMethod(
            "T3MP_CanPlantAt",
            typeof(bool),
            new[] { typeof(object), typeof(PlantingSpot), typeof(object) },
            typeof(PlantingSpotFinderOptimizer).Module,
            true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, finderType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Castclass, plantableSpecType);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return (CanPlantAtDelegate)dynamicMethod.CreateDelegate(typeof(CanPlantAtDelegate));
    }

    private static AreCoordinatesInRangeDelegate CreateAreCoordinatesInRangeDelegate(Type inRangeType, MethodInfo method)
    {
        var dynamicMethod = new DynamicMethod(
            "T3MP_ArePlantingCoordinatesInRange",
            typeof(bool),
            new[] { typeof(object), typeof(Vector3Int) },
            typeof(PlantingSpotFinderOptimizer).Module,
            true);
        var il = dynamicMethod.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, inRangeType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return (AreCoordinatesInRangeDelegate)dynamicMethod.CreateDelegate(typeof(AreCoordinatesInRangeDelegate));
    }

    private static void AddCacheBuild(int coordinates)
    {
        Interlocked.Increment(ref _cacheBuilds);
        Interlocked.Add(ref _coordinatesBuilt, coordinates);
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class FinderCache
    {
        private Vector3Int[] _orderedCoordinates = Array.Empty<Vector3Int>();
        private int _lastBuildFrame = -1000000;
        private int _generation = -1;

        public Vector3Int[]? GetOrderedCoordinates(object finder, PlantingService plantingService, Vector3 center)
        {
            var frame = Time.frameCount;
            var generation = Volatile.Read(ref _globalGeneration);
            if (_orderedCoordinates.Length > 0 &&
                _generation == generation &&
                frame - _lastBuildFrame < BenchmarkSettings.PlantingSpotFinderCacheTtlFrames)
            {
                return _orderedCoordinates;
            }

            var inRange = _inRangePlantingCoordinatesField?.GetValue(finder);
            if (inRange is null || _getCoordinatesMethod is null)
            {
                _orderedCoordinates = Array.Empty<Vector3Int>();
                _lastBuildFrame = frame;
                _generation = generation;
                return null;
            }

            var coordinates = new List<CoordinateDistance>();
            var inRangeCoordinates = _getCoordinatesMethod.Invoke(inRange, null);
            if (inRangeCoordinates is null ||
                !TryAddCoordinates(inRangeCoordinates, plantingService, center, coordinates))
            {
                _orderedCoordinates = Array.Empty<Vector3Int>();
                _lastBuildFrame = frame;
                _generation = generation;
                return null;
            }

            coordinates.Sort(static (left, right) =>
            {
                var distanceComparison = left.Distance.CompareTo(right.Distance);
                return distanceComparison != 0 ? distanceComparison : left.Ordinal.CompareTo(right.Ordinal);
            });
            _orderedCoordinates = coordinates.Select(static item => item.Coordinates).ToArray();
            _lastBuildFrame = frame;
            _generation = generation;
            AddCacheBuild(_orderedCoordinates.Length);
            return _orderedCoordinates;
        }

        private static bool TryAddCoordinates(
            object inRangeCoordinates,
            PlantingService plantingService,
            Vector3 center,
            List<CoordinateDistance> coordinates)
        {
            if (inRangeCoordinates is IEnumerable enumerable)
            {
                var ordinal = 0;
                foreach (var item in enumerable)
                {
                    if (item is Vector3Int coordinate)
                    {
                        AddCoordinate(plantingService, center, coordinates, coordinate, ordinal);
                    }

                    ordinal++;
                }

                return true;
            }

            var getEnumerator = inRangeCoordinates.GetType().GetMethod("GetEnumerator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getEnumerator is null)
            {
                return false;
            }

            var enumerator = getEnumerator.Invoke(inRangeCoordinates, null);
            if (enumerator is null)
            {
                return false;
            }

            var enumeratorType = enumerator.GetType();
            var moveNext = enumeratorType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var current = enumeratorType.GetProperty("Current", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext is null || current is null)
            {
                return false;
            }

            var reflectedOrdinal = 0;
            while (moveNext.Invoke(enumerator, null) is true)
            {
                if (current.GetValue(enumerator) is not Vector3Int coordinate)
                {
                    reflectedOrdinal++;
                    continue;
                }

                AddCoordinate(plantingService, center, coordinates, coordinate, reflectedOrdinal);
                reflectedOrdinal++;
            }

            return true;
        }

        private static void AddCoordinate(
            PlantingService plantingService,
            Vector3 center,
            List<CoordinateDistance> coordinates,
            Vector3Int coordinate,
            int ordinal)
        {
            var distance = Vector3.Distance(center, CoordinateSystem.GridToWorldCentered(coordinate));
            coordinates.Add(new CoordinateDistance(coordinate, distance, ordinal));
        }
    }

    private readonly struct CoordinateDistance
    {
        public CoordinateDistance(Vector3Int coordinates, float distance, int ordinal)
        {
            Coordinates = coordinates;
            Distance = distance;
            Ordinal = ordinal;
        }

        public Vector3Int Coordinates { get; }
        public float Distance { get; }
        public int Ordinal { get; }
    }
}
