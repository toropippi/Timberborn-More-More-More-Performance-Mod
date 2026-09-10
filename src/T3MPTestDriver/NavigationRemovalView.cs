using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Initial-load enrollment; keep native adding specifications and derive the
// inverse operation on read. Native queueing, flags and restricted coordinates
// remain in their original methods. Enrolled objects retain this representation
// when changed after load.
internal static class NavigationRemovalView
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class View : IReadOnlyList<NavMeshChangeSpecification>
    {
        internal readonly List<NavMeshChangeSpecification> Adding;
        internal readonly List<NavMeshChangeSpecification>? Shadow = _validate ? new List<NavMeshChangeSpecification>() : null;
        internal View(List<NavMeshChangeSpecification> adding) { Adding = adding; }
        public int Count => Adding.Count;
        public NavMeshChangeSpecification this[int index]
        {
            get
            {
                var value = Adding[index];
                var type = value.NavMeshChangeType == NavMeshChangeType.AddEdge ? NavMeshChangeType.RemoveEdge :
                    value.NavMeshChangeType == NavMeshChangeType.BlockEdge ? NavMeshChangeType.UnblockEdge :
                    throw new InvalidOperationException("Unexpected compact navigation specification");
                return new NavMeshChangeSpecification(value.NavMeshEdge, type);
            }
        }
        public IEnumerator<NavMeshChangeSpecification> GetEnumerator()
        { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static readonly ConditionalWeakTable<NavMeshObject, View> Views = new ConditionalWeakTable<NavMeshObject, View>();
    private static readonly List<WeakReference<NavMeshObject>> Observed = new List<WeakReference<NavMeshObject>>();
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static NavMeshObject? _last;
    [ThreadStatic] private static View? _lastView;
    private static bool _installed, _validate, _test;
    private static long _owners, _records, _checks, _reads;

    internal static void Install()
    {
        var args = Environment.GetCommandLineArgs();
        _validate = args.Contains("-t3mpTestNavRemovalValidate");
        if (!args.Contains("-t3mpTestNavRemovalView") && !_validate) return;
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.nav-removal-view");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name) => Activator.CreateInstance(hm, typeof(NavigationRemovalView).GetMethod(name, All))!;
        foreach (var name in new[] { "AddEdge", "BlockEdge", "EnqueueRemoveFromRegularNavMesh", "EnqueueRemoveFromPreviewNavMesh", "RemoveFromPreviewNavMesh" })
        {
            var rewrite = typeof(NavigationRemovalView).GetMethod(name == "AddEdge" || name == "BlockEdge" ? nameof(RewriteWrite) : nameof(RewriteRead), All)!.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
            patch.Invoke(harmony, new object?[] { typeof(NavMeshObject).GetMethod(name, All), null, null, Activator.CreateInstance(hm, rewrite), null });
        }
        patch.Invoke(harmony, new object?[] { typeof(NavMeshObject).GetMethod("Reset", All), null, Hook(nameof(Reset)), null, null });
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Begin)), null, null, Hook(nameof(End)) });
        _installed = true;
        Debug.Log("[T3MPNAVREMOVAL] installed; native adding records, inverse-operation view; validate=" + _validate);
    }

    private static IEnumerable<T> RewriteWrite<T>(IEnumerable<T> input)
    {
        var code = input.ToList();
        var op = typeof(T).GetField("opcode")!; var arg = typeof(T).GetField("operand")!;
        var field = typeof(NavMeshObject).GetField("_removingChanges", All)!;
        var matches = Enumerable.Range(0, code.Count).Where(i => Equals(arg.GetValue(code[i]), field)).ToArray();
        if (matches.Length != 1) throw new Exception("Unexpected navigation removal write field count");
        var index = matches[0];
        var add = typeof(List<NavMeshChangeSpecification>).GetMethod("Add")!;
        var next = Enumerable.Range(index + 1, code.Count - index - 1).First(i => Equals(arg.GetValue(code[i]), add));
        // Retain the owner already on the stack instead of loading its list.
        op.SetValue(code[index], OpCodes.Nop); arg.SetValue(code[index], null);
        op.SetValue(code[next], OpCodes.Call); arg.SetValue(code[next], typeof(NavigationRemovalView).GetMethod(nameof(Record), All));
        return code;
    }
    private static IEnumerable<T> RewriteRead<T>(IEnumerable<T> input)
    {
        var code = input.ToList(); var op = typeof(T).GetField("opcode")!; var arg = typeof(T).GetField("operand")!;
        var field = typeof(NavMeshObject).GetField("_removingChanges", All)!;
        var matches = code.Where(i => Equals(arg.GetValue(i), field)).ToArray();
        if (matches.Length != 1) throw new Exception("Unexpected navigation removal read field count");
        op.SetValue(matches[0], OpCodes.Call); arg.SetValue(matches[0], typeof(NavigationRemovalView).GetMethod(nameof(Removing), All));
        return code;
    }
    private static void Begin() { _depth++; _last = null; _lastView = null; }
    private static void End() { _depth--; _last = null; _lastView = null; Report("load-end"); }
    private static View? FindView(NavMeshObject owner)
    {
        if (_depth == 0 && !_test) { Views.TryGetValue(owner, out var runtime); return runtime; }
        if (ReferenceEquals(_last, owner)) return _lastView;
        _last = owner; Views.TryGetValue(owner, out _lastView); return _lastView;
    }
    private static void Record(NavMeshObject owner, NavMeshChangeSpecification specification)
    {
        var view = FindView(owner);
        // The original method has already appended its first adding record.
        // Do not enroll a pre-existing populated representation.
        if (view == null && (_depth > 0 || _test) && owner._addingChanges.Count == 1 && owner._removingChanges.Count == 0)
        {
            view = new View(owner._addingChanges); Views.Add(owner, view); _lastView = view;
            if (!_test) { _owners++; if (_validate) Observed.Add(new WeakReference<NavMeshObject>(owner)); }
        }
        if (view == null) { owner._removingChanges.Add(specification); return; }
        if (!_test) _records++;
        if (_validate)
        {
            view.Shadow!.Add(specification);
            if (!Same(view[view.Count - 1], specification)) throw new Exception("Navigation inverse specification differs");
            if (!_test) _checks++;
        }
    }
    private static IReadOnlyList<NavMeshChangeSpecification> Removing(NavMeshObject owner)
    {
        var view = FindView(owner);
        if (view == null) return owner._removingChanges;
        if (!_test) _reads++;
        return view;
    }
    private static void Reset(NavMeshObject __instance)
    {
        if (Views.TryGetValue(__instance, out var view)) view.Shadow?.Clear();
        _last = null; _lastView = null;
    }
    private static bool Same(NavMeshChangeSpecification a, NavMeshChangeSpecification b)
    {
        var x = a.NavMeshEdge; var y = b.NavMeshEdge;
        return a.NavMeshChangeType == b.NavMeshChangeType && x.Start == y.Start && x.End == y.End &&
            x.GroupId == y.GroupId && x.IsRoad == y.IsRoad && BitConverter.SingleToInt32Bits(x.Cost) == BitConverter.SingleToInt32Bits(y.Cost);
    }
    internal static void Report(string stage)
    {
        if (!_installed) return;
        Debug.Log($"[T3MPNAVREMOVAL] stage={stage} owners={_owners} records={_records} verifiedRecords={_checks} removalReads={_reads}");
        // Never keep the thread-local fast path as a persistent owner root.
        if (_depth == 0) { _last = null; _lastView = null; }
    }
    private sealed class SmallMap : INavMeshSizeProvider { public UnityEngine.Vector3Int Size => new UnityEngine.Vector3Int(4, 4, 4); }
    internal static void Validate()
    {
        if (!_validate) return;
        var live = 0; long liveRecords = 0;
        foreach (var weak in Observed)
        {
            if (!weak.TryGetTarget(out var owner) || !Views.TryGetValue(owner, out var view)) continue;
            if (owner._removingChanges.Count != 0 || view.Count != view.Shadow!.Count) throw new Exception("Compact navigation ownership/count differs");
            for (var i = 0; i < view.Count; i++) if (!Same(view[i], view.Shadow[i])) throw new Exception("Live navigation removal view differs");
            live++; liveRecords += view.Count;
        }
        var ids = new NodeIdService(new SmallMap()); ids.Load(); var factory = new NavMeshChangeFactory(ids);
        var random = new Random(10933); var transitions = 0;
        for (var test = 0; test < 32; test++)
        {
            var native = new NavMeshObject(null!, null!); var compact = new NavMeshObject(null!, null!);
            for (var i = 0; i < 128; i++)
            {
                var start = new UnityEngine.Vector3Int(random.Next(-2, 7), random.Next(-2, 7), random.Next(-2, 7));
                var end = new UnityEngine.Vector3Int(random.Next(-2, 7), random.Next(-2, 7), random.Next(-2, 7));
                var costs = new[] { 0f, -0f, 0.5f, 1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
                var edge = NavMeshEdge.CreateGrouped(start, end, random.Next(4), random.Next(2) == 0, costs[i % costs.Length]);
                if (i % 3 == 0) native.BlockEdge(edge); else native.AddEdge(edge);
                try { _test = true; if (i % 3 == 0) compact.BlockEdge(edge); else compact.AddEdge(edge); }
                finally { _test = false; }
                IReadOnlyList<NavMeshChangeSpecification> actual;
                try { _test = true; actual = Removing(compact); }
                finally { _test = false; }
                var expected = native._removingChanges;
                if (actual.Count != expected.Count || !Views.TryGetValue(compact, out _)) throw new Exception("Compact navigation test did not engage");
                for (var j = 0; j < actual.Count; j++)
                {
                    var a = actual[j]; var b = expected[j];
                    if (!Same(a, b)) throw new Exception("Synthetic navigation inverse differs");
                    var x = factory.Create(in a); var y = factory.Create(in b);
                    if (x._changeType != y._changeType || x._startNodeId != y._startNodeId || x._endNodeId != y._endNodeId ||
                        x._groupId != y._groupId || BitConverter.SingleToInt32Bits(x._cost) != BitConverter.SingleToInt32Bits(y._cost))
                        throw new Exception("Native removal change factory differs");
                }
                if (i % 31 == 30) { native.Reset(); compact.Reset(); }
                transitions++;
            }
        }
        _last = null; _lastView = null;
        Debug.Log($"[T3MPNAVREMOVAL] VALIDATE PASS liveOwners={live} liveRecords={liveRecords} syntheticTransitions={transitions} (order, inverse operation, bitwise costs, roads/groups, bounds, reset, native change factory)");
        var expectedConsumers = ValidateConsumers(false); var actualConsumers = ValidateConsumers(true);
        if (!expectedConsumers.SequenceEqual(actualConsumers)) throw new Exception("Native navigation removal consumer state differs");
        Debug.Log($"[T3MPNAVREMOVAL] CONSUMERS PASS checkpoints={actualConsumers.Length} (regular/preview queues, direct preview graph, restricted nodes, flags, repeated calls, reset)");
    }

    private static string[] ValidateConsumers(bool compact)
    {
        var ids = new NodeIdService(new SmallMap()); ids.Load();
        var terrain = Source<PreviewTerrainNavMeshSource>(); var road = Source<PreviewRoadNavMeshSource>();
        var registry = new NavMeshListenerSingletonRegistry(null!);
        foreach (var field in typeof(NavMeshListenerSingletonRegistry).GetFields(All))
            if (field.FieldType.Name == "ImmutableArray`1") field.SetValue(registry, field.FieldType.GetField("Empty", All)!.GetValue(null));
        var updater = new NavMeshUpdater(null!, terrain, null!, null!, road, null!, new NavMeshChangeFactory(ids),
            new NavMeshUpdateNotifier(registry, null!), new NavMeshUpdateBuilderFactory(ids));
        updater.Load();
        var map = new RestrictedNodeMap(ids); map.Load(); var restricted = new RestrictedNodeUpdater(map, ids);
        var owner = new NavMeshObject(updater, restricted); var snapshots = new List<string>();
        var a = new UnityEngine.Vector3Int(0, 0, 0); var b = new UnityEngine.Vector3Int(1, 0, 0);
        try
        {
            _test = compact;
            Fill(); RecordState();
            owner.EnqueueAddToRegularNavMesh(); RecordState();
            owner.EnqueueRemoveFromRegularNavMesh(); RecordState();
            restricted.ProcessRegularChanges(); RecordState();
            owner.EnqueueAddToPreviewNavMesh(); RecordState();
            owner.EnqueueAddToPreviewNavMesh(); RecordState();
            owner.EnqueueRemoveFromPreviewNavMesh(); RecordState();
            owner.EnqueueRemoveFromPreviewNavMesh(); RecordState();
            updater.ProcessPreviewChanges(new NavMeshUpdate.Builder(ids)); RecordState();
            owner.AddToPreviewNavMesh(); RecordState();
            owner.AddToPreviewNavMesh(); RecordState();
            owner.RemoveFromPreviewNavMesh(); RecordState();
            owner.RemoveFromPreviewNavMesh(); RecordState();
            owner.Reset(); RecordState();
            Fill(); owner.AddToPreviewNavMesh(); RecordState();
            owner.RemoveFromPreviewNavMesh(); RecordState();
            if (compact && !Views.TryGetValue(owner, out _)) throw new Exception("Navigation consumer test did not engage");
        }
        finally { _test = false; _last = null; _lastView = null; }
        return snapshots.ToArray();

        T Source<T>() where T : NavMeshSource
        {
            var ctor = typeof(T).GetConstructors(All).Single();
            var graphType = ctor.GetParameters()[1].ParameterType;
            var graphArgs = graphType.GetConstructors(All).Single().GetParameters().Length == 1 ?
                new object[] { ids } : new object[] { ids, new NavMeshGroupService() };
            var graph = Activator.CreateInstance(graphType, All, null, graphArgs, null)!;
            graphType.GetMethod("Load", All)!.Invoke(graph, null);
            var source = (T)ctor.Invoke(new[] { (object)ids, graph }); source.Load(); return source;
        }
        void Fill()
        {
            owner.AddEdge(NavMeshEdge.CreateGrouped(a, b, 0, true, 0.5f));
            owner.AddEdge(NavMeshEdge.CreateGrouped(b, a, 0, true, 0.5f));
            owner.AddEdge(NavMeshEdge.CreateGrouped(a, b, 0, true, 0.5f));
            owner.BlockEdge(NavMeshEdge.CreateBlocking(a, b, 1));
            owner.AddEdge(NavMeshEdge.CreateGrouped(new UnityEngine.Vector3Int(-9, 0, 0), b, 0, false, float.NaN));
            owner.AddRestrictedCoordinates(a);
        }
        void RecordState()
        {
            var text = new StringBuilder();
            text.Append(owner._addedToPreviewNavMesh).Append('|');
            foreach (var field in typeof(NavMeshUpdater).GetFields(All).Where(f => f.FieldType == typeof(Queue<NavMeshChange>)).OrderBy(f => f.Name))
            {
                text.Append(field.Name).Append(':');
                foreach (var c in (Queue<NavMeshChange>)field.GetValue(updater)!)
                    text.Append((int)c._changeType).Append(',').Append(c._startNodeId).Append(',').Append(c._endNodeId).Append(',')
                        .Append(c._groupId).Append(',').Append(BitConverter.SingleToInt32Bits(c._cost)).Append(';');
            }
            foreach (var source in new NavMeshSource[] { terrain, road })
            for (var i = 0; i < ids.NumberOfNodes; i++)
            {
                var node = source._nodes[i]; text.Append(node != null).Append(':');
                if (node != null) { Edges(node._edges); text.Append('/').Append(string.Join(",", node._blockages)); }
                text.Append('/');
                if (source._navMeshGraph is TerrainNavMeshGraph graph)
                { Edges(graph._allNeighbors[i]); text.Append('/').Append(string.Join(",", graph._cheapNeighbors[i])); }
                else Edges(((RoadNavMeshGraph)source._navMeshGraph)._neighbors[i]);
                text.Append('|');
            }
            text.Append("restricted=");
            foreach (var value in map._nodes) text.Append(value ? '1' : '0');
            foreach (var c in restricted._enqueuedChanges) text.Append(c.NodeId).Append(':').Append(c.AddingChange).Append(';');
            text.Append("builderEmpty=").Append(updater._previewNavMeshUpdateBuilder.IsEmpty);
            if (!updater._previewNavMeshUpdateBuilder.IsEmpty)
            {
                var update = updater._previewNavMeshUpdateBuilder.Build();
                text.Append("terrain=").Append(string.Join(",", update.TerrainNodeIds));
                text.Append("road=").Append(string.Join(",", update.RoadNodeIds));
                text.Append("coords=").Append(string.Join(",", update.TerrainCoordinates));
                foreach (var f in update.Bounds.GetType().GetFields(All).Where(f => !f.IsStatic).OrderBy(f => f.Name)) text.Append(f.Name).Append('=').Append(f.GetValue(update.Bounds));
            }
            snapshots.Add(text.ToString());

            void Edges(IEnumerable<NavMeshNode> edges)
            { foreach (var e in edges) text.Append(e.Id).Append(',').Append(e.GroupId).Append(',').Append(BitConverter.SingleToInt32Bits(e.Cost)).Append(';'); }
        }
    }
}
