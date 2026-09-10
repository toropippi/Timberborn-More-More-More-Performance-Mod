using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.BlockSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Load-only predicate replacement. Deliver every event in native order and
// retain the original update/callback body; index immutable changed blocks.
internal static class WaterColumnIndexExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Func<object, Vector3Int> _coordinates = null!;
    private static Func<object, BlockObject> _blockObject = null!;
    private static bool _validate;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static PositionedBlocks? _last;
    [ThreadStatic] private static Dictionary<long, int>? _columns;
    [ThreadStatic] private static long _calls, _single, _builds, _scanned, _positive, _validated;
    private struct Check { internal bool Active, Expected; }
    private static Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestWaterColumnIndex")) return;
        _validate = args.Contains("-t3mpTestWaterColumnValidate");
        var type = Find("Timberborn.WaterBuildings.WaterInputPipeCoordinates") ?? Find("Timberborn.WaterBuildings.WaterInputCoordinates")!;
        _coordinates = Getter<Vector3Int>(type, type.GetProperty("Coordinates", All)!.GetMethod!, null);
        _blockObject = Getter<BlockObject>(type, null, type.GetField("_blockObject", All)!);
        var ht = Find("HarmonyLib.Harmony")!; var hm = Find("HarmonyLib.HarmonyMethod")!;
        var harmony = Activator.CreateInstance(ht, "t3mp.test.water-column-index");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(WaterColumnIndexExperiment).GetMethod(name, All))!;
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService")!.GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        patch.Invoke(harmony, new object?[] { type.GetMethod("ShouldUpdate", All), Hook(nameof(ShouldUpdate)), Hook(nameof(AfterShouldUpdate)), null, null });
        Debug.Log("[T3MPWATERCOLUMN] installed type=" + type.FullName + " validate=" + _validate);
    }
    private static Func<object, T> Getter<T>(Type type, MethodInfo? method, FieldInfo? field)
    {
        var dynamic = new DynamicMethod("T3MPWaterColumnRead", typeof(T), new[] { typeof(object) }, typeof(WaterColumnIndexExperiment).Module, true);
        var il = dynamic.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, type);
        if (method != null) il.Emit(OpCodes.Call, method); else il.Emit(OpCodes.Ldfld, field!);
        il.Emit(OpCodes.Ret);
        return (Func<object, T>)dynamic.CreateDelegate(typeof(Func<object, T>));
    }
    private static void Begin()
    {
        if (_depth++ != 0) return;
        _last = null; _columns = new Dictionary<long, int>();
        _calls = _single = _builds = _scanned = _positive = _validated = 0;
    }
    private static void End()
    {
        if (--_depth != 0) return;
        _last = null; _columns = null;
        Debug.Log($"[T3MPWATERCOLUMN] calls={_calls} single={_single} indexBuilds={_builds} sourceBlocksScanned={_scanned} positive={_positive} validated={_validated}");
    }
    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;
    private static bool TryMinimumZ(PositionedBlocks blocks, Vector3Int query, out int minimum)
    {
        var all = blocks.GetAllBlocks();
        if (all.Length == 1)
        {
            _single++;
            var coordinate = all[0].Coordinates;
            minimum = coordinate.z;
            return coordinate.x == query.x && coordinate.y == query.y;
        }
        if (!ReferenceEquals(_last, blocks))
        {
            // Reuse storage for the most recently queried immutable shape.
            // Reentrant events can replace it; identity checks rebuild as needed.
            _columns!.Clear();
            for (var i = 0; i < all.Length; i++)
            {
                var coordinate = all[i].Coordinates;
                var key = Key(coordinate.x, coordinate.y);
                if (!_columns.TryGetValue(key, out var previous) || coordinate.z < previous) _columns[key] = coordinate.z;
            }
            _last = blocks; _builds++; _scanned += all.Length;
        }
        return _columns!.TryGetValue(Key(query.x, query.y), out minimum);
    }
    private static bool ShouldUpdate(object __instance, PositionedBlocks changedBlocks, ref bool __result, out Check __state)
    {
        __state = default;
        if (_depth == 0 || changedBlocks == null) return true;
        _calls++;
        // Preserve the empty input's lack of receiver/block-object reads.
        var expected = changedBlocks.GetAllBlocks().Length != 0 &&
            TryMinimumZ(changedBlocks, _coordinates(__instance), out var minimum) && minimum <= _blockObject(__instance).CoordinatesAtBaseZ.z;
        if (expected) _positive++;
        if (_validate) { __state = new Check { Active = true, Expected = expected }; return true; }
        __result = expected;
        return false;
    }
    private static void AfterShouldUpdate(bool __result, Check __state)
    {
        if (!__state.Active) return;
        if (__result != __state.Expected) throw new InvalidOperationException("Water column index differs from native ShouldUpdate");
        _validated++;
    }

    internal static void Validate()
    {
        if (!_validate) return;
        var factory = typeof(PositionedBlocks).GetConstructor(All, null, new[] { typeof(ImmutableArray<Block>) }, null)!;
        var random = new System.Random(39171);
        long cases = 0;
        Begin();
        try
        {
            for (var shape = 0; shape < 256; shape++)
            {
                var coordinates = new List<Vector3Int>();
                for (var i = 0; i < shape % 33; i++)
                    coordinates.Add(new Vector3Int(random.Next(-4, 5), random.Next(-4, 5), random.Next(-32, 33)));
                if (shape > 0 && shape % 7 == 0)
                    coordinates.AddRange(new[] { new Vector3Int(int.MinValue, int.MaxValue, int.MinValue), new Vector3Int(int.MinValue, int.MaxValue, int.MaxValue) });
                var blocks = (PositionedBlocks)factory.Invoke(new object[] { coordinates.Select(Block.FullFrom).ToImmutableArray() });
                foreach (var query in coordinates.Concat(new[] { new Vector3Int(100, -100, 0), new Vector3Int(0, 0, 0) }))
                foreach (var height in new[] { int.MinValue, -32, -1, 0, 1, 32, int.MaxValue })
                {
                    var expected = blocks.GetAllCoordinates().Any(c => c.x == query.x && c.y == query.y && c.z <= height);
                    var actual = TryMinimumZ(blocks, query, out var minimum) && minimum <= height;
                    if (actual != expected) throw new InvalidOperationException("Synthetic water column mismatch");
                    cases++;
                }
            }
        }
        finally { End(); }
        Debug.Log("[T3MPWATERCOLUMN] VALIDATE PASS cases=" + cases + " (empty, duplicate columns, negative/extreme coordinates, height boundaries)");
    }
}
