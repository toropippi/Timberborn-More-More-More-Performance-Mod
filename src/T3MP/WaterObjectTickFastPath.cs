using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Fast, vanilla-EXACT replacement for WaterObjectService.Tick.
//
// Vanilla re-derives every water object's WaterAboveBase every tick through
// WaterObject.UpdateWaterAboveBase() -> IThreadSafeWaterMap.CeiledWaterHeight,
// which does ITerrainService.Contains + MapIndexService.CellToIndex + a column
// search that builds ReadOnlyArray<> wrappers. Measured ~150ns/object => ~1.6ms
// per tick on a ~10k-water-object map.
//
// This replacement resolves each object's map cell (index2D) and on-map flag
// ONCE - the building's coordinates never move - then, every tick, finds the
// containing column from the CURRENT structure by indexing the backing column
// arrays directly (read once per tick to avoid the per-object Contains +
// CellToIndex + ReadOnlyArray wrapping and interface dispatch) and computes
// WaterAboveBase exactly like CeiledWaterHeight. The integer WaterAboveBase is
// compared against the object's current value; only when it changed do we call
// vanilla WaterObject.UpdateWaterAboveBase(), which recomputes and fires
// WaterAboveBaseChanged exactly as vanilla would.
//
// Key correctness points:
//  * The column is re-resolved from the live structure every tick (no cached
//    column index that could go stale across obstacle/terrain changes), so the
//    computed value is always what vanilla would compute this tick.
//  * The real mutation + event is always vanilla's own method, so a building's
//    flooded/unflooded transition is bit-identical to vanilla.
//  * A periodic full vanilla pass (WaterObjectTickSafetyNetTicks) is a belt-and-
//    suspenders net: even if the cheap computation ever diverged, every object
//    is force-refreshed within the window.
// On any reflection/lookup failure it returns true so vanilla runs unchanged.
internal static class WaterObjectTickFastPath
{
    private sealed class Entry
    {
        public WaterObject Object = null!;
        public bool Resolved;
        public bool OnMap;
        public int Cell2D;
        public int BaseZ;
    }

    private static readonly List<Entry> Entries = new List<Entry>(1024);
    private static readonly Dictionary<WaterObject, Entry> EntryByObject =
        new Dictionary<WaterObject, Entry>(ReferenceEqualityComparer.Instance);

    private static IThreadSafeWaterMap? _waterMap;
    private static int _verticalStride;
    private static object? _mapIndexService;
    private static MethodInfo? _cellToIndex;
    private static object? _terrainService;
    private static MethodInfo? _containsXY;
    private static FieldInfo? _baseCoordinatesField;
    private static FieldInfo? _waterMapField;
    // Direct-array access to skip per-object interface dispatch on the hot path.
    // The backing arrays are reassigned on column-count growth, so the field is
    // re-read once per tick (cheap) and then indexed directly per object.
    private static FieldInfo? _columnsField;
    private static FieldInfo? _countsField;
    private static bool _initDone;
    private static bool _initFailed;
    private static int _tickCounter;
    private static int _warned;

    // Postfix on WaterObjectService.RegisterWaterObject: track the new object.
    public static void OnRegister(WaterObject waterObject)
    {
        if (waterObject == null || EntryByObject.ContainsKey(waterObject))
        {
            return;
        }

        var entry = new Entry { Object = waterObject };
        Entries.Add(entry);
        EntryByObject.Add(waterObject, entry);
    }

