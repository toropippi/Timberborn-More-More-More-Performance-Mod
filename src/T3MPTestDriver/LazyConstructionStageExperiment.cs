using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Opt-in experiment: retain all stage roots and defer only their static child
// payloads. Full-hierarchy readers materialize before executing native code.
// Currently uses the exact installed GoodStack reader hooks as diagnostic seams;
// it is not a general compatibility API or a production feature.
internal static class LazyConstructionStageExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private sealed class Part { internal GameObject Source = null!; internal int[] Parent = null!; internal int Sibling; internal MeshRenderer[] ProbeRenderers = null!; }
    private sealed class Plan
    {
        internal GameObject Original = null!, Pruned = null!, Probe = null!;
        internal Part[] Parts = null!;
        internal int[] Unfinished = null!;
        internal HashSet<string> MissingNames = null!;
        internal int Nodes, Renderers, Colliders;
    }
    private sealed class Entry
    {
        internal Plan Plan = null!;
        internal GameObject Root = null!, Unfinished = null!;
        internal Transform[] Parents = null!;
        internal bool Creating, Done, HasVisibility, Visible, HasShadow, Collider;
        internal ShadowCastingMode Shadow;
    }
    private sealed class Bucket { internal readonly List<WeakReference<Entry>> Items = new(); }
    private static readonly Dictionary<GameObject, Plan?> Plans = new();
    private static readonly Dictionary<Type, bool> Eligible = new();
    private static readonly ConditionalWeakTable<GameObject, Bucket> Ancestors = new();
    private static readonly ConditionalWeakTable<GameObject, Entry> Roots = new();
    private static readonly ConditionalWeakTable<GameObject, Entry> Stages = new();
    private static readonly List<WeakReference<Entry>> Entries = new();
    private static readonly Dictionary<string, long> Reasons = new();
    private static Type _description = null!, _spec = null!, _building = null!;
    private static GameObject _holder = null!;
    private static int _depth;
    private static bool _enabled;
    private static long _enrolled, _restored, _fallback, _nodes, _renderers, _colliders;
    private static long _bounds, _boundsChecked;
    private static bool _validateBounds;
    private static Func<Transform, bool, float> _nativeBounds = null!;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestLazyConstructionStages")) return;
        var main = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Code");
        if (main.ManifestModule.ModuleVersionId.ToString() != "08c3b39d-9338-4757-ad48-76601dedcc33")
            throw new InvalidOperationException("Construction stage experiment requires its reviewed installed module");
        var hooks = main.GetType("T3MP.Loading.DeferredGoodStackModels")!;
        if (!(bool)hooks.GetProperty("Installed", All)!.GetValue(null)!)
            throw new InvalidOperationException("Required hierarchy reader hooks are absent");
        _description = Find("Timberborn.Timbermesh.TimbermeshDescription");
        _validateBounds = Environment.GetCommandLineArgs().Contains("-t3mpTestConstructionStageBoundsValidate");
        _nativeBounds = (Func<Transform, bool, float>)Delegate.CreateDelegate(typeof(Func<Transform, bool, float>),
            typeof(Timberborn.Common.BoundsCalculator).GetMethod("GetRendererYMaxBoundInternal", All)!);
        _spec = Find("Timberborn.Buildings.BuildingModelSpec"); _building = Find("Timberborn.Buildings.BuildingModel");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.lazy-construction-stages");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name) => name == null ? null : Activator.CreateInstance(hm, typeof(LazyConstructionStageExperiment).GetMethod(name, All));
        void Patch(Type type, string method, string? before = null, string? after = null, string? final = null) =>
            patch.Invoke(harmony, new object?[] { type.GetMethod(method, All), Hook(before), Hook(after), null, Hook(final) });
        Patch(Find("Timberborn.WorldPersistence.WorldEntitiesLoader"), "InstantiateEntities", nameof(Begin), final: nameof(End));
        Patch(typeof(BaseInstantiator), "InstantiateInactive", nameof(BeforeClone), nameof(AfterClone));
        // Reuse native read boundaries already audited by the installed mod.
        // No protected component Awake/Initialize method is patched.
        Patch(hooks, "BeforeObjectRead", nameof(ObjectRead));
        Patch(hooks, "BeforeBoundsRead", nameof(BoundsRead));
        Patch(hooks, "BeforeOwnerRead", nameof(OwnerRead));
        Patch(hooks, "BeforeMaterialsQuery", nameof(MaterialRead));
        Patch(hooks, "Visibility", nameof(Visibility));
        Patch(typeof(Timberborn.Common.GameObjectExtensions), "FindChildTransform", nameof(NamedRead));
        Patch(typeof(Timberborn.Common.BoundsCalculator), "GetRendererYMaxBound", nameof(SharedBounds));
        Patch(typeof(Timberborn.Common.BoundsCalculator), "GetEnabledRendererYMaxBound", nameof(EnabledBounds));
        var rewrite = T3MP.Loading.LoadPatchBridge.Create("T3MP.ConstructionStageActivation",
            typeof(LazyConstructionStageExperiment).GetMethod(nameof(RewriteActivation), All)!);
        patch.Invoke(harmony, new object?[] { Find("Timberborn.ConstructionSites.ConstructionSiteProgressVisualizer").GetMethod("UpdateVisualization", All),
            null, null, Activator.CreateInstance(hm, rewrite), null });
        _enabled = true;
        Debug.Log("[T3MPLAZYSTAGE] installed; static descendants, retained native stage roots, native full-reader fallback");
    }
    private static void Begin() { _depth++; }
    private static void End() { _depth--; Report("entities-end"); }
    private static int[] Path(Transform node, Transform root)
    {
        var result = new List<int>();
        for (; node != root; node = node.parent) result.Add(node.GetSiblingIndex());
        result.Reverse(); return result.ToArray();
    }
    private static Transform Resolve(Transform root, int[] path)
    { foreach (var index in path) root = root.GetChild(index); return root; }
    private static bool Allowed(Component component)
    {
        if (!component) return false;
        var t = component.GetType();
        return t == typeof(Transform) || t == typeof(MeshRenderer) || t == typeof(MeshFilter) || t == _description ||
            t == typeof(BoxCollider) && !((BoxCollider)component).isTrigger && !((BoxCollider)component).attachedRigidbody;
    }
    private static Plan? Build(GameObject prefab, Blueprint blueprint)
    {
        var spec = blueprint.GetSpec(_spec);
        if (spec == null) return null;
        var name = (string)_spec.GetProperty("UnfinishedModelName")!.GetValue(spec)!;
        if (string.IsNullOrEmpty(name)) return null;
        var roots = prefab.GetComponentsInChildren<Transform>(true).Where(t => t.name == name).ToArray();
        if (roots.Length != 1) return null;
        var unfinished = roots[0];
        var parts = new List<Part>();
        foreach (Transform stage in unfinished)
        foreach (Transform payload in stage)
        {
            if (payload.GetComponentsInChildren<Component>(true).Any(c => !Allowed(c))) return null;
            parts.Add(new Part { Source = payload.gameObject, Parent = Path(stage, prefab.transform), Sibling = payload.GetSiblingIndex() });
        }
        if (parts.Count == 0) return null;
        if (!_holder)
        {
            _holder = new GameObject("T3MP construction stage prototypes"); _holder.SetActive(false);
            _holder.hideFlags = HideFlags.HideAndDontSave; Object.DontDestroyOnLoad(_holder);
        }
        var plan = new Plan { Original = prefab, Parts = parts.ToArray(), Unfinished = Path(unfinished, prefab.transform),
            MissingNames = new HashSet<string>(parts.SelectMany(p => p.Source.GetComponentsInChildren<Transform>(true)).Select(t => t.name), StringComparer.Ordinal),
            Nodes = parts.Sum(p => p.Source.GetComponentsInChildren<Transform>(true).Length),
            Renderers = parts.Sum(p => p.Source.GetComponentsInChildren<Renderer>(true).Length),
            Colliders = parts.Sum(p => p.Source.GetComponentsInChildren<Collider>(true).Length) };
        plan.Pruned = Object.Instantiate(prefab, _holder.transform, false); plan.Pruned.name = prefab.name;
        plan.Probe = Object.Instantiate(prefab, _holder.transform, false); plan.Probe.SetActive(false);
        foreach (var component in plan.Probe.GetComponentsInChildren<Component>(true))
            if (component is Collider || component is MonoBehaviour) Object.DestroyImmediate(component);
        foreach (var part in plan.Parts)
            part.ProbeRenderers = Resolve(plan.Probe.transform, part.Parent).GetChild(part.Sibling).GetComponentsInChildren<MeshRenderer>(true);
        foreach (var part in parts.AsEnumerable().Reverse())
            Object.DestroyImmediate(Resolve(plan.Pruned.transform, part.Parent).GetChild(part.Sibling).gameObject);
        Debug.Log($"[T3MPLAZYSTAGEPLAN] template={blueprint.Name} parts={parts.Count} nodes={plan.Nodes} renderers={plan.Renderers} colliders={plan.Colliders}");
        return plan;
    }
    private static void BeforeClone(ref GameObject prefab, Blueprint blueprint, ImmutableArray<Type> decoratedComponents, out Plan? __state)
    {
        __state = null;
        if (_depth == 0 || !decoratedComponents.Contains(_building)) return;
        foreach (var type in decoratedComponents)
        {
            if (!Eligible.TryGetValue(type, out var safe))
                Eligible.Add(type, safe = type.Assembly.GetName().Name!.StartsWith("Timberborn.", StringComparison.Ordinal) &&
                    type.FullName is not ("Timberborn.Rendering.EntityMaterials" or "Timberborn.Rendering.MaterialLightingRenderers"));
            if (!safe) { _fallback++; return; }
        }
        if (!Plans.TryGetValue(prefab, out var plan)) Plans.Add(prefab, plan = Build(prefab, blueprint));
        if (plan == null) { _fallback++; return; }
        __state = plan; prefab = plan.Pruned;
    }
    private static void AfterClone(GameObject __result, Plan? __state)
    {
        if (__state == null || !__result) return;
        Register(__result, __state);
        _enrolled++; _nodes += __state.Nodes; _renderers += __state.Renderers; _colliders += __state.Colliders;
    }
    private static Entry Register(GameObject root, Plan plan)
    {
        var entry = new Entry { Root = root, Plan = plan, Unfinished = Resolve(root.transform, plan.Unfinished).gameObject,
            Parents = plan.Parts.Select(p => Resolve(root.transform, p.Parent)).ToArray() };
        Roots.Add(root, entry);
        foreach (var stage in plan.Parts.Select(p => Resolve(root.transform, p.Parent).gameObject).Distinct()) Stages.Add(stage, entry);
        var weak = new WeakReference<Entry>(entry); Entries.Add(weak);
        for (var t = entry.Unfinished.transform; t; t = t.parent) Ancestors.GetOrCreateValue(t.gameObject).Items.Add(weak);
        return entry;
    }
    private static void Ensure(Entry entry, string reason)
    {
        if (entry.Done || entry.Creating || !entry.Root) return;
        entry.Creating = true;
        try
        {
            foreach (var part in entry.Plan.Parts)
            {
                var clone = Object.Instantiate(part.Source, _holder.transform, false); clone.name = part.Source.name;
                if (entry.HasVisibility)
                {
                    foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true))
                    { renderer.enabled = entry.Visible; if (entry.HasShadow) renderer.shadowCastingMode = entry.Shadow; }
                    foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) collider.enabled = entry.Collider;
                }
                clone.transform.SetParent(Resolve(entry.Root.transform, part.Parent), false);
                clone.transform.SetSiblingIndex(part.Sibling);
            }
            entry.Done = true; _restored++;
            Reasons.TryGetValue(reason, out var count); Reasons[reason] = count + 1;
        }
        finally { entry.Creating = false; }
    }
    private static void Visit(GameObject root, Action<Entry> action)
    {
        if (!root || !Ancestors.TryGetValue(root, out var bucket)) return;
        foreach (var weak in bucket.Items)
            if (weak.TryGetTarget(out var entry) && entry.Root && !entry.Done && !entry.Creating) action(entry);
    }
    private static void ObjectRead(GameObject __0) => Visit(__0, e => Ensure(e, "hierarchy-reader"));
    private static void OwnerRead(object __0) { if (__0 is BaseComponent c) Visit(c.GameObject, e => Ensure(e, "owner-reader")); }
    private static void MaterialRead(object __0, Transform __1) { if (__1) Visit(__1.gameObject, e => Ensure(e, "material-reader")); }
    private static void BoundsRead(Transform __0, bool __1) { if (__1 && __0) Visit(__0.gameObject, e => Ensure(e, "bounds-reader")); }
    private static void EnabledBounds(Transform parent)
    { if (parent) Visit(parent.gameObject, e => { if (e.Plan.Parts.Any(p => Resolve(e.Root.transform, p.Parent).gameObject.activeInHierarchy)) Ensure(e, "active-bounds"); }); }
    private static IEnumerable<T> RewriteActivation<T>(IEnumerable<T> source)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!; var count = 0;
        foreach (var instruction in source)
        {
            if (operand.GetValue(instruction) is MethodInfo method && method == typeof(GameObject).GetMethod(nameof(GameObject.SetActive)))
            { operand.SetValue(instruction, typeof(LazyConstructionStageExperiment).GetMethod(nameof(ActivateStage), All)); opcode.SetValue(instruction, OpCodes.Call); count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Unexpected construction stage activation sites: " + count);
    }
    private static void ActivateStage(GameObject stage, bool active)
    {
        if (active && Stages.TryGetValue(stage, out var entry)) Ensure(entry, "stage-activation");
        stage.SetActive(active);
    }
    private static bool SharedBounds(Transform parent, ref float __result)
    {
        if (!parent || !Ancestors.TryGetValue(parent.gameObject, out var bucket)) return true;
        Entry? entry = null;
        foreach (var weak in bucket.Items)
        {
            if (!weak.TryGetTarget(out var candidate) || !candidate.Root || candidate.Done || candidate.Creating) continue;
            if (entry != null || !parent.IsChildOf(candidate.Root.transform)) return true;
            entry = candidate;
        }
        if (entry == null) return true;
        if (entry.Parents.Any(t => t.childCount != 0)) { Ensure(entry, "changed-stage-children"); return true; }
        var probe = entry.Plan.Probe.transform;
        var maximum = float.NegativeInfinity;
        var invalid = false;
        void Add(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) invalid = true;
            if (value > maximum) maximum = value;
        }
        try
        {
            // Native Renderer.bounds supplies every missing bound. Mirror each
            // real parent chain under the same actual world parent; do not
            // approximate a rotated/scaled AABB using a local height cache.
            probe.SetParent(entry.Root.transform.parent, false);
            void Copy(Transform from, Transform to)
            { to.SetLocalPositionAndRotation(from.localPosition, from.localRotation); to.localScale = from.localScale; }
            Copy(entry.Root.transform, probe);
            foreach (var part in entry.Plan.Parts)
            {
                var actual = entry.Root.transform; var target = probe;
                foreach (var i in part.Parent) { actual = actual.GetChild(i); target = target.GetChild(i); Copy(actual, target); }
            }
            // These registered query roots contain every deferred part. Keep
            // native batched traversal for existing renderers; add only the
            // missing cached renderer references instead of visiting every node.
            foreach (var renderer in parent.GetComponentsInChildren<MeshRenderer>(true)) Add(renderer.bounds.max.y);
            foreach (var part in entry.Plan.Parts)
            foreach (var renderer in part.ProbeRenderers) Add(renderer.bounds.max.y);
        }
        finally { probe.SetParent(_holder.transform, false); }
        // Finite nonzero maxima have identical bits regardless of enumeration
        // order. Signed-zero/NaN/empty cases retain original ordered behavior.
        if (invalid || maximum == 0f || float.IsInfinity(maximum)) { Ensure(entry, "ordered-bounds-fallback"); return true; }
        var value = maximum; _bounds++;
        if (_validateBounds)
        {
            Ensure(entry, "bounds-validation");
            var native = _nativeBounds(parent, true);
            if (BitConverter.SingleToInt32Bits(value) != BitConverter.SingleToInt32Bits(native))
                throw new InvalidOperationException($"Construction stage bounds differ: {parent.name} shared={value:R} native={native:R}");
            _boundsChecked++;
        }
        __result = value; return false;
    }
    private static void NamedRead(GameObject gameObject, string childName)
    {
        if (!gameObject || string.IsNullOrEmpty(childName) || !Ancestors.TryGetValue(gameObject, out var bucket)) return;
        foreach (var weak in bucket.Items)
            if (weak.TryGetTarget(out var e) && e.Root && !e.Done && !e.Creating && e.Plan.MissingNames.Contains(childName))
                Ensure(e, "named-reader/" + childName);
    }
    private static void Visibility(GameObject __0, bool __1, bool __2)
    {
        if (!__0 || !Ancestors.TryGetValue(__0, out var bucket)) return;
        foreach (var weak in bucket.Items)
        {
            if (!weak.TryGetTarget(out var e) || !e.Root || e.Done || e.Creating) continue;
            e.HasVisibility = true; e.Visible = __1 || __2; e.Collider = __1;
            if (__1 || __2)
            {
                e.HasShadow = true; e.Shadow = __2 ? (__1 ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly) : ShadowCastingMode.Off;
                foreach (var parent in e.Parents)
                    if (parent.gameObject.activeInHierarchy) { Ensure(e, "visible/" + __0.name); break; }
            }
        }
    }
    internal static void Report(string phase)
    {
        if (!_enabled) return;
        var pending = Entries.Select(w => w.TryGetTarget(out var e) && e.Root && !e.Done ? e : null).Where(e => e != null).ToArray();
        Debug.Log($"[T3MPLAZYSTAGE] phase={phase} enrolled={_enrolled} restored={_restored} pending={pending.Length} prunedNodes={_nodes} pendingNodes={pending.Sum(e => e!.Plan.Nodes)} prunedRenderers={_renderers} prunedColliders={_colliders} fallback={_fallback} bounds={_bounds} boundsChecked={_boundsChecked} reasons={string.Join(",", Reasons.Select(p => p.Key + ":" + p.Value))}");
    }
    internal static void ValidateBeforeSnapshots(IReadOnlyList<EntityComponent> entities)
    {
        if (!_enabled || !Environment.GetCommandLineArgs().Contains("-t3mpTestLazyConstructionStagesValidate")) return;
        Report("before-validation");
        ValidateFirstUse();
        SelectionSnapshot.Report(entities);
        var args = Environment.GetCommandLineArgs();
        var dir = System.IO.Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
        var rays = System.IO.Path.Combine(dir, "selection-state.tsv");
        if (File.Exists(rays)) File.Move(rays, System.IO.Path.Combine(dir, "selection-lazy-stages.tsv"));
        foreach (var weak in Entries.ToArray()) if (weak.TryGetTarget(out var e)) Ensure(e, "validation");
        Report("after-validation");
    }
    private static void ValidateFirstUse()
    {
        var toggle = Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions").GetMethod("ToggleModelVisibility", All)!;
        var selected = Plans.Values.Where(p => p != null && p.Parts.Length > 0).Take(3).Cast<Plan>().ToArray();
        if (selected.Length != 3) throw new InvalidOperationException("Insufficient construction fixture templates");
        var previous = _validateBounds; _validateBounds = false;
        var cases = 0;
        try
        {
            foreach (var plan in selected)
            for (var pose = 0; pose < 4; pose++)
            {
                var original = Object.Instantiate(plan.Original, _holder.transform, false); original.name = plan.Original.name;
                var lazy = Object.Instantiate(plan.Pruned, _holder.transform, false); lazy.name = plan.Original.name;
                try
                {
                    foreach (var root in new[] { original, lazy })
                    {
                        root.SetActive(false);
                        root.transform.localPosition = pose == 3 ? Vector3.zero : new Vector3(3.25f, 7.5f, -2.75f);
                        root.transform.localRotation = Quaternion.Euler(pose == 1 ? 15f : 0f, pose * 90f, pose == 2 ? -30f : 0f);
                        root.transform.localScale = pose == 3 ? Vector3.zero : pose == 2 ? new Vector3(-1, 2, .5f) : new Vector3(2, 3, 1);
                        foreach (var part in plan.Parts) Resolve(root.transform, part.Parent).gameObject.SetActive(false);
                    }
                    var entry = Register(lazy, plan);
                    var stage = entry.Parents[0].gameObject;
                    var nativeStage = Resolve(original.transform, plan.Parts[0].Parent).gameObject;
                    var nativeModel = Resolve(original.transform, plan.Unfinished).gameObject;
                    toggle.Invoke(null, new object[] { nativeModel, false, false });
                    toggle.Invoke(null, new object[] { entry.Unfinished, false, false });
                    var value = 0f;
                    if (SharedBounds(lazy.transform, ref value)) value = _nativeBounds(lazy.transform, true);
                    var expected = _nativeBounds(original.transform, true);
                    if (BitConverter.SingleToInt32Bits(value) != BitConverter.SingleToInt32Bits(expected))
                        throw new InvalidOperationException("Construction stage fixture bounds differ");
                    if (pose == 3 && !entry.Done) throw new InvalidOperationException("Zero bound did not use ordered fallback");
                    nativeStage.SetActive(true); ActivateStage(stage, true);
                    if (!entry.Done || stage != entry.Parents[0].gameObject)
                        throw new InvalidOperationException("Stage activation did not preserve its anchor");
                    void Compare()
                    {
                        var assets = new Dictionary<Object, int>();
                        if (FixtureState(original.transform, assets) != FixtureState(lazy.transform, assets))
                            throw new InvalidOperationException("Construction stage first-use fixture differs: " + plan.Original.name + " pose=" + pose);
                    }
                    Compare();
                    foreach (var flags in new[] { (true, true), (false, true), (true, false), (false, false) })
                    {
                        toggle.Invoke(null, new object[] { nativeModel, flags.Item1, flags.Item2 });
                        toggle.Invoke(null, new object[] { entry.Unfinished, flags.Item1, flags.Item2 });
                        Compare();
                    }
                    nativeStage.SetActive(false); ActivateStage(stage, false); Compare();
                    nativeStage.SetActive(true); ActivateStage(stage, true); Compare(); cases++;
                }
                finally { Object.DestroyImmediate(original); Object.DestroyImmediate(lazy); }
            }
        }
        finally { _validateBounds = previous; }
        Debug.Log("[T3MPLAZYSTAGE] FIRSTUSE PASS cases=" + cases + "; inactive native fixtures, bounds and repeated visibility/activation");
    }
    private static string FixtureState(Transform root, Dictionary<Object, int> assets)
    {
        var result = new StringBuilder();
        int Asset(Object asset)
        {
            if (!asset) return 0;
            if (!assets.TryGetValue(asset, out var id)) assets.Add(asset, id = assets.Count + 1);
            return id;
        }
        void F(float value) => result.Append(BitConverter.SingleToInt32Bits(value)).Append(',');
        void V(Vector3 v) { F(v.x); F(v.y); F(v.z); }
        void Visit(Transform t)
        {
            var go = t.gameObject;
            result.Append(go.name).Append('|').Append(go.activeSelf).Append('|').Append(go.layer).Append('|');
            V(t.localPosition); var q = t.localRotation; F(q.x); F(q.y); F(q.z); F(q.w); V(t.localScale);
            foreach (var c in go.GetComponents<Component>())
            {
                result.Append(c.GetType().FullName).Append('|');
                if (c is MeshRenderer renderer)
                {
                    result.Append(renderer.enabled).Append('|').Append(renderer.shadowCastingMode).Append('|').Append(renderer.receiveShadows).Append('|');
                    foreach (var material in renderer.sharedMaterials) result.Append(Asset(material)).Append(',');
                }
                if (c is MeshFilter mesh) result.Append(Asset(mesh.sharedMesh)).Append('|');
                if (c is Collider collider) result.Append(collider.enabled).Append('|').Append(collider.isTrigger).Append('|');
                if (c is BoxCollider box) { V(box.center); V(box.size); F(box.contactOffset); }
            }
            result.AppendLine();
            for (var i = 0; i < t.childCount; i++) Visit(t.GetChild(i));
        }
        Visit(root); return result.ToString();
    }
}
