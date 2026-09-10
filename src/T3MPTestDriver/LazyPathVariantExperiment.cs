using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

// World-load experiment: preserve native path selection, create its static
// candidate hierarchy only when selected or when a complete-hierarchy reader
// needs it. Gameplay components and native initialization order remain intact.
internal static partial class LazyPathVariantExperiment
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Variant { internal GameObject Source = null!; internal string Name = ""; internal int Mask, SiblingIndex; }
    private sealed class Plan
    {
        internal GameObject Original = null!, Pruned = null!, BoundsProbe = null!;
        internal MeshRenderer[] ProbeRenderers = null!;
        internal int[] ParentPath = null!;
        internal Variant[] Variants = null!;
        internal bool Tube;
        internal int[] ParentSlots = null!;
    }
    private sealed class Entry
    {
        internal Plan Plan = null!;
        internal GameObject Root = null!;
        internal Transform Parent = null!;
        internal GameObject?[] Instances = null!;
        internal Material?[] Materials = null!;
        internal bool HasVisibility, Enabled, Collider, HasShadow, Creating;
        internal ShadowCastingMode Shadow;
        internal object? LightingOwner;
        internal bool HasLighting;
        internal uint LightingValue;
        internal List<MaterialReplay>? MaterialReplays;
        internal List<LightingMember>? LightingMembership;
    }
    private sealed class Bucket { internal readonly List<WeakReference<Entry>> Entries = new List<WeakReference<Entry>>(); }
    private struct BoundsScope { internal bool Check; internal float Expected; }
    private static readonly Dictionary<GameObject, Plan?> Plans = new Dictionary<GameObject, Plan?>();
    private static readonly Dictionary<Type, bool> EligibleComponentTypes = new Dictionary<Type, bool>();
    private static readonly ConditionalWeakTable<GameObject, Entry> Roots = new ConditionalWeakTable<GameObject, Entry>();
    private static readonly ConditionalWeakTable<GameObject, Bucket> Ancestors = new ConditionalWeakTable<GameObject, Bucket>();
    private static readonly List<WeakReference<Entry>> Entries = new List<WeakReference<Entry>>();
    private static readonly Dictionary<string, long> Reasons = new Dictionary<string, long>();
    private static GameObject _holder = null!;
    private static Type _pathType = null!, _specType = null!, _description = null!;
    private static int _entityDepth;
    private static long _enrolled, _created, _fallbacks;
    private static long _boundsQueries, _boundsChecks;
    private static long _namedReads, _participatingNamedReads;
    private static bool _boundsValidate;
    private static bool _pathEnabled, _tubeEnabled;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        _pathEnabled = Environment.GetCommandLineArgs().Contains("-t3mpTestLazyPathVariants");
        _tubeEnabled = Environment.GetCommandLineArgs().Contains("-t3mpTestLazyTubeVariants");
        if (!_pathEnabled && !_tubeEnabled) return;
        _pathType = Find("Timberborn.PathSystem.DynamicPathModel");
        _specType = Find("Timberborn.PathSystem.DynamicPathModelSpec");
        _description = Find("Timberborn.Timbermesh.TimbermeshDescription");
        _boundsValidate = Environment.GetCommandLineArgs().Contains("-t3mpTestPathBoundsValidate");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.lazy-path-variants");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name)
        {
            if (name == null) return null;
            var result = Activator.CreateInstance(hm, typeof(LazyPathVariantExperiment).GetMethod(name, All))!;
            // Let ModelLayout's native query scope begin before this override;
            // its finalizer can then restore that scope normally.
            if (name == nameof(GetVariant)) hm.GetField("priority")!.SetValue(result, -100);
            if (name == nameof(InitializeTube)) hm.GetField("priority")!.SetValue(result, 800);
            return result;
        }
        void Patch(Type type, string name, string? before = null, string? after = null, string? final = null, Func<MethodInfo, bool>? select = null)
        {
            var method = type.GetMethods(All).Single(m => m.Name == name && (select == null || select(m)));
            patch.Invoke(harmony, new object?[] { method, Hook(before), Hook(after), null, Hook(final) });
        }
        Patch(Find("Timberborn.WorldPersistence.WorldEntitiesLoader"), "InstantiateEntities", nameof(BeginEntities), final: nameof(EndEntities));
        Patch(typeof(BaseInstantiator), "InstantiateInactive", nameof(BeforeClone), nameof(AfterClone));
        Patch(_pathType, "GetModelVariant", nameof(GetVariant));
        Patch(_pathType, "SetCurrentModel", nameof(SelectVariant));
        Patch(Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions"), "ToggleModelVisibility", nameof(Visibility));
        Patch(Find("Timberborn.Rendering.MaterialColorer"), "SetCachedMaterialProperties", _tubeEnabled ? nameof(BeforeMaterialProperties) : nameof(BeforeObjectRead));
        Patch(Find("Timberborn.Rendering.MaterialLightingEnabler"), "EnableLighting", _tubeEnabled ? nameof(BeforeLightingObject) : nameof(BeforeObjectRead),
            select: m => m.GetParameters()[0].ParameterType == typeof(GameObject));
        Patch(Find("Timberborn.SelectionSystem.HighlightRenderingService"), "UpdateSelectionLayer", nameof(BeforeObjectRead));
        Patch(Find("Timberborn.SlotSystem.SlotRetriever"), "GetTransformsInChildren", nameof(BeforeObjectRead));
        Patch(typeof(Timberborn.Common.BoundsCalculator), "GetRendererYMaxBoundInternal", nameof(BeforeBoundsRead), nameof(AfterBoundsRead));
        Patch(typeof(Timberborn.Common.GameObjectExtensions), "FindChildTransform", nameof(BeforeNamedRead));
        if (_tubeEnabled)
        {
            InitializeTubeHooks();
            Patch(_tubeType, "InitializeModels", nameof(InitializeTube));
            Patch(_tubeType, "SetCurrentModel", nameof(SelectVariant));
            Patch(_lightingType, "CollectRenderers", nameof(BeforeCollectLighting), nameof(AfterCollectLighting));
            Patch(_lightingType, "get_Renderers", nameof(BeforeLightingList));
            Patch(Find("Timberborn.Rendering.MaterialLightingEnabler"), "EnableLighting", nameof(BeforeLightingBase), final: nameof(AfterLightingBase),
                select: m => m.GetParameters()[0].ParameterType == typeof(BaseComponent));
            Patch(Find("Timberborn.Rendering.MaterialColorer"), "EnableLightingAndDisableChanges", nameof(BeforeDisableLightingChanges));
        }
        Debug.Log("[T3MPLAZYPATH] installed boundsValidate=" + _boundsValidate + " paths=" + _pathEnabled + " tubes=" + _tubeEnabled + "; world entity clones, native selection, static candidate factories");
    }

    private static void BeginEntities()
    {
        if (_entityDepth++ != 0) return;
        _enrolled = _created = _fallbacks = _boundsQueries = _boundsChecks = _namedReads = _participatingNamedReads = 0; Reasons.Clear();
        Entries.RemoveAll(w => !w.TryGetTarget(out var e) || !e.Root);
        foreach (var pair in Plans.Where(p => !p.Key).ToArray())
        {
            if (pair.Value?.Pruned) Object.DestroyImmediate(pair.Value.Pruned);
            if (pair.Value?.BoundsProbe) Object.DestroyImmediate(pair.Value.BoundsProbe);
            Plans.Remove(pair.Key);
        }
    }
    private static void EndEntities() { --_entityDepth; Report("entities-end"); }

    private static bool Allowed(Component component)
    {
        if (!component) return false;
        var t = component.GetType();
        return t == typeof(Transform) || t == typeof(MeshFilter) || t == typeof(MeshRenderer) || t == _description ||
               t == typeof(BoxCollider) && !((BoxCollider)component).isTrigger && !((BoxCollider)component).attachedRigidbody;
    }
    private static Plan? Build(GameObject prefab, object spec, bool tube = false)
    {
        var all = prefab.GetComponentsInChildren<Transform>(true);
        string[] names; string? tubePrefix = null;
        if (tube)
        {
            tubePrefix = (string?)_tubeSpecType.GetProperty("ModelPrefix", All)!.GetValue(spec);
            if (string.IsNullOrWhiteSpace(tubePrefix)) return null;
            // Match native StartsWith semantics and reject nonbinary/extra suffixes.
            names = all.Where(t => t != prefab.transform && t.name.StartsWith(tubePrefix)).Select(t => t.name).ToArray();
            if (names.Length == 0 || names.Any(n => n.Length != tubePrefix.Length + 6 || n.Substring(tubePrefix.Length).Any(c => c != '0' && c != '1'))) return null;
        }
        else
        {
            var ground = (string?)_specType.GetProperty("GroundModelPrefix", All)!.GetValue(spec);
            var roof = (string?)_specType.GetProperty("RoofModelPrefix", All)!.GetValue(spec);
            if (string.IsNullOrWhiteSpace(ground) || string.IsNullOrWhiteSpace(roof) || ground == roof) return null;
            names = new[] { ground, roof }.SelectMany(p => new[] { "0000", "0010", "1010", "0011", "0111", "1111" }.Select(s => p + s)).ToArray();
        }
        if (names.Any(n => all.Count(t => t.name == n) != 1)) return null;
        var variants = all.Where(t => names.Contains(t.name)).ToArray();
        var parent = variants[0].parent;
        if (!parent || variants.Any(v => v.parent != parent) || (!tube && parent.childCount != variants.Length) || parent.GetComponents<Renderer>().Length != 0) return null;
        if (variants.Any(v => !v.GetComponentsInChildren<Component>(true).All(Allowed) ||
                             v.GetComponentsInChildren<Renderer>(true).Length != 1)) return null;
        // Reject live scripts elsewhere in the copied prefab too. The existing
        // inert mod sentinel is permitted only before registration into a slot.
        foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            if (!Allowed(component) && !(component && component.GetType().FullName == "T3MP.ActiveInHierarchySentinel" &&
                (int)component.GetType().GetField("SlotIndex", All)!.GetValue(component)! < 0)) return null;
        var path = new List<int>();
        for (var t = parent; t != prefab.transform; t = t.parent) path.Add(t.GetSiblingIndex());
        path.Reverse();
        if (!_holder)
        {
            _holder = new GameObject("T3MP path variant prototypes"); _holder.SetActive(false);
            _holder.hideFlags = HideFlags.HideAndDontSave; Object.DontDestroyOnLoad(_holder);
        }
        var copy = Object.Instantiate(prefab, _holder.transform, false); copy.name = prefab.name;
        var probe = Object.Instantiate(prefab, _holder.transform, false); probe.SetActive(false);
        // The reusable probe is never rendered, injected or entered into the
        // world's physics/slot systems. Only native mesh bounds are read.
        foreach (var c in probe.GetComponentsInChildren<Component>(true))
            if (c is Collider || c is MonoBehaviour) Object.DestroyImmediate(c);
        var probeParent = probe.transform;
        foreach (var i in path) probeParent = probeParent.GetChild(i);
        var probeRenderers = variants.Select(v => probeParent.GetChild(v.GetSiblingIndex()).GetComponentInChildren<MeshRenderer>(true)).ToArray();
        var parentSlots = Enumerable.Range(0, parent.childCount).Select(i => Array.IndexOf(variants, parent.GetChild(i))).ToArray();
        var copyParent = copy.transform;
        foreach (var i in path) copyParent = copyParent.GetChild(i);
        for (var i = parentSlots.Length - 1; i >= 0; i--)
            if (parentSlots[i] >= 0) Object.DestroyImmediate(copyParent.GetChild(i).gameObject);
        return new Plan { Original = prefab, Pruned = copy, BoundsProbe = probe, ProbeRenderers = probeRenderers, ParentPath = path.ToArray(), Tube = tube, ParentSlots = parentSlots,
            Variants = variants.Select(v => new Variant { Source = v.gameObject, Name = v.name, SiblingIndex = v.GetSiblingIndex(),
                Mask = tube ? Enumerable.Range(0, 6).Sum(i => v.name[tubePrefix!.Length + i] == '1' ? 1 << i : 0) : 0 }).ToArray() };
    }
    private static void BeforeClone(ref GameObject prefab, Blueprint blueprint, ImmutableArray<Type> decoratedComponents, out Plan? __state)
    {
        __state = null;
        if (_entityDepth == 0) return;
        var tube = _tubeEnabled && decoratedComponents.Contains(_tubeType);
        if (!tube && !(_pathEnabled && decoratedComponents.Contains(_pathType))) return;
        foreach (var type in decoratedComponents)
            if (!(tube && type == _lightingType) && !EligibleComponent(type)) { _fallbacks++; return; }
        if (!Plans.TryGetValue(prefab, out var plan))
            Plans.Add(prefab, plan = Build(prefab, blueprint.GetSpec(tube ? _tubeSpecType : _specType), tube));
        if (plan == null) { _fallbacks++; return; }
        __state = plan; prefab = plan.Pruned;
    }
    private static bool EligibleComponent(Type type)
    {
        if (EligibleComponentTypes.TryGetValue(type, out var eligible)) return eligible;
        // GetName constructs assembly metadata; the same type eligibility is
        // immutable and must not be recomputed for every component of every path.
        eligible = type.Assembly.GetName().Name!.StartsWith("Timberborn.", StringComparison.Ordinal) &&
            type.FullName is not ("Timberborn.Rendering.EntityMaterials" or "Timberborn.Rendering.MaterialLightingRenderers");
        EligibleComponentTypes.Add(type, eligible); return eligible;
    }
    private static void AfterClone(GameObject __result, Plan? __state)
    {
        if (__state == null || !__result) return;
        var parent = __result.transform;
        foreach (var i in __state.ParentPath) parent = parent.GetChild(i);
        var entry = new Entry { Root = __result, Parent = parent, Plan = __state,
            Instances = new GameObject?[__state.Variants.Length], Materials = new Material?[__state.Variants.Length] };
        Roots.Add(__result, entry); var weak = new WeakReference<Entry>(entry); Entries.Add(weak); _enrolled++;
        for (var t = parent; t; t = t.parent) Ancestors.GetOrCreateValue(t.gameObject).Entries.Add(weak);
    }
    private static GameObject Ensure(Entry entry, int index, string reason)
    {
        var existing = entry.Instances[index]; if (existing) return existing!;
        entry.Creating = true;
        try
        {
            var source = entry.Plan.Variants[index].Source;
            var clone = Object.Instantiate(source, _holder.transform, false); clone.name = source.name;
            if (entry.Plan.Tube) clone.SetActive(false);
            var material = entry.Materials[index];
            if (material) { clone.SetActive(false); clone.GetComponentInChildren<Renderer>(true).sharedMaterial = material; }
            if (entry.HasVisibility)
            {
                foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                { renderer.enabled = entry.Enabled; if (entry.HasShadow) renderer.shadowCastingMode = entry.Shadow; }
                foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) collider.enabled = entry.Collider;
            }
            clone.transform.SetParent(entry.Parent, false);
            clone.transform.SetSiblingIndex(entry.Plan.Variants[index].SiblingIndex - index + entry.Instances.Take(index).Count(g => g));
            entry.Instances[index] = clone; _created++;
            if (entry.Plan.Tube)
            {
                if (entry.MaterialReplays != null)
                    foreach (var operation in entry.MaterialReplays)
                        _setMaterialProperties.Invoke(operation.Colorer, new object?[] { clone, operation.Color, operation.Grayscale, operation.LightingColor });
                if (entry.HasLighting) clone.GetComponentInChildren<MeshRenderer>(true).SetShaderUserValue(entry.LightingValue);
                RefreshLightingMembership(entry);
            }
            Reasons.TryGetValue(reason, out var count); Reasons[reason] = count + 1;
            return clone;
        }
        finally { entry.Creating = false; }
    }
    private static void EnsureAll(Entry entry, string reason)
    { for (var i = 0; i < entry.Instances.Length; i++) Ensure(entry, i, reason); }
    private static bool GetVariant(BaseComponent __instance, string prefix, string variant, Material material, ref GameObject __result)
    {
        if (!Roots.TryGetValue(__instance.GameObject, out var entry)) return true;
        var name = prefix + variant;
        var index = -1;
        for (var i = 0; i < entry.Plan.Variants.Length; i++)
            if (entry.Plan.Variants[i].Name == name) { index = i; break; }
        if (index < 0) { EnsureAll(entry, "changed-name"); return true; }
        entry.Materials[index] = material;
        if (entry.Instances[index])
        {
            entry.Instances[index]!.SetActive(false);
            entry.Instances[index]!.GetComponentInChildren<Renderer>(true).sharedMaterial = material;
        }
        __result = entry.Plan.Variants[index].Source;
        return false;
    }
    private static void SelectVariant(BaseComponent __instance, ref GameObject model)
    {
        if (!Roots.TryGetValue(__instance.GameObject, out var entry)) return;
        var index = -1;
        for (var i = 0; i < entry.Plan.Variants.Length; i++)
            if (ReferenceEquals(entry.Plan.Variants[i].Source, model)) { index = i; break; }
        if (index >= 0) model = Ensure(entry, index, "selected");
    }
    private static void Visit(GameObject root, Action<Entry> action)
    {
        if (!root || !Ancestors.TryGetValue(root, out var bucket)) return;
        foreach (var weak in bucket.Entries)
            if (weak.TryGetTarget(out var entry) && entry.Root && !entry.Creating) action(entry);
    }
    private static void BeforeObjectRead(GameObject __0, MethodBase? __originalMethod = null)
    {
        if (!__0 || !Ancestors.TryGetValue(__0, out var bucket)) return;
        foreach (var weak in bucket.Entries)
            if (weak.TryGetTarget(out var e) && e.Root && !e.Creating)
                EnsureAll(e, "hierarchy-reader/" + (__originalMethod?.Name ?? "explicit"));
    }
    private static bool BeforeBoundsRead(Transform parent, bool includeInactive, ref float __result, out BoundsScope __state)
    {
        __state = default;
        if (!includeInactive || !parent || !Ancestors.TryGetValue(parent.gameObject, out var bucket)) return true;
        var entries = bucket.Entries.Select(w => w.TryGetTarget(out var e) && e.Root ? e : null).Where(e => e != null).ToArray();
        if (entries.Length == 1 && (entries[0]!.Parent == parent || entries[0]!.Root.transform == parent) && entries[0]!.Instances.Any(g => !g))
        {
            var entry = entries[0]!;
            var probe = entry.Plan.BoundsProbe.transform;
            var values = new List<float>();
            try
            {
                // Mirror the exact local-transform chain beneath the same real
                // parent, rather than approximating world bounds from lossyScale.
                // This inactive probe has no scripts or colliders. Restore its
                // private parent before any caller/callback resumes.
                probe.SetParent(entry.Root.transform.parent, false);
                var source = entry.Root.transform; var target = probe;
                void CopyTransform()
                {
                    target.SetLocalPositionAndRotation(source.localPosition, source.localRotation);
                    target.localScale = source.localScale;
                }
                CopyTransform();
                foreach (var i in entry.Plan.ParentPath)
                { source = source.GetChild(i); target = target.GetChild(i); CopyTransform(); }
                void Gather(Transform node)
                {
                    foreach (var renderer in node.GetComponents<MeshRenderer>()) values.Add(renderer.bounds.max.y);
                    if (node == entry.Parent)
                    {
                        var actualIndex = 0;
                        foreach (var i in entry.Plan.ParentSlots)
                        {
                            if (i < 0) Gather(node.GetChild(actualIndex++));
                            else
                            {
                                values.Add((entry.Instances[i] ? entry.Instances[i]!.GetComponentInChildren<MeshRenderer>(true) : entry.Plan.ProbeRenderers[i]).bounds.max.y);
                                if (entry.Instances[i]) actualIndex++;
                            }
                        }
                    }
                    else for (var i = 0; i < node.childCount; i++) Gather(node.GetChild(i));
                }
                Gather(parent);
            }
            finally { probe.SetParent(_holder.transform, false); }
            // Preserve native ordered Enumerable.Max behavior, including signed
            // zero and NaN handling. There is exactly one renderer per candidate.
            var result = values.DefaultIfEmpty(0f).Max(); _boundsQueries++;
            if (_boundsValidate)
            { __state = new BoundsScope { Check = true, Expected = result }; EnsureAll(entry, "bounds-validation"); return true; }
            __result = result; return false;
        }
        Visit(parent.gameObject, e => EnsureAll(e, "bounds-reader/" + parent.name));
        return true;
    }
    private static void AfterBoundsRead(float __result, BoundsScope __state)
    {
        if (!__state.Check) return;
        if (BitConverter.SingleToInt32Bits(__result) != BitConverter.SingleToInt32Bits(__state.Expected))
            throw new InvalidOperationException($"Lazy path bounds differ: native={__result:R}, probe={__state.Expected:R}");
        _boundsChecks++;
    }
    private static void BeforeNamedRead(GameObject gameObject, string childName)
    {
        // This entry point wraps a recursive search and is hot for unrelated entities.
        // Do not allocate a capturing callback before discovering participation.
        _namedReads++;
        if (!gameObject || string.IsNullOrEmpty(childName) || !Ancestors.TryGetValue(gameObject, out var bucket)) return;
        _participatingNamedReads++;
        foreach (var weak in bucket.Entries)
        {
            if (!weak.TryGetTarget(out var e) || !e.Root || e.Creating) continue;
            foreach (var variant in e.Plan.Variants)
                if (childName == variant.Name || childName.Length == variant.Name.Length + 6 &&
                    childName.StartsWith(variant.Name, StringComparison.Ordinal) && childName.EndsWith(".Model", StringComparison.Ordinal))
                { EnsureAll(e, "named-reader"); break; }
        }
    }
    private static void Visibility(GameObject model, bool showModel, bool showShadows)
    {
        if (!model || !Ancestors.TryGetValue(model, out var bucket)) return;
        foreach (var weak in bucket.Entries)
        {
            if (!weak.TryGetTarget(out var e) || !e.Root || e.Creating) continue;
            e.HasVisibility = true; e.Enabled = showModel || showShadows; e.Collider = showModel;
            if (showModel || showShadows)
            { e.HasShadow = true; e.Shadow = showShadows ? showModel ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly : ShadowCastingMode.Off; }
        }
    }
    internal static void Report(string phase)
    {
        if (!_pathEnabled && !_tubeEnabled) return;
        var live = Entries.Select(w => w.TryGetTarget(out var e) && e.Root ? e : null).Where(e => e != null).ToArray();
        Debug.Log($"[T3MPLAZYPATH] phase={phase} enrolled={_enrolled} created={_created} pending={live.Sum(e => e!.Instances.Count(g => !g))} fallbacks={_fallbacks} boundsQueries={_boundsQueries} boundsChecks={_boundsChecks} namedReads={_namedReads} participatingNamedReads={_participatingNamedReads} reasons={string.Join(",", Reasons.Select(p => p.Key + ":" + p.Value))}");
        if (_tubeEnabled) Debug.Log($"[T3MPLAZYTUBE] phase={phase} owners={live.Count(e => e!.Plan.Tube)} pending={live.Where(e => e!.Plan.Tube).Sum(e => e!.Instances.Count(g => !g))} lightingOwners={live.Count(e => e!.Plan.Tube && e.LightingOwner != null)} lightingCaptured={live.Count(e => e!.Plan.Tube && e.HasLighting)}");
    }
    internal static void ValidateBeforeSnapshots(IReadOnlyList<EntityComponent> entities)
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestLazyPathVariantsValidate") && !Environment.GetCommandLineArgs().Contains("-t3mpTestLazyTubeVariantsValidate")) return;
        if (_pathEnabled) ValidateFirstUse(entities);
        if (_tubeEnabled) ValidateTubeFirstUse(entities);
        Report("before-validation");
        var args = Environment.GetCommandLineArgs();
        var directory = System.IO.Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        // Preserve raw ray output. Separately identify the original template
        // slot of each instantiated candidate; omission compacts sibling indices.
        var owners = entities.ToDictionary(e => e.GameObject, e => e.EntityId.ToString());
        using (var map = new System.IO.StreamWriter(System.IO.Path.Combine(directory, "path-variant-layout.tsv")))
        {
            map.WriteLine("actual\toriginal\tvariant");
            string Relative(Transform t, Transform root)
            {
                var parts = new List<string>();
                for (; t != root; t = t.parent) parts.Add(t.GetSiblingIndex() + ":" + t.name);
                parts.Reverse(); return string.Join("/", parts);
            }
            foreach (var weak in Entries)
            if (weak.TryGetTarget(out var e) && e.Root && owners.TryGetValue(e.Root, out var id))
            for (var i = 0; i < e.Instances.Length; i++)
            {
                var instance = e.Instances[i]; if (!instance) continue;
                var source = e.Plan.Variants[i];
                if (source.Source.transform.GetSiblingIndex() != source.SiblingIndex || source.Source.name != instance!.name)
                    throw new InvalidOperationException("Path variant source layout changed");
                var originalRoot = id + "/" + Relative(e.Parent, e.Root.transform) + "/" + source.SiblingIndex + ":" + source.Name;
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                {
                    var suffix = Relative(t, instance.transform);
                    var actual = id + "/" + Relative(t, e.Root.transform);
                    var original = originalRoot + (suffix.Length == 0 ? "" : "/" + suffix);
                    map.WriteLine(actual + "\t" + original + "\t" + source.Name);
                }
            }
        }
        SelectionSnapshot.Report(entities);
        var path = System.IO.Path.Combine(directory, "selection-state.tsv");
        if (System.IO.File.Exists(path)) System.IO.File.Move(path, System.IO.Path.Combine(directory, "selection-lazy-path.tsv"));
        foreach (var weak in Entries.ToArray()) if (weak.TryGetTarget(out var e) && e.Root) EnsureAll(e, "validation");
        Report("after-validation");
    }
}
