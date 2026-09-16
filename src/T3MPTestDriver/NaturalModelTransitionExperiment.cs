using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// The selected lifecycle model's Show writes every final active state itself.
// Avoid its preceding renderer hierarchy toggle. Explicitly retain collider
// removal/reinsertion, whose omission changed native ray-distance rounding.
// All unselected models keep native Hide.
internal static class NaturalModelTransitionExperiment
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [ThreadStatic] private static int _loadDepth;
    [ThreadStatic] private static object? _selected;
    [ThreadStatic] private static List<Collider>? _selectedColliders;
    [ThreadStatic] private static bool _pendingRestore;
    private struct Scope { internal object? Selected; internal List<Collider>? Colliders; internal bool Pending; }
    private static FieldInfo _growable = null!, _cuttable = null!, _mature = null!, _seedling = null!, _trigger = null!;
    private static PropertyInfo _grown = null!, _yielder = null!, _yieldRemoved = null!;
    private static PropertyInfo? _leftover;
    private static FieldInfo[] _roots = null!;
    private static Type _nativeTrigger = null!;
    private static readonly HashSet<Type> SafeScripts = new HashSet<Type>();
    private static readonly HashSet<string> RejectedTypes = new HashSet<string>();
    private static readonly List<Component> Components = new List<Component>();
    private static long _eligible, _skipped, _fallback;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestNaturalModelTransition")) return;
        var model = Find("Timberborn.NaturalResourcesModelSystem.NaturalResourceModel");
        _growable = model.GetField("_growable", All)!; _cuttable = model.GetField("_cuttable", All)!;
        var growth = _growable.FieldType;
        _mature = growth.GetField("_matureModel", All)!; _seedling = growth.GetField("_seedlingModel", All)!;
        _trigger = growth.GetField("_timeTrigger", All)!;
        _nativeTrigger = Find("Timberborn.TimeSystem.TimeTrigger");
        _grown = growth.GetProperty("IsGrown", All)!;
        _leftover = _cuttable.FieldType.GetProperty("CanShowLeftoverModel", All);
        if (_leftover == null)
        {
            // 1.0's native selection uses Yielder.IsYieldRemoved directly;
            // 1.1 adds the leftover-blocked condition in CanShowLeftoverModel.
            _yielder = _cuttable.FieldType.GetProperty("Yielder", All) ?? throw new MissingMemberException("Cuttable.Yielder");
            _yieldRemoved = _yielder.PropertyType.GetProperty("IsYieldRemoved", All) ?? throw new MissingMemberException("Yielder.IsYieldRemoved");
        }
        var lifecycle = _mature.FieldType;
        _roots = new[] { "_aliveModel", "_dyingModel", "_deadModel" }.Select(n => lifecycle.GetField(n, All)!).ToArray();
        foreach (var name in new[] { "Timberborn.Timbermesh.TimbermeshDescription",
            "Timberborn.TimbermeshAnimations.TimbermeshAnimator", "Timberborn.TimbermeshAnimations.NodeAnimationUpdater",
            "Timberborn.TimbermeshAnimations.VertexAnimationUpdater" })
        {
            var type = Find(name);
            if (type.GetMethod("OnEnable", All) != null || type.GetMethod("OnDisable", All) != null)
                throw new InvalidOperationException("Unexpected activation callback: " + name);
            SafeScripts.Add(type);
        }
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.natural-model-transition");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name)
        {
            if (name == null) return null;
            var value = Activator.CreateInstance(hm, typeof(NaturalModelTransitionExperiment).GetMethod(name, All))!;
            if (name == nameof(EndLoad)) hm.GetField("priority")!.SetValue(value, 900);
            return value;
        }
        void Patch(Type type, string name, string? before = null, string? final = null) =>
            patch.Invoke(harmony, new object?[] { type.GetMethod(name, All), Hook(before), null, null, Hook(final) });
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(BeginLoad), nameof(EndLoad));
        Patch(model, "ShowCurrentModel", nameof(BeginTransition), nameof(EndTransition));
        Patch(lifecycle, "Hide", nameof(BeforeHide));
        Patch(lifecycle, "Show", nameof(BeforeShow), nameof(AfterShow));
        Debug.Log("[T3MPNATURALMODEL] installed; direct selected-state activation, native collider re-registration retained, checked hierarchy, load scoped");
    }

    private static void BeginLoad()
    {
        if (_loadDepth++ != 0) return;
        _eligible = _skipped = _fallback = 0; RejectedTypes.Clear();
    }
    private static void EndLoad()
    {
        if (--_loadDepth != 0) return;
        _selected = null; _selectedColliders = null; _pendingRestore = false;
        Components.Clear(); Components.Capacity = 0;
        Debug.Log($"[T3MPNATURALMODEL] eligible={_eligible} skippedHides={_skipped} fallback={_fallback} rejected={string.Join(",", RejectedTypes)}");
    }
    private static void BeginTransition(object __instance, out Scope __state)
    {
        __state = new Scope { Selected = _selected, Colliders = _selectedColliders, Pending = _pendingRestore };
        _selected = null; _selectedColliders = null; _pendingRestore = false;
        if (_loadDepth == 0) return;
        var growth = (BaseComponent?)_growable.GetValue(__instance);
        if (!growth) return;
        // The native Finished getter is a pure state read. Do not perform an
        // extra read through an arbitrary mod-provided trigger implementation.
        var trigger = _trigger.GetValue(growth);
        if (trigger == null || trigger.GetType() != _nativeTrigger)
        { _fallback++; return; }
        var grown = (bool)_grown.GetValue(growth)!;
        var cut = (BaseComponent?)_cuttable.GetValue(__instance);
        if (grown && cut && (_leftover != null ? (bool)_leftover.GetValue(cut)! : (bool)_yieldRemoved.GetValue(_yielder.GetValue(cut))!)) return;
        var selected = (grown ? _mature : _seedling).GetValue(growth);
        var colliders = new List<Collider>();
        if (selected == null || !SafeHierarchy(selected, colliders)) { _fallback++; return; }
        _selected = selected; _selectedColliders = colliders; _eligible++;
    }
    private static void EndTransition(Scope __state)
    {
        try { if (_pendingRestore) Restore(_selectedColliders); }
        finally { _selected = __state.Selected; _selectedColliders = __state.Colliders; _pendingRestore = __state.Pending; }
    }
    private static bool BeforeHide(object __instance)
    {
        if (!ReferenceEquals(__instance, _selected)) return true;
        if (!_pendingRestore && _selectedColliders != null)
        {
            for (var i = _selectedColliders.Count - 1; i >= 0; i--)
                if (!_selectedColliders[i].enabled || !_selectedColliders[i].gameObject.activeInHierarchy) _selectedColliders.RemoveAt(i);
            _pendingRestore = true;
            foreach (var collider in _selectedColliders) collider.enabled = false;
        }
        _skipped++; return false;
    }
    private static void BeforeShow(object __instance, out List<Collider>? __state)
    {
        __state = null;
        // End suppression BEFORE native Show and before ModelChanged callbacks.
        // A listener may legitimately hide the just-shown model again.
        if (ReferenceEquals(__instance, _selected))
        {
            if (_pendingRestore) __state = _selectedColliders;
            _selected = null; _selectedColliders = null; _pendingRestore = false;
        }
    }
    private static void AfterShow(List<Collider>? __state) => Restore(__state);
    private static void Restore(List<Collider>? colliders)
    {
        if (colliders == null) return;
        foreach (var collider in colliders) if (collider) collider.enabled = true;
    }
    private static bool SafeHierarchy(object model, List<Collider>? colliders = null)
    {
        // The original Hide requires these two roots before any optional dead root.
        if (!((GameObject?)_roots[0].GetValue(model)) || !((GameObject?)_roots[1].GetValue(model))) return false;
        foreach (var field in _roots)
        {
            var root = (GameObject?)field.GetValue(model);
            if (!root) continue;
            root!.GetComponentsInChildren(true, Components);
            foreach (var component in Components)
            {
                if (!component) { RejectedTypes.Add("missing-script"); Components.Clear(); return false; }
                var type = component.GetType();
                if (type == typeof(Transform) || type == typeof(MeshFilter) || type == typeof(MeshRenderer) || SafeScripts.Contains(type)) continue;
                if ((type == typeof(BoxCollider) || type == typeof(SphereCollider) || type == typeof(CapsuleCollider)) &&
                    !((Collider)component).isTrigger && !((Collider)component).attachedRigidbody)
                { colliders?.Add((Collider)component); continue; }
                RejectedTypes.Add(type.FullName!); Components.Clear(); return false;
            }
            Components.Clear();
        }
        return true;
    }

    internal static void Validate()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestNaturalModelValidate")) return;
        if (_loadDepth != 0 || _selected != null) throw new InvalidOperationException("Natural transition scope leaked");
        var lifecycle = _mature.FieldType;
        var constructor = lifecycle.GetConstructors(All).Single();
        var parameters = constructor.GetParameters();
        var living = Activator.CreateInstance(parameters[0].ParameterType)!;
        var dying = Activator.CreateInstance(parameters[1].ParameterType)!;
        var deadProperty = living.GetType().GetProperty("IsDead", All)!;
        var dyingProperty = dying.GetType().GetProperty("IsDying", All)!;
        var hide = lifecycle.GetMethod("Hide", All)!; var show = lifecycle.GetMethod("Show", All)!;
        var comparisons = 0;
        for (var initial = 0; initial < 8; initial++)
        for (var health = 0; health < 4; health++)
        for (var hasDead = 0; hasDead < 2; hasDead++)
        for (var activeParent = 0; activeParent < 2; activeParent++)
        {
            var root = new GameObject("natural-transition-test"); root.SetActive(activeParent != 0);
            var objects = new GameObject[6];
            try
            {
                for (var i = 0; i < objects.Length; i++)
                {
                    var item = objects[i] = new GameObject("stage-" + (i % 3));
                    item.transform.SetParent(root.transform, false);
                    item.AddComponent<MeshRenderer>();
                    var collider = (Collider)item.AddComponent(new[] { typeof(BoxCollider), typeof(SphereCollider), typeof(CapsuleCollider) }[i % 3]);
                    collider.enabled = (initial + i % 3) % 2 != 0;
                    item.SetActive((initial & (1 << (i % 3))) != 0);
                }
                deadProperty.SetValue(living, (health & 1) != 0);
                dyingProperty.SetValue(dying, (health & 2) != 0);
                var a = constructor.Invoke(new object?[] { living, dying, objects[0], objects[1], hasDead != 0 ? objects[2] : null });
                var b = constructor.Invoke(new object?[] { living, dying, objects[3], objects[4], hasDead != 0 ? objects[5] : null });
                _selectedColliders = new List<Collider>();
                if (!SafeHierarchy(b, _selectedColliders)) throw new Exception("Native static hierarchy rejected");
                hide.Invoke(a, null); show.Invoke(a, null);
                _selected = b;
                hide.Invoke(b, null); show.Invoke(b, null);
                if (_selected != null) throw new Exception("Suppression survived native Show");
                for (var i = 0; i < 3; i++)
                    if (objects[i].activeSelf != objects[i + 3].activeSelf || objects[i].activeInHierarchy != objects[i + 3].activeInHierarchy)
                        throw new Exception("Natural selected-state mismatch");
                for (var i = 0; i < 3; i++)
                    if (objects[i].GetComponent<Collider>().enabled != objects[i + 3].GetComponent<Collider>().enabled)
                        throw new Exception("Collider restoration failed");
                // A later event handler's Hide must not be suppressed.
                hide.Invoke(a, null); hide.Invoke(b, null);
                for (var i = 0; i < 3; i++)
                    if (objects[i].activeSelf != objects[i + 3].activeSelf) throw new Exception("Follow-up Hide mismatch");
                objects[3].GetComponent<BoxCollider>().isTrigger = true;
                if (SafeHierarchy(b)) throw new Exception("Trigger hierarchy accepted");
                objects[3].GetComponent<BoxCollider>().isTrigger = false;
                objects[3].AddComponent<ActivationObserver>();
                if (SafeHierarchy(b)) throw new Exception("Unknown callback hierarchy accepted");
                comparisons++;
            }
            finally { _selected = null; _selectedColliders = null; _pendingRestore = false; UnityEngine.Object.DestroyImmediate(root); }
        }
        Components.Clear(); Components.Capacity = 0;
        Debug.Log($"[T3MPNATURALMODEL] VALIDATE PASS comparisons={comparisons}; initial activation, life states, absent dead model, inactive parent, Box/Sphere/Capsule enabled restoration, subsequent Hide, trigger and foreign-script fallback");
    }
    public sealed class ActivationObserver : MonoBehaviour
    {
        public int Changes;
        private void OnEnable() => Changes++;
        private void OnDisable() => Changes++;
    }
}
