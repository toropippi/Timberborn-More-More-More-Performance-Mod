using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Bindito.Core.Internal;
using Bindito.Unity;
using Timberborn.BaseComponentSystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

// Empty native adapters are represented by per-cache ordered pending lists.
// Native SetActive/Enable/Disable still perform every original transition.
internal static partial class DeferredUpdateAdapterExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string PatchId = "t3mp.test.deferred-update-adapters";
    private static readonly Type[] UpdateTypes = { typeof(BaseComponentUpdateUnityAdapter), typeof(BaseComponentLateUpdateUnityAdapter) };
    private sealed class Owner
    {
        internal ComponentCache Cache = null!;
        internal bool DeferredUpdate, DeferredLate;
        internal List<IUpdatableComponent>? Updates;
        internal List<ILateUpdatableComponent>? Lates;
        internal int Activating;
    }
    private sealed class Activation { internal Activation? Previous; internal Owner Owner = null!; }
    private sealed class Eligibility { internal Instantiator Instantiator = null!; internal InjectionListenerNotifier? Notifier; internal bool Valid; }
    private static readonly ConditionalWeakTable<ComponentCache, Owner> Owners = new ConditionalWeakTable<ComponentCache, Owner>();
    private static readonly Dictionary<IInstantiator, Eligibility> Contexts = new Dictionary<IInstantiator, Eligibility>();
    private static GameObject? _holder;
    private static BaseComponentUpdateUnityAdapter _updatePlaceholder = null!;
    private static BaseComponentLateUpdateUnityAdapter _latePlaceholder = null!;
    [ThreadStatic] private static Activation? _activation;
    [ThreadStatic] private static int _depth;
    private static long _enrolled, _deferred, _created, _lateCreated, _adds, _removes, _fallbacks;
    private static bool _validate;
    private static bool _nativeObservers;
    private static Type _harmony = null!;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestDeferredUpdateAdapters")) return;
        if (args.Contains("-t3mpTestAdapterTemplates")) throw new InvalidOperationException("Choose one adapter experiment");
        _validate = args.Contains("-t3mpTestDeferredUpdateAdaptersValidate");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        _harmony = ht;
        var harmony = Activator.CreateInstance(ht, PatchId);
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name, bool rewrite = false)
        {
            if (name == null) return null;
            var method = typeof(DeferredUpdateAdapterExperiment).GetMethod(name, All)!;
            if (rewrite) method = method.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
            var hook = Activator.CreateInstance(hm, method)!;
            if (name == nameof(EndLoad)) hm.GetField("priority")!.SetValue(hook, 900);
            return hook;
        }
        void Patch(Type type, string method, string? before = null, string? after = null, string? final = null) =>
            patch.Invoke(harmony, new object?[] { type.GetMethod(method, All), Hook(before), Hook(after), null, Hook(final) });
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(BeginLoad), final: nameof(EndLoad));
        patch.Invoke(harmony, new object?[] { typeof(BaseInstantiator).GetMethod("InstantiateInactive", All), null,
            Hook(nameof(AfterBase)), Hook(nameof(RewriteAdds), true), null });
        Patch(typeof(BaseComponentUnityAdapter), "OnEnable", nameof(BeginActivation), final: nameof(EndActivation));
        Patch(typeof(BaseComponentUpdateUnityAdapter), "Add", nameof(AddUpdate));
        Patch(typeof(BaseComponentUpdateUnityAdapter), "Remove", nameof(RemoveUpdate));
        Patch(typeof(BaseComponentLateUpdateUnityAdapter), "Add", nameof(AddLate));
        Patch(typeof(BaseComponentLateUpdateUnityAdapter), "Remove", nameof(RemoveLate));
        Debug.Log("[T3MPDEFERREDADAPTER] installed; ordered pending updates, native first-use adapters");
    }
    private static void BeginLoad()
    {
        if (_depth++ != 0) return;
        Contexts.Clear(); _enrolled = _deferred = _created = _lateCreated = _adds = _removes = _fallbacks = 0;
        _nativeObservers = NativeObservers();
    }
    private static bool NativeObservers()
    {
        var methods = typeof(SingletonListener).GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(new MethodBase[] {
            typeof(Container).GetMethod(nameof(Container.Inject))!, typeof(ValidatingMethodInjector).GetMethod(nameof(ValidatingMethodInjector.Inject))!,
            typeof(MethodInjector).GetMethod(nameof(MethodInjector.Inject))!, typeof(MethodRetriever).GetMethod(nameof(MethodRetriever.GetInjectedMethods))! });
        return methods.All(method =>
        {
            var info = _harmony.GetMethod("GetPatchInfo", All)!.Invoke(null, new object[] { method });
            return info == null || !((IEnumerable<string>)info.GetType().GetProperty("Owners", All)!.GetValue(info)!).Any();
        });
    }
    private static void EndLoad() { if (--_depth == 0) { Report("load-end"); Contexts.Clear(); } }
    internal static void Report(string phase)
    {
        if (_harmony != null) Debug.Log($"[T3MPDEFERREDADAPTER] phase={phase} enrolled={_enrolled} deferred={_deferred} createdDuringLoad={_created} createdAfterLoad={_lateCreated} pendingAdds={_adds} pendingRemoves={_removes} fallbacks={_fallbacks} nativeObservers={_nativeObservers}");
    }

    private static IEnumerable<T> RewriteAdds<T>(IEnumerable<T> instructions)
    {
        var operand = typeof(T).GetField("operand")!; var opcode = typeof(T).GetField("opcode")!; var count = 0;
        foreach (var instruction in instructions)
        {
            if (operand.GetValue(instruction) is MethodInfo method && method.DeclaringType == typeof(IInstantiator) &&
                method.Name == "AddComponent" && method.IsGenericMethod &&
                (method.GetGenericArguments()[0] == typeof(BaseComponentUpdateUnityAdapter) || method.GetGenericArguments()[0] == typeof(BaseComponentLateUpdateUnityAdapter)))
            {
                operand.SetValue(instruction, typeof(DeferredUpdateAdapterExperiment).GetMethod(nameof(Defer), All)!.MakeGenericMethod(method.GetGenericArguments()));
                opcode.SetValue(instruction, OpCodes.Call); count++;
            }
            yield return instruction;
        }
        if (count != 2) throw new InvalidOperationException("Unexpected update adapter call-site count: " + count);
    }
    private static Eligibility GetEligibility(IInstantiator instantiator)
    {
        if (Contexts.TryGetValue(instantiator, out var known)) return known;
        var result = new Eligibility(); Contexts.Add(instantiator, result);
        if (instantiator.GetType() != typeof(Instantiator)) return result;
        result.Instantiator = (Instantiator)instantiator;
        if (result.Instantiator._container.GetType() != typeof(Container)) return result;
        var container = (Container)result.Instantiator._container;
        if (container._validatingMethodInjector.GetType() != typeof(ValidatingMethodInjector)) return result;
        var validator = (ValidatingMethodInjector)container._validatingMethodInjector;
        if (validator._methodInjector.GetType() != typeof(MethodInjector) || validator._bindingValidator.GetType() != typeof(BindingValidator)) return result;
        if (((BindingValidator)validator._bindingValidator)._bindingAnalyser.GetType() != typeof(BindingAnalyser)) return result;
        var injector = (MethodInjector)validator._methodInjector;
        if (injector._methodRetriever.GetType() != typeof(MethodRetriever) || injector._injectionListenerNotifier.GetType() != typeof(InjectionListenerNotifier)) return result;
        if (injector._methodRetriever.GetInjectedMethods(typeof(BaseComponentUpdateUnityAdapter)).Any() ||
            injector._methodRetriever.GetInjectedMethods(typeof(BaseComponentLateUpdateUnityAdapter)).Any()) return result;
        result.Notifier = (InjectionListenerNotifier)injector._injectionListenerNotifier;
        result.Valid = true;
        Debug.Log("[T3MPDEFERREDADAPTER] nativeContext listeners=" + string.Join(",", result.Notifier._listeners.Select(x => x.GetType().FullName)));
        return result;
    }
    private static void EnsurePlaceholders()
    {
        if (_holder) return;
        _holder = new GameObject("T3MP dormant update adapters"); _holder.SetActive(false);
        Object.DontDestroyOnLoad(_holder);
        _updatePlaceholder = _holder.AddComponent<BaseComponentUpdateUnityAdapter>();
        _latePlaceholder = _holder.AddComponent<BaseComponentLateUpdateUnityAdapter>();
    }
    private static T Defer<T>(IInstantiator instantiator, GameObject root) where T : Component
    {
        if (_depth == 0) return instantiator.AddComponent<T>(root);
        var eligibility = GetEligibility(instantiator);
        if (!_nativeObservers || !eligibility.Valid || !InertListeners(eligibility.Notifier!) || root.GetComponent<T>())
        { _fallbacks++; return instantiator.AddComponent<T>(root); }
        EnsurePlaceholders();
        var placeholder = typeof(T) == typeof(BaseComponentUpdateUnityAdapter) ? (Component)_updatePlaceholder : _latePlaceholder;
        // No injection methods. The native singleton listener only classifies
        // these non-singleton types; keep its lookup at the original call site.
        eligibility.Instantiator._container.Inject(placeholder);
        var cache = root.GetComponent<ComponentCache>();
        if (!Owners.TryGetValue(cache, out var owner))
        { owner = new Owner { Cache = cache }; Owners.Add(cache, owner); _enrolled++; }
        if (typeof(T) == typeof(BaseComponentUpdateUnityAdapter)) owner.DeferredUpdate = true;
        else owner.DeferredLate = true;
        _deferred++;
        return (T)placeholder;
    }
    private static bool InertListeners(InjectionListenerNotifier notifier)
    {
        foreach (var listener in notifier._listeners)
        {
            if (listener.GetType() != typeof(SingletonListener)) return false;
            var singleton = (SingletonListener)listener;
            foreach (var type in UpdateTypes)
                if (singleton._typeCache.TryGetValue(type, out var cached) ? cached : SingletonListener.TypeIsSingleton(type)) return false;
        }
        return true;
    }
    private static void AfterBase(GameObject __result)
    {
        if (_depth == 0) return;
        var cache = __result.GetComponent<ComponentCache>();
        if (!Owners.TryGetValue(cache, out var owner)) return;
        if (owner.DeferredUpdate) cache._updateAdapter = _updatePlaceholder;
        if (owner.DeferredLate) cache._lateUpdateAdapter = _latePlaceholder;
    }
    private static void BeginActivation(BaseComponentUnityAdapter __instance, out Activation? __state)
    {
        __state = null;
        if (__instance._activated) return;
        var cache = __instance.GetComponent<ComponentCache>();
        if (!Owners.TryGetValue(cache, out var owner)) return;
        owner.Activating++;
        _activation = __state = new Activation { Owner = owner, Previous = _activation };
    }
    private static void EndActivation(Exception? __exception, Activation? __state)
    {
        if (__state == null) return;
        _activation = __state.Previous;
        // A throwing Awake leaves earlier native registrations in place too.
        if (--__state.Owner.Activating == 0) Flush(__state.Owner);
    }
    private static Owner OwnerFor(object component)
    {
        if (component is BaseComponent baseComponent && Owners.TryGetValue(baseComponent._componentCache, out var owner)) return owner;
        if (_activation != null) return _activation.Owner;
        throw new InvalidOperationException("Dormant update registration has no owner");
    }
    private static bool AddUpdate(BaseComponentUpdateUnityAdapter __instance, IUpdatableComponent component)
    {
        if (!ReferenceEquals(__instance, _updatePlaceholder)) return true;
        var owner = OwnerFor(component); (owner.Updates ??= new List<IUpdatableComponent>()).Add(component); _adds++;
        if (owner.Activating == 0) Flush(owner);
        return false;
    }
    private static bool RemoveUpdate(BaseComponentUpdateUnityAdapter __instance, IUpdatableComponent component)
    {
        if (!ReferenceEquals(__instance, _updatePlaceholder)) return true;
        OwnerFor(component).Updates?.Remove(component); _removes++; return false;
    }
    private static bool AddLate(BaseComponentLateUpdateUnityAdapter __instance, ILateUpdatableComponent component)
    {
        if (!ReferenceEquals(__instance, _latePlaceholder)) return true;
        var owner = OwnerFor(component); (owner.Lates ??= new List<ILateUpdatableComponent>()).Add(component); _adds++;
        if (owner.Activating == 0) Flush(owner);
        return false;
    }
    private static bool RemoveLate(BaseComponentLateUpdateUnityAdapter __instance, ILateUpdatableComponent component)
    {
        if (!ReferenceEquals(__instance, _latePlaceholder)) return true;
        OwnerFor(component).Lates?.Remove(component); _removes++; return false;
    }
    private static void Flush(Owner owner)
    {
        if (owner.Updates is { Count: > 0 } updates)
        {
            var adapter = owner.Cache.CachedGameObject.AddComponent<BaseComponentUpdateUnityAdapter>();
            owner.Cache._updateAdapter = adapter; owner.DeferredUpdate = false;
            foreach (var item in updates) adapter.Add(item);
            Created();
        }
        if (owner.Lates is { Count: > 0 } lates)
        {
            var adapter = owner.Cache.CachedGameObject.AddComponent<BaseComponentLateUpdateUnityAdapter>();
            owner.Cache._lateUpdateAdapter = adapter; owner.DeferredLate = false;
            foreach (var item in lates) adapter.Add(item);
            Created();
        }
        owner.Updates = null; owner.Lates = null;
        if (!owner.DeferredUpdate && !owner.DeferredLate) Owners.Remove(owner.Cache);
    }
    private static void Created() { if (_depth > 0) _created++; else _lateCreated++; }
}
