using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Timberborn.BlockSystem;
using Timberborn.Navigation;
using UnityEngine;

namespace T3MP.Loading;

// Keep native object ownership/queueing and reuse only immutable local shapes.
internal static class NavigationShapePlans
{
    private const BindingFlags All = LoadPatchBridge.All;
    private sealed class Plan { internal NavMeshChangeSpecification[] Changes = null!; internal Vector3Int[] Restricted = null!; }
    private struct Scope { internal object Owner; internal (object, object?, object, object, int, GeometryKey) Key; internal Plan? Expected; internal bool Capture; }
    private static readonly Dictionary<object, Dictionary<(object, object?, object, object, int, GeometryKey), Plan>> Plans = new();
    private static FieldInfo _groups = null!;
    private static int _depth;
    private static bool _validate;
    private static long _hits, _builds, _checks;
    internal static bool Installed { get; private set; }
    private static bool _production, _compatible;
    private static string _owner = "";
    private static Type _updater = null!;
    internal static void Install(string owner = "t3mp.test.nav-shape-plans", bool production = false)
    {
        var args = Environment.GetCommandLineArgs();
        if (Installed || LoadCompatibility.MainInstalled(typeof(NavigationShapePlans))) return;
        _production = production; _owner = owner;
        if (production && (!Reviewed() || args.Contains("-t3mpTestNavShapeBaseline")))
        { Debug.Log("[T3MPNAVSHAPE] native fallback: baseline or unreviewed game modules"); return; }
        _validate = args.Contains("-t3mpTestNavShapeValidate");
        if (!production && !args.Contains("-t3mpTestNavShapePlans") && !_validate) return;
        var type = _updater = LoadPatchBridge.Find("Timberborn.BlockSystemNavigation.NavMeshObjectUpdater");
        _groups = type.GetField("_navMeshGroupService", All)!;
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony"); var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var h = Activator.CreateInstance(ht, owner);
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        try
        {
            object? Hook(string? name)
            {
                if (name == null) return null;
                var hook = Activator.CreateInstance(hm, typeof(NavigationShapePlans).GetMethod(name, All));
                if (name == nameof(End)) hm.GetField("priority")!.SetValue(hook, 900);
                return hook;
            }
            patch.Invoke(h, new object?[] { type.GetMethod("Update", All), Hook(nameof(Before)), Hook(nameof(After)), null, null });
            patch.Invoke(h, new object?[] { LoadPatchBridge.Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
            Installed = true;
            Debug.Log("[T3MPNAVSHAPE] installed validate=" + _validate);
        }
        catch (Exception e)
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(h, new object[] { owner });
            Debug.LogWarning("[T3MPNAVSHAPE] disabled: " + e.GetBaseException().Message);
        }
    }
    private static int Groups(object owner) => ((NavMeshGroupService)_groups.GetValue(owner)!)._groupIds.Count;
    private static bool Reviewed() => LoadCompatibility.Reviewed(
        "Timberborn.BlockSystemNavigation|096d1a2b-1134-4849-b128-c75707ff8520",
        "Timberborn.BlockSystem|08ba0380-b21b-4e84-999e-011c1cc0b67d",
        "Timberborn.Navigation|b848ebf8-28b6-4c17-ab4f-f91582085afa",
        "Timberborn.Common|88d60edf-d568-470b-af44-77abc48c8bd0",
        "Timberborn.Coordinates|9efec216-4ae7-43da-9563-c8fc7537bcf1",
        "Timberborn.BaseComponentSystem|c0f7b920-5694-4612-bdd5-b736144e1f02",
        "UnityEngine.CoreModule|61dee272-fd45-47db-9c13-40fe43fbdc1a");
    private static void Begin()
    {
        if (_depth++ != 0) return;
        Plans.Clear(); _hits = _builds = _checks = 0;
        _compatible = !_production || Reviewed() && LoadCompatibility.Unmodified(
            _updater.Assembly.GetTypes().Concat(new[] { typeof(NavMeshObject), typeof(NavMeshGroupService), typeof(BlockObject), typeof(BlockObjectSpec), typeof(Blocks), typeof(Block), typeof(PositionedBlocks), typeof(PositionedEntrance) })
                .SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly)), _owner,
            (method, owner) => owner == "t3mp.test.nav-removal-view" &&
                Environment.GetCommandLineArgs().Contains("-t3mpTestNavRemovalView") && method.DeclaringType == typeof(NavMeshObject) &&
                new[] { "Reset", "AddEdge", "BlockEdge", "EnqueueRemoveFromRegularNavMesh", "EnqueueRemoveFromPreviewNavMesh", "RemoveFromPreviewNavMesh" }.Contains(method.Name) ||
                // These observers only increment the receiver-index generation;
                // they preserve the setter input, result and exception behavior.
                (owner == "t3mp.load.block-routing" || owner == "t3mp.test.block-event-routing" && Environment.GetCommandLineArgs().Contains("-t3mpTestBlockEventRouting")) &&
                method.DeclaringType == typeof(BlockObject) && (method.Name == "set_Coordinates" || method.Name == "set_PositionedBlocks"));
        if (!_compatible) Debug.Log("[T3MPNAVSHAPE] native fallback: modified shape methods");
    }
    private static void End() { if (--_depth == 0) { Debug.Log($"[T3MPNAVSHAPE] hits={_hits} builds={_builds} checked={_checks}"); Plans.Clear(); } }
    private static NavMeshEdge Shift(NavMeshEdge e, Vector3Int delta) => NavMeshEdge.CreateGrouped(e.Start + delta, e.End + delta, e.GroupId, e.IsRoad, e.Cost);
    // Compare the actual immutable positioned blocks directly. Hash collisions
    // still perform full equality; no geometry is identified by a hash alone.
    // This also distinguishes OccupyAllBelow at different absolute heights.
    private readonly struct GeometryKey : IEquatable<GeometryKey>
    {
        private readonly ImmutableArray<Block> _blocks;
        private readonly Vector3Int _origin, _size, _entrance, _door;
        private readonly bool _hasEntrance;
        private readonly int _hash;
        internal GeometryKey(BlockObject block)
        {
            _blocks = block.PositionedBlocks.GetAllBlocks(); _origin = block.Coordinates;
            _size = block.Blocks.Size; _hasEntrance = block.HasEntrance;
            _entrance = _hasEntrance ? block.PositionedEntrance.Coordinates - _origin : default;
            _door = _hasEntrance ? block.PositionedEntrance.DoorstepCoordinates - _origin : default;
            unchecked
            {
                var h = _size.GetHashCode();
                foreach (var b in _blocks)
                {
                    h = h * 31 + (b.Coordinates - _origin).GetHashCode();
                    h = h * 31 + b.Occupation.GetHashCode(); h = h * 31 + b.Stackable.GetHashCode();
                }
                _hash = ((h * 31 + _hasEntrance.GetHashCode()) * 31 + _entrance.GetHashCode()) * 31 + _door.GetHashCode();
            }
        }
        public bool Equals(GeometryKey other)
        {
            if (_hash != other._hash || _size != other._size || _hasEntrance != other._hasEntrance ||
                _entrance != other._entrance || _door != other._door || _blocks.Length != other._blocks.Length) return false;
            for (var i = 0; i < _blocks.Length; i++)
            {
                var a = _blocks[i]; var b = other._blocks[i];
                if (a.Coordinates - _origin != b.Coordinates - other._origin || a.Occupation != b.Occupation || a.Stackable != b.Stackable) return false;
            }
            return true;
        }
        public override bool Equals(object? other) => other is GeometryKey key && Equals(key);
        public override int GetHashCode() => _hash;
    }
    private static bool Before(object __instance, BlockObject blockObject, NavMeshObject navMeshObject, object blockObjectNavMeshSettingsSpec, out Scope __state)
    {
        __state = default;
        if (_depth == 0 || !_compatible || !blockObject.Positioned) return true;
        var key = ((object)blockObject.GetComponent<BlockObjectSpec>(), blockObjectNavMeshSettingsSpec, (object)blockObject.Orientation, (object)blockObject.FlipMode, Groups(__instance), new GeometryKey(blockObject));
        if (!Plans.TryGetValue(__instance, out var plans)) Plans.Add(__instance, plans = new());
        __state = new Scope { Owner = __instance, Key = key, Capture = true };
        if (!plans.TryGetValue(key, out var plan)) return true;
        _hits++;
        if (_validate) { __state.Expected = plan; return true; }
        __state.Capture = false;
        navMeshObject.Reset();
        var restrictionsAdded = false;
        foreach (var change in plan.Changes)
        {
            var edge = Shift(change.NavMeshEdge, blockObject.Coordinates);
            if (change.NavMeshChangeType == NavMeshChangeType.AddEdge) navMeshObject.AddEdge(edge);
            else if (change.NavMeshChangeType == NavMeshChangeType.BlockEdge)
            {
                if (!restrictionsAdded)
                {
                    foreach (var coordinates in plan.Restricted) navMeshObject.AddRestrictedCoordinates(coordinates + blockObject.Coordinates);
                    restrictionsAdded = true;
                }
                navMeshObject.BlockEdge(edge);
            }
            else throw new InvalidOperationException("Unexpected shape change type");
        }
        if (!restrictionsAdded)
            foreach (var coordinates in plan.Restricted) navMeshObject.AddRestrictedCoordinates(coordinates + blockObject.Coordinates);
        return false;
    }
    private static void After(BlockObject blockObject, NavMeshObject navMeshObject, Scope __state)
    {
        if (!__state.Capture) return;
        var delta = -blockObject.Coordinates;
        var actual = new Plan {
            Changes = navMeshObject._addingChanges.Select(c => new NavMeshChangeSpecification(Shift(c.NavMeshEdge, delta), c.NavMeshChangeType)).ToArray(),
            Restricted = navMeshObject._restrictedCoordinates.Select(c => c + delta).ToArray() };
        if (__state.Expected != null)
        {
            var expected = __state.Expected;
            if (!actual.Restricted.SequenceEqual(expected.Restricted) || actual.Changes.Length != expected.Changes.Length ||
                actual.Changes.Where((c, i) => c.NavMeshChangeType != expected.Changes[i].NavMeshChangeType || c.NavMeshEdge != expected.Changes[i].NavMeshEdge).Any())
                throw new InvalidOperationException("Navigation shape differs from ordered native records: " + blockObject.Name);
            _checks++;
        }
        else
        {
            // A group introduced partway through native Update can change
            // which earlier edges saw that group. Cache only stable inputs.
            var key = __state.Key;
            if (key.Item5 != Groups(__state.Owner)) return;
            Plans[__state.Owner][key] = actual; _builds++;
        }
    }
}
