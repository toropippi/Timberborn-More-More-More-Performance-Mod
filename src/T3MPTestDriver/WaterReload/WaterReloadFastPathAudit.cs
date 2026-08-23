using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace T3MPTestDriver;

internal enum WaterReloadAuditStatus
{
    Pending,
    Passed,
    Failed,
}

// gen1とgen2の参照を比較し、world寿命の不変条件を検証する。
// Compares gen1 and gen2 references to verify world-lifetime invariants.
internal static class WaterReloadFastPathAudit
{
    private sealed class ObjectReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ObjectReferenceComparer Instance =
            new ObjectReferenceComparer();

        public new bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    private static object? _initialService;
    private static object? _initialMap;
    private static HashSet<object>? _initialEntries;
    private static int _initialEntryCount;
    private static string _lastInitialState = "not sampled";
    private static string _lastFinalState = "not sampled";

    internal static WaterReloadAuditStatus TryCaptureInitial()
    {
        var status = WaterReloadFastPathSnapshotReader.TryRead(
            out var snapshot);
        if (status != WaterReloadAuditStatus.Passed)
        {
            return status;
        }

        var current = snapshot!;
        var mapsMatch =
            ReferenceEquals(current.RegisteredMap, current.BoundMap);
        var initialized = current.InitDone && !current.InitFailed;
        _lastInitialState =
            $"mapsMatch={mapsMatch} initialized={initialized} " +
            $"entries={current.EntryIdentities.Length}";

        // 遷移前のfast pathが実際に使用可能になるまで待つ。
        // Wait until the pre-transition fast path is genuinely usable.
        if (!mapsMatch || !initialized)
        {
            return WaterReloadAuditStatus.Pending;
        }

        _initialService = current.ActiveService;
        _initialMap = current.RegisteredMap;
        _initialEntries = new HashSet<object>(
            current.EntryIdentities,
            ObjectReferenceComparer.Instance);
        _initialEntryCount = current.EntryIdentities.Length;
        Debug.Log(
            $"[T3MPTEST] WaterReload initial service={Id(_initialService)} " +
            $"map={Id(_initialMap)} entries={_initialEntryCount}.");
        return WaterReloadAuditStatus.Passed;
    }

    internal static WaterReloadAuditStatus TryAuditFinal()
    {
        if (_initialService is null ||
            _initialMap is null ||
            _initialEntries is null)
        {
            Debug.LogError(
                "[T3MPTEST] ERROR: WaterReload initial snapshot is missing.");
            return WaterReloadAuditStatus.Failed;
        }

        var status = WaterReloadFastPathSnapshotReader.TryRead(
            out var snapshot);
        if (status != WaterReloadAuditStatus.Passed)
        {
            return status;
        }

        var current = snapshot!;
        var serviceChanged =
            !ReferenceEquals(_initialService, current.ActiveService);
        var mapsMatch =
            ReferenceEquals(current.RegisteredMap, current.BoundMap);
        var mapChanged =
            !ReferenceEquals(_initialMap, current.RegisteredMap);
        var initialized = current.InitDone && !current.InitFailed;
        var overlap = CountInitialEntryOverlap(current.EntryIdentities);

        _lastFinalState =
            $"serviceChanged={serviceChanged} mapsMatch={mapsMatch} " +
            $"mapChanged={mapChanged} initialized={initialized} overlap={overlap} " +
            $"initialEntries={_initialEntryCount} " +
            $"finalEntries={current.EntryIdentities.Length} " +
            $"initialService={Id(_initialService)} " +
            $"finalService={Id(current.ActiveService)} " +
            $"initialMap={Id(_initialMap)} " +
            $"registeredMap={Id(current.RegisteredMap)} " +
            $"boundMap={Id(current.BoundMap)}";

        // 遷移中の不一致はdeadlineまで待ち、安定後に全条件をまとめて判定する。
        // Treat transitional mismatches as pending until every final invariant is stable.
        if (!serviceChanged || !mapsMatch || !mapChanged ||
            !initialized || overlap != 0)
        {
            return WaterReloadAuditStatus.Pending;
        }

        Debug.Log(
            "[T3MPTEST] WaterReload audit passed " + _lastFinalState + ".");
        return WaterReloadAuditStatus.Passed;
    }

    internal static void LogTimeout(string phase)
    {
        var state = phase == "initial" ? _lastInitialState : _lastFinalState;
        Debug.LogError(
            $"[T3MPTEST] ERROR: WaterReload {phase} binding timed out: {state}.");
    }

    private static int CountInitialEntryOverlap(object[] finalEntries)
    {
        var overlap = 0;
        foreach (var entry in finalEntries)
        {
            if (_initialEntries!.Contains(entry))
            {
                overlap++;
            }
        }

        return overlap;
    }

    private static int Id(object value)
    {
        return RuntimeHelpers.GetHashCode(value);
    }
}
