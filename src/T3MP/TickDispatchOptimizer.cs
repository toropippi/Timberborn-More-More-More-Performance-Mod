using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
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
/// Mirrors GameObject.activeInHierarchy into TickDispatchOptimizer's dense
/// bitmask. Unity fires OnEnable/OnDisable synchronously at the exact moment
/// activeInHierarchy flips (including parent activation changes and the
/// implicit deactivation before destruction), so the bit always equals the
/// value the vanilla per-entity check would read - without the per-tick
/// native call. Attached once per tickable entity by CreateEntry.
/// </summary>
internal sealed class ActiveInHierarchySentinel : MonoBehaviour
{
    internal int SlotIndex = -1;

    private void OnEnable()
    {
        if (SlotIndex >= 0)
        {
            TickDispatchOptimizer.SetActiveBit(SlotIndex, true);
        }
    }

    private void OnDisable()
    {
        if (SlotIndex >= 0)
        {
            TickDispatchOptimizer.SetActiveBit(SlotIndex, false);
        }
    }

    private void OnDestroy()
    {
        if (SlotIndex >= 0)
        {
            TickDispatchOptimizer.FreeActiveSlot(SlotIndex);
            SlotIndex = -1;
        }
    }
}

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
    // ------------------------------------------------------------------
    // activeInHierarchy mirror. The vanilla dispatch reads
    // GameObject.activeInHierarchy per entity per tick (a native call,
    // measured ~8% of the tick budget at the compute ceiling). An
    // ActiveInHierarchySentinel MonoBehaviour on each tickable entity mirrors
    // the value into these dense bits via Unity's synchronous
    // OnEnable/OnDisable callbacks, so the sweep reads a bit with identical
    // visit-time semantics. Slots are recycled on sentinel destruction; a
    // snapshot never reads a stale slot because entity removal marks the
    // bucket dirty and the snapshot is rebuilt before its next sweep, while
    // Unity defers OnDestroy past the end of the frame.
    // ------------------------------------------------------------------
    private static ulong[] _activeBits = new ulong[1024];
    private static readonly Stack<int> _freeActiveSlots = new Stack<int>();
    private static int _nextActiveSlot;

    internal static void SetActiveBit(int slot, bool active)
    {
        if (active)
        {
            _activeBits[slot >> 6] |= 1UL << (slot & 63);
        }
        else
        {
            _activeBits[slot >> 6] &= ~(1UL << (slot & 63));
        }
    }

    internal static void FreeActiveSlot(int slot)
    {
        _activeBits[slot >> 6] &= ~(1UL << (slot & 63));
        _freeActiveSlots.Push(slot);
    }

    private static int AllocateActiveSlot(bool initialActive)
    {
        var slot = _freeActiveSlots.Count > 0 ? _freeActiveSlots.Pop() : _nextActiveSlot++;
        var words = (slot >> 6) + 1;
        if (_activeBits.Length < words)
        {
            var grown = new ulong[Math.Max(words, _activeBits.Length * 2)];
            Array.Copy(_activeBits, grown, _activeBits.Length);
            _activeBits = grown;
        }

        SetActiveBit(slot, initialActive);
        return slot;
    }

    // ------------------------------------------------------------------
    // Sentinel attachment happens in the TickableEntityBucket.Add hook, i.e.
    // during save-LOAD for the initial ~26k entities (folded into the loading
    // phase, where a few extra seconds are invisible) and per single entity
    // for births/builds afterwards. Attaching lazily in the first sweep
    // instead froze the game ~10 s at the first unpause (26k AddComponent
    // calls in one frame). Slots are looked up by CreateEntry via this table.
    // ------------------------------------------------------------------
    private sealed class ActiveSlotBox
    {
        public int Slot = -1;
    }

    private static readonly ConditionalWeakTable<TickableEntity, ActiveSlotBox> EntityActiveSlots =
        new ConditionalWeakTable<TickableEntity, ActiveSlotBox>();

    /// <summary>
    /// Called from the TickableEntityBucket.Add Harmony prefix. Attaches the
    /// activeInHierarchy sentinel to the entity's GameObject and records the
    /// mirror slot for CreateEntry.
    /// </summary>
    internal static void AttachSentinelForEntity(TickableEntity tickableEntity)
    {
        if (!BenchmarkSettings.EnableActiveInHierarchyMirror || _disabled)
        {
            return;
        }

        if (!_initialized)
        {
            Initialize();
            if (_disabled)
            {
                return;
            }
        }

        try
        {
            if (_entityComponentField?.GetValue(tickableEntity) is not BaseComponent entityComponent)
            {
                return;
            }

            var gameObject = entityComponent.GameObject;
            if (gameObject == null || !gameObject)
            {
                return;
            }

            // OnEnable may fire during AddComponent with SlotIndex still -1
            // (guarded no-op); the slot is initialized from the current truth
            // and every later transition is mirrored by the callbacks.
            var sentinel = gameObject.AddComponent<ActiveInHierarchySentinel>();
            var slot = AllocateActiveSlot(gameObject.activeInHierarchy);
            sentinel.SlotIndex = slot;
            EntityActiveSlots.GetValue(tickableEntity, CreateActiveSlotBox).Slot = slot;
        }
        catch (Exception)
        {
            // The entity keeps the exact native activeInHierarchy fallback.
        }
    }

    private static readonly ConditionalWeakTable<TickableEntity, ActiveSlotBox>.CreateValueCallback CreateActiveSlotBox =
        _ => new ActiveSlotBox();

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

    // The bucket currently being swept by TryTickBucket. Used by the
    // TickableEntityBucket.Add hook to detect a mid-sweep insertion into the
    // same bucket (which must fall back to vanilla live-list iteration).
    internal static object? CurrentSweepBucket;

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
        // Slot into the activeInHierarchy mirror bits; -1 = no sentinel yet
        // (read GameObject.activeInHierarchy directly). Assigned once by the
        // incremental sentinel warm-up.
        public int ActiveSlot = -1;

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

    // ------------------------------------------------------------------
    // Flat-array bucket dispatch (v2).
    //
    // Per bucket, all tickable components are concatenated into one flat
    // array swept front-to-back (entity boundaries in Starts). The Enabled
    // state is mirrored into EnabledBits by the EnableComponent /
    // DisableComponent Harmony prefixes - those two methods are the only
    // writers of BaseComponent.Enabled (auto-property with a private
    // setter), so the bit always equals the value the vanilla dispatch
    // would read at visit time. Disabled components therefore cost one
    // dense bit test instead of a scattered object-field read.
    //
    // The snapshot is invalidated (Dirty) by the TickableEntityBucket
    // Add/Remove hooks and by deferred removals applied at sweep end, and
    // rebuilt lazily at the start of the bucket's next sweep from the
    // per-entity EntityEntry cache (grow-only buffers, no steady-state
    // allocation). A mid-sweep Add into the bucket being swept switches
    // the remainder of that pass to vanilla live-SortedList iteration,
    // which reproduces the vanilla semantics of insertions during TickAll
    // exactly (including the re-tick after an insertion before the
    // current index).
    // ------------------------------------------------------------------

    internal static bool FlatHooksInstalled;

    private sealed class BucketSnapshot
    {
        public bool Dirty = true;
        public bool MidSweepAdd;
        public int EntityCount;
        public int ComponentCount;
        public TickableEntity[] Entities = Array.Empty<TickableEntity>();
        public EntityEntry[] Entries = Array.Empty<EntityEntry>();
        public GameObject?[] GameObjects = Array.Empty<GameObject?>();
        public int[] ActiveSlots = Array.Empty<int>();
        public int[] Starts = new int[1];
        public TickableComponent[] Components = Array.Empty<TickableComponent>();
        public ulong[] EnabledBits = Array.Empty<ulong>();
        public readonly Dictionary<TickableComponent, int> SlotMap = new Dictionary<TickableComponent, int>();
    }

    private static readonly ConditionalWeakTable<object, BucketSnapshot> Snapshots = new ConditionalWeakTable<object, BucketSnapshot>();
    private static readonly ConditionalWeakTable<object, BucketSnapshot>.CreateValueCallback CreateSnapshotCallback = _ => new BucketSnapshot();
    private static readonly ConditionalWeakTable<TickableComponent, BucketSnapshot> ComponentSnapshots = new ConditionalWeakTable<TickableComponent, BucketSnapshot>();

    /// <summary>
    /// Called from the EnableComponent/DisableComponent Harmony prefixes on an
    /// actual Enabled transition of a TickableComponent. Mirrors the new state
    /// into the owning bucket snapshot's bitmask.
    /// </summary>
    internal static void NotifyComponentEnabledChanged(TickableComponent component, bool enabled)
    {
        if (ComponentSnapshots.TryGetValue(component, out var snapshot) &&
            snapshot.SlotMap.TryGetValue(component, out var slot))
        {
            if (enabled)
            {
                snapshot.EnabledBits[slot >> 6] |= 1UL << (slot & 63);
            }
            else
            {
                snapshot.EnabledBits[slot >> 6] &= ~(1UL << (slot & 63));
            }
        }
    }

    /// <summary>
    /// Called from the TickableEntityBucket.Add/Remove Harmony prefixes.
    /// </summary>
    internal static void NotifyBucketMembershipChanged(object bucket, bool isAdd)
    {
        if (Snapshots.TryGetValue(bucket, out var snapshot))
        {
            snapshot.Dirty = true;
            if (isAdd && ReferenceEquals(CurrentSweepBucket, bucket))
            {
                snapshot.MidSweepAdd = true;
            }
        }
    }

    private static void RebuildSnapshot(SortedList<Guid, TickableEntity> entities, BucketSnapshot snapshot)
    {
        var count = entities.Count;
        var values = entities.Values;
        if (snapshot.Entities.Length < count)
        {
            var capacity = Math.Max(count, snapshot.Entities.Length * 2);
            snapshot.Entities = new TickableEntity[capacity];
            snapshot.Entries = new EntityEntry[capacity];
            snapshot.GameObjects = new GameObject?[capacity];
            snapshot.ActiveSlots = new int[capacity];
            snapshot.Starts = new int[capacity + 1];
        }

        var totalComponents = 0;
        for (var i = 0; i < count; i++)
        {
            var entity = values[i];
            var entry = Entries.GetValue(entity, CreateEntryCallback);
            snapshot.Entities[i] = entity;
            snapshot.Entries[i] = entry;
            if (entry.Valid)
            {
                totalComponents += entry.Components.Length;
            }
        }

        if (snapshot.Components.Length < totalComponents)
        {
            snapshot.Components = new TickableComponent[Math.Max(totalComponents, snapshot.Components.Length * 2)];
        }

        var words = (totalComponents + 63) >> 6;
        if (snapshot.EnabledBits.Length < words)
        {
            snapshot.EnabledBits = new ulong[Math.Max(words, snapshot.EnabledBits.Length * 2)];
        }

        Array.Clear(snapshot.EnabledBits, 0, snapshot.EnabledBits.Length);
        snapshot.SlotMap.Clear();
        var flatIndex = 0;
        for (var i = 0; i < count; i++)
        {
            snapshot.Starts[i] = flatIndex;
            var entry = snapshot.Entries[i];
            if (!entry.Valid)
            {
                snapshot.GameObjects[i] = null;
                snapshot.ActiveSlots[i] = -1;
                continue;
            }

            snapshot.GameObjects[i] = entry.GameObject;
            snapshot.ActiveSlots[i] = entry.ActiveSlot;
            var components = entry.Components;
            for (var j = 0; j < components.Length; j++)
            {
                var component = components[j];
                snapshot.Components[flatIndex] = component;
                snapshot.SlotMap[component] = flatIndex;
                ComponentSnapshots.AddOrUpdate(component, snapshot);
                if (component.Enabled)
                {
                    snapshot.EnabledBits[flatIndex >> 6] |= 1UL << (flatIndex & 63);
                }

                flatIndex++;
            }
        }

        snapshot.Starts[count] = flatIndex;

        // Clear stale tails so shrunk snapshots do not keep dead objects alive.
        for (var i = count; i < snapshot.EntityCount; i++)
        {
            snapshot.Entities[i] = null!;
            snapshot.Entries[i] = null!;
            snapshot.GameObjects[i] = null;
        }

        for (var j = flatIndex; j < snapshot.ComponentCount; j++)
        {
            snapshot.Components[j] = null!;
        }

        snapshot.EntityCount = count;
        snapshot.ComponentCount = flatIndex;
        snapshot.Dirty = false;
    }

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
        CurrentSweepBucket = bucket;

        long entityTicks = 0;
        long componentTicks = 0;
        long fallbackEntityTicks = 0;

        BucketSnapshot? snapshot = null;
        if (FlatHooksInstalled && BenchmarkSettings.EnableFlatTickDispatch)
        {
            snapshot = Snapshots.GetValue(bucket, CreateSnapshotCallback);
            if (snapshot.Dirty)
            {
                RebuildSnapshot(entities, snapshot);
            }

            snapshot.MidSweepAdd = false;
            var entityCount = snapshot.EntityCount;
            var snapshotEntities = snapshot.Entities;
            var snapshotEntries = snapshot.Entries;
            var gameObjects = snapshot.GameObjects;
            var activeSlots = snapshot.ActiveSlots;
            // No CreateEntry (and therefore no slot-array growth) can happen
            // during the sweep loop in the flat path, so this reference stays
            // current; mid-sweep transitions write into the same array.
            var activeBits = _activeBits;
            var starts = snapshot.Starts;
            var components = snapshot.Components;
            var enabledBits = snapshot.EnabledBits;
            var liveContinuationFrom = -1;
            for (var e = 0; e < entityCount; e++)
            {
                if (snapshot.MidSweepAdd)
                {
                    // An entity was inserted into THIS bucket mid-sweep.
                    // Vanilla indexes the live SortedList, so the remainder of
                    // the pass (including the possible re-tick of the current
                    // entity after an insertion before its index) must run
                    // against the live list.
                    liveContinuationFrom = e;
                    break;
                }

                var gameObject = gameObjects[e];
                if (gameObject is null)
                {
                    // Vanilla per-entity tick keeps behavior for shapes this
                    // optimizer does not understand (for example metrics enabled).
                    fallbackEntityTicks++;
                    snapshotEntities[e].Tick();
                    continue;
                }

                entityTicks++;
                try
                {
                    var slot = activeSlots[e];
                    var isActive = slot >= 0
                        ? (activeBits[slot >> 6] & (1UL << (slot & 63))) != 0UL
                        : gameObject.activeInHierarchy;
                    if (isActive)
                    {
                        var end = starts[e + 1];
                        for (var k = starts[e]; k < end; k++)
                        {
                            if ((enabledBits[k >> 6] & (1UL << (k & 63))) != 0UL)
                            {
                                componentTicks++;
                                components[k].Tick();
                            }
                        }
                    }
                }
                catch (Exception innerException)
                {
                    throw BuildVanillaStyleEntityException(snapshotEntities[e], snapshotEntries[e], innerException);
                }
            }

            if (liveContinuationFrom < 0 && snapshot.MidSweepAdd)
            {
                // The insertion happened during the LAST snapshot entity's tick;
                // vanilla would still continue over the grown live list.
                liveContinuationFrom = entityCount;
            }

            if (liveContinuationFrom >= 0)
            {
                snapshot.Dirty = true;
                var liveValues = entities.Values;
                for (var i = liveContinuationFrom; i < entities.Count; i++)
                {
                    fallbackEntityTicks++;
                    liveValues[i].Tick();
                }
            }
        }
        else
        {
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
        }

        CurrentSweepBucket = null;
        isTickingField.SetValue(bucket, BoxedFalse);
        var removedCount = entitiesToRemove.Count;
        for (var i = 0; i < removedCount; i++)
        {
            entities.Remove(entitiesToRemove[i].EntityId);
        }

        entitiesToRemove.Clear();
        if (snapshot is not null && removedCount > 0)
        {
            snapshot.Dirty = true;
        }

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

            var entry = new EntityEntry(gameObject, components, entityComponent, originalName);
            if (BenchmarkSettings.EnableActiveInHierarchyMirror &&
                EntityActiveSlots.TryGetValue(entity, out var slotBox))
            {
                // Assigned by AttachSentinelForEntity in the Bucket.Add hook
                // (during load for the initial population). -1 keeps the exact
                // native activeInHierarchy fallback.
                entry.ActiveSlot = slotBox.Slot;
            }

            return entry;
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
