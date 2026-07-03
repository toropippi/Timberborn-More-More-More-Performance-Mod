using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BaseComponentSystem;
using Timberborn.TickSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// Optimized-mode replacement for TickableEntityBucket.TickAll.
///
/// The vanilla dispatch path per entity per tick is:
///   SortedList.Values[i] -> TickableEntity.Tick() -> GameObject.activeInHierarchy
///   -> ImmutableArray enumerator over MeteredTickableComponent -> Enabled
///   -> MeteredTickableComponent.Tick() -> TickableComponent.Tick().
/// Diagnostics on save n10c measured ~8.25M entity ticks per 20s aggregate with
/// only ~4.08M reaching a component Tick, and roughly half of all simulation
/// time spent in this dispatch layer rather than in component logic.
///
/// This replacement keeps the exact vanilla semantics:
/// - iterates the live SortedList by index in the same order, so mid-tick Add
///   and Remove behave identically (Remove defers via _isTicking/_entitiesToRemove),
/// - performs the same GameObject.activeInHierarchy check per entity,
/// - performs the same per-component Enabled check,
/// - calls the same TickableComponent.Tick() virtual methods in the same order,
/// - replicates the vanilla exception wrapper text.
/// It only removes constant dispatch overhead: the MeteredTickableComponent
/// wrapper call (a no-op with metrics disabled), the per-entity method call,
/// the ImmutableArray enumerator, and the repeated GameObject property hops.
/// Per-entity data cached here is immutable for the entity's lifetime
/// (component array and cached GameObject reference), so no invalidation is
/// required; the ConditionalWeakTable releases entries with the entity.
/// </summary>
internal static class TickDispatchOptimizer
{
    private static bool _initialized;
    private static bool _disabled;
    private static FieldInfo? _tickableEntitiesField;
    private static FieldInfo? _isTickingField;
    private static FieldInfo? _entitiesToRemoveField;
    private static FieldInfo? _entityComponentField;
    private static FieldInfo? _tickableComponentsField;
    private static FieldInfo? _originalNameField;
    private static FieldInfo? _meteredTickableComponentField;
    private static FieldInfo? _meteredMetricsEnabledField;

    private static long _fastBucketTicks;
    private static long _fallbackEntityTicks;
    private static long _entityTicks;
    private static long _componentTicks;
    private static int _warnCount;

    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;

    private sealed class EntityEntry
    {
        public static readonly EntityEntry Invalid = new EntityEntry();

        public readonly bool Valid;
        public readonly GameObject? GameObject;
        public readonly TickableComponent[] Components;
        public readonly BaseComponent? EntityComponent;
        public readonly string OriginalName;

        private EntityEntry()
        {
            Valid = false;
            GameObject = null;
            Components = Array.Empty<TickableComponent>();
            EntityComponent = null;
            OriginalName = string.Empty;
        }

        public EntityEntry(GameObject gameObject, TickableComponent[] components, BaseComponent entityComponent, string originalName)
        {
            Valid = true;
            GameObject = gameObject;
            Components = components;
            EntityComponent = entityComponent;
            OriginalName = originalName;
        }
    }

    private static readonly ConditionalWeakTable<TickableEntity, EntityEntry> Entries = new ConditionalWeakTable<TickableEntity, EntityEntry>();
    private static readonly ConditionalWeakTable<TickableEntity, EntityEntry>.CreateValueCallback CreateEntryCallback = CreateEntry;

