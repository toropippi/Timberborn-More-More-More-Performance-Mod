using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Bindito.Core;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.CharacterMovementSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Development-only fixture completion and observation. FinishNow is the native
// instant-build operation; this does not test normal construction or hauling
// of construction materials. Simulation and pathfinding remain native.
internal sealed class StairsWarehouseScenario
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const int ObserveTicks = 768;
    private static StairsWarehouseScenario? _active;
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestStairsWarehouses");
    private readonly EntityRegistry _entities;
    private readonly SpeedManager _speed;
    private readonly object _manager, _settlementService, _saver;
    private readonly BlockObject[] _targets;
    private readonly object[] _sites, _stocks;
    private readonly MethodInfo _finish, _save, _stockAmount, _getEnterer;
    private readonly PropertyInfo _currentBuilding;
    private readonly FieldInfo _model;
    private readonly PropertyInfo _modelPosition;
    private readonly object _buckets, _singletons;
    private readonly FieldInfo _nextBucket;
    private readonly MethodInfo _finishParallel;
    private readonly StairsTickBoundary _boundary;
    private readonly string _output, _settlement;
    private readonly Action<string> _snapshot;
    private readonly List<string> _simulation = new(), _visual = new(), _targetRows = new();
    private readonly HashSet<Guid> _visitors = new();
    private Bounds _bounds;
    private long _startTick;
    private int _phase, _frames, _nearSamples;
    private double _deadline;
    private Exception? _tickFailure;

    internal StairsWarehouseScenario(IContainer container, SpeedManager speed, object manager, string output, Action<string> snapshot)
    {
        if (_active != null) throw new InvalidOperationException("A warehouse observation is already active");
        _speed = speed; _manager = manager; _output = output; _snapshot = snapshot;
        var args = Environment.GetCommandLineArgs();
        string Arg(string key)
        {
            var index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + key);
            return args[index + 1];
        }
        _settlement = Arg("-settlementName");
        if (!_settlement.StartsWith("t3mp-stairs-", StringComparison.Ordinal)) throw new InvalidOperationException("Warehouse fixture needs a staged settlement");
        _settlementService = container.GetInstance(Find("Timberborn.SettlementNameSystem.SettlementReferenceService"));
        RequireStage();
        var ids = Arg("-t3mpTestStairsWarehouses").Split(',').Select(Guid.Parse).ToArray();
        if (ids.Length < 1 || ids.Length > 2 || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Specify one or two distinct warehouse GUIDs");
        if (TestArguments.Speed is not float rate || rate < 1 || rate > 7) throw new ArgumentException("Warehouse observation requires speed 1 through 7");
        _entities = container.GetInstance<EntityRegistry>();
        _targets = ids.Select(id => _entities.Entities.Single(e => e.EntityId == id).GetComponent<BlockObject>()).OrderBy(b => b.Coordinates.z).ToArray();
        if (_targets.Any(b => b == null || b.Name != "SmallWarehouse.IronTeeth(Clone)" || !b.IsUnfinished))
            throw new InvalidOperationException("All fixture targets must be unfinished small Iron Teeth warehouses");
        var siteType = Find("Timberborn.ConstructionSites.ConstructionSite");
        var stockType = Find("Timberborn.Stockpiles.Stockpile");
        _sites = _targets.Select(t => Component(t, siteType)).ToArray();
        _stocks = _targets.Select(t => stockType.GetProperty("Inventory")!.GetValue(Component(t, stockType))!).ToArray();
        _finish = siteType.GetMethod("FinishNow")!;
        _stockAmount = _stocks[0].GetType().GetMethod("AmountInStock", new[] { typeof(string) })!;
        var saverType = Find("Timberborn.GameSaveRuntimeSystem.GameSaver");
        _saver = container.GetInstance(saverType);
        _save = saverType.GetMethod("SaveWithoutFinishingTick")!;
        var entererType = Find("Timberborn.EnterableSystem.Enterer");
        _getEnterer = typeof(BaseComponent).GetMethod("GetComponent", All)!.MakeGenericMethod(entererType);
        _currentBuilding = entererType.GetProperty("CurrentBuilding")!;
        _model = typeof(MovementAnimator).GetField("_characterModel", All)!;
        _modelPosition = _model.FieldType.GetProperty("Position", All)!;
        var bucketType = Find("Timberborn.TickSystem.TickableBucketService");
        _buckets = container.GetInstance(Find("Timberborn.TickSystem.ITickableBucketService"));
        _nextBucket = bucketType.GetField("_nextBucketIndex", All)!;
        _singletons = bucketType.GetField("_tickableSingletonService", All)!.GetValue(_buckets)!;
        _finishParallel = _singletons.GetType().GetMethod("ForceFinishParallelTick", All)!;
        _boundary = new StairsTickBoundary((int)bucketType.GetProperty("TotalNumberOfBuckets")!.GetValue(_buckets)!, ObserveTicks);
        InstallBoundaryHooks(bucketType);
        _deadline = Time.realtimeSinceStartupAsDouble + 120;
    }

    internal void Update()
    {
        if (_phase == 3) return;
        if (_tickFailure != null) throw new InvalidOperationException("Warehouse tick observation failed", _tickFailure);
        if (Time.realtimeSinceStartupAsDouble > _deadline) throw new TimeoutException("Warehouse observation stalled in phase " + _phase);
        if (_phase == 0)
        {
            if (++_frames < 5 || Time.timeScale != 0) return;
            if (FullTickCounter.FullTicks != 0) throw new InvalidOperationException("Warehouse fixture must begin before the first full tick");
            RequireStage();
            _snapshot("initial");
            RecordTargets("before-finish", 0);
            foreach (var site in _sites) _finish.Invoke(site, null);
            if (_targets.Any(t => !t.IsFinished)) throw new InvalidOperationException("Native FinishNow did not finish all fixture targets");
            _phase = 1; _frames = 0;
        }
        else if (_phase == 1 && ++_frames >= 2)
        {
            RequireStage();
            if (Time.timeScale != 0 || FullTickCounter.FullTicks != 0) throw new InvalidOperationException("Fixture completion advanced simulation ticks");
            RequireBucketBoundary();
            _bounds = new Bounds(_targets[0].Transform.position, Vector3.zero);
            foreach (var target in _targets)
            {
                var connection = ((IEnumerable)Field(_manager, "_activeConnections")).Cast<object>().Single(c => ReferenceEquals(Field(c, "TopBuilding"), target));
                foreach (Vector3Int point in (IEnumerable)Field(connection, "GridPath")) _bounds.Encapsulate(CoordinateSystem.GridToWorldCentered(point));
            }
            _bounds.Expand(2f);
            _snapshot("after-finish");
            RecordTargets("after-finish", 0);
            Save("after-finish");
            _startTick = FullTickCounter.FullTicks;
            _active = this;
            _phase = 2;
            _deadline = Time.realtimeSinceStartupAsDouble + 900;
            Debug.Log($"[T3MPWAREHOUSE] ready targets={string.Join(",", _targets.Select(Id))} ticks=0 bounds={_bounds}");
            _speed.ChangeSpeed(TestArguments.Speed!.Value);
        }
        else if (_phase == 2 && _boundary.CompletedTicks == ObserveTicks && Time.timeScale == 0)
        {
            if (FullTickCounter.FullTicks - _startTick != ObserveTicks) throw new InvalidOperationException("Warehouse tick boundary overshot");
            RequireBucketBoundary();
            Stop();
            _snapshot("after-progress");
            RecordTargets("after-progress", ObserveTicks);
            Save("after-progress");
            Write("simulation", _simulation);
            Write("visual", _visual);
            Write("targets", _targetRows);
            Debug.Log($"[T3MPWAREHOUSE] COMPLETE ticks={ObserveTicks} nearSamples={_nearSamples} visitors={_visitors.Count} gear={string.Join(",", _stocks.Select(Stock))}");
            _phase = 3;
        }
    }

    private void InstallBoundaryHooks(Type bucketType)
    {
        var harmonyType = Find("HarmonyLib.Harmony");
        var methodType = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.stairs-warehouse")!;
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var prefix = Activator.CreateInstance(methodType, typeof(StairsWarehouseScenario).GetMethod(nameof(BeforeBuckets), All))!;
        var postfix = Activator.CreateInstance(methodType, typeof(StairsWarehouseScenario).GetMethod(nameof(AfterBucket), All))!;
        patch.Invoke(harmony, new object?[] { bucketType.GetMethod("TickBuckets", All)!, prefix, null, null, null });
        patch.Invoke(harmony, new object?[] { bucketType.GetMethod("TickNextBucket", All)!, null, postfix, null, null });
    }

    private static void BeforeBuckets(object __instance, ref int numberOfBucketsToTick)
    {
        var current = _active;
        if (current == null || !ReferenceEquals(current._buckets, __instance)) return;
        // Preserve each native bucket up to the endpoint. Only the excess work
        // after the requested final full cycle is excluded from this test run.
        numberOfBucketsToTick = current._boundary.LimitBatch(numberOfBucketsToTick, (int)current._nextBucket.GetValue(__instance)!);
    }

    private static void AfterBucket(object __instance)
    {
        var current = _active;
        if (current == null || !ReferenceEquals(current._buckets, __instance)) return;
        try
        {
            if (!current._boundary.AfterBucket((int)current._nextBucket.GetValue(__instance)!)) return;
            current._finishParallel.Invoke(current._singletons, null);
            current.Observe(current._boundary.CompletedTicks);
        }
        catch (Exception e)
        {
            current._tickFailure = e;
            // Keep the endpoint limiter installed until LateUpdate reports the
            // failure; do not resume an unrestricted remainder of this batch.
            current._speed.ChangeSpeed(0);
        }
    }

    private void Observe(long tick)
    {
        if (tick < 1 || tick > ObserveTicks) throw new InvalidOperationException("Unexpected warehouse tick");
        // Enumerate live entities each full tick, including newly created characters.
        var nearby = new List<(EntityComponent Entity, MovementAnimator Animator, Vector3 Position)>();
        foreach (var entity in _entities.Entities)
        {
            var animator = entity.GetComponent<MovementAnimator>();
            if (animator == null) continue;
            var position = entity.Transform.position;
            if (_bounds.Contains(position)) nearby.Add((entity, animator, position));
        }
        foreach (var item in nearby.OrderBy(i => i.Entity.EntityId))
        {
            var enterer = _getEnterer.Invoke(item.Entity, null);
            var building = enterer == null ? null : (BaseComponent?)_currentBuilding.GetValue(enterer);
            var buildingId = building == null ? "-" : Id(building);
            if (_targets.Any(t => Id(t) == buildingId)) _visitors.Add(item.Entity.EntityId);
            _simulation.Add($"{tick}\t{item.Entity.EntityId}\t{Vector(item.Position)}\t{buildingId}");
            var model = _model.GetValue(item.Animator);
            if (model != null) _visual.Add($"{tick}\t{item.Entity.EntityId}\t{Vector((Vector3)_modelPosition.GetValue(model)!)}");
        }
        _nearSamples += nearby.Count;
        RecordTargets("tick", tick);
        if (tick % 128 == 0) Debug.Log($"[T3MPWAREHOUSE] progress completedTicks={tick} nearSamples={_nearSamples} visitors={_visitors.Count}");
        if (tick == ObserveTicks) _speed.ChangeSpeed(0);
    }

    private void RecordTargets(string label, long tick)
    {
        for (var i = 0; i < _targets.Length; i++)
        {
            var site = _sites[i];
            var type = site.GetType();
            _targetRows.Add($"{label}\t{tick}\t{Id(_targets[i])}\t{_targets[i].IsFinished}\t{type.GetProperty("IsOn")!.GetValue(site)}\t{Convert.ToSingle(type.GetProperty("BuildTimeProgressInHours")!.GetValue(site), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)}\t{Stock(_stocks[i])}");
        }
    }

    internal void Stop() { if (ReferenceEquals(_active, this)) _active = null; }
    private int Stock(object inventory) => (int)_stockAmount.Invoke(inventory, new object[] { "Gear" })!;
    private void RequireStage()
    {
        var reference = _settlementService.GetType().GetProperty("SettlementReference")!.GetValue(_settlementService)!;
        if ((string)reference.GetType().GetProperty("SettlementName")!.GetValue(reference)! != _settlement) throw new InvalidOperationException("Loaded settlement differs from the staged warehouse fixture");
    }
    private void Save(string label)
    {
        RequireStage();
        RequireBucketBoundary();
        _finishParallel.Invoke(_singletons, null);
        using var file = new FileStream(Path.Combine(_output, "warehouse-" + label + ".timber"), FileMode.CreateNew, FileAccess.ReadWrite);
        _save.Invoke(_saver, new object[] { file });
    }
    private void RequireBucketBoundary()
    {
        if ((int)_nextBucket.GetValue(_buckets)! != 0) throw new InvalidOperationException("Warehouse snapshot is between entity buckets");
    }
    private void Write(string label, List<string> rows)
    {
        using var file = new FileStream(Path.Combine(_output, "warehouse-" + label + ".tsv"), FileMode.CreateNew, FileAccess.Write);
        var bytes = new UTF8Encoding(false).GetBytes(string.Join("\n", rows) + "\n"); file.Write(bytes, 0, bytes.Length);
    }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).Single(t => t != null)!;
    private static object Component(BaseComponent target, Type type) => typeof(BaseComponent).GetMethod("GetComponent", All)!.MakeGenericMethod(type).Invoke(target, null)!;
    private static object Field(object target, string name) => target.GetType().GetField(name, All)!.GetValue(target)!;
    private static string Id(BaseComponent target) => target.GetComponent<EntityComponent>().EntityId.ToString();
    private static string Vector(Vector3 value) => $"{Bits(value.x)},{Bits(value.y)},{Bits(value.z)}";
    private static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
}
