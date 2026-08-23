using System.Reflection;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;

namespace T3MP;

// WaterObjectの固定座標をmap indexへ変換するreflection bindingを管理する。
// Owns reflection bindings that convert immutable WaterObject coordinates to map indices.
internal static class WaterObjectFastPathCoordinateAccess
{
    private static object? _mapIndexService;
    private static MethodInfo? _cellToIndex;
    private static object? _terrainService;
    private static MethodInfo? _containsXY;
    private static FieldInfo? _baseCoordinatesField;

    internal static int Initialize(
        WaterObject sample,
        IThreadSafeWaterMap waterMap)
    {
        var waterObjectType = sample.GetType();
        _baseCoordinatesField = waterObjectType.GetField(
            "_baseCoordinates",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var mapType = waterMap.GetType();
        _mapIndexService = mapType.GetField(
            "_mapIndexService",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(waterMap);
        _terrainService = mapType.GetField(
            "_terrainService",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(waterMap);

        // map側のstrideを優先し、存在しない版ではindex serviceから取得する。
        // Prefer the map stride and fall back to the index service on older layouts.
        var strideField = mapType.GetField(
            "_verticalStride",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var verticalStride =
            strideField is not null ? (int)strideField.GetValue(waterMap) : 0;

        if (_mapIndexService is not null)
        {
            _cellToIndex = _mapIndexService.GetType().GetMethod(
                "CellToIndex",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Vector2Int) },
                null);

            if (verticalStride == 0)
            {
                var strideProperty = _mapIndexService.GetType().GetProperty(
                    "VerticalStride",
                    BindingFlags.Instance | BindingFlags.Public);
                if (strideProperty is not null)
                {
                    verticalStride =
                        (int)strideProperty.GetValue(_mapIndexService);
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

        return _baseCoordinatesField is null ||
            _mapIndexService is null ||
            _cellToIndex is null
                ? 0
                : verticalStride;
    }

    internal static void ResolveEntry(WaterObjectFastPathEntry entry)
    {
        if (_baseCoordinatesField is null ||
            _mapIndexService is null ||
            _cellToIndex is null)
        {
            return;
        }

        // 建物座標は不変なので、on-map判定とcell indexを一度だけ解決する。
        // Building coordinates are immutable, so resolve on-map state and cell index once.
        var baseCoordinates =
            (Vector3Int)_baseCoordinatesField.GetValue(entry.WaterObject);
        var xy = new Vector2Int(baseCoordinates.x, baseCoordinates.y);

        var onMap = true;
        if (_terrainService is not null && _containsXY is not null)
        {
            onMap = (bool)_containsXY.Invoke(
                _terrainService,
                new object[] { xy });
        }

        var cell2D = 0;
        if (onMap)
        {
            cell2D = (int)_cellToIndex.Invoke(
                _mapIndexService,
                new object[] { xy });
        }

        // 全reflection処理の成功後だけcacheを確定し、例外時は次tickもvanillaへ退避する。
        // Commit the cache only after all reflection succeeds so exceptions keep failing open.
        entry.BaseZ = baseCoordinates.z;
        entry.Cell2D = cell2D;
        entry.OnMap = onMap;
        entry.Resolved = true;
    }

    internal static void Reset()
    {
        _mapIndexService = null;
        _cellToIndex = null;
        _terrainService = null;
        _containsXY = null;
        _baseCoordinatesField = null;
    }
}
