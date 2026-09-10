using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Timberborn.BlockObstacles;
using Timberborn.BlockSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Load-only rejection of disjoint changed shapes. Positive/unsupported cases
// retain the complete native handler, including its short-circuit exceptions.
internal static class LayeredObstacleIndexExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static bool _validate;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static PositionedBlocks? _last;
    [ThreadStatic] private static HashSet<long>? _columns;
    [ThreadStatic] private static long _calls, _disabled, _rejected, _positive, _fallbacks, _builds, _scanned, _validated;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestLayeredObstacleIndex")) return;
        _validate = args.Contains("-t3mpTestLayeredObstacleValidate");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.layered-obstacle-index");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(LayeredObstacleIndexExperiment).GetMethod(name, All))!;
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        patch.Invoke(harmony, new object?[] { typeof(LayeredBlockObstacle).GetMethod("OnBlockObjectSet", All), Hook(nameof(OnSet)), null, null, null });
        patch.Invoke(harmony, new object?[] { typeof(LayeredBlockObstacle).GetMethod("OnBlockObjectUnset", All), Hook(nameof(OnUnset)), null, null, null });
        Debug.Log("[T3MPLAYEREDINDEX] installed validate=" + _validate);
    }
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _last = null; _columns = new HashSet<long>();
        _calls = _disabled = _rejected = _positive = _fallbacks = _builds = _scanned = _validated = 0;
    }
    private static void End()
    {
        if (--_depth != 0) return;
        _last = null; _columns = null;
        Debug.Log($"[T3MPLAYEREDINDEX] calls={_calls} disabled={_disabled} rejected={_rejected} positive={_positive} fallbacks={_fallbacks} indexBuilds={_builds} sourceBlocksScanned={_scanned} validated={_validated}");
    }
    private static bool OnSet(LayeredBlockObstacle __instance, BlockObjectSetEvent blockObjectSetEvent)
    {
        if (_depth == 0) return true;
        _calls++;
        if (!__instance.Enabled) { _disabled++; return false; }
        return ShouldRun(__instance, blockObjectSetEvent?.BlockObject);
    }
    private static bool OnUnset(LayeredBlockObstacle __instance, BlockObjectUnsetEvent blockObjectUnsetEvent)
    {
        if (_depth == 0) return true;
        _calls++;
        if (!__instance.Enabled) { _disabled++; return false; }
        return ShouldRun(__instance, blockObjectUnsetEvent?.BlockObject);
    }
    private static bool ShouldRun(LayeredBlockObstacle owner, BlockObject? changed)
    {
        if (ReferenceEquals(changed, null) || !TryIntersects(owner, changed.PositionedBlocks, out var matches))
        { _fallbacks++; return true; }
        if (_validate)
        {
            var expected = NativeMatch(owner, changed.PositionedBlocks);
            if (matches != expected) throw new InvalidOperationException("Layered obstacle projection differs from native predicate");
            _validated++;
        }
        if (matches) { _positive++; return true; }
        _rejected++;
        return false;
    }
    private static long Key(Vector3Int coordinate) => ((long)coordinate.x << 32) | (uint)coordinate.y;
    private static bool TryIntersects(LayeredBlockObstacle owner, PositionedBlocks? blocks, out bool matches)
    {
        matches = false;
        if (blocks == null) return false;
        var all = blocks.GetAllBlocks();
        if (all.IsDefault) return false;
        if (all.Length == 0) return true; // Native does not touch its layer list.
        var layers = owner._blockOccupationLayers;
        if (layers == null || layers.Count == 0 || layers[0] == null) return false;
        var occupiers = layers[0]._blockOccupiers;
        if (occupiers == null) return false;
        if (all.Length > 1 && !ReferenceEquals(_last, blocks))
        {
            _columns!.Clear();
            for (var i = 0; i < all.Length; i++) _columns.Add(Key(all[i].Coordinates));
            _last = blocks; _builds++; _scanned += all.Length;
        }
        for (var i = 0; i < occupiers.Count; i++)
        {
            // Never cache receiver positions: native placement/layer changes
            // made by an earlier callback must be observed at this handler.
            var occupier = occupiers[i];
            if (ReferenceEquals(occupier, null) || ReferenceEquals(occupier.BlockObject, null)) return false;
            var coordinate = occupier.BlockObject.Coordinates;
            if (all.Length == 1 ? Key(all[0].Coordinates) == Key(coordinate) : _columns!.Contains(Key(coordinate)))
            { matches = true; return true; }
        }
        return true;
    }
    private static bool NativeMatch(LayeredBlockObstacle owner, PositionedBlocks blocks)
    {
        foreach (var coordinate in blocks.GetAllCoordinates())
            if (owner._blockOccupationLayers.First().Contains(new Vector2Int(coordinate.x, coordinate.y))) return true;
        return false;
    }

    internal static void Validate()
    {
        if (!_validate) return;
        var shapeConstructor = typeof(PositionedBlocks).GetConstructor(All, null, new[] { typeof(ImmutableArray<Block>) }, null)!;
        PositionedBlocks Shape(IEnumerable<Vector3Int> coordinates) => (PositionedBlocks)shapeConstructor.Invoke(new object[] { coordinates.Select(Block.FullFrom).ToImmutableArray() });
        T New<T>() => (T)Activator.CreateInstance(typeof(T), All, null, new object?[typeof(T).GetConstructors(All).Single(c => !c.IsStatic).GetParameters().Length], null)!;
        void SetCoordinates(BlockObject block, Vector3Int coordinate) => typeof(BlockObject).GetProperty("Coordinates", All)!.SetValue(block, coordinate);
        var owner = New<LayeredBlockObstacle>();
        var layer = new BlockOccupationLayer(0);
        owner._blockOccupationLayers.Add(layer);
        var occupier = New<BlockOccupier>();
        occupier.BlockObject = New<BlockObject>();
        layer.AddBlockOccupier(occupier);
        var random = new System.Random(27393);
        var cases = 0;
        Begin();
        try
        {
            for (var shape = 0; shape < 256; shape++)
            {
                var coordinates = new List<Vector3Int>();
                for (var i = 0; i < shape % 33; i++) coordinates.Add(new Vector3Int(random.Next(-4, 5), random.Next(-4, 5), random.Next(-32, 33)));
                if (shape % 7 == 0) coordinates.AddRange(new[] { new Vector3Int(int.MinValue, int.MaxValue, -1), new Vector3Int(int.MinValue, int.MaxValue, 1) });
                var blocks = Shape(coordinates);
                foreach (var query in coordinates.Concat(new[] { new Vector3Int(100, -100, 0), Vector3Int.zero }))
                {
                    SetCoordinates(occupier.BlockObject, query);
                    if (!TryIntersects(owner, blocks, out var actual) || actual != NativeMatch(owner, blocks)) throw new Exception("Layered obstacle synthetic mismatch");
                    cases++;
                    // Replace the last-shape cache as a nested event would.
                    if (!TryIntersects(owner, Shape(new[] { Vector3Int.zero, Vector3Int.one }), out _)) throw new Exception("Layered obstacle nested-shape fixture failed");
                    if (!TryIntersects(owner, blocks, out actual) || actual != NativeMatch(owner, blocks)) throw new Exception("Layered obstacle resumed shape mismatch");
                    cases++;
                }
            }
            var empty = Shape(Array.Empty<Vector3Int>());
            var nonempty = Shape(new[] { Vector3Int.zero });
            var other = New<BlockOccupier>();
            other.BlockObject = New<BlockObject>();
            SetCoordinates(occupier.BlockObject, new Vector3Int(100, 100, 0));
            SetCoordinates(other.BlockObject, Vector3Int.zero);
            layer.AddBlockOccupier(other);
            if (!TryIntersects(owner, nonempty, out var secondMatch) || !secondMatch || !NativeMatch(owner, nonempty)) throw new Exception("Second footprint member was omitted");
            SetCoordinates(other.BlockObject, Vector3Int.one);
            if (!TryIntersects(owner, nonempty, out secondMatch) || secondMatch || NativeMatch(owner, nonempty)) throw new Exception("Moved footprint member was cached");
            var replacementLayer = new BlockOccupationLayer(1);
            SetCoordinates(other.BlockObject, Vector3Int.zero);
            replacementLayer.AddBlockOccupier(other);
            layer._blockOccupiers.Remove(other);
            owner._blockOccupationLayers.Add(replacementLayer);
            if (!TryIntersects(owner, nonempty, out var laterLayer) || laterLayer || NativeMatch(owner, nonempty)) throw new Exception("Later layer affected the predicate");
            owner._blockOccupationLayers[0] = replacementLayer;
            if (!TryIntersects(owner, nonempty, out var replacedLayer) || !replacedLayer || !NativeMatch(owner, nonempty)) throw new Exception("First layer replacement was cached");
            cases += 4;
            owner._blockOccupationLayers.Clear();
            if (!TryIntersects(owner, empty, out var emptyMatch) || emptyMatch || TryIntersects(owner, nonempty, out _)) throw new Exception("Empty-layer fallback differs");
            owner._blockOccupationLayers.Add(layer);
            layer._blockOccupiers.Clear();
            if (!TryIntersects(owner, nonempty, out var noOccupiers) || noOccupiers) throw new Exception("Empty footprint differs");
            layer._blockOccupiers.Add(null!);
            if (TryIntersects(owner, nonempty, out _)) throw new Exception("Null occupier was accepted");
            layer._blockOccupiers[0] = occupier;
            occupier.BlockObject = null!;
            if (TryIntersects(owner, nonempty, out _)) throw new Exception("Null block was accepted");
            cases += 4;
        }
        finally { End(); }
        Debug.Log("[T3MPLAYEREDINDEX] VALIDATE PASS cases=" + cases + " (empty, duplicate XY, extremes, receiver movement, nested shape, invalid footprint fallback)");
    }
}