    public static string GetSummary()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "fastBuckets={0}, entityTicks={1}, componentTicks={2}, fallbackEntities={3}",
            Interlocked.Read(ref _fastBucketTicks),
            Interlocked.Read(ref _entityTicks),
            Interlocked.Read(ref _componentTicks),
            Interlocked.Read(ref _fallbackEntityTicks));
    }

    public static bool TryTickBucket(object bucket)
    {
        if (_disabled)
        {
            return false;
        }

        if (!_initialized)
        {
            Initialize();
            if (_disabled)
            {
                return false;
            }
        }

        SortedList<Guid, TickableEntity>? entities;
        List<TickableEntity>? entitiesToRemove;
        try
        {
            entities = _tickableEntitiesField!.GetValue(bucket) as SortedList<Guid, TickableEntity>;
            entitiesToRemove = _entitiesToRemoveField!.GetValue(bucket) as List<TickableEntity>;
        }
        catch (Exception exception)
        {
            Disable($"bucket field access failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }

        if (entities is null || entitiesToRemove is null)
        {
            Disable("bucket fields had unexpected values.");
            return false;
        }

        var isTickingField = _isTickingField!;
        isTickingField.SetValue(bucket, BoxedTrue);

        long entityTicks = 0;
        long componentTicks = 0;
        long fallbackEntityTicks = 0;
        var values = entities.Values;
        for (var i = 0; i < entities.Count; i++)
        {
            var entity = values[i];
            var entry = Entries.GetValue(entity, CreateEntryCallback);
            if (!entry.Valid)
            {
                // Vanilla per-entity tick keeps behavior for shapes this
                // optimizer does not understand (for example metrics enabled).
                fallbackEntityTicks++;
                entity.Tick();
                continue;
            }

            entityTicks++;
            try
            {
                if (entry.GameObject!.activeInHierarchy)
                {
                    var components = entry.Components;
                    for (var j = 0; j < components.Length; j++)
                    {
                        var component = components[j];
                        if (component.Enabled)
                        {
                            componentTicks++;
                            component.Tick();
                        }
                    }
                }
            }
            catch (Exception innerException)
            {
                throw BuildVanillaStyleEntityException(entity, entry, innerException);
            }
        }

        isTickingField.SetValue(bucket, BoxedFalse);
        for (var i = 0; i < entitiesToRemove.Count; i++)
        {
            entities.Remove(entitiesToRemove[i].EntityId);
        }

        entitiesToRemove.Clear();

        var fastBuckets = Interlocked.Increment(ref _fastBucketTicks);
        if (entityTicks > 0)
        {
            Interlocked.Add(ref _entityTicks, entityTicks);
        }

        if (componentTicks > 0)
        {
            Interlocked.Add(ref _componentTicks, componentTicks);
        }

        if (fallbackEntityTicks > 0)
        {
            Interlocked.Add(ref _fallbackEntityTicks, fallbackEntityTicks);
        }

        // 128 buckets per full tick round; log roughly every ~470 rounds.
        if (fastBuckets % 60000 == 5000)
        {
            Debug.Log($"[T3MP] TickDispatch summary: {GetSummary()}");
        }

        return true;
    }

    private static Exception BuildVanillaStyleEntityException(TickableEntity entity, EntityEntry entry, Exception innerException)
    {
        var text = $"Exception thrown while ticking entity {entity.EntityId}";
        try
        {
            var entityComponent = entry.EntityComponent!;
            text = !entityComponent
                ? text + " '" + entry.OriginalName + "' (destroyed)"
                : text + " '" + entityComponent.Name + "'";
        }
        catch (Exception)
        {
            text = text + " '" + entry.OriginalName + "'";
        }

        return new Exception(text, innerException);
    }

    private static EntityEntry CreateEntry(TickableEntity entity)
    {
        try
        {
            if (_entityComponentField?.GetValue(entity) is not BaseComponent entityComponent)
            {
                return EntityEntry.Invalid;
            }

            if (_tickableComponentsField?.GetValue(entity) is not ImmutableArray<MeteredTickableComponent> metered)
            {
                return EntityEntry.Invalid;
            }

            var originalName = _originalNameField?.GetValue(entity) as string ?? "<unknown>";
            var components = new TickableComponent[metered.Length];
            for (var i = 0; i < metered.Length; i++)
            {
                var meteredComponent = metered[i];
                if (meteredComponent is null ||
                    _meteredMetricsEnabledField?.GetValue(meteredComponent) is not bool metricsEnabled ||
                    metricsEnabled)
                {
                    return EntityEntry.Invalid;
                }

                if (_meteredTickableComponentField?.GetValue(meteredComponent) is not TickableComponent tickableComponent)
                {
                    return EntityEntry.Invalid;
                }

                components[i] = tickableComponent;
            }

            var gameObject = entityComponent.GameObject;
            if (gameObject is null)
            {
                return EntityEntry.Invalid;
            }

            return new EntityEntry(gameObject, components, entityComponent, originalName);
        }
        catch (Exception)
        {
            return EntityEntry.Invalid;
        }
    }

    private static void Initialize()
    {
        _initialized = true;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var bucketType = typeof(TickableEntity).Assembly.GetType("Timberborn.TickSystem.TickableEntityBucket");
        if (bucketType is null)
        {
            Disable("TickableEntityBucket type was not found.");
            return;
        }

        _tickableEntitiesField = bucketType.GetField("_tickableEntities", instanceFlags);
        _isTickingField = bucketType.GetField("_isTicking", instanceFlags);
        _entitiesToRemoveField = bucketType.GetField("_entitiesToRemove", instanceFlags);
        _entityComponentField = typeof(TickableEntity).GetField("_entityComponent", instanceFlags);
        _tickableComponentsField = typeof(TickableEntity).GetField("_tickableComponents", instanceFlags);
        _originalNameField = typeof(TickableEntity).GetField("_originalName", instanceFlags);
        _meteredTickableComponentField = typeof(MeteredTickableComponent).GetField("_tickableComponent", instanceFlags);
        _meteredMetricsEnabledField = typeof(MeteredTickableComponent).GetField("_metricsEnabled", instanceFlags);

        if (_tickableEntitiesField is null ||
            _isTickingField is null ||
            _entitiesToRemoveField is null ||
            _entityComponentField is null ||
            _tickableComponentsField is null ||
            _meteredTickableComponentField is null ||
            _meteredMetricsEnabledField is null)
        {
            Disable("one or more private tick dispatch fields were not found.");
        }
    }

    private static void Disable(string reason)
    {
        _disabled = true;
        if (_warnCount++ < 3)
        {
            Debug.LogWarning($"[T3MP] TickDispatchOptimizer disabled: {reason}");
        }
    }
}
