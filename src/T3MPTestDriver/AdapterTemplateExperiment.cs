using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Bindito.Unity;
using Timberborn.BaseComponentSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

// Opt-in load experiment: clone four inert adapters, inject at the original
// AddComponent call sites, then let native cache initialization/activation run.
internal static partial class AdapterTemplateExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type[] Adapters = { typeof(ComponentCache), typeof(BaseComponentUnityAdapter),
        typeof(BaseComponentUpdateUnityAdapter), typeof(BaseComponentLateUpdateUnityAdapter) };
    private sealed class Frame { internal Frame? Previous; internal GameObject? Root; }
    private sealed class InjectionFrame { internal InjectionFrame? Previous; internal bool First = true; }
    [ThreadStatic] private static Frame? _current;
    [ThreadStatic] private static InjectionFrame? _injection;
    [ThreadStatic] private static int _depth;
    private static readonly Dictionary<GameObject, GameObject?> Prefabs = new Dictionary<GameObject, GameObject?>();
    private static long _prepared, _clones, _reused, _filtered, _fallbacks;
    private static bool _validate;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestAdapterTemplates")) return;
        _validate = Environment.GetCommandLineArgs().Contains("-t3mpTestAdapterTemplatesValidate");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.adapter-templates");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name, bool rewrite = false)
        {
            if (name == null) return null;
            var method = typeof(AdapterTemplateExperiment).GetMethod(name, All)!;
            if (rewrite) method = method.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
            var hook = Activator.CreateInstance(hm, method)!;
            if (name == nameof(EndLoad)) hm.GetField("priority")!.SetValue(hook, 900);
            return hook;
        }
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All),
            Hook(nameof(BeginLoad)), null, null, Hook(nameof(EndLoad)) });
        patch.Invoke(harmony, new object?[] { typeof(BaseInstantiator).GetMethod("InstantiateInactive", All),
            Hook(nameof(BeginBase)), null, Hook(nameof(RewriteBase), true), Hook(nameof(EndBase)) });
        patch.Invoke(harmony, new object?[] { typeof(Instantiator).GetMethod("InjectIntoObjectAndChildren", All),
            null, null, Hook(nameof(RewriteInjection), true), null });
        Debug.Log("[T3MPADAPTER] installed; inactive cached adapter templates, original injection call order");
    }

    private static void BeginLoad()
    {
        if (_depth++ != 0) return;
        _prepared = _clones = _reused = _filtered = _fallbacks = 0;
    }
    private static void EndLoad()
    {
        if (--_depth != 0) return;
        Debug.Log($"[T3MPADAPTER] prepared={_prepared} clones={_clones} reusedAdds={_reused} filteredEarlyInjections={_filtered} fallbacks={_fallbacks}");
        ReleaseTemplates();
    }
    private static void BeginBase(out Frame? __state)
    {
        __state = _depth == 0 ? null : new Frame { Previous = _current };
        if (__state != null) _current = __state;
    }
    private static void EndBase(Frame? __state) { if (__state != null) _current = __state.Previous; }

    private static IEnumerable<T> RewriteBase<T>(IEnumerable<T> instructions)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!;
        var clones = 0; var adds = 0;
        foreach (var instruction in instructions)
        {
            if (operand.GetValue(instruction) is MethodInfo method && method.DeclaringType == typeof(IInstantiator))
            {
                MethodInfo? replacement = null;
                if (method.Name == "InstantiateInactive" && !method.IsGenericMethod)
                { replacement = typeof(AdapterTemplateExperiment).GetMethod(nameof(Clone), All); clones++; }
                else if (method.Name == "AddComponent" && method.IsGenericMethod && Adapters.Contains(method.GetGenericArguments()[0]))
                { replacement = typeof(AdapterTemplateExperiment).GetMethod(nameof(Add), All)!.MakeGenericMethod(method.GetGenericArguments()); adds++; }
                if (replacement != null) { operand.SetValue(instruction, replacement); opcode.SetValue(instruction, OpCodes.Call); }
            }
            yield return instruction;
        }
        if (clones != 1 || adds != 4) throw new InvalidOperationException($"Adapter call sites changed: {clones}/{adds}");
    }
    private static IEnumerable<T> RewriteInjection<T>(IEnumerable<T> instructions)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!; var count = 0;
        foreach (var instruction in instructions)
        {
            if (operand.GetValue(instruction) is MethodInfo method && method.DeclaringType == typeof(GameObject) &&
                method.Name == "GetComponents" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(MonoBehaviour))
            { operand.SetValue(instruction, typeof(AdapterTemplateExperiment).GetMethod(nameof(OriginalScripts), All)); opcode.SetValue(instruction, OpCodes.Call); count++; }
            yield return instruction;
        }
        if (count != 1) throw new InvalidOperationException("Adapter injection call site changed: " + count);
    }

    private static GameObject Clone(IInstantiator instantiator, GameObject prefab, Transform parent, out bool wasActive)
    {
        if (_current == null || instantiator.GetType() != typeof(Instantiator))
            return instantiator.InstantiateInactive(prefab, parent, out wasActive);
        var prepared = GetPrepared(prefab);
        if (prepared == null) { _fallbacks++; return instantiator.InstantiateInactive(prefab, parent, out wasActive); }
        var previous = _injection;
        _injection = new InjectionFrame { Previous = previous };
        try
        {
            var result = instantiator.InstantiateInactive(prepared, parent, out wasActive);
            _current.Root = result; _clones++;
            return result;
        }
        finally { _injection = previous; }
    }
    private static MonoBehaviour[] OriginalScripts(GameObject root)
    {
        var scripts = root.GetComponents<MonoBehaviour>();
        if (_injection == null || !_injection.First) return scripts;
        _injection.First = false;
        var result = scripts.Where(s => !Adapters.Contains(s.GetType())).ToArray();
        if (scripts.Length - result.Length != 4) throw new InvalidOperationException("Adapter clone lost root scripts");
        _filtered += 4;
        return result;
    }
    private static T Add<T>(IInstantiator instantiator, GameObject root) where T : Component
    {
        if (_current?.Root != root) return instantiator.AddComponent<T>(root);
        var result = root.GetComponent<T>();
        if (!result) throw new InvalidOperationException("Missing cloned adapter " + typeof(T));
        ((Instantiator)instantiator)._container.Inject(result);
        _reused++;
        return result;
    }
    private static GameObject? GetPrepared(GameObject prefab)
    {
        if (Prefabs.TryGetValue(prefab, out var cached)) return cached;
        if (prefab.activeInHierarchy) { Prefabs.Add(prefab, null); return null; }
        foreach (var script in prefab.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!script) { Prefabs.Add(prefab, null); return null; }
            var name = script.GetType().FullName;
            if (name == "Timberborn.Timbermesh.TimbermeshDescription") continue;
            if (name == "T3MP.ActiveInHierarchySentinel" &&
                (int)script.GetType().GetField("SlotIndex", All)!.GetValue(script)! == -1) continue;
            Prefabs.Add(prefab, null); return null;
        }
        // Do not make a second native hierarchy: even an inactive extra copy
        // changes physics allocation order and equal-distance selection ties.
        foreach (var type in Adapters) prefab.AddComponent(type);
        Prefabs.Add(prefab, prefab); _prepared++;
        return prefab;
    }
    private static void ReleaseTemplates()
    {
        foreach (var item in Prefabs)
            if (item.Value)
                foreach (var type in Adapters) Object.DestroyImmediate(item.Value!.GetComponent(type));
        Prefabs.Clear();
    }
}
