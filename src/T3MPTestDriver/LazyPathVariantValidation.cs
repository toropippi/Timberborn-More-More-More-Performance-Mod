using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

internal static partial class LazyPathVariantExperiment
{
    // Isolated native-reference fixtures: no registration in the live entity,
    // physics or simulation systems. Each model gets freshly constructed maps.
    private static void ValidateFirstUse(IReadOnlyList<EntityComponent> entities)
    {
        var live = entities.SelectMany(e => e.AllComponents).First(c => c.GetType() == _pathType);
        var liveRoot = ((BaseComponent)live).GameObject;
        if (!Roots.TryGetValue(liveRoot, out var liveEntry)) throw new InvalidOperationException("No lazy path validation subject");
        var plan = liveEntry.Plan;
        var originalState = VariantState(plan.Original.transform);
        var ctor = _pathType.GetConstructors(All).Single();
        var dependencies = ctor.GetParameters().Select(p => _pathType.GetField("_" + p.Name, All)!.GetValue(live)).ToArray();
        var initialize = _pathType.GetMethod("InitializeModels", All)!;
        var select = _pathType.GetMethod("SetCurrentModel", All)!;
        var orientationType = select.GetParameters()[1].ParameterType;
        var toggle = Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions").GetMethod("ToggleModelVisibility", All)!;
        var bounds = typeof(Timberborn.Common.BoundsCalculator).GetMethod("GetRendererYMaxBoundInternal", All)!;
        var cacheType = Find("Timberborn.BaseComponentSystem.ComponentCache");
        var enrolled = _enrolled; var created = _created; var queries = _boundsQueries;
        var reasons = Reasons.ToArray();
        var named = _namedReads; var participating = _participatingNamedReads;
        var shadowBounds = _boundsValidate;
        int fixtures = 0, firstUses = 0, transitions = 0, boundsChecks = 0;
        object Model(GameObject root)
        {
            var model = ctor.Invoke(dependencies);
            var cache = root.AddComponent(cacheType);
            cacheType.GetProperty("CachedGameObject", All)!.SetValue(cache, root);
            cacheType.GetProperty("CachedTransform", All)!.SetValue(cache, root.transform);
            typeof(BaseComponent).GetField("_componentCache", All)!.SetValue(model, cache);
            foreach (var field in new[] { "_buildingSpec", "_blockObject", "_dynamicPathModelSpec" })
                _pathType.GetField(field, All)!.SetValue(model, _pathType.GetField(field, All)!.GetValue(live));
            return model;
        }
        try
        {
            // The fixture compares against its separate native root. Do not let
            // the optional live-world shadow check materialize its lazy root.
            _boundsValidate = false;
            for (var transformCase = 0; transformCase < 3; transformCase++)
            for (var visibility = 0; visibility < 4; visibility++)
            for (var initialOrientation = 0; initialOrientation < 4; initialOrientation++)
            {
                GameObject? a = null, b = null;
                try
                {
                    a = Object.Instantiate(plan.Original, _holder.transform, false);
                    b = Object.Instantiate(plan.Pruned, _holder.transform, false);
                    a.name = b.name = "Path first-use fixture";
                    foreach (var root in new[] { a, b })
                    {
                        root.transform.localPosition = new Vector3(13.25f, -2.75f, 7.5f);
                        root.transform.localRotation = transformCase == 0 ? Quaternion.identity : Quaternion.Euler(15f, 37f, -9f);
                        root.transform.localScale = transformCase == 0 ? Vector3.one :
                            new Vector3(transformCase == 2 ? -1.5f : 1.5f, .75f, 2.25f);
                    }
                    AfterClone(b, plan);
                    var ma = Model(a); var mb = Model(b);
                    initialize.Invoke(ma, null); initialize.Invoke(mb, null);
                    var pa = a.transform; foreach (var i in plan.ParentPath) pa = pa.GetChild(i);
                    var entry = Roots.GetValue(b, _ => throw new InvalidOperationException());
                    if (entry.Instances.Any(g => g)) throw new InvalidOperationException("Fixture materialized before first use");
                    void VisibilityBoth(int state)
                    {
                        toggle.Invoke(null, new object[] { a, (state & 1) != 0, (state & 2) != 0 });
                        toggle.Invoke(null, new object[] { b, (state & 1) != 0, (state & 2) != 0 });
                    }
                    void Check()
                    {
                        for (var i = 0; i < entry.Instances.Length; i++)
                            if (entry.Instances[i] && VariantState(pa.GetChild(i)) != VariantState(entry.Instances[i]!.transform))
                                throw new InvalidOperationException($"Path first-use variant mismatch fixture={fixtures} index={i} transition={transitions}");
                        var ca = (GameObject?)_pathType.GetField("_currentModel", All)!.GetValue(ma);
                        var cb = (GameObject?)_pathType.GetField("_currentModel", All)!.GetValue(mb);
                        if (ca?.name != cb?.name || !Equals(_pathType.GetField("_currentModelOrientation", All)!.GetValue(ma),
                            _pathType.GetField("_currentModelOrientation", All)!.GetValue(mb)))
                            throw new InvalidOperationException("Path selection identity/orientation mismatch");
                        foreach (var includeInactive in new[] { true, false })
                        {
                            var va = (float)bounds.Invoke(null, new object[] { a.transform, includeInactive })!;
                            var vb = (float)bounds.Invoke(null, new object[] { b.transform, includeInactive })!;
                            if (BitConverter.SingleToInt32Bits(va) != BitConverter.SingleToInt32Bits(vb))
                                throw new InvalidOperationException($"Path first-use bounds mismatch fixture={fixtures} transition={transitions} inactive={includeInactive} A={va:R} B={vb:R}");
                            boundsChecks++;
                        }
                    }
                    // Establish a shadow mode, then hide everything: native hide
                    // preserves the preceding mode for candidates not yet created.
                    VisibilityBoth(2); VisibilityBoth(0); VisibilityBoth(visibility);
                    for (var n = 0; n < plan.Variants.Length; n++)
                    {
                        var i = (n * 5 + initialOrientation) % plan.Variants.Length;
                        if (entry.Instances[i]) throw new InvalidOperationException("First-use candidate already exists");
                        var orientation = Enum.ToObject(orientationType, initialOrientation);
                        select.Invoke(ma, new object[] { pa.GetChild(i).gameObject, orientation });
                        select.Invoke(mb, new object[] { plan.Variants[i].Source, orientation });
                        firstUses++; transitions++; Check();
                        pa.GetChild(i).gameObject.SetActive(false);
                        entry.Instances[i]!.SetActive(false);
                        select.Invoke(ma, new object[] { pa.GetChild(i).gameObject, orientation });
                        select.Invoke(mb, new object[] { plan.Variants[i].Source, orientation });
                        transitions++; Check();
                        // Same-model early-return, reactivation and rotation paths.
                        for (var turn = 0; turn < 4; turn++)
                        {
                            VisibilityBoth((visibility + turn) % 4);
                            orientation = Enum.ToObject(orientationType, (initialOrientation + turn) % 4);
                            select.Invoke(ma, new object[] { pa.GetChild(i).gameObject, orientation });
                            select.Invoke(mb, new object[] { plan.Variants[i].Source, orientation });
                            transitions++; Check();
                        }
                    }
                    if (VariantState(a.transform) != VariantState(b.transform))
                        throw new InvalidOperationException("Materialized fixture hierarchy differs");
                    fixtures++;
                }
                finally
                {
                    if (a) Object.DestroyImmediate(a);
                    if (b) { Roots.Remove(b!); Object.DestroyImmediate(b); }
                    Entries.RemoveAll(w => !w.TryGetTarget(out var e) || !e.Root);
                    if (Ancestors.TryGetValue(_holder, out var bucket))
                        bucket.Entries.RemoveAll(w => !w.TryGetTarget(out var e) || !e.Root);
                }
            }
            if (VariantState(plan.Original.transform) != originalState)
                throw new InvalidOperationException("First-use fixture mutated source prefab");
            Debug.Log($"[T3MPLAZYPATH] FIRST USE PASS fixtures={fixtures} firstUses={firstUses} transitions={transitions} boundsChecks={boundsChecks}; native selection and visibility, inactive isolated roots");
        }
        finally
        {
            _enrolled = enrolled; _created = created; _boundsQueries = queries;
            _namedReads = named; _participatingNamedReads = participating;
            _boundsValidate = shadowBounds;
            Reasons.Clear(); foreach (var pair in reasons) Reasons.Add(pair.Key, pair.Value);
        }
    }

