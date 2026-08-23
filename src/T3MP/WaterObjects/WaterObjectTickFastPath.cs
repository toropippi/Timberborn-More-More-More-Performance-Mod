using System;
using System.Threading;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// world境界を追跡しつつ、WaterObjectService.Tickをvanilla同値の高速経路へ置き換える。
// Replaces WaterObjectService.Tick with a vanilla-exact fast path while tracking world boundaries.
internal static class WaterObjectTickFastPath
{
    private static int _tickCounter;
    private static int _warned;

    public static void OnRegister(WaterObjectService service, WaterObject waterObject)
    {
        WaterObjectFastPathLifecycle.Register(service, waterObject);
    }

    public static void OnUnregister(WaterObjectService service, WaterObject waterObject)
    {
        WaterObjectFastPathLifecycle.Unregister(service, waterObject);
    }

    // 条件または同一性検証を満たさない場合はtrueを返し、vanillaへ退避する。
    // Returns true to fail open to vanilla whenever settings or identity checks fail.
    public static bool RunTick(WaterObjectService service)
    {
        if (!BenchmarkSettings.EnableWaterObjectTickFastPath ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            !WaterObjectFastPathLifecycle.IsActiveService(service))
        {
            return true;
        }

        try
        {
            if (!WaterObjectFastPathMapAccess.EnsureInitialized(
                    WaterObjectFastPathLifecycle.Entries) ||
                !WaterObjectFastPathLifecycle.MatchesRegisteredMap(
                    WaterObjectFastPathMapAccess.BoundWaterMap))
            {
                return true;
            }

            _tickCounter++;
            var forceAll = BenchmarkSettings.WaterObjectTickSafetyNetTicks > 0 &&
                _tickCounter % BenchmarkSettings.WaterObjectTickSafetyNetTicks == 0;

            // backing arrayは増設時に差し替わるため、object loopの直前に一度だけ再取得する。
            // Re-read backing arrays once before the object loop because growth can replace them.
            if (!WaterObjectFastPathMapAccess.TryGetLiveColumns(
                    out var columns, out var counts))
            {
                return true;
            }

            var entries = WaterObjectFastPathLifecycle.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var waterObject = entry.WaterObject;

                // 初回だけ固定座標を解決し、vanillaで値とeventを同期する。
                // Resolve immutable coordinates once, then use vanilla to synchronize value and event.
                if (!entry.Resolved)
                {
                    WaterObjectFastPathMapAccess.ResolveEntry(entry);
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
                    // 実際の値更新とevent発火はvanillaに任せ、挙動を完全一致させる。
                    // Delegate mutation and event dispatch to vanilla for bit-identical behavior.
                    waterObject.UpdateWaterAboveBase();
                }
            }

            // callbackでworldが切り替わった可能性も、vanilla抑止の直前に再検証する。
            // Revalidate after callbacks and immediately before suppressing vanilla.
            if (!WaterObjectFastPathLifecycle.IsActiveService(service) ||
                !WaterObjectFastPathLifecycle.MatchesRegisteredMap(
                    WaterObjectFastPathMapAccess.BoundWaterMap))
            {
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            // 決定的な失敗を毎tick繰り返さず、このworldではvanillaへ固定退避する。
            // Avoid retrying a deterministic failure every tick; use vanilla for this world.
            WaterObjectFastPathMapAccess.MarkFailed();

            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Debug.LogWarning(
                    "[T3MP] WaterObjectTickFastPath fell back to vanilla: " + exception);
            }

            return true;
        }
    }

    internal static void ResetWorldState()
    {
        _tickCounter = 0;
        _warned = 0;
    }

    // live columnからCeiledWaterHeight-baseZを再現し、0未満を切り捨てる。
    // Recreates CeiledWaterHeight minus baseZ from live columns and clamps below zero.
    private static int ComputeWaterAboveBase(
        WaterObjectFastPathEntry entry,
        ReadOnlyWaterColumn[] columns,
        byte[] counts)
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
            var index3D = i * WaterObjectFastPathMapAccess.VerticalStride + cell2D;
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
}
