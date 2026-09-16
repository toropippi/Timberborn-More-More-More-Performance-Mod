using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

// Shadow-only prototype. The proposed cache never supplies game simulation
// values: every Read returns Unity's native result and audits the prediction.
public sealed class ActiveTransitionValidation : MonoBehaviour
{
    private const string Owner = "t3mp.test.active-transition";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestActiveTransitionAudit");
    private sealed class State { internal long Epoch; internal bool Known, Value, PendingDestroy; }
    private static ConditionalWeakTable<GameObject, State> _states = new();
    private static long _epoch;
    private static int _depth, _entries, _reads, _hits, _mismatches, _callbacks;
    private static bool _running, _protectDeferred, _guardsEnabled = true;
    private static readonly List<string> Trace = new();
    private static string _scenario = "setup";
    private readonly List<GameObject> _owned = new();
    private object? _harmony;
    private Type? _harmonyType;
    private Action<GameObject, bool> _setActive = null!;
    private Action<Transform, Transform, bool> _setParent = null!;
    private Action<Object, float> _destroy = null!;
    private Action<Object, bool> _destroyImmediate = null!;
    private Action<AnimationClip, GameObject, float> _sampleAnimation = null!;
    private int _failures;
    private int _scenarios;
    private Action? _restoreSpeed;
    private readonly List<Object> _assets = new();

    internal static void Begin(Action restoreSpeed)
    {
        var runner = new GameObject("T3MPTEST.ActiveTransition.Runner").AddComponent<ActiveTransitionValidation>();
        runner._restoreSpeed = restoreSpeed;
    }

    // Warm the caller before Harmony installation to detect inlined bypasses.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarmSetActive(GameObject target, bool value) => target.SetActive(value);

    private IEnumerator Start()
    {
        try
        {
            var warm = New("warm");
            for (var i = 0; i < 100; i++) WarmSetActive(warm, (i & 1) == 0);
            Install();
            _running = true;
            // Both policies use transition guards. The second additionally
            // refuses cached reads for descendants scheduled for destruction.
            foreach (var protect in new[] { false, true })
            {
                _protectDeferred = protect;
                _states = new ConditionalWeakTable<GameObject, State>();
                var parent = New("parent");
                var child = New("child");
                child.transform.SetParent(parent.transform);
                var callback = child.AddComponent<ActiveTransitionCallback>(); // before observer
                child.AddComponent<ActiveTransitionObserver>();
                callback.Observe = true;

                _guardsEnabled = false;
                Case("unguarded-control", child, () => { _setActive(child, false); _setActive(child, true); }, expectedEntries: 0);
                if (_mismatches != 2) throw new InvalidOperationException("Stale-read negative control not reproduced");
                _guardsEnabled = true;
                Case("delegate-self", child, () => { _setActive(child, false); _setActive(child, true); });
                Case("warmed-self", child, () => { WarmSetActive(child, false); WarmSetActive(child, true); });
                Case("delegate-parent", child, () => { _setActive(parent, false); _setActive(parent, true); });
                var inactive = New("inactive-parent");
                _setActive(inactive, false);
                Case("reparent", child, () => {
                    _setParent(child.transform, inactive.transform, false);
                    _setParent(child.transform, parent.transform, false);
                });
                var nested = New("nested");
                var nestedCallback = nested.AddComponent<ActiveTransitionCallback>();
                nested.AddComponent<ActiveTransitionObserver>();
                nestedCallback.Observe = true;
                callback.Nested = () => { _setActive(nested, false); _setActive(nested, true); };
                Read(nested);
                Case("nested-callback", child, () => { _setActive(child, false); _setActive(child, true); }, expectedCallbacks: 6, expectedEntries: 6);
                callback.Nested = null;

                var immediate = New("immediate");
                immediate.AddComponent<ActiveTransitionCallback>().Observe = true;
                immediate.AddComponent<ActiveTransitionObserver>();
                Case("immediate-destroy", immediate, () => _destroyImmediate(immediate, false), false, 1, 1);
                _destroy(immediate, 0f); // Unity-null wrapper must remain a native no-op.

                // The native animation sampler can change hierarchy activation
                // without calling any of the four managed transition wrappers.
                var clip = new AnimationClip { legacy = true };
                _assets.Add(clip);
                clip.SetCurve(child.name, typeof(GameObject), "m_IsActive", AnimationCurve.Constant(0, 1, 0));
                Case("native-animation-sample", child, () => _sampleAnimation(clip, parent, 0f), expectedCallbacks: 1, expectedEntries: protect ? 1 : 0);
                _setActive(child, true);

                var animatedParent = New("animated-parent");
                var animatedChild = New("animated-child");
                animatedChild.transform.SetParent(animatedParent.transform);
                animatedChild.AddComponent<ActiveTransitionCallback>().Observe = true;
                animatedChild.AddComponent<ActiveTransitionObserver>();
                var automaticClip = new AnimationClip { legacy = true };
                _assets.Add(automaticClip);
                automaticClip.SetCurve(animatedChild.name, typeof(GameObject), "m_IsActive", AnimationCurve.Constant(0, 1, 0));
                var animation = animatedParent.AddComponent<Animation>();
                animation.cullingType = AnimationCullingType.AlwaysAnimate;
                animation.AddClip(automaticClip, "deactivate");
                BeginCase("native-animation-player");
                Read(animatedChild);
                if (!animation.Play("deactivate")) throw new InvalidOperationException("Animation did not start");
                for (var frame = 0; frame < 10 && _callbacks == 0; frame++) yield return null;
                EndCase(1, 0);
                animation.Stop();

                // Destroy returns before native destruction. A later ordinary
                // read can refill the cache before the first OnDisable callback.
                BeginCase("deferred-destroy");
                Read(child);
                _destroy(parent, 0f);
                Read(child);
                yield return null;
                yield return null;
                if (parent != null) throw new InvalidOperationException("Deferred destruction did not finish");
                EndCase(1, 1);
            }
        }
        finally
        {
            Cleanup();
        }
        Debug.Log($"[T3MPTEST] ActiveTransition COMPLETE scenarios={_scenarios} failingScenarios={_failures} simulationUsesCache=False cleanupCompleted=True");
    }

