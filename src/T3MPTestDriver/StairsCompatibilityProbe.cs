using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Bindito.Core;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Development-only comparison of Hooman Stairs' topology. Optional deletion
// uses native EntityService on an explicitly staged test settlement. This probe
// never edits connection lists or graphs itself.
public sealed class StairsCompatibilityProbe : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestStairsSnapshot");
    private object _manager = null!;
    private Type _registry = null!;
    private SpeedManager _speed = null!;
    private RoadNavMeshGraph _road = null!;
    private TerrainNavMeshGraph _terrain = null!;
    private string _output = "";
    private int _frames, _phase;
    private long _startTick;
    private bool _ready;
    private double _deadline;
    private MethodInfo _getBuildingAccessible = null!;
    private PropertyInfo _buildingAccess = null!;
    private EntityService _entityService = null!;
    private EntityRegistry _entities = null!;
    private object _undo = null!;
    private MethodInfo _commitUndo = null!, _getDeconstructible = null!;
    private string? _deleteTarget;
    private Guid _deletedId;
    private long _deleteTick;
    private object _settlementService = null!;
    private PropertyInfo _settlementReference = null!, _stackedDeletionBlocked = null!;
    private string _stagedSettlement = "";
    private readonly HashSet<int> _observedNodes = new();
    private StairsWarehouseScenario? _warehouses;
    private static StairsCompatibilityProbe? _active;
    private static bool _boundaryHooksInstalled;
    private object _buckets = null!, _singletons = null!;
    private FieldInfo _nextBucket = null!;
    private MethodInfo _finishParallel = null!;
    private int _bucketCount, _completedCycles;
    private StairsTickBoundary? _boundary;
    private Exception? _tickFailure;

    internal void Initialize(IContainer container, SpeedManager speed)
    {
        try
        {
            _speed = speed;
            _speed.ChangeSpeed(0f);
            var args = Environment.GetCommandLineArgs();
            var allowed = new[] { "-t3mpTestStairsSnapshot", "-t3mpTestStairsOutput", "-t3mpTestSpeed", "-t3mpTestStairsDeleteTop", "-t3mpTestStairsWarehouses" };
            if (args.Any(a => a.StartsWith("-t3mpTest", StringComparison.OrdinalIgnoreCase) && !allowed.Contains(a, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Run the stairs snapshot without other diagnostic flags");
            if (!FullTickCounter.Available) throw new InvalidOperationException("Full tick counter is unavailable");
            var index = Array.IndexOf(args, "-t3mpTestStairsOutput");
            if (index < 0 || index + 1 == args.Length || !Directory.Exists(args[index + 1])) throw new ArgumentException("Missing stairs output directory");
            _output = Path.GetFullPath(args[index + 1]);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetType("Calloatti.HoomanStairs.HoomanStairsManager") != null);
            _manager = container.GetInstance(assembly.GetType("Calloatti.HoomanStairs.HoomanStairsManager", true)!);
            _registry = assembly.GetType("Calloatti.HoomanStairs.HoomanStairsRegistry", true)!;
            _road = container.GetInstance<RoadNavMeshGraph>();
            _terrain = container.GetInstance<TerrainNavMeshGraph>();
            var buildingAccessible = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Timberborn.Buildings.BuildingAccessible")).Single(t => t != null)!;
            _getBuildingAccessible = typeof(BaseComponent).GetMethod("GetComponent", All)!.MakeGenericMethod(buildingAccessible);
            _buildingAccess = buildingAccessible.GetProperty("Accessible", All)!;
            index = Array.IndexOf(args, "-t3mpTestStairsDeleteTop");
            if (index >= 0)
            {
                if (index + 1 == args.Length || (args[index + 1] != "auto" && !Guid.TryParse(args[index + 1], out _)))
                    throw new ArgumentException("Stairs deletion requires auto or an entity GUID");
                var settlementIndex = Array.IndexOf(args, "-settlementName");
                if (settlementIndex < 0 || settlementIndex + 1 == args.Length || !args[settlementIndex + 1].StartsWith("t3mp-stairs-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Stairs deletion requires an explicitly staged settlement");
                _deleteTarget = args[index + 1] == "auto" ? "auto" : Guid.Parse(args[index + 1]).ToString();
                _stagedSettlement = args[settlementIndex + 1];
                _entityService = container.GetInstance<EntityService>();
                _entities = container.GetInstance<EntityRegistry>();
                Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).Single(t => t != null)!;
                var settlementType = Find("Timberborn.SettlementNameSystem.SettlementReferenceService");
                _settlementService = container.GetInstance(settlementType);
                _settlementReference = settlementType.GetProperty("SettlementReference")!;
                RequireStagedWorld();
                _getDeconstructible = typeof(BaseComponent).GetMethod("GetComponent", All)!.MakeGenericMethod(Find("Timberborn.DeconstructionSystem.Deconstructible"));
                _stackedDeletionBlocked = Find("Timberborn.BlockSystem.IBlockObjectDeletionBlocker").GetProperty("IsStackedDeletionBlocked")!;
                var undoType = Find("Timberborn.UndoSystem.IUndoRegistry");
                _undo = container.GetInstance(undoType);
                _commitUndo = undoType.GetMethod("CommitStack")!;
            }
            if (StairsWarehouseScenario.Requested)
            {
                if (_deleteTarget != null) throw new ArgumentException("Choose warehouse completion or deletion, not both");
                _warehouses = new StairsWarehouseScenario(container, speed, _manager, _output, Snapshot);
            }
            else InstallBoundary(container);
            _deadline = Time.realtimeSinceStartupAsDouble + 120;
            _ready = true;
            Debug.Log("[T3MPSTAIRS] initialized module=" + assembly.ManifestModule.ModuleVersionId);
        }
        catch (Exception e) { Fail(e); }
    }

    private void LateUpdate()
    {
        if (!_ready) return;
        try
        {
            if (_warehouses != null) { _warehouses.Update(); return; }
            if (_tickFailure != null) throw new InvalidOperationException("Stairs cycle boundary failed", _tickFailure);
            if (Time.realtimeSinceStartupAsDouble > _deadline) throw new TimeoutException("Stairs probe stalled in phase " + _phase);
            if (_phase == 0)
            {
                if (++_frames < 5 || Time.timeScale != 0f) return;
                if (FullTickCounter.FullTicks != 0) throw new InvalidOperationException("Initial snapshot requires zero simulation ticks");
                RequireCompletedBoundary();
                _finishParallel.Invoke(_singletons, null);
                Snapshot("initial");
                _startTick = FullTickCounter.FullTicks;
                BeginWindow();
                _speed.ChangeSpeed(TestArguments.Speed ?? 3f);
                _phase = 1;
                _deadline = Time.realtimeSinceStartupAsDouble + 120;
            }
            else if (_phase == 1 && _boundary!.CompletedTicks == 64 && Time.timeScale == 0f)
            {
                EndWindow(_startTick);
                _frames = 0;
                _phase = 2;
                _deadline = Time.realtimeSinceStartupAsDouble + 15;
            }
            else if (_phase == 2 && ++_frames >= 2 && Time.timeScale == 0f)
            {
                Snapshot("after-progress");
                if (_deleteTarget == null) Complete();
                else
                {
                    DeleteTop();
                    _phase = 3;
                    _frames = 0;
                    _deadline = Time.realtimeSinceStartupAsDouble + 15;
                }
            }
            else if (_phase == 3 && ++_frames >= 2 && Time.timeScale == 0f)
            {
                RequireDeleted();
                Snapshot("after-delete");
                _deleteTick = FullTickCounter.FullTicks;
                BeginWindow();
                _speed.ChangeSpeed(TestArguments.Speed ?? 3f);
                _phase = 4;
                _deadline = Time.realtimeSinceStartupAsDouble + 120;
            }
            else if (_phase == 4 && _boundary!.CompletedTicks == 64 && Time.timeScale == 0f)
            {
                EndWindow(_deleteTick);
                _phase = 5;
                _frames = 0;
                _deadline = Time.realtimeSinceStartupAsDouble + 15;
            }
            else if (_phase == 5 && ++_frames >= 2 && Time.timeScale == 0f)
            {
                RequireDeleted();
                Snapshot("after-delete-progress");
                Complete();
            }
        }
        catch (Exception e) { Fail(e); }
    }

    private void Snapshot(string label)
    {
        if (_warehouses == null)
        {
            RequireCompletedBoundary();
            Debug.Log($"[T3MPSTAIRS] boundary snapshot={label} completedCycles={_completedCycles} nextBucket=0");
        }
        var rows = new List<string>();
        var neighborOrder = new List<string>();
        var connections = ((IEnumerable)Field(_manager, "_activeConnections")).Cast<object>().ToArray();
        if (connections.Length == 0 && label == "initial") throw new InvalidOperationException("Save has no active Hooman stairs connections");
        foreach (var connection in connections.OrderBy(c => Id(Field(c, "TopBuilding")), StringComparer.Ordinal))
        {
            var top = (BaseComponent)Field(connection, "TopBuilding");
            var key = Id(top);
            rows.Add("connection\t" + key + "\t" + Id(Field(connection, "BottomBuilding")) + "\t" + top.Name);
            var path = (IEnumerable<Vector3Int>)Field(connection, "GridPath");
            var n = 0;
            foreach (var point in path) rows.Add($"path\t{key}\t{n++}\t{Grid(point)}");
            n = 0;
            foreach (NavMeshEdge edge in (IEnumerable)Field(connection, "InjectedEdges"))
                rows.Add($"injected\t{key}\t{n++}\t{Grid(edge.Start)}\t{Grid(edge.End)}\t{edge.GroupId}\t{edge.IsRoad}\t{Bits(edge.Cost)}");
            var buildingAccessible = _getBuildingAccessible.Invoke(top, null);
            var accessible = buildingAccessible == null ? null : (Accessible?)_buildingAccess.GetValue(buildingAccessible);
            if (accessible == null) { rows.Add("access-absent\t" + key); continue; }
            n = 0;
            foreach (var point in accessible.Accesses)
                rows.Add($"access\t{key}\t{n++}\t{Bits(point.x)},{Bits(point.y)},{Bits(point.z)}");
        }
        var nodes = (IDictionary)_registry.GetField("StairNodeIds", All)!.GetValue(null)!;
        _observedNodes.UnionWith(nodes.Keys.Cast<int>());
        foreach (var id in _observedNodes.OrderBy(x => x))
        {
            rows.Add($"node\t{id}\t{(nodes.Contains(id) ? nodes[id] : 0)}");
            AddNeighbors("road", id, _road.GetNeighbors(id));
            AddNeighbors("terrain", id, _terrain.GetNeighbors(id));
        }
        foreach (var building in ((IEnumerable)_registry.GetField("TopBuildings", All)!.GetValue(null)!).Cast<object>().OrderBy(Id, StringComparer.Ordinal))
            rows.Add("top\t" + Id(building));
        var hash = Write(label, rows);
        var orderHash = Write(label + "-neighbor-order", neighborOrder);
        Debug.Log($"[T3MPSTAIRS] snapshot={label} ticks={FullTickCounter.FullTicks} connections={connections.Length} nodes={nodes.Count} rows={rows.Count} sha256={hash} neighborOrderSha256={orderHash}");

        void AddNeighbors(string graph, int id, IEnumerable<NavMeshNode> neighbors)
        {
            var copy = neighbors.ToArray();
            var n = 0;
            foreach (var neighbor in copy)
                neighborOrder.Add($"{graph}\t{id}\t{n++}\t{neighbor.Id}\t{neighbor.GroupId}\t{Bits(neighbor.Cost)}");
            n = 0;
            foreach (var neighbor in copy.OrderBy(x => x.Id).ThenBy(x => x.GroupId).ThenBy(x => BitConverter.SingleToInt32Bits(x.Cost)))
                rows.Add($"{graph}\t{id}\t{n++}\t{neighbor.Id}\t{neighbor.GroupId}\t{Bits(neighbor.Cost)}");
        }
    }

    private void DeleteTop()
    {
        RequireStagedWorld();
        var tops = ((IEnumerable)Field(_manager, "_activeConnections")).Cast<object>()
            .Select(c => (BaseComponent)Field(c, "TopBuilding")).OrderBy(Id, StringComparer.Ordinal).ToArray();
        bool Eligible(BaseComponent top)
        {
            var marker = (BaseComponent?)_getDeconstructible.Invoke(top, null);
            return marker != null && marker.Enabled && (bool)top.GetType().GetMethod("CanDelete", All)!.Invoke(top, null)! &&
                !((IEnumerable)Field(top, "_deletionBlockers")).Cast<object>().Any(b => (bool)_stackedDeletionBlocked.GetValue(b)!);
        }
        var selected = _deleteTarget == "auto" ? tops.FirstOrDefault(Eligible) : tops.SingleOrDefault(t => Id(t) == _deleteTarget);
        if (selected == null || !Eligible(selected)) throw new InvalidOperationException("No eligible requested top building to deconstruct");
        _deletedId = selected.GetComponent<EntityComponent>().EntityId;
        using (var stream = new FileStream(Path.Combine(_output, "stairs-delete-target.txt"), FileMode.CreateNew, FileAccess.Write))
        {
            var bytes = Encoding.UTF8.GetBytes(_deletedId.ToString() + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
        Debug.Log($"[T3MPSTAIRS] delete target={_deletedId} name={selected.Name} ticks={FullTickCounter.FullTicks} canDelete=true");
        _entityService.Delete(selected);
        _commitUndo.Invoke(_undo, null);
    }

    private void RequireStagedWorld()
    {
        var reference = _settlementReference.GetValue(_settlementService) ?? throw new InvalidOperationException("No loaded settlement reference");
        var name = (string)reference.GetType().GetProperty("SettlementName")!.GetValue(reference)!;
        if (name != _stagedSettlement) throw new InvalidOperationException("Loaded settlement differs from the authorized stage");
    }

    private void RequireDeleted()
    {
        if (_entities.Entities.Any(e => e.EntityId == _deletedId)) throw new InvalidOperationException("Deleted entity remains in registry");
        foreach (var connection in (IEnumerable)Field(_manager, "_activeConnections"))
            if (Id(Field(connection, "TopBuilding")) == _deletedId.ToString() || Id(Field(connection, "BottomBuilding")) == _deletedId.ToString())
                throw new InvalidOperationException("Deleted entity remains in a stairs connection");
    }

    private void Complete()
    {
        StopBoundary();
        Debug.Log("[T3MPSTAIRS] COMPLETE progressedTicks=" + (FullTickCounter.FullTicks - _startTick));
        enabled = false;
    }

    private string Write(string label, List<string> rows)
    {
        var bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", rows) + "\n");
        using (var stream = new FileStream(Path.Combine(_output, "stairs-" + label + ".tsv"), FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes, 0, bytes.Length);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, All)!.GetValue(target)!;
    private static string Id(object component) => ((BaseComponent)component).GetComponent<EntityComponent>().EntityId.ToString();
    private static string Grid(Vector3Int point) => $"{point.x},{point.y},{point.z}";
    private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
    private void Fail(Exception e)
    {
        StopBoundary();
        _warehouses?.Stop();
        Debug.LogError("[T3MPTEST] ERROR stairs snapshot: " + e);
        _ready = false;
        enabled = false;
        _speed?.ChangeSpeed(0f);
    }

    private void OnDestroy() { StopBoundary(); _warehouses?.Stop(); }

    private void InstallBoundary(IContainer container)
    {
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).Single(t => t != null)!;
        var bucketType = Find("Timberborn.TickSystem.TickableBucketService");
        _buckets = container.GetInstance(Find("Timberborn.TickSystem.ITickableBucketService"));
        _nextBucket = bucketType.GetField("_nextBucketIndex", All)!;
        _singletons = bucketType.GetField("_tickableSingletonService", All)!.GetValue(_buckets)!;
        _finishParallel = _singletons.GetType().GetMethod("ForceFinishParallelTick", All)!;
        _bucketCount = (int)bucketType.GetProperty("TotalNumberOfBuckets")!.GetValue(_buckets)!;
        if (_boundaryHooksInstalled) return;
        var harmonyType = Find("HarmonyLib.Harmony");
        var methodType = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.stairs-boundary")!;
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var prefix = Activator.CreateInstance(methodType, typeof(StairsCompatibilityProbe).GetMethod(nameof(BeforeBuckets), All))!;
        var postfix = Activator.CreateInstance(methodType, typeof(StairsCompatibilityProbe).GetMethod(nameof(AfterBucket), All))!;
        patch.Invoke(harmony, new object?[] { bucketType.GetMethod("TickBuckets", All)!, prefix, null, null, null });
        patch.Invoke(harmony, new object?[] { bucketType.GetMethod("TickNextBucket", All)!, null, postfix, null, null });
        _boundaryHooksInstalled = true;
    }

    private void BeginWindow()
    {
        RequireCompletedBoundary();
        if (_active != null) throw new InvalidOperationException("Another stairs observation is active");
        _boundary = new StairsTickBoundary(_bucketCount, 64);
        _active = this;
    }

    private void EndWindow(long start)
    {
        RequireCompletedBoundary();
        if (_boundary!.CompletedTicks != 64 || FullTickCounter.FullTicks - start != 64 || Time.timeScale != 0)
            throw new InvalidOperationException("Stairs observation did not stop at exactly 64 completed cycles");
        _completedCycles += 64;
        StopBoundary();
    }

    private void RequireCompletedBoundary()
    {
        if ((int)_nextBucket.GetValue(_buckets)! != 0) throw new InvalidOperationException("Stairs snapshot is between entity buckets");
    }

    private void StopBoundary() { if (ReferenceEquals(_active, this)) _active = null; }

    private static void BeforeBuckets(object __instance, ref int numberOfBucketsToTick)
    {
        var current = _active;
        if (current == null || !ReferenceEquals(current._buckets, __instance)) return;
        // Stop only after the final requested complete native cycle. Later
        // work belongs to the next observation window, not this snapshot.
        numberOfBucketsToTick = current._tickFailure != null ? 0 :
            current._boundary!.LimitBatch(numberOfBucketsToTick, (int)current._nextBucket.GetValue(__instance)!);
    }

    private static void AfterBucket(object __instance)
    {
        var current = _active;
        if (current == null || !ReferenceEquals(current._buckets, __instance)) return;
        try
        {
            if (current._boundary!.AfterBucket((int)current._nextBucket.GetValue(__instance)!) && current._boundary.CompletedTicks == 64)
            {
                current._finishParallel.Invoke(current._singletons, null);
                current._speed.ChangeSpeed(0f);
            }
        }
        catch (Exception e) { current._tickFailure = e; current._speed.ChangeSpeed(0f); }
    }
}
