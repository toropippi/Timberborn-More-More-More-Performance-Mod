using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.Planting;
using Timberborn.PlantingUI;
using UnityEngine;

namespace T3MPTestDriver;

// Initial hidden planting overlays only. PlantingService, terrain, crops and
// native set/unset events retain their original behavior.
internal static class LazyPlantablePreviewExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Pending
    {
        internal string Resource = "";
        internal int Sequence;
    }
    private sealed class Session
    {
        internal readonly Dictionary<Vector3Int, Pending> Pending = new Dictionary<Vector3Int, Pending>();
        internal readonly Dictionary<Vector3Int, int> Sequences = new Dictionary<Vector3Int, int>();
        internal readonly SortedList<int, PlantablePreview> Created = new SortedList<int, PlantablePreview>();
        internal int Next, Deferred, Materialized, Prototypes, Fallbacks;
        internal Pending? Creating;
        internal Vector3Int CreatingAt;
    }
    private static readonly ConditionalWeakTable<PlantablePreviewService, Session> Sessions = new ConditionalWeakTable<PlantablePreviewService, Session>();
    private static readonly List<WeakReference<PlantablePreviewService>> Services = new List<WeakReference<PlantablePreviewService>>();
    private static bool _enabled, _validate;
    [ThreadStatic] private static int _loadDepth;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        _enabled = args.Contains("-t3mpTestLazyPlantablePreview");
        _validate = args.Contains("-t3mpTestLazyPlantablePreviewValidate");
        if (!_enabled) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.lazy-plantable-preview");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object? Hook(string? name) => name == null ? null : Activator.CreateInstance(hm, typeof(LazyPlantablePreviewExperiment).GetMethod(name, All));
        void Patch(Type type, string method, string? before = null, string? after = null, string? final = null) =>
            patch.Invoke(harmony, new object?[] { type.GetMethod(method, All), Hook(before), Hook(after), null, Hook(final) });
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(Begin), final: nameof(End));
        var service = typeof(PlantablePreviewService);
        Patch(service, "Load", after: nameof(Reset));
        Patch(service, "PostLoad", nameof(PostLoad));
        Patch(service, "GetPreview", nameof(BeforeRead));
        Patch(service, "ShowPreview", nameof(BeforeRead));
        Patch(service, "CreatePreview", nameof(BeforeCreate), nameof(AfterCreate));
        Patch(service, "OnPlantingCoordinatesUnset", nameof(BeforeUnset));
        Debug.Log("[T3MPPLANTPREVIEW] installed; hidden initial previews materialize on read/show");
    }
    private static void Begin() { if (_loadDepth++ == 0) Services.RemoveAll(w => !w.TryGetTarget(out _)); }
    private static void End() { if (--_loadDepth == 0) Report("load-end"); }
    private static void Reset(PlantablePreviewService __instance) => Sessions.Remove(__instance);
    private static bool PostLoad(PlantablePreviewService __instance)
    {
        if (_loadDepth == 0 || Sessions.TryGetValue(__instance, out _)) return true;
        var session = new Session(); Sessions.Add(__instance, session);
        Services.Add(new WeakReference<PlantablePreviewService>(__instance));
        var safe = new Dictionary<string, bool>();
        foreach (var coordinate in __instance._plantingService.PlantingCoordinates)
        {
            var resource = __instance._plantingService.GetResourceAt(coordinate);
            if (!safe.TryGetValue(resource, out var canDefer))
            {
                var randomBefore = UnityEngine.Random.state;
                var prototype = __instance.CreatePreview(resource, coordinate);
                prototype.Hide();
                canDefer = randomBefore.Equals(UnityEngine.Random.state) && Safe(prototype);
                safe.Add(resource, canDefer); session.Prototypes++;
                if (_validate) Debug.Log($"[T3MPPLANTPREVIEW] prototype resource={resource} safe={canDefer} randomSame={randomBefore.Equals(UnityEngine.Random.state)} components=" +
                    string.Join(",", prototype.AllComponents.Select(c => c.GetType().FullName)) + " unity=" +
                    string.Join(",", prototype.GameObject.GetComponentsInChildren<Component>(true).Select(c => c.GetType().FullName).Distinct()));
                continue;
            }
            if (!canDefer || __instance._previews[coordinate.x, coordinate.y, coordinate.z] != null)
            {
                __instance.CreatePreview(resource, coordinate).Hide(); session.Fallbacks++; continue;
            }
            session.Pending.Add(coordinate, new Pending { Resource = resource, Sequence = session.Next++ });
            session.Deferred++;
        }
        return false;
    }
    private static bool Safe(PlantablePreview preview)
    {
        if (preview.GameObject.activeSelf || preview.GameObject.GetComponentsInChildren<Collider>(true).Length != 0) return false;
        foreach (var component in preview.AllComponents)
        {
            var type = component.GetType(); var name = type.FullName!;
            if (!(name is "Timberborn.PlantingUI.PlantablePreviewSpec" or "Timberborn.Timbermesh.TimbermeshSpec" or
                  "Timberborn.PlantingUI.PlantablePreview" or "Timberborn.SelectionSystem.HighlightableObject" or
                  "Timberborn.BaseComponentSystem.ComponentCache")) return false;
            if (type.GetFields(All).Any(f => f.FieldType.FullName == "Timberborn.Common.IRandomNumberGenerator")) return false;
        }
        foreach (var component in preview.GameObject.GetComponentsInChildren<Component>(true))
        {
            if (component is Transform || component is MeshFilter || component is MeshRenderer) continue;
            var name = component.GetType().FullName!;
            // T3MP adds this marker to cached prefabs. UI-only previews never
            // acquire a simulation slot, so its callbacks do nothing.
            if (name == "T3MP.ActiveInHierarchySentinel" && (int)component.GetType().GetField("SlotIndex", All)!.GetValue(component)! < 0) continue;
            if (name is "Timberborn.Timbermesh.TimbermeshDescription" or "Timberborn.BaseComponentSystem.ComponentCache" or
                "Timberborn.BaseComponentSystem.BaseComponentUnityAdapter" or "Timberborn.BaseComponentSystem.BaseComponentUpdateUnityAdapter" or
                "Timberborn.BaseComponentSystem.BaseComponentLateUpdateUnityAdapter") continue;
            return false;
        }
        return true;
    }
    private static void BeforeRead(PlantablePreviewService __instance, Vector3Int coordinates) => Ensure(__instance, coordinates);
    private static void Ensure(PlantablePreviewService service, Vector3Int coordinate)
    {
        // Preserve native indexing exceptions and already materialized identity.
        if (service._previews[coordinate.x, coordinate.y, coordinate.z] != null ||
            !Sessions.TryGetValue(service, out var session) || !session.Pending.TryGetValue(coordinate, out var pending)) return;
        if (session.Creating != null) throw new InvalidOperationException("Reentrant planting-preview materialization");
        session.Creating = pending; session.CreatingAt = coordinate;
        try
        {
            service.CreatePreview(pending.Resource, coordinate).Hide();
            session.Pending.Remove(coordinate); session.Materialized++;
        }
        finally { session.Creating = null; }
    }
    private static void BeforeCreate(PlantablePreviewService __instance, Vector3Int coords)
    {
        if (!Sessions.TryGetValue(__instance, out var session)) return;
        if (session.Creating != null && session.CreatingAt == coords) return;
        session.Pending.Remove(coords);
    }
    private static void AfterCreate(PlantablePreviewService __instance, Vector3Int coords, PlantablePreview __result)
    {
        if (!Sessions.TryGetValue(__instance, out var session)) return;
        if (session.Sequences.TryGetValue(coords, out var old)) session.Created.Remove(old);
        var sequence = session.Creating != null && session.CreatingAt == coords ? session.Creating.Sequence : session.Next++;
        session.Sequences[coords] = sequence; session.Created.Add(sequence, __result);
        var rank = session.Created.IndexOfKey(sequence);
        if (rank + 1 < session.Created.Count)
        {
            var next = session.Created.Values[rank + 1].Transform;
            __result.Transform.SetSiblingIndex(next.GetSiblingIndex());
        }
    }
    private static void BeforeUnset(PlantablePreviewService __instance, PlantingCoordinatesUnsetEvent plantingCoordinatesUnsetEvent)
    {
        if (!Sessions.TryGetValue(__instance, out var session)) return;
        var coordinate = plantingCoordinatesUnsetEvent.Coordinates;
        session.Pending.Remove(coordinate);
        if (session.Sequences.TryGetValue(coordinate, out var sequence))
        { session.Created.Remove(sequence); session.Sequences.Remove(coordinate); }
    }
    internal static void Report(string phase)
    {
        if (!_enabled) return;
        foreach (var weak in Services)
            if (weak.TryGetTarget(out var service) && Sessions.TryGetValue(service, out var session))
                Debug.Log($"[T3MPPLANTPREVIEW] phase={phase} deferred={session.Deferred} materialized={session.Materialized} pending={session.Pending.Count} prototypes={session.Prototypes} fallbacks={session.Fallbacks}");
    }
    internal static void Validate()
    {
        if (!_validate) return;
        var tested = 0;
        foreach (var weak in Services)
        {
            if (!weak.TryGetTarget(out var service) || !Sessions.TryGetValue(service, out var session)) continue;
            if (session.Deferred == 0) throw new Exception("Plantable preview deferral not exercised");
            var randomBefore = UnityEngine.Random.state;
            var groups = session.Pending.ToArray().GroupBy(p => p.Value.Resource).SelectMany(g => g.Take(4)).ToArray();
            foreach (var item in groups)
            {
                var expected = service._plantablePreviewFactory.CreatePreview(item.Value.Resource, item.Key); expected.Hide();
                try
                {
                    var actual = service.GetPreview(item.Key);
                    if (!ReferenceEquals(actual, service.GetPreview(item.Key)) || actual.IsShown) throw new Exception("Plantable preview read state differs");
                    LazyBuildingPreviewExperiment.CompareVisualClones(actual, expected);
                    service.ShowPreview(item.Key); expected.Show();
                    LazyBuildingPreviewExperiment.CompareVisualClones(actual, expected);
                    service.HidePreview(item.Key); expected.Hide();
                    LazyBuildingPreviewExperiment.CompareVisualClones(actual, expected);
                    tested++;
                }
                finally { UnityEngine.Object.Destroy(expected.GameObject); }
            }
            foreach (var item in session.Pending.Take(8).ToArray())
            {
                var made = session.Materialized;
                service.OnPlantingCoordinatesUnset(new PlantingCoordinatesUnsetEvent(item.Key, item.Value.Resource));
                if (service.GetPreview(item.Key) != null || session.Materialized != made) throw new Exception("Unset materialized a pending preview");
                service.OnPlantingCoordinatesSet(new PlantingCoordinatesSetEvent(item.Key, item.Value.Resource));
                if (service.GetPreview(item.Key) == null) throw new Exception("Reset did not create native preview");
                service.HidePreview(item.Key); tested++;
            }
            service.HidePreviews();
            var previousSibling = -1;
            foreach (var preview in session.Created.Values)
            {
                var sibling = preview.Transform.GetSiblingIndex();
                if (sibling <= previousSibling) throw new Exception("Plantable preview creation order differs");
                previousSibling = sibling;
            }
            if (!randomBefore.Equals(UnityEngine.Random.state)) throw new Exception("Plantable preview first use changed random state");
        }
        if (tested == 0) throw new Exception("No plantable preview first-use cases");
        Debug.Log("[T3MPPLANTPREVIEW] VALIDATE PASS cases=" + tested + " (native geometry/material/visibility, identity, pending unset/set, sibling order, random stream)");
        Report("validated");
    }
}
