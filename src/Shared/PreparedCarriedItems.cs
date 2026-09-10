using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.Goods;
using UnityEngine;
using Object = UnityEngine.Object;

namespace T3MP.Loading;

internal static class PreparedCarriedItems
{
    private const BindingFlags All = LoadPatchBridge.All;
    private static bool _staging, _metadata;
    private static int _loadDepth, _carrierDepth;
    private static GameObject? _holder;
    private static readonly Dictionary<GameObject, GameObject?> Prototypes = new Dictionary<GameObject, GameObject?>();
    private static readonly Dictionary<string, VisibleContainer> Containers = new Dictionary<string, VisibleContainer>(StringComparer.Ordinal);
    private static Func<object, GameObject, Transform, GameObject> _instantiate = null!;
    private static long _clones, _fallbacks, _hits;
    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        _staging = args.Contains("-t3mpTestCarriedPreparation");
        _metadata = args.Contains("-t3mpTestCarriedMetadata");
        if (!_staging && !_metadata) return;
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony"); var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.load.carried-items");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name)
        {
            if (name == null) return null;
            var hook = Activator.CreateInstance(hm, typeof(PreparedCarriedItems).GetMethod(name, All))!;
            if (name == nameof(EndLoad)) hm.GetField("priority")!.SetValue(hook, 900);
            return hook;
        }
        void Patch(string type, string method, string? before = null, string? after = null, string? final = null) =>
            patch.Invoke(harmony, new object?[] { LoadPatchBridge.Find(type).GetMethod(method, All), Hook(before), Hook(after), null, Hook(final) });
        Patch("Timberborn.SingletonSystem.SingletonLifecycleService", "LoadAll", nameof(BeginLoad), final: nameof(EndLoad));
        Patch("Timberborn.Carrying.GoodCarrierModel", "InitializeItems", nameof(BeginCarrier), final: nameof(EndCarrier));
        if (_metadata) Patch("Timberborn.Carrying.CarriedItem", "ParseGoodContainerFromName", nameof(Parse));
        if (_staging)
        {
            var rewrite = LoadPatchBridge.Create("T3MP.CarriedCloneBridge", typeof(PreparedCarriedItems).GetMethod(nameof(Rewrite), All)!);
            patch.Invoke(harmony, new object?[] { LoadPatchBridge.Find("Timberborn.PrefabOptimization.OptimizedPrefabInstantiator").GetMethod("Instantiate", All),
                null, null, Activator.CreateInstance(hm, rewrite), null });
        }
        Debug.Log($"[T3MPCARRIED] installed staging={_staging} metadata={_metadata}");
    }
    private static void BeginLoad() { if (_loadDepth++ == 0) { _clones = _fallbacks = _hits = 0; Prototypes.Clear(); Containers.Clear(); } }
    private static void EndLoad()
    {
        if (--_loadDepth != 0) return;
        Debug.Log($"[T3MPCARRIED] clones={_clones} fallbacks={_fallbacks} prototypes={Prototypes.Count} parseHits={_hits}");
        Prototypes.Clear(); Containers.Clear();
        if (_holder) Object.DestroyImmediate(_holder);
        _holder = null;
    }
    private static void BeginCarrier() { if (_loadDepth > 0) _carrierDepth++; }
    private static void EndCarrier() { if (_loadDepth > 0) _carrierDepth--; }
    private static bool Parse(Object itemObject, ref VisibleContainer __result)
    {
        if (_carrierDepth == 0) return true;
        var name = itemObject.name;
        if (Containers.TryGetValue(name, out __result)) { _hits++; return false; }
        var split = name.LastIndexOf(".", StringComparison.Ordinal);
        if (split < 0) return true;
        // Keep native Enum.Parse exception/undefined numeric-value semantics.
        __result = (VisibleContainer)Enum.Parse(typeof(VisibleContainer), name.Substring(split + 1));
        Containers.Add(name, __result); return false;
    }
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> source)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!; var count = 0;
        foreach (var instruction in source)
        {
            if (operand.GetValue(instruction) is MethodInfo m && m.Name == "Instantiate" && m.ReturnType == typeof(GameObject) &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Transform) }))
            {
                var o = Expression.Parameter(typeof(object)); var a = Expression.Parameter(typeof(GameObject)); var b = Expression.Parameter(typeof(Transform));
                _instantiate = Expression.Lambda<Func<object, GameObject, Transform, GameObject>>(
                    Expression.Call(Expression.Convert(o, m.DeclaringType!), m, a, b), o, a, b).Compile();
                operand.SetValue(instruction, typeof(PreparedCarriedItems).GetMethod(nameof(Clone), All)); opcode.SetValue(instruction, OpCodes.Call); count++;
            }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Unexpected carried attachment clone shape: " + count);
    }
    private static GameObject Clone(object instantiator, GameObject prefab, Transform parent)
    {
        if (_loadDepth == 0 || _carrierDepth == 0) return _instantiate(instantiator, prefab, parent);
        if (!Prototypes.TryGetValue(prefab, out var prototype))
        {
            var safe = prefab.transform.childCount > 0 && prefab.GetComponentsInChildren<Component>(true).All(c => c &&
                (c.GetType() == typeof(Transform) || c.GetType() == typeof(MeshFilter) || c.GetType() == typeof(MeshRenderer) ||
                 c.GetType().FullName == "Timberborn.Timbermesh.TimbermeshDescription" ||
                 c is Collider col && (c.GetType() == typeof(BoxCollider) || c.GetType() == typeof(SphereCollider) || c.GetType() == typeof(CapsuleCollider)) && !col.isTrigger && !col.attachedRigidbody));
            if (safe)
            {
                if (!_holder) { _holder = new GameObject("T3MP carried templates"); _holder.SetActive(false); }
                prototype = Object.Instantiate(prefab, _holder.transform); prototype.name = prefab.name;
                var items = prototype.transform.GetChild(0);
                for (var i = 0; i < items.childCount; i++) items.GetChild(i).gameObject.SetActive(false);
            }
            Prototypes.Add(prefab, prototype);
            Debug.Log("[T3MPCARRIED] prefab=" + prefab.name + " safe=" + safe);
        }
        if (!prototype) { _fallbacks++; return _instantiate(instantiator, prefab, parent); }
        _clones++; return _instantiate(instantiator, prototype!, parent);
    }
}
