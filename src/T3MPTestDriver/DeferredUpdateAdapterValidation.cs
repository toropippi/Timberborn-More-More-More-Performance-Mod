using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bindito.Core;
using Bindito.Core.Internal;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

internal static partial class DeferredUpdateAdapterExperiment
{
    private class Probe : BaseComponent, IAwakableComponent
    {
        internal string Id = "";
        internal bool DisableInAwake, ThrowInAwake;
        internal List<string> Trace = null!;
        public void Awake()
        {
            Trace.Add("Awake" + Id);
            if (DisableInAwake) DisableComponent();
            if (ThrowInAwake) throw new InvalidOperationException("fixture awake");
        }
    }
    private sealed class UpdateProbe : Probe, IUpdatableComponent { public void Update() => Trace.Add("Update" + Id); }
    private sealed class LateProbe : Probe, ILateUpdatableComponent { public void LateUpdate() => Trace.Add("Late" + Id); }
    private sealed class BothProbe : Probe, IUpdatableComponent, ILateUpdatableComponent
    { public void Update() => Trace.Add("Update" + Id); public void LateUpdate() => Trace.Add("Late" + Id); }
    private sealed class PlainProbe : IUpdatableComponent, ILateUpdatableComponent
    {
        internal List<string> Trace = null!;
        public void Update() => Trace.Add("plain-update");
        public void LateUpdate() => Trace.Add("plain-late");
    }
    private sealed class ForeignListener : IInjectionListener { public void Listen(object instance) { } }
    private static bool _exerciseReal;
    private static int _foreignObserverCalls;

    internal static void Validate()
    {
        if (!_validate) return;
        _validate = false; _exerciseReal = true; EnsurePlaceholders();
        var cases = 0;
        var before = (_enrolled, _deferred, _created, _lateCreated, _adds, _removes, _fallbacks);
        try
        {
            foreach (var count in new[] { 0, 1, 3 })
            foreach (var role in new[] { 0, 1, 2, 3 })
            foreach (var disable in new[] { false, true })
            {
                var expected = Exercise(false, count, role, disable, false, false);
                var actual = Exercise(true, count, role, disable, false, false);
                if (expected != actual) throw new InvalidOperationException($"Deferred adapter lifecycle mismatch: {count}/{role}/{disable}: {expected} != {actual}");
                cases++;
            }
            foreach (var throwAwake in new[] { false, true })
            {
                var expected = Exercise(false, 3, 3, false, true, throwAwake);
                var actual = Exercise(true, 3, 3, false, true, throwAwake);
                if (expected != actual) throw new InvalidOperationException("Deferred adapter plain/exception mismatch: " + expected + " != " + actual);
                cases++;
            }
            if (_activation != null || _updatePlaceholder._updatableComponents.Count != 0 || _latePlaceholder._lateUpdatableComponents.Count != 0)
                throw new InvalidOperationException("Deferred adapters leaked activation or shared mutable state");
            var guardCases = ValidateObservers();
            Debug.Log($"[T3MPDEFERREDADAPTER] VALIDATE PASS lifecycleCases={cases} observerCases={guardCases} firstUse=True duplicateRemoval=True exception=True plainComponent=True");
        }
        finally
        {
            (_enrolled, _deferred, _created, _lateCreated, _adds, _removes, _fallbacks) = before;
        }
    }

