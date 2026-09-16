using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BaseComponentSystem;
using Timberborn.TickSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Experimental consolidated component lists per bucket. Membership snapshots are
// immutable so a nested sweep cannot overwrite the outer sweep's arrays.
// Enabled follows the actual setter, not a frame/tick or an enable request.
internal static class TickComponentDispatch
{
    internal const string Owner = "t3mp.runtime.component-dispatch";
    private static readonly ConditionalWeakTable<TickableEntityBucket, BucketState> Buckets = new();
    private static readonly ConditionalWeakTable<BaseComponent, EnabledState> Enabled = new();
    private static readonly HashSet<MethodBase> Observed = new();
    private static readonly Dictionary<MethodBase, int> Generations = new();
    private static readonly HashSet<MethodBase> FrontierWriters = new();
    private static Type? _harmony;
    private static int _invalidated;
    private static bool _attempted;
    private static Action<TickableComponent>? _startAndTick;
    private static Action<MeteredTickableComponent>? _meteredTick;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;
    internal static bool NeedsNativeTraversal => _attempted && Volatile.Read(ref _invalidated) != 0;

    internal sealed class EnabledState { internal bool Value; }
    private sealed class BucketState { internal Snapshot? Value; internal bool Dirty = true; }
    internal sealed class Snapshot
    {
        internal TickableEntity[] Entities = Array.Empty<TickableEntity>();
        internal ImmutableArray<MeteredTickableComponent>[] Lists = Array.Empty<ImmutableArray<MeteredTickableComponent>>();
        internal int[] Starts = Array.Empty<int>();
        internal MeteredTickableComponent[] Wrappers = Array.Empty<MeteredTickableComponent>();
        internal TickableComponent[] Components = Array.Empty<TickableComponent>();
        internal EnabledState?[] States = Array.Empty<EnabledState?>();
    }

    internal static bool CompatibleFrontierPath(MethodBase method) => Observed.Contains(method);

