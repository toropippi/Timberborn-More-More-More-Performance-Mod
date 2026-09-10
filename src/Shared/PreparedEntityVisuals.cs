using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.BaseComponentSystem;
using Timberborn.Common;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Prepare only reviewed visual subtrees while their entity root is inactive.
// Native Awake/load/model decisions and every later notification still execute.
internal static class PreparedEntityVisuals
{
    internal static bool Natural = Environment.GetCommandLineArgs().Contains("-t3mpTestNaturalVisualPreparation");
    internal static bool Building = Environment.GetCommandLineArgs().Contains("-t3mpTestBuildingVisualPreparation");
    internal static bool Enabled => Natural || Building;
    internal static bool Installed { get; private set; }
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<GameObject, GameObject> Clones = new Dictionary<GameObject, GameObject>();
    [ThreadStatic] private static int _depth;
    private static Type? _harmonyType;
    private static string _owner = "";
    private static bool _compatible = true;
    private static readonly Dictionary<Type, bool> ComponentSafety = new Dictionary<Type, bool>();
    private sealed class Node { internal int[] Path = null!; internal string Name = ""; }
    private sealed class Plan { internal Node[] Nodes = Array.Empty<Node>(); internal bool Natural, Rejected; }
    private static readonly Dictionary<object, Plan> Plans = new Dictionary<object, Plan>();
    private static readonly HashSet<string> Reasons = new HashSet<string>();
    private static long _natural, _building, _nodes, _renderers, _colliders, _fallback;