    private GameObject New(string name)
    {
        var result = new GameObject("T3MPTEST.ActiveTransition." + name);
        _owned.Add(result);
        // Isolate invalidation to this independent synthetic hierarchy. No game
        // object is cached; unrelated UI mutations cannot affect these objects.
        _states.GetOrCreateValue(result);
        return result;
    }

    private void BeginCase(string name)
    {
        if (_depth != 0) throw new InvalidOperationException("Unbalanced transition guard");
        _scenario = name;
        _reads = _hits = _mismatches = _entries = _callbacks = 0;
        Trace.Clear();
    }
    private void EndCase(int expectedCallbacks, int expectedEntries)
    {
        if (_depth != 0) throw new InvalidOperationException("Unbalanced transition finalizer");
        if (_callbacks != expectedCallbacks || _entries != expectedEntries)
            throw new InvalidOperationException($"Unexercised/contaminated {_scenario}: callbacks={_callbacks}/{expectedCallbacks}, entries={_entries}/{expectedEntries}");
        if (_mismatches != 0 && _scenario != "unguarded-control") _failures++;
        _scenarios++;
        Debug.Log($"[T3MPTEST] ActiveTransition case={_scenario} pendingProtection={_protectDeferred} sampleGuard={_protectDeferred} reads={_reads} hits={_hits} mismatches={_mismatches} guardEntries={_entries} callbacks={_callbacks} trace={string.Join(",", Trace)}");
    }
    private void Case(string name, GameObject target, Action action, bool readAfter = true, int expectedCallbacks = 2, int expectedEntries = 2)
    {
        BeginCase(name);
        Read(target);
        action();
        if (readAfter) Read(target);
        EndCase(expectedCallbacks, expectedEntries);
    }

    internal static void Callback(GameObject target)
    {
        _callbacks++;
        Trace.Add("R" + _depth + ":" + target.name);
        Read(target);
    }

    internal static bool Read(GameObject target)
    {
        var native = target.activeInHierarchy;
        if (!_running) return native;
        _reads++;
        var state = _states.GetOrCreateValue(target);
        if (_depth == 0 && state.Known && state.Epoch == _epoch && !state.PendingDestroy)
        {
            _hits++;
            if (state.Value != native) _mismatches++;
        }
        if (_depth == 0 && !state.PendingDestroy)
        {
            state.Value = native;
            state.Epoch = _epoch;
            state.Known = true;
        }
        return native;
    }
    internal static void Publish(GameObject target)
    {
        if (!_running) return;
        Trace.Add("P:" + target.name);
        var state = _states.GetOrCreateValue(target);
        state.Value = target.activeInHierarchy;
        state.Epoch = _epoch;
        state.Known = _depth == 0;
    }
    private static void EnterInstance(object __instance, ref bool __state)
    {
        if (!_running) return;
        var target = __instance as GameObject;
        if (ReferenceEquals(target, null) && __instance is Transform transform && transform != null) target = transform.gameObject;
        if (target == null || !_states.TryGetValue(target, out _)) return;
        Enter(ref __state);
    }
    private static void Enter(ref bool __state)
    {
        if (!_running || !_guardsEnabled) return;
        __state = true;
        _epoch++;
        _depth++;
        _entries++;
    }
    private static void EnterSample(GameObject __0, ref bool __state)
    {
        if (_protectDeferred && __0 != null && _states.TryGetValue(__0, out _)) Enter(ref __state);
    }
    private static void EnterDestroy(Object obj, ref bool __state)
    {
        if (obj is not GameObject target || target == null || !_states.TryGetValue(target, out _)) return;
        Enter(ref __state);
        if (!_running || !_protectDeferred) return;
        foreach (var transform in target.GetComponentsInChildren<Transform>(true))
            _states.GetOrCreateValue(transform.gameObject).PendingDestroy = true;
    }
    private static Exception? Exit(Exception? __exception, bool __state)
    {
        if (__state) _depth--;
        return __exception;
    }

