using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MP.Loading;

// Prepare one private static icon prototype under an inactive parent, then
// clone it directly into each native parent. Its children start hidden, as
// native initialization requires. Later native status events show the icon.
internal static class PreparedStatusIcons
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int _depth;
    private static GameObject? _holder;
    private static readonly Dictionary<GameObject, bool> Eligibility = new Dictionary<GameObject, bool>();
    private static readonly Dictionary<GameObject, GameObject> Prototypes = new Dictionary<GameObject, GameObject>();
    private static readonly HashSet<GameObject> Pending = new HashSet<GameObject>();
    private static long _clones, _fallbacks, _rendererBindings;
    private static bool _native;
    private static string _owner = "";
    private static Type _harmonyType = null!;
    private static MethodInfo[] _guarded = Array.Empty<MethodInfo>();
    private static FieldInfo _icon = null!, _renderer = null!;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static bool Installed { get; private set; }
    internal static void Install(string owner)
    {
        if (Installed) return;
        object? harmony = null;
        Type? ht = null;
        try
        {
            var cycler = Find("Timberborn.StatusSystem.StatusIconCycler");
            var factory = Find("Timberborn.StatusSystem.StatusIconCyclerFactory");
            _icon = cycler.GetField("_statusIcon", All) ?? throw new MissingFieldException(cycler.FullName, "_statusIcon");
            _renderer = cycler.GetField("_statusIconRenderer", All) ?? throw new MissingFieldException(cycler.FullName, "_statusIconRenderer");
            ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
            harmony = Activator.CreateInstance(ht, owner);
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            _guarded = factory.GetMethods(All | BindingFlags.DeclaredOnly).Concat(cycler.GetMethods(All | BindingFlags.DeclaredOnly)).ToArray();
            _owner = owner; _harmonyType = ht;
            _native = NativeMethods();
            if (!_native)
            {
                Debug.Log("[T3MPSTATUSSTAGING] skipped: native factory/cycler already patched");
                return;
            }
            object Hook(string name, bool rewrite = false)
            {
                var m = typeof(PreparedStatusIcons).GetMethod(name, All)!;
                if (rewrite) m = m.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
                var h = Activator.CreateInstance(hm, m)!;
                if (name == nameof(End)) hm.GetField("priority")!.SetValue(h, 900);
                return h;
            }
            patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
            patch.Invoke(harmony, new object?[] { factory.GetMethod("CreateAsChild", All), null, null, Hook(nameof(Rewrite), true), null });
            patch.Invoke(harmony, new object?[] { cycler.GetMethod("InitializeIcon", All), null, Hook(nameof(BindRenderer)), null, null });
            Installed = true;
            Debug.Log("[T3MPSTATUSSTAGING] installed native=" + _native);
        }
        catch (Exception exception)
        {
            _native = false;
            try { if (harmony != null && ht != null) ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner }); }
            catch (Exception cleanup) { Debug.LogError("[T3MPSTATUSSTAGING] hook cleanup failed: " + cleanup); }
            Debug.LogWarning("[T3MPSTATUSSTAGING] unavailable; native cloning retained: " + exception);
        }
    }
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _native = NativeMethods();
        _clones = _fallbacks = _rendererBindings = 0; Eligibility.Clear(); Pending.Clear(); Prototypes.Clear();
    }
    private static bool NativeMethods() => _guarded.All(method =>
    {
        var info = _harmonyType.GetMethod("GetPatchInfo", All)!.Invoke(null, new object[] { method });
        return info == null || !((IEnumerable<string>)info.GetType().GetProperty("Owners", All)!.GetValue(info)!).Any(owner => owner != _owner);
    });
    private static void End()
    {
        if (--_depth != 0) return;
        Debug.Log($"[T3MPSTATUSSTAGING] clones={_clones} prototypes={Prototypes.Count} rendererBindings={_rendererBindings} fallback={_fallbacks} pending={Pending.Count} native={_native}");
        Eligibility.Clear(); Pending.Clear(); Prototypes.Clear();
        if (_holder) Object.DestroyImmediate(_holder);
        _holder = null;
    }
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!; var count = 0;
        foreach (var instruction in instructions)
        {
            if (operand.GetValue(instruction) is MethodInfo m && m.DeclaringType == typeof(Object) && m.Name == "Instantiate" &&
                m.IsGenericMethod && m.GetGenericArguments().Single() == typeof(GameObject) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Transform) }))
            {
                operand.SetValue(instruction, typeof(PreparedStatusIcons).GetMethod(nameof(Clone), All));
                opcode.SetValue(instruction, OpCodes.Call); count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Unexpected status factory clone call count: " + count);
    }
    private static bool Safe(GameObject prefab)
    {
        Debug.Log("[T3MPSTATUSSTAGING] prefab=" + prefab.name + " active=" + prefab.activeSelf +
            " children=" + prefab.transform.childCount + " components=" + string.Join(",", prefab.GetComponentsInChildren<Component>(true)
                .Select(c => c ? c.gameObject.name + ":" + c.GetType().FullName + ":active=" + c.gameObject.activeSelf : "missing")));
        if (prefab.transform.childCount != 2) return false;
        foreach (var c in prefab.GetComponentsInChildren<Component>(true))
        {
            var type = c ? c.GetType() : null;
            if (type == typeof(Transform) || type == typeof(RectTransform) || type == typeof(MeshFilter) || type == typeof(MeshRenderer)) continue;
            if ((type == typeof(BoxCollider) || type == typeof(SphereCollider)) && c is Collider collider &&
                !collider.isTrigger && !collider.attachedRigidbody) continue;
            return false;
        }
        // Native InitializeIcon searches the active first subtree for a mesh
        // renderer. Keep exactly that selection when rebinding after staging.
        var icon = prefab.transform.GetChild(0);
        return icon.gameObject.activeSelf && icon.GetComponentsInChildren<MeshRenderer>(true).Length == 1 &&
            icon.GetComponentInChildren<MeshRenderer>();
    }
    private static GameObject Clone(GameObject prefab, Transform parent)
    {
        if (_depth == 0 || !_native) return Object.Instantiate(prefab, parent);
        if (!Eligibility.TryGetValue(prefab, out var safe)) Eligibility[prefab] = safe = Safe(prefab);
        if (!safe) { _fallbacks++; return Object.Instantiate(prefab, parent); }
        if (!Prototypes.TryGetValue(prefab, out var prototype))
        {
            if (!_holder) { _holder = new GameObject("T3MP status staging"); _holder.SetActive(false); }
            prototype = Object.Instantiate(prefab, _holder.transform);
            prototype.name = prefab.name;
            prototype.transform.GetChild(0).gameObject.SetActive(false);
            prototype.transform.GetChild(1).gameObject.SetActive(false);
            Prototypes.Add(prefab, prototype);
        }
        var clone = Object.Instantiate(prototype, parent);
        Pending.Add(clone.transform.GetChild(0).gameObject);
        _clones++;
        return clone;
    }
    private static void BindRenderer(object __instance)
    {
        var icon = (GameObject)_icon.GetValue(__instance)!;
        if (!Pending.Remove(icon)) return;
        _renderer.SetValue(__instance, icon.GetComponentInChildren<MeshRenderer>(true));
        _rendererBindings++;
    }
}
