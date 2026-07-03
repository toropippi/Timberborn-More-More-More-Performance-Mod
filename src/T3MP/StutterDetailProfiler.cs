using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class StutterDetailProfiler
{
    public static void BeginRangedEffectBuildingTick(object instance, out RangedEffectBuildingState state)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            state = RangedEffectBuildingState.Inactive;
            return;
        }

        state = new RangedEffectBuildingState(
            true,
            Stopwatch.GetTimestamp(),
            BenchmarkModeController.CurrentMode,
            Time.frameCount,
            TryGetBoolField(instance, "_wasActive"),
            TryGetPrivateBoolProperty(instance, "Active"),
            TryGetRangedEffectSubscriberCount(instance));
    }

    public static void EndRangedEffectBuildingTick(object instance, RangedEffectBuildingState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        if (elapsedMilliseconds < BenchmarkSettings.RangedEffectSlowThresholdMilliseconds)
        {
            return;
        }

        var afterWasActive = TryGetBoolField(instance, "_wasActive");
        var afterActive = TryGetPrivateBoolProperty(instance, "Active");
        var afterSubscriberCount = TryGetRangedEffectSubscriberCount(instance);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RangedEffectSlow ms={0:F3}, frame={1}, mode={2}, type={3}, name={4}, template={5}, radius={6}, effectArea={7}, beforeActive={8}, afterActive={9}, beforeWasActive={10}, afterWasActive={11}, activeChanged={12}, subscribersBefore={13}, subscribersAfter={14}, mechanical={15}, powered={16}, mechanicalEfficiency={17:F3}, blockUnblocked={18}, applierActive={19}, applierEffects={20}, managedMemoryMb={21:F1}",
            elapsedMilliseconds,
            state.Frame,
            state.Mode,
            GetShortTypeName(instance.GetType()),
            TryGetStringProperty(instance, "Name") ?? "<unknown>",
            TryGetTemplateName(instance) ?? "<unknown>",
            TryGetEffectRadius(instance),
            TryGetEffectAreaCount(instance),
            FormatNullable(state.BeforeActive),
            FormatNullable(afterActive),
            FormatNullable(state.BeforeWasActive),
            FormatNullable(afterWasActive),
            state.BeforeWasActive.HasValue && afterWasActive.HasValue && state.BeforeWasActive.Value != afterWasActive.Value,
            state.SubscriberCount,
            afterSubscriberCount,
            HasFieldObject(instance, "_mechanicalBuilding"),
            FormatNullable(TryGetMechanicalActiveAndPowered(instance)),
            TryGetMechanicalEfficiency(instance),
            FormatNullable(TryGetBlockUnblocked(instance)),
            FormatNullable(TryGetApplierActive(instance)),
            TryGetApplierEffectsCount(instance),
            GC.GetTotalMemory(false) / 1048576.0));
    }

    public static void BeginRangedEffectApplierUpdate(object instance, bool active, out RangedEffectApplierState state)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            state = RangedEffectApplierState.Inactive;
            return;
        }

        state = new RangedEffectApplierState(
            true,
            Stopwatch.GetTimestamp(),
            BenchmarkModeController.CurrentMode,
            Time.frameCount,
            active,
            TryGetBoolProperty(instance, "Active"),
            TryGetDelegateInvocationCount(instance, "ActiveChanged"),
            TryGetImmutableArrayLength(instance, "_effectAreaCoords"),
            TryGetImmutableArrayLength(instance, "<Effects>k__BackingField"));
    }

    public static void EndRangedEffectApplierUpdate(object instance, RangedEffectApplierState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        if (elapsedMilliseconds < BenchmarkSettings.RangedEffectSlowThresholdMilliseconds)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RangedEffectApplierSlow ms={0:F3}, frame={1}, mode={2}, requestedActive={3}, beforeActive={4}, afterActive={5}, subscribersBefore={6}, subscribersAfter={7}, effectArea={8}, effects={9}, name={10}, managedMemoryMb={11:F1}",
            elapsedMilliseconds,
            state.Frame,
            state.Mode,
            state.RequestedActive,
            FormatNullable(state.BeforeActive),
            FormatNullable(TryGetBoolProperty(instance, "Active")),
            state.SubscriberCount,
            TryGetDelegateInvocationCount(instance, "ActiveChanged"),
            state.EffectAreaCount,
            state.EffectsCount,
            TryGetStringProperty(instance, "Name") ?? "<unknown>",
            GC.GetTotalMemory(false) / 1048576.0));
    }

    public static void BeginStatusAggregatorUpdate(object instance, out StatusAggregatorState state)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            state = StatusAggregatorState.Inactive;
            return;
        }

        state = new StatusAggregatorState(
            true,
            Stopwatch.GetTimestamp(),
            BenchmarkModeController.CurrentMode,
            Time.frameCount,
            TryGetCollectionCount(instance, "_statuses"),
            TryGetDictionaryCount(instance, "_visibleStatuses"));
    }

    public static void EndStatusAggregatorUpdate(object instance, StatusAggregatorState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        if (elapsedMilliseconds < BenchmarkSettings.StatusAggregatorSlowThresholdMilliseconds)
        {
            return;
        }

        var activeStatuses = CountActiveStatuses(instance);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] StatusAggregatorSlow ms={0:F3}, frame={1}, mode={2}, statusesBefore={3}, statusesAfter={4}, activeStatuses={5}, visibleKeysBefore={6}, visibleKeysAfter={7}, visibleInstances={8}, managedMemoryMb={9:F1}",
            elapsedMilliseconds,
            state.Frame,
            state.Mode,
            state.StatusCount,
            TryGetCollectionCount(instance, "_statuses"),
            activeStatuses,
            state.VisibleKeyCount,
            TryGetDictionaryCount(instance, "_visibleStatuses"),
            CountVisibleStatusInstances(instance),
            GC.GetTotalMemory(false) / 1048576.0));
    }

    public static void BeginUnreachableHomeTick(object instance, out UnreachableHomeState state)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            state = UnreachableHomeState.Inactive;
            return;
        }

        state = new UnreachableHomeState(
            true,
            Stopwatch.GetTimestamp(),
            BenchmarkModeController.CurrentMode,
            Time.frameCount,
            TryGetBoolField(instance, "_checkHomeReachability"),
            TryGetDwellerHasHome(instance),
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static void EndUnreachableHomeTick(object instance, UnreachableHomeState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        var afterCheck = TryGetBoolField(instance, "_checkHomeReachability");
        var afterHasHome = TryGetDwellerHasHome(instance);
        var didUnassign = state.BeforeHasHome == true && afterHasHome == false;
        if (elapsedMilliseconds < BenchmarkSettings.UnreachableHomeSlowThresholdMilliseconds && !didUnassign)
        {
            return;
        }

        var managedMemory = GC.GetTotalMemory(false);
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] UnreachableHomeSlow ms={0:F3}, frame={1}, mode={2}, checkBefore={3}, checkAfter={4}, hasHomeBefore={5}, hasHomeAfter={6}, didUnassign={7}, citizenHasDistrict={8}, homeDistrictMatches={9}, managedMemoryMb={10:F1}, managedMemoryDeltaMb={11:F1}, gc0Delta={12}, gc1Delta={13}, gc2Delta={14}",
            elapsedMilliseconds,
            state.Frame,
            state.Mode,
            FormatNullable(state.BeforeCheck),
            FormatNullable(afterCheck),
            FormatNullable(state.BeforeHasHome),
            FormatNullable(afterHasHome),
            didUnassign,
            FormatNullable(TryGetCitizenHasAssignedDistrict(instance)),
            FormatNullable(TryGetHomeDistrictMatchesAssignedDistrict(instance)),
            managedMemory / 1048576.0,
            (managedMemory - state.ManagedMemory) / 1048576.0,
            gc0 - state.Gc0,
            gc1 - state.Gc1,
            gc2 - state.Gc2));
    }

    public static void BeginNavMeshNotify(object instance, MethodBase originalMethod, object? navMeshUpdate, out NavMeshNotifyState state)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            state = NavMeshNotifyState.Inactive;
            return;
        }

        state = new NavMeshNotifyState(
            true,
            Stopwatch.GetTimestamp(),
            BenchmarkModeController.CurrentMode,
            Time.frameCount,
            originalMethod.Name,
            GetNavMeshListenerCount(instance, originalMethod.Name),
            navMeshUpdate is null ? "<null>" : GetShortTypeName(navMeshUpdate.GetType()),
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static void EndNavMeshNotify(NavMeshNotifyState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        if (elapsedMilliseconds < BenchmarkSettings.NavMeshNotifySlowThresholdMilliseconds)
        {
            return;
        }

        var managedMemory = GC.GetTotalMemory(false);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] NavMeshNotifySlow method={0}, updateType={1}, listeners={2}, ms={3:F3}, frame={4}, mode={5}, managedMemoryMb={6:F1}, managedMemoryDeltaMb={7:F1}, gc0Delta={8}, gc1Delta={9}, gc2Delta={10}",
            state.MethodName,
            state.UpdateType,
            state.ListenerCount,
            elapsedMilliseconds,
            state.Frame,
            state.Mode,
            managedMemory / 1048576.0,
            (managedMemory - state.ManagedMemory) / 1048576.0,
            GC.CollectionCount(0) - state.Gc0,
            GC.CollectionCount(1) - state.Gc1,
            GC.CollectionCount(2) - state.Gc2));
    }

    public static void BeginRuntimeHotspot(object instance, MethodBase originalMethod, object? context, out RuntimeHotspotState state)
    {
        if (!BenchmarkSettings.EnableStutterDetailProfiler)
        {
            state = RuntimeHotspotState.Inactive;
            return;
        }

        state = new RuntimeHotspotState(
            true,
            Stopwatch.GetTimestamp(),
            BenchmarkModeController.CurrentMode,
            Time.frameCount,
            GetShortTypeName(instance.GetType()),
            originalMethod.Name,
            context is null ? string.Empty : GetShortTypeName(context.GetType()),
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static void EndRuntimeHotspot(RuntimeHotspotState state)
    {
        if (!state.Active)
        {
            return;
        }

        var elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - state.StartTimestamp);
        if (elapsedMilliseconds < BenchmarkSettings.RuntimeHotspotSlowThresholdMilliseconds)
        {
            return;
        }

        var managedMemory = GC.GetTotalMemory(false);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RuntimeHotspotSlow type={0}, method={1}, context={2}, ms={3:F3}, frame={4}, mode={5}, managedMemoryMb={6:F1}, managedMemoryDeltaMb={7:F1}, gc0Delta={8}, gc1Delta={9}, gc2Delta={10}",
            state.TypeName,
            state.MethodName,
            state.ContextType,
            elapsedMilliseconds,
            state.Frame,
            state.Mode,
            managedMemory / 1048576.0,
            (managedMemory - state.ManagedMemory) / 1048576.0,
            GC.CollectionCount(0) - state.Gc0,
            GC.CollectionCount(1) - state.Gc1,
            GC.CollectionCount(2) - state.Gc2));
    }

    private static int TryGetRangedEffectSubscriberCount(object rangedEffectBuilding)
    {
        var applier = TryGetFieldObject(rangedEffectBuilding, "_rangedEffectApplier");
        return applier is null ? -1 : TryGetDelegateInvocationCount(applier, "ActiveChanged");
    }

    private static int TryGetEffectAreaCount(object rangedEffectBuilding)
    {
        var applier = TryGetFieldObject(rangedEffectBuilding, "_rangedEffectApplier");
        if (applier is not null)
        {
            var count = TryGetImmutableArrayLength(applier, "_effectAreaCoords");
            if (count >= 0)
            {
                return count;
            }
        }

        var range = TryGetFieldObject(rangedEffectBuilding, "_blockObjectRange");
        var radius = TryGetEffectRadius(rangedEffectBuilding);
        if (range is null || radius < 0)
        {
            return -1;
        }

        try
        {
            var method = range.GetType().GetMethod("GetBlocksInRectangularRadius", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method?.Invoke(range, new object[] { radius }) is IEnumerable enumerable)
            {
                var count = 0;
                foreach (var _ in enumerable)
                {
                    count++;
                }

                return count;
            }
        }
        catch (Exception)
        {
            return -1;
        }

        return -1;
    }

    private static int TryGetEffectRadius(object rangedEffectBuilding)
    {
        var spec = TryGetFieldObject(rangedEffectBuilding, "_rangedEffectBuildingSpec");
        if (spec is null)
        {
            return -1;
        }

        return TryGetIntProperty(spec, "EffectRadius") ?? -1;
    }

    private static string? TryGetTemplateName(object rangedEffectBuilding)
    {
        var spec = TryGetFieldObject(rangedEffectBuilding, "_templateSpec");
        return spec is null ? null : TryGetStringProperty(spec, "TemplateName");
    }

    private static bool? TryGetMechanicalActiveAndPowered(object rangedEffectBuilding)
    {
        var mechanical = TryGetFieldObject(rangedEffectBuilding, "_mechanicalBuilding");
        return mechanical is null ? null : TryGetBoolProperty(mechanical, "ActiveAndPowered");
    }

    private static float TryGetMechanicalEfficiency(object rangedEffectBuilding)
    {
        var mechanical = TryGetFieldObject(rangedEffectBuilding, "_mechanicalBuilding");
        return mechanical is null ? -1f : TryGetFloatProperty(mechanical, "Efficiency") ?? -1f;
    }

    private static bool? TryGetBlockUnblocked(object rangedEffectBuilding)
    {
        var blockable = TryGetFieldObject(rangedEffectBuilding, "_blockableObject");
        return blockable is null ? null : TryGetBoolProperty(blockable, "IsUnblocked");
    }

    private static bool? TryGetApplierActive(object rangedEffectBuilding)
    {
        var applier = TryGetFieldObject(rangedEffectBuilding, "_rangedEffectApplier");
        return applier is null ? null : TryGetBoolProperty(applier, "Active");
    }

    private static bool? TryGetDwellerHasHome(object unreachableHomeUnassigner)
    {
        var dweller = TryGetFieldObject(unreachableHomeUnassigner, "_dweller");
        return dweller is null ? null : TryGetBoolProperty(dweller, "HasHome");
    }

    private static bool? TryGetCitizenHasAssignedDistrict(object unreachableHomeUnassigner)
    {
        var citizen = TryGetFieldObject(unreachableHomeUnassigner, "_citizen");
        return citizen is null ? null : TryGetBoolProperty(citizen, "HasAssignedDistrict");
    }

    private static bool? TryGetHomeDistrictMatchesAssignedDistrict(object unreachableHomeUnassigner)
    {
        try
        {
            var dweller = TryGetFieldObject(unreachableHomeUnassigner, "_dweller");
            var citizen = TryGetFieldObject(unreachableHomeUnassigner, "_citizen");
            var home = dweller is null ? null : TryGetPropertyObject(dweller, "Home");
            var assignedDistrict = citizen is null ? null : TryGetPropertyObject(citizen, "AssignedDistrict");
            if (home is null || assignedDistrict is null)
            {
                return null;
            }

            var getComponent = home.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "GetComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
            if (getComponent is null)
            {
                return null;
            }

            var districtBuildingType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Timberborn.GameDistricts.DistrictBuilding", throwOnError: false))
                .FirstOrDefault(type => type is not null);
            if (districtBuildingType is null)
            {
                return null;
            }

            var districtBuilding = getComponent.MakeGenericMethod(districtBuildingType).Invoke(home, Array.Empty<object>());
            var homeDistrict = districtBuilding is null ? null : TryGetPropertyObject(districtBuilding, "District");
            return homeDistrict is not null && ReferenceEquals(homeDistrict, assignedDistrict);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryGetApplierEffectsCount(object rangedEffectBuilding)
    {
        var applier = TryGetFieldObject(rangedEffectBuilding, "_rangedEffectApplier");
        return applier is null ? -1 : TryGetImmutableArrayLength(applier, "<Effects>k__BackingField");
    }

    private static int CountActiveStatuses(object statusAggregator)
    {
        var statuses = TryGetFieldObject(statusAggregator, "_statuses") as IEnumerable;
        if (statuses is null)
        {
            return -1;
        }

        var count = 0;
        foreach (var status in statuses)
        {
            if (status is not null && TryGetBoolProperty(status, "IsActive") == true)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountVisibleStatusInstances(object statusAggregator)
    {
        if (TryGetFieldObject(statusAggregator, "_visibleStatuses") is not IDictionary dictionary)
        {
            return -1;
        }

        var count = 0;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is ICollection collection)
            {
                count += collection.Count;
            }
        }

        return count;
    }

    private static int GetNavMeshListenerCount(object registry, string methodName)
    {
        var fieldName = methodName == "NotifyAllInstant" ? "_instantNavMeshListeners" : "_navMeshListeners";
        return TryGetFieldObject(registry, fieldName) is ICollection collection ? collection.Count : -1;
    }

    private static bool HasFieldObject(object instance, string fieldName)
    {
        return TryGetFieldObject(instance, fieldName) is not null;
    }

    private static object? TryGetFieldObject(object instance, string fieldName)
    {
        try
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool? TryGetBoolField(object instance, string fieldName)
    {
        return TryGetFieldObject(instance, fieldName) is bool value ? value : null;
    }

    private static bool? TryGetPrivateBoolProperty(object instance, string propertyName)
    {
        return TryGetBoolProperty(instance, propertyName, includeNonPublic: true);
    }

    private static bool? TryGetBoolProperty(object instance, string propertyName, bool includeNonPublic = false)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic)
            {
                flags |= BindingFlags.NonPublic;
            }

            var property = instance.GetType().GetProperty(propertyName, flags);
            return property?.GetValue(instance) is bool value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? TryGetIntProperty(object instance, string propertyName)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance) is int value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static float? TryGetFloatProperty(object instance, string propertyName)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance) is float value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetStringProperty(object instance, string propertyName)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryGetDelegateInvocationCount(object instance, string eventFieldName)
    {
        try
        {
            var field = instance.GetType().GetField(eventFieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(instance) is Delegate handler)
            {
                return handler.GetInvocationList().Length;
            }
        }
        catch (Exception)
        {
            return -1;
        }

        return 0;
    }

    private static object? TryGetPropertyObject(object instance, string propertyName)
    {
        try
        {
            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int TryGetImmutableArrayLength(object instance, string fieldName)
    {
        try
        {
            var value = TryGetFieldObject(instance, fieldName);
            if (value is null)
            {
                return -1;
            }

            var lengthProperty = value.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public);
            if (lengthProperty?.GetValue(value) is int length)
            {
                return length;
            }
        }
        catch (Exception)
        {
            return -1;
        }

        return -1;
    }

    private static int TryGetCollectionCount(object instance, string fieldName)
    {
        return TryGetFieldObject(instance, fieldName) is ICollection collection ? collection.Count : -1;
    }

    private static int TryGetDictionaryCount(object instance, string fieldName)
    {
        return TryGetFieldObject(instance, fieldName) is IDictionary dictionary ? dictionary.Count : -1;
    }

    private static string FormatNullable(bool? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "<null>";
    }

    private static string GetShortTypeName(Type type)
    {
        var fullName = type.FullName ?? type.Name;
        const string prefix = "Timberborn.";
        return fullName.StartsWith(prefix, StringComparison.Ordinal) ? fullName.Substring(prefix.Length) : fullName;
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    public readonly struct RangedEffectBuildingState
    {
        public static readonly RangedEffectBuildingState Inactive = new RangedEffectBuildingState(false, 0, BenchmarkMode.Vanilla, 0, null, null, -1);

        public RangedEffectBuildingState(bool active, long startTimestamp, BenchmarkMode mode, int frame, bool? beforeWasActive, bool? beforeActive, int subscriberCount)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Mode = mode;
            Frame = frame;
            BeforeWasActive = beforeWasActive;
            BeforeActive = beforeActive;
            SubscriberCount = subscriberCount;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public BenchmarkMode Mode { get; }
        public int Frame { get; }
        public bool? BeforeWasActive { get; }
        public bool? BeforeActive { get; }
        public int SubscriberCount { get; }
    }

    public readonly struct RangedEffectApplierState
    {
        public static readonly RangedEffectApplierState Inactive = new RangedEffectApplierState(false, 0, BenchmarkMode.Vanilla, 0, false, null, -1, -1, -1);

        public RangedEffectApplierState(bool active, long startTimestamp, BenchmarkMode mode, int frame, bool requestedActive, bool? beforeActive, int subscriberCount, int effectAreaCount, int effectsCount)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Mode = mode;
            Frame = frame;
            RequestedActive = requestedActive;
            BeforeActive = beforeActive;
            SubscriberCount = subscriberCount;
            EffectAreaCount = effectAreaCount;
            EffectsCount = effectsCount;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public BenchmarkMode Mode { get; }
        public int Frame { get; }
        public bool RequestedActive { get; }
        public bool? BeforeActive { get; }
        public int SubscriberCount { get; }
        public int EffectAreaCount { get; }
        public int EffectsCount { get; }
    }

    public readonly struct StatusAggregatorState
    {
        public static readonly StatusAggregatorState Inactive = new StatusAggregatorState(false, 0, BenchmarkMode.Vanilla, 0, -1, -1);

        public StatusAggregatorState(bool active, long startTimestamp, BenchmarkMode mode, int frame, int statusCount, int visibleKeyCount)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Mode = mode;
            Frame = frame;
            StatusCount = statusCount;
            VisibleKeyCount = visibleKeyCount;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public BenchmarkMode Mode { get; }
        public int Frame { get; }
        public int StatusCount { get; }
        public int VisibleKeyCount { get; }
    }

    public readonly struct UnreachableHomeState
    {
        public static readonly UnreachableHomeState Inactive = new UnreachableHomeState(false, 0, BenchmarkMode.Vanilla, 0, null, null, 0, 0, 0, 0);

        public UnreachableHomeState(
            bool active,
            long startTimestamp,
            BenchmarkMode mode,
            int frame,
            bool? beforeCheck,
            bool? beforeHasHome,
            long managedMemory,
            int gc0,
            int gc1,
            int gc2)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Mode = mode;
            Frame = frame;
            BeforeCheck = beforeCheck;
            BeforeHasHome = beforeHasHome;
            ManagedMemory = managedMemory;
            Gc0 = gc0;
            Gc1 = gc1;
            Gc2 = gc2;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public BenchmarkMode Mode { get; }
        public int Frame { get; }
        public bool? BeforeCheck { get; }
        public bool? BeforeHasHome { get; }
        public long ManagedMemory { get; }
        public int Gc0 { get; }
        public int Gc1 { get; }
        public int Gc2 { get; }
    }

    public readonly struct NavMeshNotifyState
    {
        public static readonly NavMeshNotifyState Inactive = new NavMeshNotifyState(false, 0, BenchmarkMode.Vanilla, 0, string.Empty, -1, string.Empty, 0, 0, 0, 0);

        public NavMeshNotifyState(
            bool active,
            long startTimestamp,
            BenchmarkMode mode,
            int frame,
            string methodName,
            int listenerCount,
            string updateType,
            long managedMemory,
            int gc0,
            int gc1,
            int gc2)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Mode = mode;
            Frame = frame;
            MethodName = methodName;
            ListenerCount = listenerCount;
            UpdateType = updateType;
            ManagedMemory = managedMemory;
            Gc0 = gc0;
            Gc1 = gc1;
            Gc2 = gc2;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public BenchmarkMode Mode { get; }
        public int Frame { get; }
        public string MethodName { get; }
        public int ListenerCount { get; }
        public string UpdateType { get; }
        public long ManagedMemory { get; }
        public int Gc0 { get; }
        public int Gc1 { get; }
        public int Gc2 { get; }
    }

    public readonly struct RuntimeHotspotState
    {
        public static readonly RuntimeHotspotState Inactive = new RuntimeHotspotState(false, 0, BenchmarkMode.Vanilla, 0, string.Empty, string.Empty, string.Empty, 0, 0, 0, 0);

        public RuntimeHotspotState(
            bool active,
            long startTimestamp,
            BenchmarkMode mode,
            int frame,
            string typeName,
            string methodName,
            string contextType,
            long managedMemory,
            int gc0,
            int gc1,
            int gc2)
        {
            Active = active;
            StartTimestamp = startTimestamp;
            Mode = mode;
            Frame = frame;
            TypeName = typeName;
            MethodName = methodName;
            ContextType = contextType;
            ManagedMemory = managedMemory;
            Gc0 = gc0;
            Gc1 = gc1;
            Gc2 = gc2;
        }

        public bool Active { get; }
        public long StartTimestamp { get; }
        public BenchmarkMode Mode { get; }
        public int Frame { get; }
        public string TypeName { get; }
        public string MethodName { get; }
        public string ContextType { get; }
        public long ManagedMemory { get; }
        public int Gc0 { get; }
        public int Gc1 { get; }
        public int Gc2 { get; }
    }
}
