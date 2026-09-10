using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using Timberborn.Common;
using Timberborn.EntitySystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

internal static partial class LazyPathVariantExperiment
{
    private static void ValidateTubeFirstUse(IReadOnlyList<EntityComponent> entities)
    {
        var models = entities.SelectMany(e => e.AllComponents).OfType<BaseComponent>()
            .Where(c => c.GetType() == _tubeType && Roots.TryGetValue(c.GameObject, out var e) && e.Plan.Tube)
            .GroupBy(c => Roots.GetValue(c.GameObject, _ => throw new InvalidOperationException()).Plan).ToArray();
        if (models.Length < 2) throw new InvalidOperationException("Tube fixture requires horizontal and vertical layouts");
        var cacheType = Find("Timberborn.BaseComponentSystem.ComponentCache");
        var indexType = Find("Timberborn.BaseComponentSystem.TypeIndexMap");
        var ctor = _tubeType.GetConstructors(All).Single();
        var initialize = _tubeType.GetMethod("InitializeModels", All)!;
        var select = _tubeType.GetMethod("SetCurrentModel", All)!;
        var match = _tubeType.GetMethods(All).Single(m => m.Name == "UpdateModel" && m.GetParameters().Length == 6);
        var orientationType = select.GetParameters()[1].ParameterType;
        var toggle = Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions").GetMethod("ToggleModelVisibility", All)!;
        var bounds = typeof(BoundsCalculator).GetMethod("GetRendererYMaxBoundInternal", All)!;
        var enablerType = Find("Timberborn.Rendering.MaterialLightingEnabler");
        var enableBase = enablerType.GetMethods(All).Single(m => m.Name == "EnableLighting" && m.GetParameters()[0].ParameterType == typeof(BaseComponent));
        var enableObject = enablerType.GetMethods(All).Single(m => m.Name == "EnableLighting" && m.GetParameters()[0].ParameterType == typeof(GameObject));
        var materialCacheType = Find("Timberborn.Rendering.ColoredMaterialCache");
        var colorerType = Find("Timberborn.Rendering.MaterialColorer");
        var exclude = colorerType.GetMethod("EnableLightingAndDisableChanges", All)!;
        var enrolled = _enrolled; var created = _created; var queries = _boundsQueries;
        var reasons = Reasons.ToArray(); var named = _namedReads; var participating = _participatingNamedReads;
        var shadow = _boundsValidate;
        var fixtures = 0; var firstUses = 0; var selections = 0; var graphInputs = 0; var checks = 0; var historyLimits = 0;
        try
        {
            _boundsValidate = false;
            foreach (var group in models)
            {
                var plan = group.Key; var live = group.First();
                var original = VariantState(plan.Original.transform, true);
                var dependencies = ctor.GetParameters().Select(p => _tubeType.GetField("_" + p.Name, All)!.GetValue(live)).ToArray();
                for (var route = 0; route < 4; route++)
                for (var initial = 0; initial < 4; initial++)
                {
                    GameObject? a = null, b = null; object? materialCache = null;
                    try
                    {
                        a = Object.Instantiate(plan.Original, _holder.transform, false);
                        b = Object.Instantiate(plan.Pruned, _holder.transform, false);
                        a.name = b.name = "Tube first-use fixture";
                        foreach (var root in new[] { a, b })
                        {
                            root.transform.localPosition = new Vector3(-12.25f, 7.5f, 23.125f);
                            root.transform.localRotation = Quaternion.Euler(13f, 37f, -9f);
                            root.transform.localScale = new Vector3(initial % 2 == 0 ? 1.5f : -1.5f, .75f, 2.25f);
                        }
                        AfterClone(b, plan); var entry = Roots.GetValue(b, _ => throw new InvalidOperationException());
                        var enabler = Activator.CreateInstance(enablerType, true)!;
                        materialCache = Activator.CreateInstance(materialCacheType)!;
                        var colorer = colorerType.GetConstructors(All).Single(c => !c.IsStatic).Invoke(new[] { materialCache, enabler });
                        (object Model, object Lighting) Components(GameObject root)
                        {
                            var model = ctor.Invoke(dependencies); var lighting = Activator.CreateInstance(_lightingType)!;
                            var components = new List<object> { model, lighting };
                            var cache = root.AddComponent(cacheType); var index = Activator.CreateInstance(indexType, true)!;
                            indexType.GetMethod("CacheType", All)!.MakeGenericMethod(_lightingType).Invoke(index, new object[] { components.AsReadOnlyList() });
                            cacheType.GetProperty("CachedGameObject", All)!.SetValue(cache, root);
                            cacheType.GetProperty("CachedTransform", All)!.SetValue(cache, root.transform);
                            cacheType.GetField("_components", All)!.SetValue(cache, components);
                            cacheType.GetField("_typeIndexMap", All)!.SetValue(cache, index);
                            foreach (var component in components) typeof(BaseComponent).GetField("_componentCache", All)!.SetValue(component, cache);
                            foreach (var field in new[] { "_buildingSpec", "_blockObject", "_tubeModelSpec" })
                                _tubeType.GetField(field, All)!.SetValue(model, _tubeType.GetField(field, All)!.GetValue(live));
                            return (model, lighting);
                        }
                        var ca = Components(a); var cb = Components(b);
                        if (initial % 2 == 0) { _collectLighting.Invoke(ca.Lighting, null); _collectLighting.Invoke(cb.Lighting, null); }
                        initialize.Invoke(ca.Model, null); initialize.Invoke(cb.Model, null);
                        if (initial % 2 != 0) { _collectLighting.Invoke(ca.Lighting, null); _collectLighting.Invoke(cb.Lighting, null); }
                        var pa = a.transform; foreach (var slot in plan.ParentPath) pa = pa.GetChild(slot);
                        if (entry.Instances.Any(g => g)) throw new InvalidOperationException("Tube fixture materialized during initialization");
                        void Light(float? strength, bool useBase)
                        {
                            var method = useBase ? enableBase : enableObject;
                            method.Invoke(enabler, new[] { useBase ? ca.Model : a, strength });
                            method.Invoke(enabler, new[] { useBase ? cb.Model : b, strength });
                        }
                        void Color(Color? emission, float? gray, Color? light)
                        {
                            _setMaterialProperties.Invoke(colorer, new object?[] { a, emission, gray, light });
                            _setMaterialProperties.Invoke(colorer, new object?[] { b, emission, gray, light });
                        }
                        void Visible(int state)
                        {
                            toggle.Invoke(null, new object[] { a, (state & 1) != 0, (state & 2) != 0 });
                            toggle.Invoke(null, new object[] { b, (state & 1) != 0, (state & 2) != 0 });
                        }
                        string Key(MeshRenderer renderer, Transform root)
                        {
                            var names = new List<string>();
                            for (var t = renderer.transform; t != root; t = t.parent) names.Add(t.name);
                            names.Reverse(); return string.Join("/", names);
                        }
                        void Check()
                        {
                            foreach (var i in Enumerable.Range(0, plan.Variants.Length))
                                if (entry.Instances[i] && VariantState(pa.GetChild(plan.Variants[i].SiblingIndex), true) != VariantState(entry.Instances[i]!.transform, true))
                                    throw new InvalidOperationException($"Tube candidate differs fixture={fixtures} route={route} index={i} check={checks}");
                            var present = new HashSet<string>(b.GetComponentsInChildren<MeshRenderer>(true).Select(r => Key(r, b.transform)));
                            foreach (var field in new[] { _lightingRenderers, _disabledLightingRenderers })
                            {
                                var expected = ((List<MeshRenderer>)field.GetValue(ca.Lighting)!).Select(r => Key(r, a.transform)).Where(present.Contains);
                                var actual = ((List<MeshRenderer>)field.GetValue(cb.Lighting)!).Select(r => Key(r, b.transform));
                                if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Tube ordered lighting membership differs: " + field.Name);
                            }
                            var va = (float)bounds.Invoke(null, new object[] { a.transform, true })!;
                            var vb = (float)bounds.Invoke(null, new object[] { b.transform, true })!;
                            if (BitConverter.SingleToInt32Bits(va) != BitConverter.SingleToInt32Bits(vb))
                                throw new InvalidOperationException($"Tube bounds differ fixture={fixtures} A={va:R} B={vb:R}");
                            var ma = (GameObject?)_tubeType.GetField("_currentModel", All)!.GetValue(ca.Model);
                            var mb = (GameObject?)_tubeType.GetField("_currentModel", All)!.GetValue(cb.Model);
                            if (ma?.name != mb?.name || !Equals(_tubeType.GetField("_currentModelOrientation", All)!.GetValue(ca.Model),
                                _tubeType.GetField("_currentModelOrientation", All)!.GetValue(cb.Model))) throw new InvalidOperationException("Tube selection differs");
                            checks++;
                        }
                        Visible(2); Visible(0); Light(.5f, true); Light(null, false);
                        Color(new Color(.125f, .25f, .5f, 1f), null, null); Color(null, 1f, UnityEngine.Color.gray);
                        if (route == 1)
                        {
                            // Mirrors a renderer attached after the original Awake
                            // collection. It must not enter that list until recollect.
                            foreach (var root in new[] { a, b })
                            {
                                var child = new GameObject("Late renderer"); child.transform.SetParent(root.transform, false);
                                child.AddComponent<MeshFilter>().sharedMesh = plan.Variants[0].Source.GetComponentInChildren<MeshFilter>(true).sharedMesh;
                                child.AddComponent<MeshRenderer>().sharedMaterial = plan.Variants[0].Source.GetComponentInChildren<MeshRenderer>(true).sharedMaterial;
                            }
                        }
                        if (route == 3)
                        {
                            for (var n = 0; n < 65; n++) Color(null, n % 2, null);
                            if (entry.Instances.Any(g => !g)) throw new InvalidOperationException("Tube material history limit did not materialize");
                            historyLimits++;
                        }
                        for (var n = 0; n < plan.Variants.Length; n++)
                        {
                            var i = (n * 5 + initial) % plan.Variants.Length;
                            if (!entry.Instances[i]) firstUses++;
                            var orientation = Enum.ToObject(orientationType, initial);
                            select.Invoke(ca.Model, new object[] { pa.GetChild(plan.Variants[i].SiblingIndex).gameObject, orientation });
                            select.Invoke(cb.Model, new object[] { plan.Variants[i].Source, orientation }); selections++; Check();
                            if (route == 2 && n == 0)
                            {
                                exclude.Invoke(colorer, new object[] { ca.Model, pa.GetChild(plan.Variants[i].SiblingIndex).gameObject });
                                exclude.Invoke(colorer, new object[] { cb.Model, entry.Instances[i]! });
                            }
                            if (route == 1 && n == 3) { _collectLighting.Invoke(ca.Lighting, null); _collectLighting.Invoke(cb.Lighting, null); }
                            Visible((n + initial) % 4); Light(n % 2 == 0 ? 0f : 4f, true); Check();
                            pa.GetChild(plan.Variants[i].SiblingIndex).gameObject.SetActive(false); entry.Instances[i]!.SetActive(false);
                            select.Invoke(ca.Model, new object[] { pa.GetChild(plan.Variants[i].SiblingIndex).gameObject, orientation });
                            select.Invoke(cb.Model, new object[] { plan.Variants[i].Source, orientation }); selections++; Check();
                        }
                        for (var mask = 0; mask < 64; mask++)
                        {
                            var args = Enumerable.Range(0, 6).Select(i => (object)((mask & (1 << i)) != 0)).ToArray();
                            Exception? Invoke(object m) { try { match.Invoke(m, args); return null; } catch (TargetInvocationException e) { return e.InnerException; } }
                            var ea = Invoke(ca.Model); var eb = Invoke(cb.Model);
                            if (ea?.GetType() != eb?.GetType() || ea?.Message != eb?.Message) throw new InvalidOperationException("Tube matching exception differs");
                            graphInputs++; Check();
                        }
                        _lightingType.GetProperty("Renderers", All)!.GetValue(cb.Lighting); Check();
                        if (VariantState(a.transform, true) != VariantState(b.transform, true)) throw new InvalidOperationException("Full tube fixture differs");
                        fixtures++;
                    }
                    finally
                    {
                        if (a) Object.DestroyImmediate(a);
                        if (b) { Roots.Remove(b!); Object.DestroyImmediate(b); }
                        if (materialCache != null)
                            foreach (Material material in ((IDictionary)materialCacheType.GetField("_coloredToInitial", All)!.GetValue(materialCache)!).Keys) Object.DestroyImmediate(material);
                        Entries.RemoveAll(w => !w.TryGetTarget(out var e) || !e.Root);
                        if (Ancestors.TryGetValue(_holder, out var bucket)) bucket.Entries.RemoveAll(w => !w.TryGetTarget(out var e) || !e.Root);
                    }
                }
                if (VariantState(plan.Original.transform, true) != original) throw new InvalidOperationException("Tube fixture mutated source prefab");
            }
            Debug.Log($"[T3MPLAZYTUBE] FIRST USE PASS layouts={models.Length} fixtures={fixtures} firstUses={firstUses} selections={selections} graphInputs={graphInputs} checks={checks} historyLimits={historyLimits}; native lighting/material/exclusion/recollect, inactive isolated roots");
        }
        finally
        {
            _enrolled = enrolled; _created = created; _boundsQueries = queries; _namedReads = named; _participatingNamedReads = participating; _boundsValidate = shadow;
            Reasons.Clear(); foreach (var pair in reasons) Reasons.Add(pair.Key, pair.Value);
        }
    }
}
