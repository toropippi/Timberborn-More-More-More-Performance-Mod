using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

// Load-only visibility writes. Always query the current hierarchy, including
// inactive children; no cached component membership or assumed previous state.
internal static class ModelVisibilityExperiment
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Stack<List<Renderer>>? _renderers;
    [ThreadStatic] private static Stack<List<Collider>>? _colliders;
    private static MethodInfo _toggle = null!;
    private static long _rendererReads, _colliderReads, _enabledWrites, _shadowWrites, _colliderWrites;
    private static bool _installed;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestVisibilityWrites")) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.visibility-writes");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name)
        {
            var hook = Activator.CreateInstance(hm, typeof(ModelVisibilityExperiment).GetMethod(name, All))!;
            if (name == nameof(End)) hm.GetField("priority")!.SetValue(hook, 900);
            return hook;
        }
        var extensions = Find("Timberborn.BlockObjectModelSystem.GameObjectExtensions");
        _toggle = extensions.GetMethod("ToggleModelVisibility", All)!;
        patch.Invoke(harmony, new object?[] { extensions.GetMethod("ToggleRenderers", All), Hook(nameof(Renderers)), null, null, null });
        patch.Invoke(harmony, new object?[] { extensions.GetMethod("ToggleColliders", All), Hook(nameof(Colliders)), null, null, null });
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        _installed = true;
        Debug.Log("[T3MPVISIBILITY] installed; current hierarchy, reusable lists, changed native values only, load scoped");
    }

    private static void Begin()
    {
        if (_depth++ != 0) return;
        _rendererReads = _colliderReads = _enabledWrites = _shadowWrites = _colliderWrites = 0;
    }
    private static void End()
    {
        if (--_depth != 0) return;
        _renderers = null; _colliders = null;
        Debug.Log($"[T3MPVISIBILITY] rendererVisits={_rendererReads} colliderVisits={_colliderReads} rendererEnabledWrites={_enabledWrites} shadowWrites={_shadowWrites} colliderWrites={_colliderWrites}");
    }

    private static bool Renderers(GameObject model, bool showModel, bool showShadows)
    {
        if (_depth == 0) return true;
        var pool = _renderers ??= new Stack<List<Renderer>>();
        var values = pool.Count == 0 ? new List<Renderer>() : pool.Pop();
        try
        {
            model.GetComponentsInChildren(true, values);
            var enabled = showModel || showShadows;
            var shadow = showShadows ? (showModel ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly) : ShadowCastingMode.Off;
            foreach (var renderer in values)
            {
                _rendererReads++;
                if (renderer.enabled != enabled) { renderer.enabled = enabled; _enabledWrites++; }
                // Native code leaves shadow mode untouched when both are false.
                if (enabled && renderer.shadowCastingMode != shadow) { renderer.shadowCastingMode = shadow; _shadowWrites++; }
            }
        }
        finally { values.Clear(); pool.Push(values); }
        return false;
    }

    private static bool Colliders(GameObject model, bool showModel)
    {
        if (_depth == 0) return true;
        var pool = _colliders ??= new Stack<List<Collider>>();
        var values = pool.Count == 0 ? new List<Collider>() : pool.Pop();
        try
        {
            model.GetComponentsInChildren(true, values);
            foreach (var collider in values)
            {
                _colliderReads++;
                if (collider.enabled != showModel) { collider.enabled = showModel; _colliderWrites++; }
            }
        }
        finally { values.Clear(); pool.Push(values); }
        return false;
    }

    internal static void Validate()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestVisibilityValidate")) return;
        if (!_installed || _depth != 0) throw new InvalidOperationException("Visibility validation outside expected scope");
        var comparisons = 0;
        for (var initial = 0; initial < 8; initial++)
        for (var desired = 0; desired < 4; desired++)
        {
            var original = Make(); var optimized = Make();
            try
            {
                Configure(original, initial); Configure(optimized, initial);
                Apply(original, false, desired); Apply(optimized, true, desired);
                if (Signature(original) != Signature(optimized)) throw new Exception("Visibility output differs");
                // Hierarchy mutation between calls must be observed by both.
                var a = new GameObject("added"); a.transform.SetParent(original.transform, false); a.AddComponent<MeshRenderer>(); a.AddComponent<BoxCollider>();
                var b = new GameObject("added"); b.transform.SetParent(optimized.transform, false); b.AddComponent<MeshRenderer>(); b.AddComponent<BoxCollider>();
                Apply(original, false, 3 - desired); Apply(optimized, true, 3 - desired);
                if (Signature(original) != Signature(optimized)) throw new Exception("Visibility changed hierarchy differs");
                comparisons += 2;
            }
            finally { Object.DestroyImmediate(original); Object.DestroyImmediate(optimized); }
        }
        _renderers = null; _colliders = null;
        Debug.Log($"[T3MPVISIBILITY] VALIDATE PASS comparisons={comparisons} (active/inactive children, Mesh/Skinned renderers, Box/Sphere/Capsule colliders, initial shadow modes, hierarchy changes)");
    }

    private static GameObject Make()
    {
        var root = new GameObject("visibility-test"); root.SetActive(false);
        root.AddComponent<MeshRenderer>(); root.AddComponent<BoxCollider>();
        var child = new GameObject("child"); child.transform.SetParent(root.transform, false);
        child.AddComponent<SkinnedMeshRenderer>(); child.AddComponent<SphereCollider>();
        var hidden = new GameObject("hidden"); hidden.transform.SetParent(child.transform, false); hidden.SetActive(false);
        hidden.AddComponent<MeshRenderer>(); hidden.AddComponent<CapsuleCollider>();
        return root;
    }
    private static void Configure(GameObject root, int initial)
    {
        var i = 0;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        { r.enabled = (initial + i) % 2 != 0; r.shadowCastingMode = (ShadowCastingMode)((initial + i++) % 4); }
        foreach (var c in root.GetComponentsInChildren<Collider>(true)) c.enabled = (initial + i++) % 2 != 0;
    }
    private static void Apply(GameObject root, bool optimized, int flags)
    {
        _depth = optimized ? 1 : 0;
        try { _toggle.Invoke(null, new object[] { root, (flags & 1) != 0, (flags & 2) != 0 }); }
        finally { _depth = 0; }
    }
    private static string Signature(GameObject root) => string.Join("|", root.GetComponentsInChildren<Renderer>(true)
        .Select(r => r.GetType().FullName + ":" + r.enabled + ":" + r.shadowCastingMode)) + "/" +
        string.Join("|", root.GetComponentsInChildren<Collider>(true).Select(c => c.GetType().FullName + ":" + c.enabled));
}
