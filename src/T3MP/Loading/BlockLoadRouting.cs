using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.BlockObstacles;
using Timberborn.BlockSystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Optional provider for LoadEventRouter. Native PostNow still invokes handlers
// and owns pending registrations, nesting and exception wrapping.
internal static class BlockLoadRouting
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Owner = "t3mp.load.block-routing";
    private sealed class Marker { internal object Subscriber = null!; internal byte Kind; }
    private sealed class Session
    {
        internal SubscriptionRegistry Registry = null!;
        internal readonly Dictionary<Type, Plan> Plans = new Dictionary<Type, Plan>();
        internal long Events, Rebuilds, IndexBuilds, Skipped, Delivered, Unknown, Validated, FallbackRuns;
    }
    private sealed class Plan
    {
        internal Dictionary<object, Action<object>> Dictionary = null!;
        internal int Version;
        internal Subscription[] Items = null!;
        internal byte[] Kinds = null!;
        internal Run?[] Runs = null!;
    }
    private sealed class Run
    {
        internal int Start, End, Epoch = -1;
        internal Dictionary<long, List<int>>? Columns;
    }
    private static readonly ConditionalWeakTable<Action<object>, Marker> Markers = new ConditionalWeakTable<Action<object>, Marker>();
    private static Type _water = null!, _harmony = null!;
    private static MethodInfo[] _waterHandlers = null!, _layeredHandlers = null!, _guardMethods = null!;
    private static Func<object, Vector3Int> _waterCoordinates = null!;
    private static Func<Dictionary<object, Action<object>>, int> _version = null!;
    private static int _epoch;
    private static bool _validate;
    private static bool _baseline = false;
    private static Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
    private static int Epoch => Volatile.Read(ref _epoch);

    internal static bool Installed { get; private set; }
    private static bool Reviewed() => LoadCompatibility.Reviewed(
        "Timberborn.BlockObstacles|825233ec-0cd7-4d95-b79d-3ab47b8c4333",
        "Timberborn.BlockSystem|08ba0380-b21b-4e84-999e-011c1cc0b67d",
        "Timberborn.WaterBuildings|8efae8cc-faad-45bf-8be5-2f90d9e40435",
        "Timberborn.SingletonSystem|962512a9-30fb-4e42-b29f-b0115c9e9015",
        "Timberborn.Common|88d60edf-d568-470b-af44-77abc48c8bd0",
        "mscorlib|816f70bc-2a9c-4332-b0bd-119906a8d40e");

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        if (Installed || args.Contains("-t3mpTestBlockRoutingBaseline")) return;
        if (!Reviewed()) { Debug.Log("[T3MPBLOCKROUTING] native fallback: unreviewed modules"); return; }
        // The four hooks are one provider; claim them only as a complete set.
        if (LoadEventRouter.AdditionalRememberHandler != null || LoadEventRouter.AdditionalCreateSession != null ||
            LoadEventRouter.AdditionalGetSubscriptions != null || LoadEventRouter.AdditionalEndSession != null)
        { Debug.Log("[T3MPBLOCKROUTING] native fallback: routing provider already attached"); return; }
        _harmony = Find("HarmonyLib.Harmony")!; var hm = Find("HarmonyLib.HarmonyMethod")!;
        var harmony = Activator.CreateInstance(_harmony, Owner);
        try
        {
            _validate = args.Contains("-t3mpTestProductionBlockValidate");
            _water = Find("Timberborn.WaterBuildings.WaterInputPipeCoordinates") ?? Find("Timberborn.WaterBuildings.WaterInputCoordinates")!;
            _waterHandlers = new[] { _water.GetMethod("OnBlockObjectSetEvent", All)!, _water.GetMethod("OnBlockObjectUnsetEvent", All)! };
            _layeredHandlers = new[] { typeof(LayeredBlockObstacle).GetMethod("OnBlockObjectSet", All)!, typeof(LayeredBlockObstacle).GetMethod("OnBlockObjectUnset", All)! };
            _waterCoordinates = Getter<object, Vector3Int>(_water, _water.GetProperty("Coordinates", All)!.GetMethod, null);
            var dictionary = typeof(Dictionary<object, Action<object>>);
            _version = Getter<Dictionary<object, Action<object>>, int>(dictionary, null, dictionary.GetField("_version", All) ?? dictionary.GetField("version", All)!);
            _guardMethods = _waterHandlers.Concat(_layeredHandlers).Concat(new[] {
                _water.GetMethod("ShouldUpdate", All)!, _water.GetProperty("Coordinates", All)!.GetMethod!,
                typeof(LayeredBlockObstacle).GetMethod("UpdateMaxOccupancyRangeIfCoordinatesMatch", All, null, new[] { typeof(IEnumerable<Vector3Int>) }, null)!,
                typeof(LayeredBlockObstacle).GetMethod("UpdateMaxOccupancyRangeIfCoordinatesMatch", All, null, new[] { typeof(Vector2Int) }, null)!,
                typeof(BlockOccupationLayer).GetMethod("Contains", All)!, typeof(BlockOccupier).GetProperty("BlockObject", All)!.GetMethod!,
                typeof(BlockOccupier).GetProperty("BlockObject", All)!.SetMethod!,
                typeof(BlockObjectSetEvent).GetProperty("BlockObject", All)!.GetMethod!,
                typeof(BlockObjectUnsetEvent).GetProperty("BlockObject", All)!.GetMethod!,
                typeof(BlockObject).GetProperty("Coordinates", All)!.GetMethod!, typeof(BlockObject).GetProperty("PositionedBlocks", All)!.GetMethod!,
                typeof(PositionedBlocks).GetMethod("GetAllCoordinates", All)!, typeof(PositionedBlocks).GetMethod("GetAllBlocks", All)!,
                typeof(Block).GetProperty("Coordinates", All)!.GetMethod!,
                _water.GetProperty("Coordinates", All)!.SetMethod!,
                typeof(BlockOccupier).GetProperty("BlockObject", All)!.SetMethod!,
                typeof(BlockObject).GetProperty("Coordinates", All)!.SetMethod!,
                typeof(BlockObject).GetProperty("PositionedBlocks", All)!.SetMethod!,
                typeof(BlockOccupationLayer).GetMethod("AddBlockOccupier", All)!,
                typeof(LayeredBlockObstacle).GetMethod("CreateBlockOccupationLayers", All)!,
                typeof(LayeredBlockObstacle).GetMethod("RemoveBlockOccupationLayers", All)!
            }).Concat(typeof(Timberborn.Common.VectorExtensions).GetMethods(All).Where(m => m.Name == "XY")).Distinct().ToArray();
            var patch = _harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            var hook = Activator.CreateInstance(hm, typeof(BlockLoadRouting).GetMethod(nameof(Changed), All));
            foreach (var method in new[] {
                _water.GetProperty("Coordinates", All)!.SetMethod!,
                typeof(BlockOccupier).GetProperty("BlockObject", All)!.SetMethod!,
                typeof(BlockObject).GetProperty("Coordinates", All)!.SetMethod!,
                typeof(BlockObject).GetProperty("PositionedBlocks", All)!.SetMethod!,
                typeof(BlockOccupationLayer).GetMethod("AddBlockOccupier", All)!
            }) patch.Invoke(harmony, new object?[] { method, null, hook, null, null });
            foreach (var name in new[] { "CreateBlockOccupationLayers", "RemoveBlockOccupationLayers" })
                patch.Invoke(harmony, new object?[] { typeof(LayeredBlockObstacle).GetMethod(name, All), hook, null, null, hook });
            LoadEventRouter.AdditionalRememberHandler = Remember;
            LoadEventRouter.AdditionalCreateSession = CreateSession;
            LoadEventRouter.AdditionalGetSubscriptions = GetSubscriptions;
            LoadEventRouter.AdditionalEndSession = Report;
            Installed = true;
            Debug.Log("[T3MPBLOCKROUTING] installed validate=" + _validate);
        }
        catch (Exception e)
        {
            _harmony.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
            LoadEventRouter.AdditionalRememberHandler = null;
            LoadEventRouter.AdditionalCreateSession = null;
            LoadEventRouter.AdditionalGetSubscriptions = null;
            LoadEventRouter.AdditionalEndSession = null;
            Debug.LogWarning("[T3MPBLOCKROUTING] disabled: " + e.GetBaseException().Message);
        }
    }
    private static Func<T, R> Getter<T, R>(Type owner, MethodInfo? method, FieldInfo? field)
    {
        var dynamic = new DynamicMethod("T3MPBlockRouteRead", typeof(R), new[] { typeof(T) }, typeof(BlockLoadRouting).Module, true);
        var il = dynamic.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
        if (typeof(T) != owner) il.Emit(OpCodes.Castclass, owner);
        if (method != null) il.Emit(OpCodes.Call, method); else il.Emit(OpCodes.Ldfld, field!);
        il.Emit(OpCodes.Ret);
        return (Func<T, R>)dynamic.CreateDelegate(typeof(Func<T, R>));
    }
    private static void Changed() => Interlocked.Increment(ref _epoch);
    private static void Remember(Action<object> action, object subscriber, MethodInfo method)
    {
        var kind = subscriber.GetType() == _water && _waterHandlers.Contains(method) ? (byte)1 :
            subscriber.GetType() == typeof(LayeredBlockObstacle) && _layeredHandlers.Contains(method) ? (byte)2 : (byte)0;
        if (kind != 0) Markers.Add(action, new Marker { Subscriber = subscriber, Kind = kind });
    }
    private static object? CreateSession(SubscriptionRegistry registry)
    {
        if (_baseline) return null;
        if (!Reviewed() || !LoadCompatibility.Unmodified(_guardMethods, Owner)) return null;
        return new Session { Registry = registry };
    }
    private static IEnumerable<Subscription>? GetSubscriptions(object context, object eventObject)
    {
        var type = eventObject.GetType();
        if (type != typeof(BlockObjectSetEvent) && type != typeof(BlockObjectUnsetEvent)) return null;
        var session = (Session)context;
        if (!session.Registry._subscriptions.TryGetValue(type, out var source)) return null;
        if (!session.Plans.TryGetValue(type, out var plan) || !ReferenceEquals(plan.Dictionary, source) || plan.Version != _version(source))
        { session.Plans[type] = plan = Build(source); session.Rebuilds++; }
        session.Events++;
        return Enumerate(session, plan, eventObject);
    }
    private static Plan Build(Dictionary<object, Action<object>> source)
    {
        var items = source.Select(p => new Subscription(p.Key, p.Value)).ToArray();
        var plan = new Plan { Dictionary = source, Version = _version(source), Items = items, Kinds = new byte[items.Length], Runs = new Run?[items.Length] };
        for (var i = 0; i < items.Length; i++)
            if (Markers.TryGetValue(items[i].Action, out var marker) && ReferenceEquals(marker.Subscriber, items[i].Subscriber)) plan.Kinds[i] = marker.Kind;
        for (var i = 0; i < items.Length;)
        {
            if (plan.Kinds[i] == 0) { i++; continue; }
            var end = i + 1;
            while (end < items.Length && plan.Kinds[end] != 0) end++;
            plan.Runs[i] = new Run { Start = i, End = end }; i = end;
        }
        return plan;
    }
    private static long Key(Vector3Int coordinate) => ((long)coordinate.x << 32) | (uint)coordinate.y;
    private static void Add(Dictionary<long, List<int>> columns, Vector3Int coordinate, int index)
    {
        var key = Key(coordinate);
        if (!columns.TryGetValue(key, out var list)) columns.Add(key, list = new List<int>());
        if (list.Count == 0 || list[list.Count - 1] != index) list.Add(index);
    }
    private static Dictionary<long, List<int>>? BuildColumns(Plan plan, Run run)
    {
        var columns = new Dictionary<long, List<int>>();
        for (var i = run.Start; i < run.End; i++)
        {
            if (plan.Kinds[i] == 1) { Add(columns, _waterCoordinates(plan.Items[i].Subscriber), i); continue; }
            var layers = ((LayeredBlockObstacle)plan.Items[i].Subscriber)._blockOccupationLayers;
            if (layers == null || layers.Count == 0 || layers[0] == null || layers[0]._blockOccupiers == null) return null;
            foreach (var occupier in layers[0]._blockOccupiers)
            {
                if (ReferenceEquals(occupier, null) || ReferenceEquals(occupier.BlockObject, null)) return null;
                Add(columns, occupier.BlockObject.Coordinates, i);
            }
        }
        return columns;
    }
    private static PositionedBlocks? Shape(object eventObject)
    {
        var block = eventObject is BlockObjectSetEvent set ? set.BlockObject : ((BlockObjectUnsetEvent)eventObject).BlockObject;
        return ReferenceEquals(block, null) ? null : block.PositionedBlocks;
    }
    private static List<int>? Query(Session session, Plan plan, Run run, object eventObject, int next, int epoch, out bool fallback)
    {
        fallback = true;
        var shape = Shape(eventObject);
        if (shape == null || shape.GetAllBlocks().IsDefault) return null;
        if (run.Epoch != epoch)
        { run.Columns = BuildColumns(plan, run); run.Epoch = epoch; session.IndexBuilds++; }
        if (run.Columns == null) return null;
        List<int>? selected = null;
        var all = shape.GetAllBlocks();
        for (var i = 0; i < all.Length; i++)
            if (run.Columns.TryGetValue(Key(all[i].Coordinates), out var bucket))
                foreach (var index in bucket)
                    if (index >= next) (selected ??= new List<int>()).Add(index);
        selected?.Sort();
        fallback = false;
        return selected;
    }
    private static void CheckVersion(Plan plan)
    {
        if (_version(plan.Dictionary) != plan.Version) throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
    }
    private static void ValidateSkipped(Session session, Plan plan, object eventObject, int start, int end)
    {
        if (!_validate) return;
        var shape = Shape(eventObject)!;
        for (var i = start; i < end; i++)
        {
            if (plan.Kinds[i] == 1)
            {
                var coordinate = _waterCoordinates(plan.Items[i].Subscriber);
                foreach (var changed in shape.GetAllCoordinates())
                    if (Key(changed) == Key(coordinate)) throw new Exception("Skipped a water receiver's column");
            }
            else
            {
                var layered = (LayeredBlockObstacle)plan.Items[i].Subscriber;
                if (layered.Enabled)
                    foreach (var changed in shape.GetAllCoordinates())
                        if (layered._blockOccupationLayers.First().Contains(new Vector2Int(changed.x, changed.y))) throw new Exception("Skipped an intersecting obstacle");
            }
            session.Validated++;
        }
    }
    private static IEnumerable<Subscription> Enumerate(Session session, Plan plan, object eventObject)
    {
        var cursor = 0;
        while (cursor < plan.Items.Length)
        {
            CheckVersion(plan);
            var run = plan.Runs[cursor];
            if (run == null) { session.Unknown++; yield return plan.Items[cursor++]; continue; }
            var next = run.Start;
            while (next < run.End)
            {
                CheckVersion(plan);
                var epoch = Epoch;
                var selected = Query(session, plan, run, eventObject, next, epoch, out var fallback);
                // Unexpected concurrent changes use native delivery for the rest
                // of this run rather than retrying indefinitely.
                if (fallback || Epoch != epoch)
                {
                    session.FallbackRuns++;
                    while (next < run.End) { CheckVersion(plan); session.Delivered++; yield return plan.Items[next++]; }
                    break;
                }
                if (selected != null)
                    foreach (var index in selected)
                    {
                        if (index < next) continue; // duplicate XY in the source
                        if (Epoch != epoch) break;
                        CheckVersion(plan);
                        ValidateSkipped(session, plan, eventObject, next, index);
                        session.Skipped += index - next;
                        next = index + 1; session.Delivered++;
                        yield return plan.Items[index];
                    }
                CheckVersion(plan);
                if (Epoch != epoch) continue; // a delivered callback moved a receiver
                ValidateSkipped(session, plan, eventObject, next, run.End);
                session.Skipped += run.End - next; next = run.End;
            }
            cursor = run.End;
        }
        CheckVersion(plan); // native MoveNext also checks after the last callback
    }
    private static void Report(object context)
    {
        var s = (Session)context;
        Debug.Log($"[T3MPBLOCKROUTING] events={s.Events} plans={s.Rebuilds} indexBuilds={s.IndexBuilds} skipped={s.Skipped} delivered={s.Delivered} unknown={s.Unknown} validated={s.Validated} fallbackRuns={s.FallbackRuns}");
    }
}
