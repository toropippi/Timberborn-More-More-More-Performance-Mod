using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace T3MPTestDriver;

internal sealed class WaterReloadFastPathSnapshot
{
    internal readonly object ActiveService;
    internal readonly object RegisteredMap;
    internal readonly object BoundMap;
    internal readonly object[] EntryIdentities;
    internal readonly bool InitDone;
    internal readonly bool InitFailed;

    internal WaterReloadFastPathSnapshot(
        object activeService,
        object registeredMap,
        object boundMap,
        object[] entryIdentities,
        bool initDone,
        bool initFailed)
    {
        ActiveService = activeService;
        RegisteredMap = registeredMap;
        BoundMap = boundMap;
        EntryIdentities = entryIdentities;
        InitDone = initDone;
        InitFailed = initFailed;
    }
}

// reflection結果を一度のsnapshotへ固定し、遷移中の混在読取を避ける。
// Freezes reflection results into one snapshot to avoid mixed transition reads.
internal static class WaterReloadFastPathSnapshotReader
{
    private const BindingFlags AllStatic =
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    internal static WaterReloadAuditStatus TryRead(
        out WaterReloadFastPathSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            var lifecycleType = FindType(
                "T3MP.WaterObjectFastPathLifecycle");
            var mapAccessType = FindType(
                "T3MP.WaterObjectFastPathMapAccess");
            if (lifecycleType is null || mapAccessType is null)
            {
                Debug.LogError(
                    "[T3MPTEST] ERROR: WaterReload audit types were not found.");
                return WaterReloadAuditStatus.Failed;
            }

            var activeField =
                lifecycleType.GetField("_activeService", AllStatic);
            var registeredField =
                lifecycleType.GetField("_registeredWaterMap", AllStatic);
            var entriesField =
                lifecycleType.GetField("Entries", AllStatic);
            var boundField =
                mapAccessType.GetField("BoundWaterMap", AllStatic);
            var initDoneField =
                mapAccessType.GetField("_initDone", AllStatic);
            var initFailedField =
                mapAccessType.GetField("_initFailed", AllStatic);
            if (activeField is null || registeredField is null ||
                entriesField is null || boundField is null ||
                initDoneField is null || initFailedField is null)
            {
                Debug.LogError(
                    "[T3MPTEST] ERROR: WaterReload audit fields were not found.");
                return WaterReloadAuditStatus.Failed;
            }

            var activeService = activeField.GetValue(null);
            var registeredMap = registeredField.GetValue(null);
            var boundMap = boundField.GetValue(null);
            var entries = entriesField.GetValue(null) as IEnumerable;
            var initDone = initDoneField.GetValue(null) as bool?;
            var initFailed = initFailedField.GetValue(null) as bool?;
            if (activeService is null || registeredMap is null ||
                boundMap is null || entries is null ||
                initDone is null || initFailed is null)
            {
                return WaterReloadAuditStatus.Pending;
            }

            // wrapper参照を保存し、旧world entryが1件も残らないことを後で検証する。
            // Retain wrapper identities so the final audit can require zero old-world overlap.
            var entryIdentities = new List<object>();
            foreach (var entry in entries)
            {
                if (entry is not null)
                {
                    entryIdentities.Add(entry);
                }
            }

            if (entryIdentities.Count == 0)
            {
                return WaterReloadAuditStatus.Pending;
            }

            snapshot = new WaterReloadFastPathSnapshot(
                activeService,
                registeredMap,
                boundMap,
                entryIdentities.ToArray(),
                initDone.Value,
                initFailed.Value);
            return WaterReloadAuditStatus.Passed;
        }
        catch (Exception exception)
        {
            Debug.LogError(
                "[T3MPTEST] ERROR: WaterReload audit failed: " + exception);
            return WaterReloadAuditStatus.Failed;
        }
    }

    private static Type? FindType(string fullName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName))
            .FirstOrDefault(type => type is not null);
    }
}