    internal static void Install(Type harmony, Type harmonyMethod, MethodInfo patch)
    {
        if (_attempted) return;
        _attempted = true;
        _harmony = harmony;
        Installed = RuntimePatches.TryInstall(Owner, harmony, harmonyMethod, patch, apply =>
        {
            var flags = RuntimePatches.All | BindingFlags.DeclaredOnly;
            MethodInfo Method(Type type, string name) => type.GetMethod(name, flags)!;
            var setter = Method(typeof(BaseComponent), "set_Enabled");
            var methods = new Dictionary<MethodInfo, string[]>
            {
                [setter] = new[] { "1A01B68E1AC30751014D0B2F3D7635E6D259CC1D0C9C4FA0C0B71D74230857AE" },
                [Method(typeof(BaseComponent), "get_Enabled")] = new[] { "A42B33234BAD279EC5D573C04257AB8869486DCF80EFE867031389346376EBBE" },
                [Method(typeof(MeteredTickableComponent), "get_Enabled")] = new[] { "130955918CD7930B535E1996D9CDFB35A82E4E9078B14566E5B9F6F944244CB9" },
                [Method(typeof(TickableEntity), "Tick")] = new[] {
                    "1505E4964C1B5B1243D4E48190FCC49617B6A34362A012A60937B1AF26130C34",
                    "9DC6FCDBD41B512C73522FBD1AE22893ADA21277BB8E56621F44D34DB159E2CA" },
                [Method(typeof(TickableEntity), "TickTickableComponents")] = new[] {
                    "F7D063689233726D8BB0CE335466978A40CC9B86E088D99721D5A18216119180",
                    "ADF101E3EEB721D1535EC28BAC0105EB26853DB0617C8998EA65F4808BC6F5CB" }
            };
            var meteredTick = Method(typeof(MeteredTickableComponent), "StartAndTick")
                ?? Method(typeof(MeteredTickableComponent), "Tick");
            methods[meteredTick] = new[] {
                "452693D36F7D5C7384B73CF01C66C6FF2F7B0CC5DF641C09B6A7BCB7EB4A0FD3",
                "1E7F912A17BE37FDC74F6B4EF96D61341000557C001ACA2E919EA80BFCF684B5" };
            var enable = Method(typeof(BaseComponent), "EnableComponent");
            var disable = Method(typeof(BaseComponent), "DisableComponent");
            var start = Method(typeof(TickableComponent), "StartAndTick");
            if (start != null) methods[start] = new[] { "365648C9AB828D839C649596376931709AEA1FBAE50FCD1FBA50235E72DC4840" };
            FrontierWriters.Add(enable); FrontierWriters.Add(disable);
            methods[enable] = new[] { "0EAA2D69E54CD4FBA253B1F545C30D8DC811F48448E66D3BA4E3AB78319D3D6A", "D19B477511BF2B0CEFF9200180B9ECC4DAA996CB9B5C7DDCE9B9E8D03ADB6C25" };
            methods[disable] = new[] { "673CF76EED94947F860C29E7F449DB8D4A152D433523214EF1CDEC50DE5B89B5", "9DE14C9B9436399BB7DFD959FCADA10A7EAD4DC59CA597F30D553024093563A9" };
            foreach (var pair in methods)
            {
                if (!RuntimePatches.ReviewedBody(pair.Key, pair.Value) ||
                    Foreign(pair.Key))
                    throw new InvalidOperationException("unreviewed or patched component dispatch: " + pair.Key);
                Observed.Add(pair.Key);
            }
            // 1.0's one-time Start remains at the same call site, including its
            // exception and reentrancy behavior. 1.1 calls Tick directly.
            // Passthrough observers ensure any later hook makes already-built
            // snapshots fall back immediately; they also prevent JIT inlining
            // of the original setter/getters before another mod patches them.
            var ordered = new List<MethodInfo> {
                setter, Method(typeof(BaseComponent), "get_Enabled"), Method(typeof(MeteredTickableComponent), "get_Enabled"),
                meteredTick, enable, disable, Method(typeof(TickableEntity), "TickTickableComponents"), Method(typeof(TickableEntity), "Tick")
            };
            if (start != null) ordered.Insert(0, start);
            foreach (var method in ordered)
                apply(method, null, method == setter ? nameof(AfterSetEnabled) : null, nameof(Observe), null);
            _meteredTick = NativeCall<MeteredTickableComponent>(meteredTick);
            if (start != null) _startAndTick = NativeCall<TickableComponent>(start);
        }, typeof(TickComponentDispatch));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Active) Debug.Log("[T3MP] Component dispatch installed (ordered bucket arrays, setter-tracked Enabled).");
    }

    private static Action<T> NativeCall<T>(MethodInfo target)
    {
        var method = new DynamicMethod("T3MP_Call_" + target.Name, typeof(void), new[] { typeof(T) }, typeof(TickComponentDispatch).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, target); il.Emit(OpCodes.Ret);
        return (Action<T>)method.CreateDelegate(typeof(Action<T>));
    }

    private static bool Foreign(MethodBase method) => RuntimePatches.ForeignPatched(_harmony!, method,
        FrontierWriters.Contains(method) ? new[] { Owner, "t3mp.runtime.frontier" } : new[] { Owner }, false);

    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, MethodBase original)
    {
        Generations.TryGetValue(original, out var count);
        Generations[original] = count + 1;
        if (count != 0) Interlocked.Exchange(ref _invalidated, 1);
        return instructions;
    }

    private static void AfterSetEnabled(BaseComponent __instance, bool __0)
    {
        if (!Active) return;
        if (Enabled.TryGetValue(__instance, out var state)) state.Value = __0;
        TickFrontier.EnabledStored(__instance, __0);
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmony == null) return;
        foreach (var method in Observed)
            if (Foreign(method))
                Interlocked.Exchange(ref _invalidated, 1);
        if (Active) return;
        RuntimePatches.Unpatch(_harmony, Owner);
        Installed = false;
        Debug.Log("[T3MP] Component dispatch removed after a call-chain change; native traversal retained.");
    }

    internal static void MembershipChanged(TickableEntityBucket bucket)
    {
        if (Buckets.TryGetValue(bucket, out var state)) state.Dirty = true;
    }

    internal static Snapshot? Prepare(TickableEntityBucket bucket)
    {
        if (!Active) return null;
        var state = Buckets.GetOrCreateValue(bucket);
        var source = bucket._tickableEntities;
        if (!state.Dirty && state.Value != null && state.Value.Entities.Length == source.Count) return state.Value;
        // Failures while preparing optional data must not move native exceptions
        // before the entity that would have thrown them.
        try
        {
            var snapshot = new Snapshot {
                Entities = new TickableEntity[source.Count],
                Lists = new ImmutableArray<MeteredTickableComponent>[source.Count],
                Starts = new int[source.Count + 1]
            };
            var total = 0;
            for (var i = 0; i < source.Count; i++)
            {
                var entity = source.Values[i];
                snapshot.Entities[i] = entity;
                snapshot.Lists[i] = entity._tickableComponents;
                snapshot.Starts[i] = total;
                total = checked(total + entity._tickableComponents.Length);
            }
            snapshot.Starts[source.Count] = total;
            snapshot.Wrappers = new MeteredTickableComponent[total];
            snapshot.Components = new TickableComponent[total];
            snapshot.States = new EnabledState?[total];
            for (var i = 0; i < source.Count; i++)
                for (var j = 0; j < snapshot.Lists[i].Length; j++)
                {
                    var slot = snapshot.Starts[i] + j;
                    var wrapper = snapshot.Lists[i][j];
                    snapshot.Wrappers[slot] = wrapper;
                    var component = wrapper?._tickableComponent;
                    snapshot.Components[slot] = component!;
                    if (component != null)
                        snapshot.States[slot] = Enabled.GetValue(component, c => new EnabledState { Value = c.Enabled });
                }
            state.Value = snapshot;
            state.Dirty = false;
            return snapshot;
        }
        catch { state.Dirty = true; return null; }
    }

    internal static void TickEntity(Snapshot? snapshot, int index, TickableEntity entity)
    {
        // The live sorted list remains authoritative, including insertions before
        // the cursor and removals made by nested sweeps. Never retry a tick.
        if (!Active || snapshot == null || index >= snapshot.Entities.Length ||
            !ReferenceEquals(entity, snapshot.Entities[index]) ||
            !entity._tickableComponents.Equals(snapshot.Lists[index]))
        {
            entity.Tick();
            return;
        }
        try
        {
            if (entity._entityComponent.GameObject.activeInHierarchy)
            {
                var current = entity._tickableComponents;
                if (!current.Equals(snapshot.Lists[index])) entity.TickTickableComponents();
                else TickComponents(snapshot, current, snapshot.Starts[index], snapshot.Starts[index + 1]);
            }
        }
        catch (Exception innerException)
        {
            // Native exception scope and live message reads, including failures
            // while constructing the message itself.
            var text = $"Exception thrown while ticking entity {entity.EntityId}";
            text = !entity._entityComponent
                ? text + " '" + entity._originalName + "' (destroyed)"
                : text + " '" + entity._entityComponent.Name + "'";
            throw new Exception(text, innerException);
        }
    }

    internal static void TickComponents(Snapshot snapshot, ImmutableArray<MeteredTickableComponent> source, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            // ImmutableArray normally never changes in place. Keep the original
            // backing-array read so reflection-based mods still observe native
            // enumeration semantics, including a later slot replaced by null.
            var wrapper = source[i - start];
            var component = snapshot.Components[i];
            if (!Active || wrapper == null || component == null ||
                !ReferenceEquals(component, wrapper._tickableComponent))
            {
                if (wrapper!.Enabled) NativeTick(wrapper);
                continue;
            }
            if (!snapshot.States[i]!.Value) continue;
            if (wrapper._metricsEnabled) wrapper._timerMetric.Resume();
            // A timer callback can change the wrapper or install another patch.
            // The native helper has already begun; retain its two live reads.
            if (_startAndTick == null) wrapper._tickableComponent.Tick();
            else _startAndTick(wrapper._tickableComponent);
            if (wrapper._metricsEnabled) wrapper._timerMetric.Pause();
        }
    }

    private static void NativeTick(MeteredTickableComponent wrapper)
    {
        _meteredTick!(wrapper);
    }
}