    internal static void Install(string owner)
    {
        if (Installed) return;
        Natural = Building = !Environment.GetCommandLineArgs().Contains("-t3mpTestVisualPreparationBaseline");
        if (!Enabled) return;
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        Type? ht = null;
        object? harmony = null;
        try
        {
            if (!ReviewedModules())
            {
                Natural = Building = false;
                Debug.Log("[T3MPVISUALPREP] native fallback: visual preparation not enabled for these game/Unity modules");
                return;
            }
            ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
            _harmonyType = ht; _owner = owner;
            harmony = Activator.CreateInstance(ht, owner)!;
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            object Hook(string name, int priority = 400)
            {
                var hook = Activator.CreateInstance(hm, typeof(PreparedEntityVisuals).GetMethod(name, All))!;
                hm.GetField("priority")!.SetValue(hook, priority); return hook;
            }
            patch.Invoke(harmony, new object?[] { typeof(BaseInstantiator).GetMethod("InstantiateInactive", All), null, Hook(nameof(RememberClone)), null, null });
            patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All),
                Hook(nameof(BeginLoad)), null, null, Hook(nameof(EndLoad), 900) });
            var rewrite = MakeTranspiler(Find("HarmonyLib.CodeInstruction"));
            patch.Invoke(harmony, new object?[] { Find("Timberborn.TemplateInstantiation.TemplateInstantiator").GetMethod("Instantiate", All),
                null, null, Activator.CreateInstance(hm, rewrite), null });
            Installed = true;
            Debug.Log("[T3MPVISUALPREP] installed natural=True building=True");
        }
        catch (Exception e)
        {
            Natural = Building = false;
            try { if (ht != null && harmony != null) ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner }); }
            catch (Exception cleanup) { Debug.LogError("[T3MPVISUALPREP] hook cleanup failed: " + cleanup); }
            Debug.LogWarning("[T3MPVISUALPREP] disabled: " + e.GetBaseException().Message);
        }
    }
    private static void BeginLoad()
    {
        if (_depth++ != 0) return;
        Clones.Clear(); ComponentSafety.Clear(); Begin(); _compatible = ReviewedModules();
        if (!_compatible) Debug.Log("[T3MPVISUALPREP] native fallback: module compatibility");
    }
    private static void EndLoad() { if (--_depth == 0) { Clones.Clear(); End(); } }
    private static void RememberClone(GameObject prefab, GameObject __result)
    { if (_depth > 0 && __result) Clones[__result] = prefab; }
    internal static void Activate(GameObject root, bool active)
    {
        if (_depth > 0 && _compatible && active && !root.activeInHierarchy && Clones.TryGetValue(root, out var prefab)) Prepare(root, prefab);
        root.SetActive(active);
    }
    private static IEnumerable<T> RewriteActivation<T>(IEnumerable<T> source)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!; var matches = 0;
        foreach (var instruction in source)
        {
            if (operand.GetValue(instruction) is MethodInfo method && method == typeof(GameObject).GetMethod(nameof(GameObject.SetActive)))
            { operand.SetValue(instruction, typeof(PreparedEntityVisuals).GetMethod(nameof(Activate), All)); opcode.SetValue(instruction, OpCodes.Call); matches++; }
            yield return instruction;
        }
        if (matches != 1) throw new InvalidOperationException("Unexpected template activation call count: " + matches);
    }
    private static MethodInfo MakeTranspiler(Type instruction)
    {
        // Harmony must persist a nongeneric MethodInfo when another mod later
        // patches the same method. A closed generic transpiler loses its type
        // arguments on this game's Harmony/Mono combination.
        var sequence = typeof(IEnumerable<>).MakeGenericType(instruction);
        var callbackType = typeof(Func<,>).MakeGenericType(sequence, sequence);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("T3MP.VisualPreparationBridge"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main").DefineType("T3MPVisualPreparationBridge", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var callback = type.DefineField("Rewrite", callbackType, FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod("Transpile", MethodAttributes.Public | MethodAttributes.Static, sequence, new[] { sequence });
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, callback); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, callbackType.GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
        var created = type.CreateType()!;
        created.GetField("Rewrite")!.SetValue(null, Delegate.CreateDelegate(callbackType,
            typeof(PreparedEntityVisuals).GetMethod(nameof(RewriteActivation), All)!.MakeGenericMethod(instruction)));
        return created.GetMethod("Transpile")!;
    }
    private static bool ReviewedModules()
    {
        var rows = new[] {
            "Timberborn.BaseComponentSystem|c0f7b920-5694-4612-bdd5-b736144e1f02|e1c14a06-f064-460c-be91-3d6a2fee5cd2",
            "Timberborn.BlockObjectModelSystem|a1669a88-0ece-4f14-95d1-bc59fe5e6eaa|a2906a09-7142-48d6-8c2d-e7318606e86b",
            "Timberborn.NaturalResourcesModelSystem|4b988987-94dd-4862-b337-5e31b1b44423|5f3ffcae-b6cf-43ba-80ce-05a5581e364d",
            "Timberborn.NaturalResourcesLifecycleModelSystem|2b446b61-6908-4ba8-8122-cf46a872471e|09658cf5-48bb-4af6-a3fc-a2c4272edfe4",
            "Timberborn.Buildings|67e23e56-01c7-4200-bfa4-b735eaa5f28a|a0cdc4e9-c72b-4716-8f12-f30761f12159",
            "UnityEngine.CoreModule|61dee272-fd45-47db-9c13-40fe43fbdc1a|4a34e92e-a25e-4043-a947-f7ae39562b49" };
        // 1.0 passed model/world comparison but changed 155 native ray records.
        // Keep native activation there until physics equivalence is established.
        return LoadCompatibility.Reviewed(rows.Select(row => string.Join("|", row.Split('|').Take(2))).ToArray());
    }
    private static bool SafeComponent(Type type)
    {
        if (ComponentSafety.TryGetValue(type, out var safe)) return safe;
        safe = type.Assembly.GetName().Name?.StartsWith("Timberborn.", StringComparison.Ordinal) ?? false;
        if (safe && _harmonyType != null)
        {
            var infoMethod = _harmonyType.GetMethod("GetPatchInfo", All)!;
            foreach (var method in type.GetMethods(All).Where(m => m.Name == "Awake" || m.Name.Contains("Initialize") ||
                         m.Name.Contains("Model") || m.Name.Contains("Material") || m.Name.Contains("Bounds")))
            {
                var info = infoMethod.Invoke(null, new object[] { method });
                if (info == null) continue;
                var owners = (IEnumerable<string>)info.GetType().GetProperty("Owners", All)!.GetValue(info)!;
                bool Accepted(string owner) => owner == _owner || owner == "local.gpupathinginvestigation.benchmarkprobe" ||
                    (owner == "t3mp.load.good-stack-models" || owner == "t3mp.test.lazy-good-stack" && Environment.GetCommandLineArgs().Contains("-t3mpTestLazyGoodStack")) &&
                    (method.DeclaringType?.FullName == "Timberborn.Rendering.EntityMaterials" && (method.Name == "AddMaterials" || method.Name == "AddMaterial" || method.Name == "GetChildMaterials") ||
                     method.DeclaringType?.FullName == "Timberborn.GoodStackSystem.GoodStackModel" && method.Name == "UpdateModel" ||
                     method.DeclaringType?.FullName == "Timberborn.ForestryEffects.TreeShaker" && method.Name == "InitializeEntity") ||
                    owner == "t3mp.load.status-icons" && method.DeclaringType?.FullName == "Timberborn.StatusSystem.StatusIconCycler" && method.Name == "InitializeIcon" ||
                    owner == "t3mp.test.model-layout" && (method.DeclaringType?.FullName == "Timberborn.TubeSystem.TubeModel" && method.Name == "InitializeModels" ||
                        method.DeclaringType?.FullName == "Timberborn.PathSystem.DynamicPathModel" && method.Name == "GetModelVariant" ||
                        method.DeclaringType?.FullName == "Timberborn.ConstructionSites.ConstructionSiteProgressVisualizer" && method.Name == "InitializeStages");
                if (owners.Any(o => !Accepted(o)))
                {
                    Debug.Log("[T3MPVISUALPREP] native component fallback: " + method.DeclaringType!.FullName + "." + method.Name);
                    safe = false; break;
                }
            }
        }
        ComponentSafety[type] = safe; return safe;
    }

    internal static void Begin()
    { Plans.Clear(); Reasons.Clear(); ComponentSafety.Clear(); _natural = _building = _nodes = _renderers = _colliders = _fallback = 0; }
    internal static void End()
    {
        if (Enabled) Debug.Log($"[T3MPVISUALPREP] natural={_natural} building={_building} nodes={_nodes} renderers={_renderers} colliders={_colliders} fallback={_fallback} plans={Plans.Count}");
        Plans.Clear(); Reasons.Clear(); ComponentSafety.Clear();
    }
    internal static void Prepare(GameObject root, object layout)
    {
        if (!Enabled || root.activeInHierarchy) return;
        if (!Plans.TryGetValue(layout, out var plan)) Plans.Add(layout, plan = Build(root));
        if (plan.Rejected) { _fallback++; return; }
        if (plan.Nodes.Length == 0) return;
        var targets = new GameObject[plan.Nodes.Length];
        for (var i = 0; i < targets.Length; i++)
        {
            var current = root.transform;
            foreach (var index in plan.Nodes[i].Path)
            {
                if (index >= current.childCount) { _fallback++; return; }
                current = current.GetChild(index);
            }
            if (current.name != plan.Nodes[i].Name) { _fallback++; return; }
            targets[i] = current.gameObject;
        }
        foreach (var target in targets)
        {
            if (plan.Natural) { if (target.activeSelf) { target.SetActive(false); _nodes++; } }
            else
            {
                foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
                    if (renderer.enabled) { renderer.enabled = false; _renderers++; }
                foreach (var collider in target.GetComponentsInChildren<Collider>(true))
                    if (collider.enabled) { collider.enabled = false; _colliders++; }
            }
        }
        if (plan.Natural) _natural++; else _building++;
    }
    private static Plan Build(GameObject root)
    {
        var plan = new Plan(); var cache = root.GetComponent<ComponentCache>();
        if (!cache) return plan;
        var components = cache.AllComponents.ToArray();
        if (components.Any(c => !SafeComponent(c.GetType())))
        { plan.Rejected = true; return plan; }
        object? Spec(string name) => components.FirstOrDefault(c => c.GetType().FullName == name);
        var targets = new List<GameObject>();
        if (Natural && Spec("Timberborn.NaturalResourcesModelSystem.NaturalResourceModel") != null)
        {
            plan.Natural = true;
            var spec = Spec("Timberborn.BlockObjectModelSystem.BlockObjectModelSpec");
            if (spec == null) return plan;
            var name = (string)spec.GetType().GetProperty("FullModelName")!.GetValue(spec)!;
            var full = root.FindChildIfNameNotEmpty(name);
            if (!full) return plan;
            foreach (var stage in new[] { "Mature", "Seedling" })
            {
                var parent = full.GetDirectChildren().SingleOrDefault(c => c.name == stage);
                if (!parent) return new Plan();
                foreach (var state in new[] { "#Alive", "#Dying", "#Dead" })
                {
                    var child = parent.GetDirectChildren().SingleOrDefault(c => c.name == state);
                    if (child) targets.Add(child);
                    else if (state != "#Dead") return new Plan();
                }
            }
        }
        else if (Building && Spec("Timberborn.Buildings.BuildingModel") != null)
        {
            var spec = Spec("Timberborn.Buildings.BuildingModelSpec");
            if (spec == null) return plan;
            foreach (var property in new[] { "FinishedModelName", "UnfinishedModelName", "FinishedUncoveredModelName", "UndergroundModelName" })
            {
                var name = (string)spec.GetType().GetProperty(property)!.GetValue(spec)!;
                var model = root.FindChildIfNameNotEmpty(name);
                if (model) targets.Add(model);
            }
        }
        foreach (var target in targets)
        foreach (var component in target.GetComponentsInChildren<Component>(true))
        {
            var type = component ? component.GetType() : null;
            var allowed = type == typeof(Transform) || type == typeof(MeshRenderer) || type == typeof(MeshFilter) ||
                (type == typeof(BoxCollider) || type == typeof(SphereCollider) || type == typeof(CapsuleCollider)) &&
                component is Collider collider && !collider.isTrigger && !collider.attachedRigidbody;
            if (type?.FullName == "Timberborn.Timbermesh.TimbermeshDescription") allowed = true;
            if (!allowed)
            {
                var reason = type?.FullName ?? "missing script";
                if (Reasons.Add(reason)) Debug.Log("[T3MPVISUALPREP] excluded=" + reason);
                plan.Rejected = true; return plan;
            }
        }
        plan.Nodes = targets.Distinct().Select(target =>
        {
            var path = new List<int>();
            for (var current = target.transform; current != root.transform; current = current.parent)
                path.Add(current.GetSiblingIndex());
            path.Reverse(); return new Node { Path = path.ToArray(), Name = target.name };
        }).ToArray();
        return plan;
    }
}
