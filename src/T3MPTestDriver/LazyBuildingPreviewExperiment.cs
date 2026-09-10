using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Globalization;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using Timberborn.BaseComponentSystem;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only: retain the game's PreviewPlacer and its normal lazy array,
// but create the first hidden preview in that lazy factory as well. Tool/button
// creation, unlocking and group registration still run at the original time.
internal static class LazyBuildingPreviewExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Func<PreviewPlacerFactory, PlaceableBlockObjectSpec, Preview> First =
        (Func<PreviewPlacerFactory, PlaceableBlockObjectSpec, Preview>)typeof(PreviewPlacerFactory)
            .GetMethod("EagerlyCreateFirstPreview", All)!.CreateDelegate(typeof(Func<PreviewPlacerFactory, PlaceableBlockObjectSpec, Preview>));
    private static readonly Func<PreviewPlacerFactory, PlaceableBlockObjectSpec, int, Preview, Preview[]> Remaining =
        (Func<PreviewPlacerFactory, PlaceableBlockObjectSpec, int, Preview, Preview[]>)typeof(PreviewPlacerFactory)
            .GetMethod("CreatePreviews", All)!.CreateDelegate(typeof(Func<PreviewPlacerFactory, PlaceableBlockObjectSpec, int, Preview, Preview[]>));
    [ThreadStatic] private static int _depth, _materializing;
    private static bool _enabled, _validate, _audit;
    private static int _deferred, _created, _fallbacks, _audited, _unsafe;
    private static readonly FieldInfo PreviewFactoryField = typeof(PreviewPlacerFactory).GetField("_previewFactory", All)!;
    private static readonly FieldInfo InstantiatorField = typeof(PreviewFactory).GetField("_templateInstantiator", All)!;
    private static readonly FieldInfo PlacerPreviews = typeof(PreviewPlacer).GetField("_previews", All)!;
    private static readonly Dictionary<PlaceableBlockObjectSpec, bool> Eligibility = new Dictionary<PlaceableBlockObjectSpec, bool>();
    private static readonly Dictionary<Type, bool> TypeEligibility = new Dictionary<Type, bool>();
    internal sealed class AuditState
    {
        internal string Name = "";
        internal bool Eligible;
        internal UnityEngine.Random.State Random;
    }
    private static long _firstUseTicks;
    private sealed class Entry
    {
        internal PreviewPlacerFactory Factory = null!;
        internal PlaceableBlockObjectSpec Spec = null!;
        internal Lazy<Preview[]> Value = null!;
        internal int Amount;
        internal PreviewPlacer Placer = null!;
    }
    // Only validation keeps strong references, and releases them after checks.
    private static readonly List<Entry> Entries = new List<Entry>();
    private static readonly Dictionary<UnityEngine.Object, int> ValidationIdentities = new Dictionary<UnityEngine.Object, int>();
    private static readonly Dictionary<Mesh, string> ValidationMeshes = new Dictionary<Mesh, string>();
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        _enabled = args.Contains("-t3mpTestLazyBuildingPreview");
        _audit = args.Contains("-t3mpTestBuildingPreviewGuardValidate");
        if (!_enabled && !_audit) return;
        _validate = args.Contains("-t3mpTestLazyBuildingPreviewValidate");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.lazy-building-preview");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(LazyBuildingPreviewExperiment).GetMethod(name, All))!;
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        patch.Invoke(harmony, new object?[] { typeof(PreviewPlacerFactory).GetMethod("EagerlyCreateFirstPreview", All), Hook(nameof(DeferFirst)), Hook(nameof(AuditFirst)), null, null });
        patch.Invoke(harmony, new object?[] { typeof(PreviewPlacerFactory).GetMethod("LazyCreateRemainingPreviews", All), Hook(nameof(DeferArray)), null, null, null });
        if (_validate) patch.Invoke(harmony, new object?[] { typeof(PreviewPlacerFactory).GetMethod("Create", All), null, Hook(nameof(CreatedPlacer)), null, null });
        Debug.Log($"[T3MPBUILDINGPREVIEW] installed enabled={_enabled} audit={_audit}; random-dependent component layouts keep native eager creation");
    }
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _deferred = _created = _fallbacks = _audited = _unsafe = 0; _firstUseTicks = 0; Entries.Clear(); Eligibility.Clear(); TypeEligibility.Clear();
    }
    private static void End() { if (--_depth == 0) { Report("load-end"); Eligibility.Clear(); TypeEligibility.Clear(); } }
    private static bool Eligible(PreviewPlacerFactory factory, PlaceableBlockObjectSpec spec)
    {
        if (Eligibility.TryGetValue(spec, out var result)) return result;
        var instantiator = InstantiatorField.GetValue(PreviewFactoryField.GetValue(factory))!;
        var blueprint = spec.Blueprint;
        var arguments = new object?[] { blueprint, null, null };
        instantiator.GetType().GetMethod("GetInstanceComponents", All)!.Invoke(instantiator, arguments);
        var components = (List<Type>)arguments[2]!;
        // Direct dependency guard, audited against the complete native call for
        // every tested layout, including decorators and prefab import.
        result = components.All(EligibleType);
        Eligibility.Add(spec, result);
        return result;
    }
    private static bool EligibleType(Type type)
    {
        if (TypeEligibility.TryGetValue(type, out var result)) return result;
        result = type.Assembly.GetName().Name!.StartsWith("Timberborn.") &&
            !type.GetFields(All).Any(f => f.FieldType.FullName == "Timberborn.Common.IRandomNumberGenerator");
        TypeEligibility.Add(type, result);
        return result;
    }
    private static bool DeferFirst(PreviewPlacerFactory __instance, PlaceableBlockObjectSpec spec, ref Preview __result, out AuditState? __state)
    {
        __state = null;
        if (_depth == 0 || _materializing != 0) return true;
        var eligible = Eligible(__instance, spec);
        if (_audit) __state = new AuditState { Name = spec.Blueprint.Name, Eligible = eligible, Random = UnityEngine.Random.state };
        if (!_enabled || _audit || !eligible) { if (!eligible) _fallbacks++; return true; }
        __result = null!;
        return false;
    }
    private static void AuditFirst(AuditState? __state)
    {
        if (__state == null) return;
        _audited++;
        var changed = !__state.Random.Equals(UnityEngine.Random.state);
        if (changed && __state.Eligible) _unsafe++;
        if (changed) Debug.Log($"[T3MPBUILDINGPREVIEW] randomChanged eligible={__state.Eligible} template={__state.Name}");
    }
    private static void CreatedPlacer(PreviewPlacer __result)
    {
        if (_depth == 0 || Entries.Count == 0) return;
        var entry = Entries[Entries.Count - 1];
        if (ReferenceEquals(entry.Value, PlacerPreviews.GetValue(__result))) entry.Placer = __result;
    }
    private static bool DeferArray(PreviewPlacerFactory __instance, PlaceableBlockObjectSpec spec, int amount,
        Preview firstPreview, ref Lazy<Preview[]> __result)
    {
        if (_depth == 0 || _materializing != 0 || firstPreview != null) return true;
        var factory = __instance;
        __result = new Lazy<Preview[]>(() =>
        {
            var started = Stopwatch.GetTimestamp();
            _materializing++;
            try
            {
                var first = First(factory, spec);
                var result = Remaining(factory, spec, amount, first);
                _created++;
                return result;
            }
            finally { _materializing--; _firstUseTicks += Stopwatch.GetTimestamp() - started; }
        });
        _deferred++;
        if (_validate) Entries.Add(new Entry { Factory = factory, Spec = spec, Amount = amount, Value = __result });
        return false;
    }
    internal static void Report(string phase)
    {
        if (_enabled || _audit) Debug.Log(FormattableString.Invariant($"[T3MPBUILDINGPREVIEW] phase={phase} deferred={_deferred} created={_created} fallbacks={_fallbacks} audited={_audited} unsafe={_unsafe} firstUseMs={_firstUseTicks * 1000.0 / Stopwatch.Frequency:F3}"));
    }
    internal static void Validate()
    {
        if (_audit && (_audited == 0 || _unsafe != 0)) throw new Exception("Building preview RNG guard audit failed");
        if (_audit) Debug.Log("[T3MPBUILDINGPREVIEW] GUARD PASS audited=" + _audited);
        if (!_validate) return;
        if (_depth != 0 || _deferred == 0) throw new Exception("Building preview deferral was not exercised");
        var exercised = 0;
        ValidationIdentities.Clear();
        ValidationMeshes.Clear();
        // Real first-use through the game's Lazy array; bounded to single-preview
        // layouts so validation doesn't spawn whole construction brush pools.
        foreach (var entry in Entries.Where(e => e.Amount == 1 && !e.Value.IsValueCreated).Take(16))
        {
            var expectedPlacer = entry.Factory.Create(entry.Spec);
            var expected = ((Lazy<Preview[]>)PlacerPreviews.GetValue(expectedPlacer)!).Value[0];
            try
            {
                var actual = entry.Value.Value;
                if (actual.Length != 1 || !ReferenceEquals(actual, entry.Value.Value) ||
                    actual[0].GetType() != expected.GetType() || actual[0].GameObject.activeSelf ||
                    expected.GameObject.activeSelf || actual[0].GameObject.name != expected.GameObject.name ||
                    !actual[0].GameObject.GetComponentsInChildren<Transform>(true).Select(t => t.name)
                        .SequenceEqual(expected.GameObject.GetComponentsInChildren<Transform>(true).Select(t => t.name)))
                    throw new Exception("Lazy building preview differs from native first preview");
                CompareSnapshot(Snapshot(actual[0]), Snapshot(expected), "initial " + expected.GameObject.name);
                foreach (var placements in new[] { Array.Empty<Placement>(), new[] { new Placement(new Vector3Int(2, 2, 1)) },
                    new[] { new Placement(new Vector3Int(3, 3, 2), Orientation.Cw90, FlipMode.Unflipped) } })
                {
                    expectedPlacer.ShowPreviews(placements);
                    var expectedWarning = expectedPlacer.WarningText;
                    var expectedState = Snapshot(expected);
                    var expectedCoordinates = expectedPlacer.GetBuildableCoordinates(placements).ToArray();
                    expectedPlacer.HideAllPreviews();
                    entry.Placer.ShowPreviews(placements);
                    var actualWarning = entry.Placer.WarningText;
                    var actualState = Snapshot(actual[0]);
                    var actualCoordinates = entry.Placer.GetBuildableCoordinates(placements).ToArray();
                    entry.Placer.HideAllPreviews();
                    CompareSnapshot(actualState, expectedState, "placed " + expected.GameObject.name);
                    if (expectedWarning != actualWarning || !expectedCoordinates.SequenceEqual(actualCoordinates) ||
                        actual[0].GameObject.activeSelf || actual[0].BlockObject.AddedToService)
                        throw new Exception("Building preview placement workflow differs: " + expected.GameObject.name);
                }
                exercised++;
            }
            finally { expectedPlacer.HideAllPreviews(); entry.Placer.HideAllPreviews(); UnityEngine.Object.Destroy(expected.GameObject); }
        }
        if (exercised == 0) throw new Exception("No bounded first-use layout found");
        Debug.Log("[T3MPBUILDINGPREVIEW] VALIDATE PASS singleLayouts=" + exercised + " (native first creation, hierarchy, mesh/material values, transforms, collider state, array reuse, show/buildability/hide at three placements)");
        Entries.Clear();
        ValidationIdentities.Clear();
        ValidationMeshes.Clear();
        Report("validated");
    }

    internal static void CompareVisualClones(BaseComponent actual, BaseComponent expected)
    {
        ValidationIdentities.Clear(); ValidationMeshes.Clear();
        try { CompareSnapshot(Snapshot(actual), Snapshot(expected), actual.GameObject.name); }
        finally { ValidationIdentities.Clear(); ValidationMeshes.Clear(); }
    }
    private static string Snapshot(BaseComponent preview)
    {
        var state = new StringBuilder();
        void F(float value) => state.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        void V(Vector3 v) { F(v.x); F(v.y); F(v.z); }
        void V4(Vector4 v) { F(v.x); F(v.y); F(v.z); F(v.w); }
        if (preview is Preview building)
            state.Append(building.PreviewState).Append('|').Append(building.BlockObject.IsPreview).Append('|').Append(building.BlockObject.AddedToService);
        foreach (var transform in preview.GameObject.GetComponentsInChildren<Transform>(true))
        {
            var go = transform.gameObject;
            state.Append('|').Append(go.name).Append('|').Append(transform.childCount).Append('|').Append(go.activeSelf).Append('|').Append(go.activeInHierarchy).Append('|').Append(go.layer);
            V(transform.localPosition); V(transform.localScale);
            var q = transform.localRotation; F(q.x); F(q.y); F(q.z); F(q.w);
            foreach (var component in go.GetComponents<Component>()) state.Append('|').Append(component ? component.GetType().FullName : "missing");
            foreach (var collider in go.GetComponents<Collider>())
            {
                state.Append('|').Append(collider.enabled).Append('|').Append(collider.isTrigger); F(collider.contactOffset);
                if (collider is BoxCollider box) { V(box.center); V(box.size); }
            }
            foreach (var renderer in go.GetComponents<Renderer>())
            {
                state.Append('|').Append(renderer.enabled).Append('|').Append(renderer.shadowCastingMode).Append('|').Append(renderer.receiveShadows);
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material) { state.Append("|null material"); continue; }
                    var shader = material.shader;
                    // Material instances are intentionally private per preview;
                    // compare their values, not allocation identity or clone name.
                    state.Append('|').Append(shader.name).Append('|').Append(material.renderQueue).Append('|')
                        .Append(string.Join(",", material.shaderKeywords.OrderBy(s => s, StringComparer.Ordinal)));
                    for (var i = 0; i < shader.GetPropertyCount(); i++)
                    {
                        var name = shader.GetPropertyName(i); state.Append('|').Append(name).Append('=');
                        switch (shader.GetPropertyType(i))
                        {
                            case ShaderPropertyType.Color: V4(material.GetColor(name)); break;
                            case ShaderPropertyType.Vector: V4(material.GetVector(name)); break;
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range: F(material.GetFloat(name)); break;
                            case ShaderPropertyType.Int: state.Append(material.GetInteger(name)); break;
                            case ShaderPropertyType.Texture:
                                state.Append(Identity(material.GetTexture(name))); V4(material.GetTextureScale(name)); V4(material.GetTextureOffset(name)); break;
                        }
                    }
                }
            }
            foreach (var filter in go.GetComponents<MeshFilter>())
            {
                var mesh = filter.sharedMesh;
                if (!mesh) { state.Append("|null mesh"); continue; }
                if (!mesh.isReadable) { state.Append("|meshIdentity=").Append(Identity(mesh)); continue; }
                if (!ValidationMeshes.TryGetValue(mesh, out var digest)) ValidationMeshes.Add(mesh, digest = MeshSnapshot.Digest(mesh));
                state.Append('|').Append(digest);
            }
        }
        return state.ToString();
    }
    private static int Identity(UnityEngine.Object value)
    {
        if (!value) return 0;
        if (!ValidationIdentities.TryGetValue(value, out var id)) ValidationIdentities.Add(value, id = ValidationIdentities.Count + 1);
        return id;
    }
    private static void CompareSnapshot(string actual, string expected, string label)
    {
        if (actual == expected) return;
        var i = 0; while (i < Math.Min(actual.Length, expected.Length) && actual[i] == expected[i]) i++;
        var start = Math.Max(0, i - 60);
        throw new Exception("Building preview state differs " + label + " offset=" + i + " actual=" +
            actual.Substring(start, Math.Min(180, actual.Length - start)) + " expected=" + expected.Substring(start, Math.Min(180, expected.Length - start)));
    }
}
