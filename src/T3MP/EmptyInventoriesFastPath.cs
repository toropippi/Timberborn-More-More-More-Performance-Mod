using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BaseComponentSystem;
using Timberborn.BehaviorSystem;
using Timberborn.InventorySystem;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// Fast path for Timberborn.Emptying.EmptyInventoriesLaborBehavior.Decide.
///
/// The district's emptiable-inventories list is only populated while a
/// building is explicitly marked for emptying; on typical unattended runs it
/// is empty almost all of the time, yet every idle laborer pays two component
/// lookups plus an enumerator per tick to find that out. When the list is
/// empty, vanilla Decide performs no side effects and returns
/// Decision.ReleaseNow(), so returning that directly is behavior-identical.
/// Any non-empty list falls through to the vanilla method untouched.
/// </summary>
internal static class EmptyInventoriesFastPath
{
    private static bool _initialized;
    private static bool _disabled;
    private static FieldInfo? _districtBuildingField;
    private static PropertyInfo? _districtProperty;
    private static MethodInfo? _getRegistryMethod;
    private static FieldInfo? _emptiableInventoriesField;
    private static int _warnCount;

    private static long _fastReleases;
    private static long _vanillaPasses;

    private sealed class BehaviorContext
    {
        public BaseComponent? DistrictBuilding;
    }

    private sealed class DistrictContext
    {
        public object? Registry;
    }

    private static readonly ConditionalWeakTable<object, BehaviorContext> BehaviorContexts =
        new ConditionalWeakTable<object, BehaviorContext>();

    private static readonly ConditionalWeakTable<object, DistrictContext> DistrictContexts =
        new ConditionalWeakTable<object, DistrictContext>();

    public static string GetSummary()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "fastReleases={0}, vanillaPasses={1}",
            Interlocked.Read(ref _fastReleases),
            Interlocked.Read(ref _vanillaPasses));
    }

    public static bool TryFastDecide(object behavior, ref Decision result)
    {
        if (_disabled)
        {
            return false;
        }

        if (!_initialized)
        {
            Initialize(behavior.GetType());
            if (_disabled)
            {
                return false;
            }
        }

        try
        {
            if (!BehaviorContexts.TryGetValue(behavior, out var behaviorContext))
            {
                behaviorContext = new BehaviorContext
                {
                    DistrictBuilding = _districtBuildingField!.GetValue(behavior) as BaseComponent
                };
                BehaviorContexts.Add(behavior, behaviorContext);
            }

            var districtBuilding = behaviorContext.DistrictBuilding;
            if (districtBuilding is null)
            {
                return false;
            }

            // DistrictBuilding.District changes at runtime; read it live.
            var district = _districtProperty!.GetValue(districtBuilding) as BaseComponent;
            if (!district)
            {
                // Vanilla skips the loop entirely and returns ReleaseNow.
                result = Decision.ReleaseNow();
                Interlocked.Increment(ref _fastReleases);
                return true;
            }

            if (!DistrictContexts.TryGetValue(district!, out var districtContext))
            {
                districtContext = new DistrictContext
                {
                    Registry = _getRegistryMethod!.Invoke(district, null)
                };
                DistrictContexts.Add(district!, districtContext);
            }

            if (districtContext.Registry is null)
            {
                return false;
            }

            if (_emptiableInventoriesField!.GetValue(districtContext.Registry) is not List<Inventories> emptiableInventories)
            {
                return false;
            }

            if (emptiableInventories.Count == 0)
            {
                // Vanilla would resolve EmptyingStarter (a pure lookup),
                // iterate zero inventories, and return ReleaseNow.
                result = Decision.ReleaseNow();
                Interlocked.Increment(ref _fastReleases);
                return true;
            }

            Interlocked.Increment(ref _vanillaPasses);
            return false;
        }
        catch (Exception exception)
        {
            Disable($"runtime failure: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static void Initialize(Type behaviorType)
    {
        _initialized = true;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _districtBuildingField = behaviorType.GetField("_districtBuilding", instanceFlags);
        if (_districtBuildingField is null)
        {
            Disable("_districtBuilding field was not found.");
            return;
        }

        _districtProperty = _districtBuildingField.FieldType.GetProperty("District", instanceFlags);
        if (_districtProperty is null)
        {
            Disable("DistrictBuilding.District property was not found.");
            return;
        }

        var registryType = behaviorType.Assembly.GetType("Timberborn.Emptying.DistrictEmptiableInventoriesRegistry");
        if (registryType is null)
        {
            Disable("DistrictEmptiableInventoriesRegistry type was not found.");
            return;
        }

        _emptiableInventoriesField = registryType.GetField("_emptiableInventories", instanceFlags);
        if (_emptiableInventoriesField is null)
        {
            Disable("_emptiableInventories field was not found.");
            return;
        }

        var getComponent = typeof(BaseComponent).GetMethod("GetComponent", Type.EmptyTypes);
        if (getComponent is null)
        {
            Disable("BaseComponent.GetComponent<T>() was not found.");
            return;
        }

        try
        {
            _getRegistryMethod = getComponent.MakeGenericMethod(registryType);
        }
        catch (Exception exception)
        {
            Disable($"GetComponent specialization failed: {exception.Message}");
        }
    }

    private static void Disable(string reason)
    {
        _disabled = true;
        if (_warnCount++ < 3)
        {
            Debug.LogWarning($"[T3MP] EmptyInventories fast path disabled: {reason}");
        }
    }
}
