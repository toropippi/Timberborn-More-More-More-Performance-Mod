using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Timberborn.BlockObstacles;
using Timberborn.BlockSystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static partial class BlockEventRoutingExperiment
{
    public sealed class RoutingListener
    {
        public Action<object> Action = null!;
        [OnEvent] public void OnSet(BlockObjectSetEvent e) => Action(e);
        [OnEvent] public void OnUnset(BlockObjectUnsetEvent e) => Action(e);
    }
    private sealed class Receiver
    {
        internal object Instance = null!;
        internal Action<Vector3Int> Move = null!;
        internal Func<Vector3Int> Position = null!;
    }
    internal static void Validate()
    {
        if (!_validate) return;
        var cases = 0;
        foreach (var kind in new byte[] { 1, 2 })
        foreach (var unset in new[] { false, true })
        foreach (var scenario in new[] { "order", "move-within-run", "interleaved", "shape-change", "nested", "deferred-registration", "direct-removal", "missing-removal", "other-event", "last-callback-removal", "throw", "runtime", "empty-shape", "null-shape", "null-block" })
        {
            var native = RunCase(false, kind, unset, scenario);
            var routed = RunCase(true, kind, unset, scenario);
            if (native != routed) throw new Exception("Block event route differs: " + kind + "/" + unset + "/" + scenario + " native=" + native + " routed=" + routed);
            cases++;
        }
        Debug.Log("[T3MPBLOCKROUTING] VALIDATE PASS cases=" + cases + " (native EventBus order, moving receivers, changing shape, nested dispatch, deferred registration, dictionary invalidation, exceptions, runtime)");
        var foreignOwner = "t3mp.test.block-routing-foreign-validation";
        var harmony = Activator.CreateInstance(_harmony, foreignOwner)!;
        var hm = Find("HarmonyLib.HarmonyMethod")!;
        foreach (var target in new[] { _waterHandlers[0], _water.GetProperty("Coordinates", All)!.SetMethod! })
        {
            try
            {
                var patch = _harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
                patch.Invoke(harmony, new object?[] { target, Activator.CreateInstance(hm, typeof(BlockEventRoutingExperiment).GetMethod(nameof(ForeignValidationPrefix), All)), null, null, null });
                if (CreateSession(new SubscriptionRegistry()) != null) throw new Exception("Foreign water handler/mutator patch was accepted");
            }
            finally { _harmony.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { foreignOwner }); }
            if (CreateSession(new SubscriptionRegistry()) == null) throw new Exception("Block routing compatibility did not recover after unpatch");
        }
        Debug.Log("[T3MPBLOCKROUTING] foreign-handler/mutator fallback PASS cases=2");
    }
    private static void ForeignValidationPrefix() { }
    private static string RunCase(bool fast, byte kind, bool unset, string scenario)
    {
        // Detached managed fixtures: callbacks model the reviewed XY predicate
        // and mutate fixtures only. Real water/obstacle update bodies are covered
        // by live skipped-predicate checks and full-state comparisons.
        object New(Type type) => Activator.CreateInstance(type, All, null,
            new object?[type.GetConstructors(All).Single(c => !c.IsStatic).GetParameters().Length], null)!;
        void Set(object instance, string name, object value) => instance.GetType().GetProperty(name, All)!.SetValue(instance, value);
        Receiver Make(Vector3Int coordinate)
        {
            if (kind == 1)
            {
                var water = New(_water);
                var receiver = new Receiver { Instance = water, Move = c => Set(water, "Coordinates", c), Position = () => _waterCoordinates(water) };
                receiver.Move(coordinate); return receiver;
            }
            var layered = (LayeredBlockObstacle)New(typeof(LayeredBlockObstacle));
            var layer = new BlockOccupationLayer(0);
            var occupier = (BlockOccupier)New(typeof(BlockOccupier));
            occupier.BlockObject = (BlockObject)New(typeof(BlockObject));
            layer.AddBlockOccupier(occupier); layered._blockOccupationLayers.Add(layer);
            var result = new Receiver { Instance = layered, Move = c => Set(occupier.BlockObject, "Coordinates", c), Position = () => occupier.BlockObject.Coordinates };
            result.Move(coordinate); return result;
        }
        PositionedBlocks Blocks(params Vector3Int[] coordinates) => (PositionedBlocks)typeof(PositionedBlocks)
            .GetConstructor(All, null, new[] { typeof(ImmutableArray<Block>) }, null)!
            .Invoke(new object[] { coordinates.Select(Block.FullFrom).ToImmutableArray() });
        var source = (BlockObject)New(typeof(BlockObject));
        var other = (BlockObject)New(typeof(BlockObject));
        Set(source, "PositionedBlocks", Blocks(Vector3Int.zero, new Vector3Int(0, 0, 2)));
        Set(other, "PositionedBlocks", Blocks(new Vector3Int(1, 1, 0)));
        if (scenario == "empty-shape") Set(source, "PositionedBlocks", Blocks());
        if (scenario == "null-shape") Set(source, "PositionedBlocks", null!);
        object Event(BlockObject block) => unset ? (object)new BlockObjectUnsetEvent(block) : new BlockObjectSetEvent(block);
        var eventType = unset ? typeof(BlockObjectUnsetEvent) : typeof(BlockObjectSetEvent);
        var bus = new EventBus();
        var a = Make(Vector3Int.zero); var b = Make(new Vector3Int(1, 1, 0)); var c = Make(Vector3Int.zero);
        var trace = new List<string>();
        var changed = false; var nesting = 0;
        var postNow = (Action<object>)Delegate.CreateDelegate(typeof(Action<object>), bus, typeof(EventBus).GetMethod("PostNow", All)!);
        void AddReceiver(string name, Receiver receiver)
        {
            Action<object> action = e =>
            {
                if (!Shape(e)!.GetAllCoordinates().Any(p => Key(p) == Key(receiver.Position()))) return;
                trace.Add(name);
                if (scenario == "nested" && name == "b" && nesting > 0) c.Move(new Vector3Int(9, 9, 0));
                if (scenario == "last-callback-removal" && name == "c") bus._subscriptions.RemoveAll(b.Instance);
                if (name != "a" || changed) return;
                changed = true;
                if (scenario == "move-within-run") { b.Move(Vector3Int.zero); c.Move(new Vector3Int(9, 9, 0)); }
                if (scenario == "nested") { nesting++; try { postNow(Event(other)); } finally { nesting--; } }
                if (scenario == "deferred-registration") { bus.Register(new RoutingListener { Action = _ => trace.Add("late") }); bus.Unregister(c.Instance); }
                if (scenario == "direct-removal") bus._subscriptions.RemoveAll(c.Instance);
                if (scenario == "missing-removal") bus._subscriptions.RemoveAll(new object());
                if (scenario == "other-event") bus._subscriptions.Add(unset ? typeof(BlockObjectSetEvent) : typeof(BlockObjectUnsetEvent), new object(), _ => trace.Add("other"));
                if (scenario == "throw") throw new InvalidOperationException("fixture failure");
            };
            // Marking controlled actions is test-only; normal enrollment accepts
            // only wrappers for the actual reviewed native handler MethodInfos.
            Markers.Add(action, new Marker { Subscriber = receiver.Instance, Kind = kind });
            bus._subscriptions.Add(eventType, receiver.Instance, action);
        }
        AddReceiver("a", a);
        if (scenario == "interleaved" || scenario == "shape-change")
            bus.Register(new RoutingListener { Action = _ =>
            {
                trace.Add("foreign");
                if (scenario == "interleaved") { b.Move(Vector3Int.zero); c.Move(new Vector3Int(9, 9, 0)); }
                else Set(source, "PositionedBlocks", other.PositionedBlocks);
            } });
        AddReceiver("b", b); AddReceiver("c", c);
        bus.Post(Event(scenario == "null-block" ? null! : source)); bus.Post(Event(other));
        _testNative = !fast;
        try
        {
            try { bus.PostLoad(); }
            catch (Exception exception)
            {
                for (var error = exception; error != null; error = error.InnerException) trace.Add(error.GetType().FullName + ":" + error.Message);
            }
            if (scenario == "runtime") { trace.Add("runtime"); bus.Post(Event(source)); }
        }
        finally { _testNative = false; }
        return string.Join("|", trace);
    }
}