    private void Install()
    {
        _harmonyType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HarmonyLib.Harmony")).First(t => t != null)!;
        var hm = _harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod")!;
        _harmony = Activator.CreateInstance(_harmonyType, Owner);
        var patch = _harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var active = typeof(GameObject).GetMethod("SetActive", new[] { typeof(bool) })!;
        var parent = typeof(Transform).GetMethod("SetParent", new[] { typeof(Transform), typeof(bool) })!;
        var destroy = typeof(Object).GetMethod("Destroy", new[] { typeof(Object), typeof(float) })!;
        var immediate = typeof(Object).GetMethod("DestroyImmediate", new[] { typeof(Object), typeof(bool) })!;
        foreach (var method in new[] { active, parent, destroy, immediate })
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            Debug.Log($"[T3MPTEST] ActiveTransition method={method.DeclaringType!.Name}.{method.Name} ilBytes={il?.Length ?? 0}");
            if (il == null) throw new InvalidOperationException("No managed transition body: " + method);
            var prefix = typeof(ActiveTransitionValidation).GetMethod(method == destroy || method == immediate ? nameof(EnterDestroy) : nameof(EnterInstance), All)!;
            patch.Invoke(_harmony, new[] { method, Activator.CreateInstance(hm, prefix), null, null,
                Activator.CreateInstance(hm, typeof(ActiveTransitionValidation).GetMethod(nameof(Exit), All)) });
        }
        _setActive = (Action<GameObject, bool>)active.CreateDelegate(typeof(Action<GameObject, bool>));
        _setParent = (Action<Transform, Transform, bool>)parent.CreateDelegate(typeof(Action<Transform, Transform, bool>));
        _destroy = (Action<Object, float>)destroy.CreateDelegate(typeof(Action<Object, float>));
        _destroyImmediate = (Action<Object, bool>)immediate.CreateDelegate(typeof(Action<Object, bool>));
        var sample = typeof(AnimationClip).GetMethod("SampleAnimation", new[] { typeof(GameObject), typeof(float) })!;
        patch.Invoke(_harmony, new[] { sample, Activator.CreateInstance(hm, typeof(ActiveTransitionValidation).GetMethod(nameof(EnterSample), All)), null, null,
            Activator.CreateInstance(hm, typeof(ActiveTransitionValidation).GetMethod(nameof(Exit), All)) });
        _sampleAnimation = (Action<AnimationClip, GameObject, float>)sample.CreateDelegate(typeof(Action<AnimationClip, GameObject, float>));
    }

    private void Cleanup()
    {
        _running = false;
        if (_harmony != null)
        {
            _harmonyType!.GetMethod("UnpatchAll", All)!.Invoke(_harmony, new object[] { Owner });
            _harmony = null;
        }
        foreach (var target in _owned) if (target != null) Object.DestroyImmediate(target);
        _owned.Clear();
        foreach (var asset in _assets) if (asset != null) Object.DestroyImmediate(asset);
        _assets.Clear();
        var restore = _restoreSpeed;
        _restoreSpeed = null;
        restore?.Invoke();
    }
    private void OnDestroy() => Cleanup();
}

public sealed class ActiveTransitionCallback : MonoBehaviour
{
    internal bool Observe;
    internal Action? Nested;
    private void OnEnable() => Read();
    private void OnDisable() => Read();
    private void Read()
    {
        if (!Observe) return;
        ActiveTransitionValidation.Callback(gameObject);
        Nested?.Invoke();
    }
}

public sealed class ActiveTransitionObserver : MonoBehaviour
{
    private void OnEnable() => ActiveTransitionValidation.Publish(gameObject);
    private void OnDisable() => ActiveTransitionValidation.Publish(gameObject);
}