    private static int ValidateObservers()
    {
        var notifier = new InjectionListenerNotifier();
        if (!InertListeners(notifier)) throw new InvalidOperationException("Empty notifier rejected");
        var listener = new SingletonListener(); notifier.AddListener(listener);
        if (!InertListeners(notifier)) throw new InvalidOperationException("Native non-singleton classification rejected");
        listener._typeCache[typeof(BaseComponentUpdateUnityAdapter)] = true;
        if (InertListeners(notifier)) throw new InvalidOperationException("Changed singleton classification accepted");
        listener._typeCache.Clear(); notifier.AddListener(new ForeignListener());
        if (InertListeners(notifier)) throw new InvalidOperationException("Unknown injection observer accepted");
        const string id = "t3mp.test.deferred-adapter-foreign-fixture";
        var hm = Find("HarmonyLib.HarmonyMethod"); var harmony = Activator.CreateInstance(_harmony, id);
        var patch = _harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var method = typeof(SingletonListener).GetMethod("ObjectIsSingleton", All)!;
        try
        {
            _foreignObserverCalls = 0;
            patch.Invoke(harmony, new object?[] { method,
                Activator.CreateInstance(hm, typeof(DeferredUpdateAdapterExperiment).GetMethod(nameof(ForeignObserver), All)), null, null, null });
            if (NativeObservers()) throw new InvalidOperationException("Foreign singleton observer patch accepted");
            ((IInjectionListener)listener).Listen(_updatePlaceholder);
            if (_foreignObserverCalls != 1) throw new InvalidOperationException("Native foreign callback did not run");
        }
        finally { _harmony.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { id }); }
        if (!NativeObservers()) throw new InvalidOperationException("Observer fixture did not restore native path");
        return 5;
    }
    private static void ForeignObserver() { _foreignObserverCalls++; }

    internal static void ExerciseReal(IEnumerable<EntityComponent> entities)
    {
        if (!_exerciseReal) return;
        _exerciseReal = false;
        var type = Find("Timberborn.CameraSystem.FacingCamera");
        var targetField = type.GetField("_transform", All)!;
        var enable = type.GetMethod("Enable", All)!; var disable = type.GetMethod("Disable", All)!;
        var candidates = entities.OrderBy(e => e.EntityId).SelectMany(e => e.AllComponents).OfType<BaseComponent>()
            .Where(c => c.GetType() == type && !c.Enabled && targetField.GetValue(c) == null &&
                Owners.TryGetValue(c._componentCache, out var owner) && owner.DeferredLate).Take(16).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No real deferred first-use candidates");
        var target = new GameObject("T3MP deferred camera validation target"); target.SetActive(false);
        try
        {
            foreach (var component in candidates)
            {
                var cache = component._componentCache;
                BaseComponentLateUpdateUnityAdapter? first = null;
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    enable.Invoke(component, new object[] { target.transform });
                    var expected = target.transform.rotation;
                    var adapter = cache._lateUpdateAdapter;
                    if (ReferenceEquals(adapter, _latePlaceholder) || !adapter.enabled ||
                        !adapter._lateUpdatableComponents.Contains((ILateUpdatableComponent)component))
                        throw new InvalidOperationException("First-use late adapter was not registered");
                    if (first && !ReferenceEquals(first, adapter)) throw new InvalidOperationException("First-use adapter was recreated");
                    first = adapter;
                    target.transform.rotation = Quaternion.Euler(17, 43, 91);
                    adapter.Start();
                    typeof(BaseComponentLateUpdateUnityAdapter).GetMethod("LateUpdate", All)!.Invoke(adapter, null);
                    var actual = target.transform.rotation;
                    if (actual.x != expected.x || actual.y != expected.y || actual.z != expected.z || actual.w != expected.w)
                        throw new InvalidOperationException("Real first-use late update did not reproduce native rotation");
                    disable.Invoke(component, null);
                    if (component.Enabled || targetField.GetValue(component) != null || adapter._lateUpdatableComponents.Count != 0 || adapter.enabled)
                        throw new InvalidOperationException("Real first-use disable did not restore dormant state");
                }
            }
            Debug.Log($"[T3MPDEFERREDADAPTER] REAL FIRST USE PASS components={candidates.Length} cycles={candidates.Length * 3} exactRotation=True");
        }
        finally
        {
            foreach (var component in candidates) if (component.Enabled) disable.Invoke(component, null);
            Object.DestroyImmediate(target);
        }
    }

    private static string Exercise(bool deferred, int count, int role, bool disable, bool plain, bool throwing)
    {
        var root = new GameObject("T3MP deferred adapter fixture"); root.SetActive(false);
        var cache = root.AddComponent<ComponentCache>();
        var activation = root.AddComponent<BaseComponentUnityAdapter>();
        var trace = new List<string>();
        var probes = Enumerable.Range(0, count).Select(i =>
        {
            Probe probe = role == 1 ? new UpdateProbe() : role == 2 ? new LateProbe() : role == 3 ? new BothProbe() : new Probe();
            probe.Id = i.ToString(); probe.Trace = trace; probe.DisableInAwake = disable; probe.ThrowInAwake = throwing && i == 1;
            return probe;
        }).ToArray();
        var components = probes.Cast<object>().ToList();
        if (plain) components.Insert(0, new PlainProbe { Trace = trace });
        components.Add(cache);
        try
        {
            if (!deferred) { root.AddComponent<BaseComponentUpdateUnityAdapter>(); root.AddComponent<BaseComponentLateUpdateUnityAdapter>(); }
            cache.InjectDependencies(new ComponentCacheService(), new TypeBlacklist());
            cache.Initialize(components, "DeferredAdapterFixture", new TypeIndexMap());
            if (deferred)
            {
                Owners.Add(cache, new Owner { Cache = cache, DeferredUpdate = true, DeferredLate = true });
                cache._updateAdapter = _updatePlaceholder; cache._lateUpdateAdapter = _latePlaceholder;
            }
            // Invoke the native activation method on an inactive fixture so
            // deliberate Awake exceptions are observed without Unity log errors.
            typeof(BaseComponentUnityAdapter).GetMethod("Awake", All)?.Invoke(activation, null);
            void Activate()
            {
                try { typeof(BaseComponentUnityAdapter).GetMethod("OnEnable", All)!.Invoke(activation, null); }
                catch (Exception e) { trace.Add("exception:" + e.GetBaseException().GetType().Name + ":" + e.GetBaseException().Message); }
            }
            void Tick()
            {
                var update = root.GetComponent<BaseComponentUpdateUnityAdapter>(); var late = root.GetComponent<BaseComponentLateUpdateUnityAdapter>();
                update?.Start(); late?.Start();
                trace.Add($"counts:{(update ? update._updatableComponents.Count : 0)}/{(late ? late._lateUpdatableComponents.Count : 0)}");
                if (update && update.enabled) typeof(BaseComponentUpdateUnityAdapter).GetMethod("Update", All)!.Invoke(update, null);
                if (late && late.enabled) typeof(BaseComponentLateUpdateUnityAdapter).GetMethod("LateUpdate", All)!.Invoke(late, null);
            }
            Activate(); Tick(); Activate(); Tick();
            foreach (var probe in probes) probe.DisableComponent();
            Tick();
            foreach (var probe in probes.Reverse()) probe.EnableComponent();
            Tick();
            if (probes.Length > 0)
            {
                if (probes[0] is IUpdatableComponent update)
                { cache._updateAdapter.Add(update); cache._updateAdapter.Add(update); cache._updateAdapter.Remove(update); }
                if (probes[0] is ILateUpdatableComponent late)
                { cache._lateUpdateAdapter.Add(late); cache._lateUpdateAdapter.Add(late); cache._lateUpdateAdapter.Remove(late); }
                Tick();
            }
            if (!activation._activated) throw new InvalidOperationException("Activation was not recorded");
            return string.Join("|", trace);
        }
        finally { Owners.Remove(cache); Object.DestroyImmediate(root); }
    }
}
