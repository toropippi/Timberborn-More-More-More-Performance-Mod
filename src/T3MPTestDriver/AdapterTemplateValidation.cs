using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

internal static partial class AdapterTemplateExperiment
{
    private sealed class ProbeComponent : BaseComponent, IAwakableComponent, IUpdatableComponent, ILateUpdatableComponent
    {
        internal string Id = "";
        internal bool DisableInAwake;
        internal List<string> Trace = null!;
        public void Awake() { Trace.Add("Awake" + Id); if (DisableInAwake) DisableComponent(); }
        public void Update() => Trace.Add("Update" + Id);
        public void LateUpdate() => Trace.Add("Late" + Id);
    }

    internal static void Validate()
    {
        if (!_validate) return;
        _validate = false;
        var holder = new GameObject("T3MP adapter validation"); holder.SetActive(false);
        var checks = 0;
        try
        {
            foreach (var count in new[] { 0, 1, 3 })
            foreach (var disable in new[] { false, true })
            foreach (var activeSelf in new[] { false, true })
            {
                var source = new GameObject("AdapterFixture"); source.transform.SetParent(holder.transform, false); source.SetActive(activeSelf);
                var nativeSource = Object.Instantiate(source, holder.transform, false);
                var prepared = GetPrepared(source)!;
                var first = Object.Instantiate(prepared, holder.transform, false);
                var second = Object.Instantiate(prepared, holder.transform, false);
                if (ReferenceEquals(first.GetComponent<BaseComponentUpdateUnityAdapter>()._updatableComponents,
                        second.GetComponent<BaseComponentUpdateUnityAdapter>()._updatableComponents) ||
                    ReferenceEquals(first.GetComponent<BaseComponentLateUpdateUnityAdapter>()._lateUpdatableComponents,
                        second.GetComponent<BaseComponentLateUpdateUnityAdapter>()._lateUpdatableComponents))
                    throw new InvalidOperationException("Cloned adapters share mutable lists");
                foreach (var clone in new[] { first, second })
                {
                    var original = Object.Instantiate(nativeSource, holder.transform, false);
                    original.SetActive(false); clone.SetActive(false);
                    foreach (var type in Adapters) original.AddComponent(type);
                    try
                    {
                        var expected = Exercise(original, count, disable);
                        var actual = Exercise(clone, count, disable);
                        if (expected != actual) throw new InvalidOperationException($"Adapter lifecycle differs: {count}/{disable}/{activeSelf}: {expected} != {actual}");
                        checks++;
                    }
                    finally { Object.DestroyImmediate(original); Object.DestroyImmediate(clone); }
                }
                if (prepared.GetComponent<BaseComponentUnityAdapter>()._activated ||
                    prepared.GetComponent<ComponentCache>()._components != null ||
                    prepared.GetComponent<BaseComponentUpdateUnityAdapter>()._updatableComponents.Count != 0 ||
                    prepared.GetComponent<BaseComponentLateUpdateUnityAdapter>()._lateUpdatableComponents.Count != 0)
                    throw new InvalidOperationException("Adapter template mutated by a clone");
                ReleaseTemplates();
                if (source.GetComponents<MonoBehaviour>().Length != 0) throw new InvalidOperationException("Adapter template was not restored");
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(nativeSource);
            }
            Debug.Log($"[T3MPADAPTER] VALIDATE PASS lifecycleCases={checks} freshLists=True templateUnchanged=True");
        }
        finally
        {
            _injection = null;
            ReleaseTemplates();
            Object.DestroyImmediate(holder);
        }
    }

    private static string Exercise(GameObject root, int count, bool disable)
    {
        var cache = root.GetComponent<ComponentCache>();
        var activation = root.GetComponent<BaseComponentUnityAdapter>();
        var update = root.GetComponent<BaseComponentUpdateUnityAdapter>();
        var late = root.GetComponent<BaseComponentLateUpdateUnityAdapter>();
        if (cache._components != null || activation._activated || update._updatableComponents.Count != 0 || late._lateUpdatableComponents.Count != 0)
            throw new InvalidOperationException("Adapter clone is not fresh");
        _injection = new InjectionFrame();
        try { if (OriginalScripts(root).Length != 0) throw new InvalidOperationException("Early adapter injection was not filtered"); }
        finally { _injection = null; }
        cache.InjectDependencies(new ComponentCacheService(), new TypeBlacklist());
        var trace = new List<string>();
        var probes = Enumerable.Range(0, count).Select(i => new ProbeComponent { Id = i.ToString(), DisableInAwake = disable, Trace = trace }).ToArray();
        var components = probes.Cast<object>().ToList(); components.Add(cache);
        cache.Initialize(components, "AdapterFixture", new TypeIndexMap());
        root.transform.SetParent(null, false);
        root.SetActive(true);
        update.Start(); late.Start();
        void Tick()
        {
            trace.Add($"enabled:{update.enabled}/{late.enabled};counts:{update._updatableComponents.Count}/{late._lateUpdatableComponents.Count}");
            if (update.enabled) typeof(BaseComponentUpdateUnityAdapter).GetMethod("Update", All)!.Invoke(update, null);
            if (late.enabled) typeof(BaseComponentLateUpdateUnityAdapter).GetMethod("LateUpdate", All)!.Invoke(late, null);
        }
        Tick();
        root.SetActive(false); root.SetActive(true); Tick();
        foreach (var probe in probes) probe.DisableComponent();
        Tick();
        foreach (var probe in probes.Reverse()) probe.EnableComponent();
        Tick();
        if (!activation._activated || !ReferenceEquals(cache.CachedGameObject, root)) throw new InvalidOperationException("Adapter activation missing");
        return string.Join("|", trace);
    }
}
