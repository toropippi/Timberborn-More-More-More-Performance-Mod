using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using Debug = UnityEngine.Debug;

namespace T3MP;

// world固有のwater map参照とbacking array bindingを管理する。
// Owns world-specific water-map references and backing-array bindings.
internal static class WaterObjectFastPathMapAccess
{
    internal static IThreadSafeWaterMap? BoundWaterMap;
    internal static int VerticalStride;

    private static FieldInfo? _waterMapField;
    private static FieldInfo? _columnsField;
    private static FieldInfo? _countsField;
    private static bool _initDone;
    private static bool _initFailed;
    private static int _warned;

    // map同一性の取得は登録時だけ行い、毎tickのobject loopへ追加しない。
    // Read map identity only during registration, never in the per-tick object loop.
    internal static IThreadSafeWaterMap? GetRegisteredMap(WaterObject waterObject)
    {
        try
        {
            _waterMapField ??= waterObject.GetType().GetField(
                "_threadSafeWaterMap",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return _waterMapField?.GetValue(waterObject) as IThreadSafeWaterMap;
        }
        catch (Exception exception)
        {
            MarkFailed();
            WarnOnce(
                "[T3MP] WaterObject map lookup failed; using vanilla: " +
                exception);
            return null;
        }
    }

    internal static bool EnsureInitialized(
        IReadOnlyList<WaterObjectFastPathEntry> entries)
    {
        if (_initDone)
        {
            return !_initFailed;
        }

        if (entries.Count == 0)
        {
            return false;
        }

        try
        {
            // sampleから共有mapを取得し、座標変換とarray参照をbindする。
            // Resolve the shared map from one sample, then bind coordinates and arrays.
            var sample = entries[0].WaterObject;
            _waterMapField = sample.GetType().GetField(
                "_threadSafeWaterMap",
                BindingFlags.Instance | BindingFlags.NonPublic);
            BoundWaterMap =
                _waterMapField?.GetValue(sample) as IThreadSafeWaterMap;
            if (BoundWaterMap is null)
            {
                MarkFailed();
                return false;
            }

            VerticalStride =
                WaterObjectFastPathCoordinateAccess.Initialize(
                    sample,
                    BoundWaterMap);

            // backing arrayは増設時に差し替わるのでFieldInfoだけを保持する。
            // Cache only FieldInfo because growth can replace the backing arrays.
            var mapType = BoundWaterMap.GetType();
            _columnsField = mapType.GetField(
                "_threadSafeWaterColumns",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _countsField = mapType.GetField(
                "_threadSafeColumnCounts",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _initFailed = VerticalStride <= 0 ||
                _columnsField is null ||
                _countsField is null;
        }
        catch (Exception exception)
        {
            MarkFailed();
            WarnOnce(
                "[T3MP] WaterObjectTickFastPath init failed, using vanilla: " +
                exception);
        }

        _initDone = true;
        return !_initFailed;
    }

    internal static bool TryGetLiveColumns(
        out ReadOnlyWaterColumn[] columns,
        out byte[] counts)
    {
        columns = Array.Empty<ReadOnlyWaterColumn>();
        counts = Array.Empty<byte>();
        if (BoundWaterMap is null || _columnsField is null || _countsField is null)
        {
            return false;
        }

        // backing arrayはcolumn増設で差し替わるため、tickごとに再取得する。
        // Re-read backing arrays each tick because column growth can replace them.
        var liveColumns =
            _columnsField.GetValue(BoundWaterMap) as ReadOnlyWaterColumn[];
        var liveCounts = _countsField.GetValue(BoundWaterMap) as byte[];
        if (liveColumns is null || liveCounts is null)
        {
            return false;
        }

        columns = liveColumns;
        counts = liveCounts;
        return true;
    }

    internal static void ResolveEntry(WaterObjectFastPathEntry entry)
    {
        WaterObjectFastPathCoordinateAccess.ResolveEntry(entry);
    }

    internal static void MarkFailed()
    {
        _initFailed = true;
        _initDone = true;
    }

    internal static void Reset()
    {
        BoundWaterMap = null;
        VerticalStride = 0;
        _waterMapField = null;
        _columnsField = null;
        _countsField = null;
        _initDone = false;
        _initFailed = false;
        _warned = 0;
        WaterObjectFastPathCoordinateAccess.Reset();
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Debug.LogWarning(message);
        }
    }
}
