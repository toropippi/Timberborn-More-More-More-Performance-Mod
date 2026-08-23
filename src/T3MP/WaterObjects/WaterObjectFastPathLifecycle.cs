using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.WaterObjects;
using Timberborn.WaterSystem;
using UnityEngine;

namespace T3MP;

// active worldだけを追跡し、退役worldの遅延callbackを安全に無視する。
// Tracks only the active world and safely rejects late callbacks from retired worlds.
internal static class WaterObjectFastPathLifecycle
{
    private sealed class RetiredServiceMarker
    {
    }

    internal static readonly List<WaterObjectFastPathEntry> Entries =
        new List<WaterObjectFastPathEntry>(1024);

    private static readonly Dictionary<WaterObject, WaterObjectFastPathEntry> EntryByObject =
        new Dictionary<WaterObject, WaterObjectFastPathEntry>(
            WaterObjectReferenceEqualityComparer.Instance);

    private static readonly ConditionalWeakTable<WaterObjectService, RetiredServiceMarker>
        RetiredServices =
            new ConditionalWeakTable<WaterObjectService, RetiredServiceMarker>();

    private static WaterObjectService? _activeService;
    private static IThreadSafeWaterMap? _registeredWaterMap;
    private static int _warned;

    internal static void Register(
        WaterObjectService service,
        WaterObject waterObject)
    {
        if (waterObject == null)
        {
            return;
        }

        // 退役serviceはmapへ触れる前に拒否し、active worldの高速経路を保護する。
        // Reject retired services before map lookup to protect the active world's fast path.
        if (!ReferenceEquals(service, _activeService) &&
            RetiredServices.TryGetValue(service, out _))
        {
            return;
        }

        // entry追加前にserviceとmapを検証し、旧worldへの逆戻りを防ぐ。
        // Validate service and map before insertion to prevent rebinding to an old world.
        var registeredMap = WaterObjectFastPathMapAccess.GetRegisteredMap(waterObject);
        if (!SwitchActiveContextIfNeeded(service, registeredMap) ||
            EntryByObject.ContainsKey(waterObject))
        {
            return;
        }

        var entry = new WaterObjectFastPathEntry(waterObject);
        Entries.Add(entry);
        EntryByObject.Add(waterObject, entry);
    }

    internal static void Unregister(
        WaterObjectService service,
        WaterObject waterObject)
    {
        if (!ReferenceEquals(service, _activeService) ||
            waterObject == null ||
            !EntryByObject.Remove(waterObject))
        {
            return;
        }

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Entries[i].WaterObject, waterObject))
            {
                Entries.RemoveAt(i);
                break;
            }
        }
    }

    internal static bool IsActiveService(WaterObjectService service)
    {
        return ReferenceEquals(service, _activeService);
    }

    internal static bool MatchesRegisteredMap(IThreadSafeWaterMap? candidate)
    {
        return candidate is not null &&
            _registeredWaterMap is not null &&
            ReferenceEquals(candidate, _registeredWaterMap);
    }

    private static bool SwitchActiveContextIfNeeded(
        WaterObjectService service,
        IThreadSafeWaterMap? registeredMap)
    {
        if (_activeService is null)
        {
            _activeService = service;
            _registeredWaterMap = registeredMap;
            return true;
        }

        if (ReferenceEquals(service, _activeService))
        {
            return ValidateActiveMap(registeredMap);
        }

        // 退役serviceから遅れて届いたRegisterは、active worldを書き換えさせない。
        // Never let a late Register from a retired service replace the active world.
        if (RetiredServices.TryGetValue(service, out _))
        {
            return false;
        }

        AdoptNewContext(service, registeredMap);
        return true;
    }

    private static bool ValidateActiveMap(IThreadSafeWaterMap? registeredMap)
    {
        if (registeredMap is null)
        {
            return true;
        }

        if (_registeredWaterMap is null)
        {
            _registeredWaterMap = registeredMap;
            return true;
        }

        if (ReferenceEquals(registeredMap, _registeredWaterMap))
        {
            return true;
        }

        // 同一service内のmap交換は曖昧なので、高速化を止めてvanillaへ退避する。
        // A map swap inside one service is ambiguous, so disable the fast path and fail open.
        WaterObjectFastPathMapAccess.MarkFailed();
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Debug.LogWarning(
                "[T3MP] Water map changed inside one service; using vanilla.");
        }

        return false;
    }

    private static void AdoptNewContext(
        WaterObjectService service,
        IThreadSafeWaterMap? registeredMap)
    {
        var previousService = _activeService!;
        RetiredServices.GetValue(
            previousService,
            _ => new RetiredServiceMarker());

        // old world所有の参照を全破棄してから、新serviceとmapを採用する。
        // Drop every old-world reference before adopting the new service and map.
        ResetTrackedWorld();
        _activeService = service;
        _registeredWaterMap = registeredMap;
    }

    private static void ResetTrackedWorld()
    {
        Entries.Clear();
        EntryByObject.Clear();
        _registeredWaterMap = null;
        _warned = 0;
        WaterObjectFastPathMapAccess.Reset();
        WaterObjectTickFastPath.ResetWorldState();
    }
}