    private static string VariantState(Transform root, bool lighting = false)
    {
        var s = new StringBuilder();
        var instanceId = typeof(Object).GetMethod("GetInstanceID", All)!;
        object Id(Object value) => value ? instanceId.Invoke(value, null)! : 0;
        void F(float f) => s.Append(BitConverter.SingleToInt32Bits(f)).Append(',');
        void V(Vector3 v) { F(v.x); F(v.y); F(v.z); }
        void Visit(Transform t)
        {
            var g = t.gameObject;
            s.Append(g.name).Append('|').Append(g.layer).Append('|').Append(g.activeSelf).Append('|').Append(g.activeInHierarchy).Append('|');
            V(t.localPosition); V(t.localScale); var q = t.localRotation; F(q.x); F(q.y); F(q.z); F(q.w);
            foreach (var mesh in g.GetComponents<MeshFilter>()) s.Append(Id(mesh.sharedMesh)).Append('|');
            foreach (var r in g.GetComponents<Renderer>())
            {
                if (lighting && r is MeshRenderer mr) s.Append(mr.GetShaderUserValue()).Append('|');
                s.Append(r.enabled).Append('|').Append(r.shadowCastingMode).Append('|').Append(r.receiveShadows).Append('|');
                foreach (var m in r.sharedMaterials) s.Append(Id(m)).Append('|');
            }
            foreach (var c in g.GetComponents<BoxCollider>())
            { s.Append(c.enabled).Append('|').Append(c.isTrigger).Append('|'); V(c.center); V(c.size); F(c.contactOffset); }
            s.Append('['); for (var i = 0; i < t.childCount; i++) Visit(t.GetChild(i)); s.Append(']');
        }
        Visit(root); return s.ToString();
    }
}
