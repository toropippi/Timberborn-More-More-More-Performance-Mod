using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Test-driver only: preserve native status decisions, publish the last visual
// activation after PreInitializeEntity. The first hide remains at its native
// position so collider removal/reinsertion order is retained.
internal static class StatusFinalStateExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Id = "t3mp.test.status-final-state";
    private sealed class Scope
    {
        internal object Cycler = null!;
        internal Scope? Previous;
        internal bool First = true, Pending, Visible, AppliedVisible;
    }
    [ThreadStatic] private static Scope? _scope;
    private static int _depth;
    private static bool _enabled, _snapshot, _native;
    private static long _eligible, _fallback, _requested, _applied;
    private static Type _type = null!;
    private static Func<object, GameObject> _icon = null!;
    private static Func<object, Transform> _collider = null!;
    private static Func<object, BaseComponent> _facing = null!;
    private static Action<object, bool> _toggle = null!;
    private static readonly SortedDictionary<string, string> Rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    private static Func<object, T> Getter<T>(string field)
    {
        var p = Expression.Parameter(typeof(object));
        return Expression.Lambda<Func<object, T>>(Expression.Convert(Expression.Field(Expression.Convert(p, _type), field), typeof(T)), p).Compile();
    }
    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        _enabled = args.Contains("-t3mpTestStatusFinalState");
        _snapshot = args.Contains("-t3mpTestStatusStateSnapshot");
        if (!_enabled && !_snapshot) return;
        _type = Find("Timberborn.StatusSystem.StatusIconCycler");
        if (_type.GetMethod("PreInitializeEntity", All) == null)
            throw new NotSupportedException("Status preinitialization coalescing/snapshot requires the native 1.1 PreInitializeEntity lifecycle.");
        _icon = Getter<GameObject>("_statusIcon"); _collider = Getter<Transform>("_colliderTransform"); _facing = Getter<BaseComponent>("_facingCamera");
        var toggle = _type.GetMethod("ToggleStatusIcon", All)!;
        var p = Expression.Parameter(typeof(object)); var v = Expression.Parameter(typeof(bool));
        _toggle = Expression.Lambda<Action<object, bool>>(Expression.Call(Expression.Convert(p, _type), toggle, v), p, v).Compile();
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, Id);
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        // Only native methods may observe the temporarily deferred activation.
        var observed = _type.GetMethods(All | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(Find("Timberborn.CameraSystem.FacingCamera").GetMethods(All | BindingFlags.DeclaredOnly)
                .Where(m => m.Name is "Enable" or "Disable" or "SetRotation"))
            .Concat(Find("Timberborn.StatusSystem.StatusIconMaterials").GetMethods(All | BindingFlags.DeclaredOnly));
        _native = observed.All(m => ht.GetMethod("GetPatchInfo", All)!.Invoke(null, new object[] { m }) == null);
        object? Hook(string? n) => n == null ? null : Activator.CreateInstance(hm, typeof(StatusFinalStateExperiment).GetMethod(n, All));
        void Patch(MethodInfo m, string? pre, string? post, string? final) =>
            patch.Invoke(harmony, new object?[] { m, Hook(pre), Hook(post), null, Hook(final) });
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All)!, nameof(BeginLoad), null, nameof(EndLoad));
        Patch(_type.GetMethod("PreInitializeEntity", All)!, nameof(Begin), null, nameof(End));
        if (_enabled) Patch(toggle, nameof(Toggle), null, null);
        Debug.Log($"[T3MPSTATUSFINAL] installed enabled={_enabled} snapshot={_snapshot} native={_native}");
    }
    private static void BeginLoad()
    {
        if (_depth++ != 0) return;
        _eligible = _fallback = _requested = _applied = 0; Rows.Clear();
    }
    private static void EndLoad()
    {
        if (--_depth != 0) return;
        Debug.Log($"[T3MPSTATUSFINAL] eligible={_eligible} fallback={_fallback} requested={_requested} applied={_applied} saved={_requested - _applied}");
        if (!_snapshot) return;
        var args = Environment.GetCommandLineArgs();
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!, "status-initial-state.tsv"), Rows.Select(r => r.Key + "\t" + r.Value));
    }
    private static bool Safe(GameObject go)
    {
        if (!go) return false;
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            var t = c ? c.GetType() : null;
            if (t == typeof(Transform) || t == typeof(MeshFilter) || t == typeof(MeshRenderer)) continue;
            if (c is Collider collider && (t == typeof(BoxCollider) || t == typeof(SphereCollider) || t == typeof(CapsuleCollider)) &&
                !collider.isTrigger && !collider.attachedRigidbody) continue;
            return false;
        }
        return true;
    }
    private static void Begin(object __instance, out Scope? __state)
    {
        __state = null;
        if (!_enabled || _depth == 0) return;
        var icon = _icon(__instance); var collider = _collider(__instance); var facing = _facing(__instance);
        if (!_native || __instance.GetType() != _type || facing.Enabled || !icon || !collider ||
            !Safe(icon) || !Safe(collider.gameObject)) { _fallback++; return; }
        _eligible++;
        __state = new Scope { Cycler = __instance, Previous = _scope }; _scope = __state;
    }
    private static bool Toggle(object __instance, bool visible)
    {
        var scope = _scope;
        if (scope == null || !ReferenceEquals(scope.Cycler, __instance)) return true;
        _requested++;
        if (scope.First)
        {
            scope.First = false; scope.AppliedVisible = visible; _applied++;
            return true;
        }
        scope.Pending = true; scope.Visible = visible; return false;
    }
    private static void End(object __instance, Scope? __state)
    {
        if (__state != null)
        {
            _scope = __state.Previous;
            if (__state.Pending && __state.Visible != __state.AppliedVisible) { _toggle(__instance, __state.Visible); _applied++; }
        }
        if (!_snapshot || _depth == 0) return;
        object? Field(string name) => _type.GetField(name, All)!.GetValue(__instance);
        var component = (BaseComponent)__instance;
        var entity = component.GetComponent<EntityComponent>();
        if (entity == null) return;
        var shown = Field("_shownIconStatus");
        string Status(object? s) => s == null ? "null" : string.Join("|", new[] { "StatusDescription", "ShowFloatingIcon", "IsActive" }
            .Select(n => Convert.ToString(s.GetType().GetProperty(n, All)!.GetValue(s))));
        var subject = Field("_statusSubject")!;
        var statuses = (IEnumerable)subject.GetType().GetProperty("ActiveStatuses", All)!.GetValue(subject)!;
        var facing = _facing(__instance);
        var late = facing._componentCache._lateUpdateAdapter;
        Rows[entity.EntityId.ToString()] = string.Join(";", Field("_indexOfStatusCheckedInLastUpdate"), Field("_visible"),
            _type.GetProperty("VisibleAndActive")!.GetValue(__instance), Status(shown),
            string.Join(",", statuses.Cast<object>().Select(Status)), facing.Enabled,
            string.Join(",", late._lateUpdatableComponents.Select(c => c.GetType().FullName)));
    }
}