    // Postfix on WaterObjectService.UnregisterWaterObject: drop it.
    public static void OnUnregister(WaterObject waterObject)
    {
        if (waterObject == null || !EntryByObject.Remove(waterObject))
        {
            return;
        }

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Entries[i].Object, waterObject))
            {
                Entries.RemoveAt(i);
                break;
            }
        }
    }

    // Prefix on WaterObjectService.Tick; returns false when we handled the tick.
    public static bool RunTick()
    {
        if (!BenchmarkSettings.EnableWaterObjectTickFastPath ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        try
        {
            if (!EnsureInit())
            {
                return true;
            }

            if (_waterMap is null)
            {
                return true;
            }

            _tickCounter++;
            var forceAll = BenchmarkSettings.WaterObjectTickSafetyNetTicks > 0 &&
                _tickCounter % BenchmarkSettings.WaterObjectTickSafetyNetTicks == 0;

            // Re-read the backing arrays once per tick (they can be reassigned on
            // growth); index them directly per object to avoid interface dispatch.
            var columns = _columnsField!.GetValue(_waterMap) as ReadOnlyWaterColumn[];
            var counts = _countsField!.GetValue(_waterMap) as byte[];
            if (columns is null || counts is null)
            {
                return true;
            }

            for (var i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                var waterObject = entry.Object;
                if (waterObject == null)
                {
                    continue;
                }

                if (!entry.Resolved)
                {
                    ResolveEntry(entry, waterObject);
                    // Sync exactly once via vanilla on first sight.
                    waterObject.UpdateWaterAboveBase();
                    continue;
                }

                if (forceAll)
                {
                    waterObject.UpdateWaterAboveBase();
                    continue;
                }

                var aboveBase = ComputeWaterAboveBase(entry, columns, counts);
                if (aboveBase != waterObject.WaterAboveBase)
                {
                    // Let vanilla do the actual mutation + event, bit-exact.
                    waterObject.UpdateWaterAboveBase();
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Debug.LogWarning("[T3MP] WaterObjectTickFastPath fell back to vanilla: " + exception);
            }

            return true;
        }
    }

    // Exact replica of IThreadSafeWaterMap.CeiledWaterHeight(baseCoords) minus
    // baseZ, floored at 0 - i.e. WaterObject.CurrentWaterAboveBase - but using a
    // cached cell index and the index-based reads.
    // Exact inline of IThreadSafeWaterMap.CeiledWaterHeight(baseCoords) - baseZ,
    // floored at 0 (= WaterObject.CurrentWaterAboveBase), resolved from the LIVE
    // column arrays every tick. No cached column index: reading the current
    // structure each tick is what keeps this correct when water rises to flood a
    // building or drains away, with no dependence on AnyColumnChanged timing.
    private static int ComputeWaterAboveBase(Entry entry, ReadOnlyWaterColumn[] columns, byte[] counts)
    {
        if (!entry.OnMap)
        {
            return 0;
        }

        var cell2D = entry.Cell2D;
        if ((uint)cell2D >= (uint)counts.Length)
        {
            return 0;
        }

        var z = entry.BaseZ;
        int count = counts[cell2D];
        for (var i = 0; i < count; i++)
        {
            var index3D = i * _verticalStride + cell2D;
            if ((uint)index3D >= (uint)columns.Length)
            {
                break;
            }

            var column = columns[index3D];
            int floor = column.Floor;
            if (z < floor)
            {
                break;
            }

            if (z < column.Ceiling)
            {
                var depth = column.WaterDepth;
                if (depth <= 0f)
                {
                    return 0;
                }

                var ceiled = Mathf.CeilToInt(floor + depth);
                var above = ceiled - z;
                return above > 0 ? above : 0;
            }
        }

        return 0;
    }

    private static void ResolveEntry(Entry entry, WaterObject waterObject)
    {
        entry.Resolved = true;
        entry.OnMap = false;
        entry.Cell2D = 0;
        entry.BaseZ = 0;
        if (_baseCoordinatesField is null || _mapIndexService is null || _cellToIndex is null)
        {
            return;
        }

        var baseCoordinates = (Vector3Int)_baseCoordinatesField.GetValue(waterObject);
        entry.BaseZ = baseCoordinates.z;
        var xy = new Vector2Int(baseCoordinates.x, baseCoordinates.y);

        var onMap = true;
        if (_terrainService is not null && _containsXY is not null)
        {
            onMap = (bool)_containsXY.Invoke(_terrainService, new object[] { xy });
        }

        entry.OnMap = onMap;
        if (onMap)
        {
            entry.Cell2D = (int)_cellToIndex.Invoke(_mapIndexService, new object[] { xy });
        }
    }

    private static bool EnsureInit()
    {
        if (_initDone)
        {
            return !_initFailed;
        }

        // Need at least one registered object to reach the shared water map.
        if (Entries.Count == 0)
        {
            return false;
        }

        try
        {
            var sample = Entries[0].Object;
            var waterObjectType = sample.GetType();
            _baseCoordinatesField = waterObjectType.GetField("_baseCoordinates", BindingFlags.Instance | BindingFlags.NonPublic);
            _waterMapField = waterObjectType.GetField("_threadSafeWaterMap", BindingFlags.Instance | BindingFlags.NonPublic);
            _waterMap = _waterMapField?.GetValue(sample) as IThreadSafeWaterMap;
            if (_waterMap is null || _baseCoordinatesField is null)
            {
                _initFailed = true;
                _initDone = true;
                return false;
            }

            var mapType = _waterMap.GetType();
            _mapIndexService = mapType.GetField("_mapIndexService", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_waterMap);
            _terrainService = mapType.GetField("_terrainService", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_waterMap);
            var strideField = mapType.GetField("_verticalStride", BindingFlags.Instance | BindingFlags.NonPublic);
            _verticalStride = strideField is not null ? (int)strideField.GetValue(_waterMap) : 0;
            _columnsField = mapType.GetField("_threadSafeWaterColumns", BindingFlags.Instance | BindingFlags.NonPublic);
            _countsField = mapType.GetField("_threadSafeColumnCounts", BindingFlags.Instance | BindingFlags.NonPublic);

            if (_mapIndexService is not null)
            {
                _cellToIndex = _mapIndexService.GetType().GetMethod(
                    "CellToIndex",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(Vector2Int) },
                    null);
                if (_verticalStride == 0)
                {
                    var strideProp = _mapIndexService.GetType().GetProperty("VerticalStride", BindingFlags.Instance | BindingFlags.Public);
                    if (strideProp is not null)
                    {
                        _verticalStride = (int)strideProp.GetValue(_mapIndexService);
                    }
                }
            }

            if (_terrainService is not null)
            {
                _containsXY = _terrainService.GetType().GetMethod(
                    "Contains",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(Vector2Int) },
                    null);
            }

            _initFailed = _cellToIndex is null || _verticalStride <= 0 ||
                _columnsField is null || _countsField is null;
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Debug.LogWarning("[T3MP] WaterObjectTickFastPath init failed, using vanilla: " + exception);
            }

            _initFailed = true;
        }

        _initDone = true;
        return !_initFailed;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<WaterObject>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

        public bool Equals(WaterObject x, WaterObject y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(WaterObject obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
