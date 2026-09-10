using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.BaseComponentSystem;
using Timberborn.Common;
using Timberborn.Coordinates;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only: share structural metadata, never instance GameObjects.
internal static class ModelLayoutExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Node { internal int[] Path = null!; internal string Name = ""; internal int Mask; internal string? UnsafeReason; }
    private sealed class Layout
    {
        internal readonly Dictionary<string, Node[]> Tubes = new Dictionary<string, Node[]>(StringComparer.Ordinal);
        internal readonly Dictionary<string, Node> Names = new Dictionary<string, Node>(StringComparer.Ordinal);
        internal Node[]? Stages;
    }
    private struct TubeScope { internal Layout? Layout; internal GameObject Root; internal string Key, Prefix; }
    private struct PathScope { internal GameObject Previous; }
    private static readonly Dictionary<GameObject, Layout> Prefabs = new Dictionary<GameObject, Layout>();
    private static readonly Dictionary<GameObject, Layout> Instances = new Dictionary<GameObject, Layout>();
    private static FieldInfo _tubeSpec = null!, _tubeModels = null!;
    private static PropertyInfo _modelPrefix = null!;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static GameObject _pathRoot = null!;
    private static bool _validate, _prehide, _modelPrehide, _constructionPrehide, _visualSeed;
    private static FieldInfo _constructionStages = null!;
    private static Type _staticMarker = null!;
    private static long _stagePlans;
    private static long _prepared, _hidden, _unsafe;
    private static readonly HashSet<string> UnsafeReasons = new HashSet<string>();
    private static long _tubeHits, _tubeBuilds, _pathHits, _pathBuilds, _fallbacks, _checks;
    private static Action<GameObject, bool> _activate = (root, active) => root.SetActive(active);
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestModelLayout")) return;
        VisualPreparationExperiment.ObserveMainModule();
        _validate = args.Contains("-t3mpTestModelLayoutValidate");
        _visualSeed = args.Contains("-t3mpTestModelVisualSeed");
        _modelPrehide = args.Contains("-t3mpTestModelPrehide");
        _constructionPrehide = args.Contains("-t3mpTestConstructionPrehide");
        _prehide = _modelPrehide || _constructionPrehide || VisualPreparationExperiment.Enabled;
        _staticMarker = Find("Timberborn.Timbermesh.TimbermeshDescription");
        var tube = Find("Timberborn.TubeSystem.TubeModel");
        _tubeSpec = tube.GetField("_tubeModelSpec", All)!;
        _tubeModels = tube.GetField("_models", All)!;
        _modelPrefix = _tubeSpec.FieldType.GetProperty("ModelPrefix", All)!;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.model-layout");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name)
        {
            if (name == null) return null;
            var h = Activator.CreateInstance(hm, typeof(ModelLayoutExperiment).GetMethod(name, All))!;
            if (name == nameof(EndLoad)) hm.GetField("priority")!.SetValue(h, 900); // Release metadata before final GC.
            return h;
        }
        void Patch(Type t, string name, string? before = null, string? after = null, string? final = null) =>
            patch.Invoke(harmony, new object?[] { t.GetMethod(name, All), Hook(before), Hook(after), null, Hook(final) });
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(BeginLoad), final: nameof(EndLoad));
        Patch(typeof(BaseInstantiator), "InstantiateInactive", after: nameof(Cloned));
        Patch(tube, "InitializeModels", nameof(BeforeTube), nameof(AfterTube));
        Patch(Find("Timberborn.PathSystem.DynamicPathModel"), "GetModelVariant", nameof(BeginPath), final: nameof(EndPath));
        Patch(typeof(Timberborn.Common.GameObjectExtensions), "FindChild", nameof(BeforeFind), nameof(AfterFind));
        if (_constructionPrehide)
        {
            var visualizer = Find("Timberborn.ConstructionSites.ConstructionSiteProgressVisualizer");
            _constructionStages = visualizer.GetField("_stages", All)!;
            Patch(visualizer, "InitializeStages", after: nameof(AfterStages));
            Debug.Log("[T3MPCONSTRUCTIONPREHIDE] installed");
        }
        if (_prehide)
        {
            var rewrite = typeof(ModelLayoutExperiment).GetMethod(nameof(RewriteActivation), All)!.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
            patch.Invoke(harmony, new object?[] { Find("Timberborn.TemplateInstantiation.TemplateInstantiator").GetMethod("Instantiate", All), null, null, Activator.CreateInstance(hm, rewrite), null });
        }
        Debug.Log("[T3MPMODELLAYOUT] installed validate=" + _validate);
        if (_prehide) Debug.Log("[T3MPPREHIDE] installed");
        if (_visualSeed) Debug.Log("[T3MPVISUALSEED] enabled value=1729; diagnostic input only");
    }

    private static void BeginLoad()
    {
        if (_depth++ != 0) return;
        if (_visualSeed) UnityEngine.Random.InitState(1729);
        Prefabs.Clear(); Instances.Clear(); _tubeHits = _tubeBuilds = _pathHits = _pathBuilds = _fallbacks = _checks = 0;
        _prepared = _hidden = _unsafe = _stagePlans = 0;
        UnsafeReasons.Clear();
        VisualPreparationExperiment.Begin();
    }
    private static void EndLoad()
    {
        if (--_depth != 0) return;
        if (_visualSeed) Debug.Log("[T3MPVISUALSEED] finalState=" + JsonUtility.ToJson(UnityEngine.Random.state));
        Debug.Log($"[T3MPMODELLAYOUT] tubeHits={_tubeHits} tubePlans={_tubeBuilds} pathHits={_pathHits} pathPlans={_pathBuilds} checks={_checks} fallbacks={_fallbacks}");
        if (_prehide) Debug.Log($"[T3MPPREHIDE] instances={_prepared} hidden={_hidden} unsafe={_unsafe}");
        if (_constructionPrehide) Debug.Log($"[T3MPCONSTRUCTIONPREHIDE] plans={_stagePlans}");
        Prefabs.Clear(); Instances.Clear(); _pathRoot = null!;
        VisualPreparationExperiment.End();
    }
    private static void Cloned(GameObject prefab, GameObject __result)
    {
        if (_depth == 0 || !__result) return;
        if (!Prefabs.TryGetValue(prefab, out var layout)) Prefabs.Add(prefab, layout = new Layout());
        Instances[__result] = layout;
    }
    private static int[] PathTo(Transform root, Transform target)
    {
        var path = new List<int>();
        for (var current = target; current != root; current = current.parent)
        {
            if (!current) throw new Exception("Model is outside instance hierarchy");
            path.Add(current.GetSiblingIndex());
        }
        path.Reverse(); return path.ToArray();
    }
    private static GameObject? Resolve(Transform root, Node node)
    {
        var current = root;
        foreach (var i in node.Path)
        {
            if (i >= current.childCount) return null;
            current = current.GetChild(i);
        }
        return current.name == node.Name ? current.gameObject : null;
    }
    private static Node[] DescribeTube(GameObject root, string prefix)
    {
        var nodes = new List<Node>();
        foreach (var child in root.GetAllChildren())
        {
            var name = child.name;
            if (!name.StartsWith(prefix)) continue;
            if (name.Length < prefix.Length + 6) throw new Exception("Invalid tube model suffix");
            var mask = 0;
            for (var i = 0; i < 6; i++) if (name[prefix.Length + i] == '1') mask |= 1 << i;
            nodes.Add(new Node { Path = PathTo(root.transform, child.transform), Name = name, Mask = mask,
                UnsafeReason = ActivationUnsafeReason(child) });
        }
        return nodes.ToArray();
    }
    private static bool BeforeTube(object __instance, out TubeScope __state)
    {
        __state = default;
        var root = ((BaseComponent)__instance).GameObject;
        if (_depth == 0 || !Instances.TryGetValue(root, out var layout)) return true;
        var prefix = (string)_modelPrefix.GetValue(_tubeSpec.GetValue(__instance))!;
        if (prefix == null) return true;
        var key = CultureInfo.CurrentCulture.Name + "\0" + prefix;
        if (!layout.Tubes.TryGetValue(key, out var nodes))
        {
            __state = new TubeScope { Layout = layout, Root = root, Key = key, Prefix = prefix };
            return true; // First instance runs the original initialization before capturing metadata.
        }
        var objects = new GameObject[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var found = Resolve(root.transform, nodes[i]);
            if (!found) { _fallbacks++; return true; }
            objects[i] = found!;
        }
        if (_validate)
        {
            var expected = root.GetAllChildren().Where(c => c.name.StartsWith(prefix)).ToArray();
            if (expected.Length != objects.Length || !expected.SequenceEqual(objects)) throw new Exception("Tube layout sequence differs");
            _checks += expected.Length;
        }
        var values = (NeighboredValues6<GameObject>)_tubeModels.GetValue(__instance)!;
        for (var i = 0; i < nodes.Length; i++)
        {
            var m = nodes[i].Mask;
            objects[i].SetActive(false);
            values.AddVariants(objects[i], (m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0, (m & 16) != 0, (m & 32) != 0);
        }
        _tubeHits++; return false;
    }
    private static void AfterTube(TubeScope __state)
    {
        if (__state.Layout == null) return;
        __state.Layout.Tubes[__state.Key] = DescribeTube(__state.Root, __state.Prefix); _tubeBuilds++;
    }
    private static void BeginPath(object __instance, out PathScope __state)
    { __state = new PathScope { Previous = _pathRoot }; _pathRoot = _depth > 0 ? ((BaseComponent)__instance).GameObject : null!; }
    private static void EndPath(PathScope __state) => _pathRoot = __state.Previous;
    private static bool BeforeFind(GameObject gameObject, string childName, ref GameObject __result, out Layout? __state)
    {
        __state = null;
        if (_depth == 0 || !ReferenceEquals(gameObject, _pathRoot) || string.IsNullOrEmpty(childName) || !Instances.TryGetValue(gameObject, out var layout)) return true;
        if (!layout.Names.TryGetValue(childName, out var node)) { __state = layout; return true; }
        var found = Resolve(gameObject.transform, node);
        if (!found) { _fallbacks++; return true; }
        if (_validate)
        {
            if (gameObject.FindChildTransform(childName).gameObject != found) throw new Exception("Path model lookup differs");
            _checks++;
        }
        __result = found!; _pathHits++; return false;
    }
    private static void AfterFind(GameObject gameObject, string childName, GameObject __result, Layout? __state)
    {
        if (__state == null || !__result) return;
        __state.Names[childName] = new Node { Path = PathTo(gameObject.transform, __result.transform), Name = __result.name,
            UnsafeReason = ActivationUnsafeReason(__result) }; _pathBuilds++;
    }

    private static string? ActivationUnsafeReason(GameObject target)
    {
        // Audited in game 1.0 and 1.1: this exact type only stores a model-name
        // string and has no Unity lifecycle callbacks. Do not allow subclasses.
        // Native components can have activation side effects too (animators,
        // particles, audio). The game's static selection boxes are allowed;
        // trigger volumes and boxes attached to a rigid body are not.
        var unknown = target.GetComponentsInChildren<Component>(true)
            .Where(c => !c || (c.GetType() != _staticMarker && c.GetType() != typeof(Transform) &&
                c.GetType() != typeof(MeshFilter) && c.GetType() != typeof(MeshRenderer) &&
                !(c.GetType() == typeof(BoxCollider) && !((BoxCollider)c).isTrigger && !((BoxCollider)c).attachedRigidbody)))
            .Select(c => c ? c.GetType().FullName : "missing script").Distinct().ToArray();
        return unknown.Length == 0 ? null : string.Join(",", unknown);
    }

    private static void AfterStages(object __instance)
    {
        var root = ((BaseComponent)__instance).GameObject;
        if (_depth == 0 || !Instances.TryGetValue(root, out var layout) || layout.Stages != null) return;
        layout.Stages = ((IEnumerable<GameObject>)_constructionStages.GetValue(__instance)!).Select(stage =>
            new Node { Path = PathTo(root.transform, stage.transform), Name = stage.name,
                UnsafeReason = ActivationUnsafeReason(stage) }).ToArray();
        _stagePlans++;
    }

    private static IEnumerable<T> RewriteActivation<T>(IEnumerable<T> instructions)
    {
        var list = instructions.ToList();
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!;
        var original = typeof(GameObject).GetMethod(nameof(GameObject.SetActive))!;
        var matches = list.Where(i => Equals(operand.GetValue(i), original) || operand.GetValue(i) is MethodInfo method &&
            method.DeclaringType?.FullName == "T3MP.Loading.PreparedEntityVisuals" && method.Name == "Activate").ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Unexpected template activation shape");
        if (operand.GetValue(matches[0]) is MethodInfo existing && existing.IsStatic)
            _activate = (Action<GameObject, bool>)Delegate.CreateDelegate(typeof(Action<GameObject, bool>), existing);
        operand.SetValue(matches[0], typeof(ModelLayoutExperiment).GetMethod(nameof(Activate), All));
        opcode.SetValue(matches[0], OpCodes.Call);
        return list;
    }
    private static void Activate(GameObject root, bool active)
    {
        if (_depth > 0 && active && !root.activeInHierarchy && Instances.TryGetValue(root, out var layout))
        {
            VisualPreparationExperiment.Prepare(root, layout);
            IEnumerable<Node> candidates = _modelPrehide
                ? layout.Tubes.Values.SelectMany(n => n).Concat(layout.Names.Values) : Enumerable.Empty<Node>();
            if (_constructionPrehide && layout.Stages != null) candidates = candidates.Concat(layout.Stages);
            var nodes = candidates.ToArray();
            if (nodes.Length > 0)
            {
                // Resolve before mutation. Child scripts could observe a changed
                // activation schedule, so retain the original route for them.
                var targets = new HashSet<GameObject>();
                var compatible = true;
                foreach (var node in nodes)
                {
                    var target = Resolve(root.transform, node);
                    if (node.UnsafeReason != null || !target)
                    {
                        var reason = !target ? "changed hierarchy" : node.UnsafeReason!;
                        if (UnsafeReasons.Add(reason)) Debug.Log("[T3MPPREHIDE] skipped reason=" + reason);
                        compatible = false; break;
                    }
                    targets.Add(target!);
                }
                if (compatible)
                {
                    foreach (var target in targets) if (target.activeSelf) { target.SetActive(false); _hidden++; }
                    _prepared++;
                }
                else _unsafe++;
            }
        }
        _activate(root, active);
    }
}
