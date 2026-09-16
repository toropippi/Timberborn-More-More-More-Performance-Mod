using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Bindito.Core;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Explicit copied-world diagnostic. Water enters/leaves through IWaterService;
// no simulation, status, death or rendering fields are assigned by this probe.
public sealed partial class FloodRegression : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const int TargetCount = 6;
    private static FloodRegression? _active;
    private static bool _patched;
    private readonly List<Entry> _entries = new();
    private readonly List<string> _states = new(), _events = new();
    private readonly HashSet<int> _frames = new();
    private readonly Dictionary<Type, MethodInfo> _getters = new();
    private SpeedManager _speed = null!;
    private object _waterService = null!, _waterMap = null!;
    private MethodInfo _add = null!, _remove = null!, _height = null!, _floor = null!;
    private string _output = "";
    private int _ticks;
    private bool _done;
    private EntityRegistry _registry = null!;
    private IContainer _container = null!;
    private bool _prepared;

    internal static bool Requested => ArgumentPresent("-t3mpTestFlood");
    private static bool ArgumentPresent(string flag) => Environment.GetCommandLineArgs().Contains(flag, StringComparer.OrdinalIgnoreCase);
    private static string? Argument(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-")) throw new ArgumentException("Missing " + flag);
        return args[index + 1];
    }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    private static object Get(object instance, string name) => instance.GetType().GetProperty(name, All)!.GetValue(instance)!;
    private static object Field(object instance, string name) => instance.GetType().GetField(name, All)!.GetValue(instance)!;
    private object? Component(EntityComponent entity, Type type)
    {
        if (!_getters.TryGetValue(type, out var method))
            _getters[type] = method = typeof(BaseComponent).GetMethod("GetComponent", All)!.MakeGenericMethod(type);
        return method.Invoke(entity, null);
    }
    private int Height(Vector3Int position) => (int)_height.Invoke(_waterMap, new object[] { position })!;
    private bool FlatCell(Vector3Int position)
    {
        var args = new object[] { position, 0 };
        return (bool)_floor.Invoke(_waterMap, args)! && (int)args[1] == position.z;
    }

    internal void Initialize(EntityRegistry registry, IContainer container, SpeedManager speed)
    {
        if (!Requested) throw new InvalidOperationException("Flood diagnostic needs its explicit flag");
        if (FixedTickBenchmark.Requested || TestArguments.LoadRoutingRequested) throw new InvalidOperationException("Flood diagnostic must run separately");
        _speed = speed;
        _output = Path.GetFullPath(Argument("-t3mpTestFloodOutput") ?? throw new ArgumentException("Missing flood output directory"));
        Directory.CreateDirectory(_output);
        _registry = registry;
        _container = container;
        ConfigurePersistence();
        if (!_patched)
        {
            var harmonyType = Find("HarmonyLib.Harmony");
            var harmonyMethod = Find("HarmonyLib.HarmonyMethod");
            var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.flood")!;
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            var prefix = Activator.CreateInstance(harmonyMethod, typeof(FloodRegression).GetMethod(nameof(BeforeSimulation), All));
            var postfix = Activator.CreateInstance(harmonyMethod, typeof(FloodRegression).GetMethod(nameof(AfterWaterObjects), All));
            patch.Invoke(harmony, new object?[] { Find("Timberborn.TickSystem.Ticker").GetMethod("Update", All)!, prefix, null, null, null });
            patch.Invoke(harmony, new object?[] { Find("Timberborn.WaterObjects.WaterObjectService").GetMethod("Tick", All)!, null, postfix, null, null });
            _patched = true;
        }
        _active = this;
        Debug.Log("[T3MPTEST] Flood regression awaiting first ticker update after entity post-load");
    }

    private void Prepare()
    {
        // Singleton PostLoad precedes entity post-load. Ticker.Update begins
        // after it, before singleton ticks can advance the clock/death progress.
        var water = Find("Timberborn.WaterObjects.WaterObject");
        var needs = Find("Timberborn.NaturalResourcesMoisture.LivingWaterObject");
        var dying = Find("Timberborn.NaturalResourcesMoisture.LivingWaterNaturalResource");
        var living = Find("Timberborn.NaturalResourcesLifecycle.LivingNaturalResource");
        var aggregate = Find("Timberborn.NaturalResourcesLifecycle.DyingNaturalResource");
        var spec = Find("Timberborn.NaturalResourcesMoisture.FloodableNaturalResourceSpec");
        var status = Find("Timberborn.NaturalResourcesMoistureUI.LivingWaterNaturalResourceStatus");
        var serviceType = Find("Timberborn.WaterSystem.IWaterService");
        var mapType = Find("Timberborn.WaterSystem.IThreadSafeWaterMap");
        _waterService = _container.GetInstance(serviceType);
        _waterMap = _container.GetInstance(mapType);
        _add = serviceType.GetMethod("AddCleanWater")!;
        _remove = serviceType.GetMethod("RemoveCleanWater")!;
        _height = mapType.GetMethod("CeiledWaterHeight")!;
        _floor = mapType.GetMethod("TryGetColumnFloor")!;
        var planPath = Argument("-t3mpTestFloodPlan");
        var plan = planPath == null ? null : File.ReadAllLines(planPath).Select(Guid.Parse).ToArray();
        if (plan != null && (plan.Length != TargetCount || plan.Distinct().Count() != TargetCount)) throw new InvalidOperationException("Invalid flood fixture plan");
        var byId = _registry.Entities.ToDictionary(e => e.EntityId);
        var candidates = plan == null ? byId.Values.OrderBy(e => e.EntityId) : plan.Select(id => byId[id]);
        foreach (var entity in candidates)
        {
            // Ordinary trees only; water crops and trees with different water
            // requirements must not be treated as the same fixture.
            var name = entity.Name;
            if (!new[] { "Birch", "Maple", "Oak", "Pine" }.Any(n => name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            var waterObject = Component(entity, water);
            var needsObject = Component(entity, needs);
            var dyingObject = Component(entity, dying);
            var livingObject = Component(entity, living);
            var aggregateObject = Component(entity, aggregate);
            var specObject = Component(entity, spec);
            var statusObject = Component(entity, status);
            if (waterObject == null || needsObject == null || dyingObject == null || livingObject == null || aggregateObject == null || specObject == null || statusObject == null) continue;
            if ((int)Get(specObject, "MinWaterHeight") != 0 || (int)Get(specObject, "MaxWaterHeight") != 0 ||
                (bool)Get(livingObject, "IsDead") || (!Reload && ((bool)Get(aggregateObject, "IsDying") || !(bool)Get(needsObject, "WaterNeedsAreMet")))) continue;
            var position = (Vector3Int)Field(waterObject, "_baseCoordinates");
            if (_entries.Any(e => Math.Abs(e.Position.x - position.x) < 6 && Math.Abs(e.Position.y - position.y) < 6)) continue;
            var wetCells = new List<Vector3Int>();
            var drainCells = new List<Vector3Int>();
            for (var x = -2; x <= 2; x++)
                for (var y = -2; y <= 2; y++)
                {
                    var cell = position + new Vector3Int(x, y, 0);
                    if (FlatCell(cell) && (Reload || Height(cell) <= cell.z))
                    {
                        drainCells.Add(cell);
                        if (Math.Abs(x) <= 1 && Math.Abs(y) <= 1) wetCells.Add(cell);
                    }
                }
            if (wetCells.Count != 9 || drainCells.Count != 25) continue;
            _entries.Add(new Entry(entity.EntityId, name, position, waterObject, needsObject, dyingObject, livingObject,
                Field(statusObject, "_tooMuchWaterStatusToggle"), wetCells, drainCells));
            if (_entries.Count == TargetCount) break;
        }
        if (_entries.Count != TargetCount || (plan != null && !_entries.Select(e => e.Id).SequenceEqual(plan)))
            throw new InvalidOperationException("Could not reach all six healthy, dry, flat-ground tree fixtures");
        File.WriteAllLines(Path.Combine(_output, "fixture-plan.txt"), _entries.Select(e => e.Id.ToString()));
        _states.Add("tick\tid\tname\tx\ty\tz\twaterAboveBase\twaterNeedsMet\tisDying\tprogress\tisDead\ttooMuchWaterStatus\tdeathByFlooding");
        _events.Add("tick\tid\tevent\twaterAboveBase\twaterNeedsMet\tisDying\tprogress\tisDead\ttooMuchWaterStatus\tdeathByFlooding");
        foreach (var entry in _entries)
        {
            Subscribe(entry, entry.Water, "WaterAboveBaseChanged");
            Subscribe(entry, entry.Needs, "WaterNeedsMet");
            var unmet = entry.Needs.GetType().GetEvent("WaterNeedsUnmet", All)!;
            typeof(FloodRegression).GetMethod(nameof(SubscribeUnmet), All)!
                .MakeGenericMethod(unmet.EventHandlerType!.GetGenericArguments()[0])
                .Invoke(this, new object[] { entry, unmet });
            Subscribe(entry, entry.Dying, "StartedDying");
            Subscribe(entry, entry.Dying, "StoppedDying");
            Subscribe(entry, entry.Living, "Died");
        }
        if (Reload) CheckReload();
        _prepared = true;
        Debug.Log("[T3MPTEST] Flood regression ready targets=6; native water injection; copied-world diagnostic");
    }

    private void Subscribe(Entry entry, object sender, string name)
    {
        EventHandler callback = (_, _) => _events.Add((_ticks + 1) + "\t" + entry.Id + "\t" + name + "\t" + State(entry));
        var info = sender.GetType().GetEvent(name, All)!;
        info.AddEventHandler(sender, callback);
        entry.Subscriptions.Add((sender, info, callback));
    }
    private void SubscribeUnmet<T>(Entry entry, EventInfo info)
    {
        EventHandler<T> callback = (_, args) => _events.Add((_ticks + 1) + "\t" + entry.Id +
            "\tWaterNeedsUnmet(Flooded=" + Get(args!, "Flooded") + ")\t" + State(entry));
        info.AddEventHandler(entry.Needs, callback);
        entry.Subscriptions.Add((entry.Needs, info, callback));
    }
    private static string State(Entry entry)
    {
        var progress = Get(entry.Dying, "DyingProgress");
        return Get(entry.Water, "WaterAboveBase") + "\t" + Get(entry.Needs, "WaterNeedsAreMet") + "\t" + Get(progress, "IsDying") + "\t" +
            ((float)Get(progress, "Progress")).ToString("R", CultureInfo.InvariantCulture) + "\t" + Get(entry.Living, "IsDead") + "\t" +
            Get(entry.Status, "IsActive") + "\t" + Get(entry.Dying, "DeathByFlooding");
    }
    private static void BeforeSimulation()
    {
        var active = _active;
        if (active == null || active._done || active._prepared) return;
        try { active.Prepare(); }
        catch (Exception exception)
        {
            active._done = true;
            active._speed.ChangeSpeed(0f);
            active.SaveRecords();
            Debug.LogError("[T3MPTEST] Flood regression FAIL: " + exception);
            throw;
        }
    }
    private static void AfterWaterObjects() { if (_active != null && !_active._done && _active._prepared) _active.SampleAndDrive(); }
    private void SampleAndDrive()
    {
        try
        {
            if (_capturePending)
            {
                Drive(_mode == "CaptureWet", _mode == "CaptureDry");
                return;
            }
            _ticks++;
            _frames.Add(Time.frameCount);
            CheckDeferredStatus();
            foreach (var entry in _entries)
            {
                var water = (int)Get(entry.Water, "WaterAboveBase");
                var expectedWater = Math.Max(0, Height(entry.Position) - entry.Position.z);
                var met = (bool)Get(entry.Needs, "WaterNeedsAreMet");
                var progress = Get(entry.Dying, "DyingProgress");
                var dying = (bool)Get(progress, "IsDying");
                var dead = (bool)Get(entry.Living, "IsDead");
                var status = (bool)Get(entry.Status, "IsActive");
                if (water != expectedWater || met != (water == 0) || dead || dying == met || status != dying ||
                    (met && (float)Get(progress, "Progress") != 0f))
                    throw new InvalidOperationException("Flood state mismatch at water tick " + _ticks + " entity=" + entry.Id + " expectedWater=" + expectedWater + " state=" + State(entry));
                _states.Add(_ticks + "\t" + entry.Id + "\t" + entry.Name + "\t" + entry.Position.x + "\t" + entry.Position.y + "\t" + entry.Position.z + "\t" + State(entry));
                if (_ticks >= 17 && _ticks <= 48 && !met) entry.Wet[0] = true;
                if (_ticks >= 49 && _ticks <= 112 && met && entry.Wet[0]) entry.Recovered[0] = true;
                if (_ticks >= 113 && _ticks <= 144 && !met) entry.Wet[1] = true;
                if (_ticks >= 145 && met && entry.Wet[1]) entry.Recovered[1] = true;
                if ((_ticks == 112 || _ticks == 208) && !met) throw new InvalidOperationException("Drain fixture did not become dry");
            }
            if (Reload)
            {
                if (_ticks == 64)
                {
                    if (_entries.Any(e => !(bool)Get(e.Needs, "WaterNeedsAreMet") || (bool)Get(e.Status, "IsActive") ||
                        (float)Get(Get(e.Dying, "DyingProgress"), "Progress") != 0f))
                        throw new InvalidOperationException("Reload did not recover by water tick 64");
                    Complete("mode=" + _mode + " waterTicks=64 stateChecks=384 initialChecks=6");
                }
                else Drive(false, true);
                return;
            }
            if ((_mode == "CaptureWet" && _ticks == 48) || (_mode == "CaptureDry" && _ticks == 112))
            {
                _capturePending = true;
                _speed.ChangeSpeed(0f);
                Drive(_mode == "CaptureWet", _mode == "CaptureDry");
                return;
            }
            if (_ticks == 208)
            {
                if (_entries.Any(e => e.Wet.Any(v => !v) || e.Recovered.Any(v => !v))) throw new InvalidOperationException("Wet/recovery fixture not reached twice for every tree");
                _done = true;
                _speed.ChangeSpeed(0f);
                SaveRecords();
                var result = "PASS targets=6 waterTicks=208 stateChecks=1248 wetCycles=2 recoveredCycles=2 frames=" + _frames.Count;
                File.WriteAllText(Path.Combine(_output, "result.txt"), result);
                Debug.Log("[T3MPTEST] Flood regression " + result);
                return;
            }
            var wet = (_ticks >= 16 && _ticks < 48) || (_ticks >= 112 && _ticks < 144);
            var drain = (_ticks >= 48 && _ticks < 112) || _ticks >= 144;
            Drive(wet, drain);
        }
        catch (Exception exception)
        {
            _done = true;
            _speed.ChangeSpeed(0f);
            SaveRecords();
            Debug.LogError("[T3MPTEST] Flood regression FAIL: " + exception);
            throw;
        }
    }
    private void SaveRecords()
    {
        File.WriteAllLines(Path.Combine(_output, "states.tsv"), _states);
        File.WriteAllLines(Path.Combine(_output, "events.tsv"), _events);
    }
    private void OnDestroy()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        foreach (var entry in _entries)
            foreach (var (sender, info, callback) in entry.Subscriptions) info.RemoveEventHandler(sender, callback);
    }
    private sealed class Entry
    {
        internal readonly Guid Id;
        internal readonly string Name;
        internal readonly Vector3Int Position;
        internal readonly object Water, Needs, Dying, Living, Status;
        internal readonly List<Vector3Int> WetCells, DrainCells;
        internal readonly bool[] Wet = new bool[2], Recovered = new bool[2];
        internal readonly List<(object Sender, EventInfo Info, Delegate Callback)> Subscriptions = new();
        internal Entry(Guid id, string name, Vector3Int position, object water, object needs, object dying, object living, object status,
            List<Vector3Int> wetCells, List<Vector3Int> drainCells)
        { Id=id; Name=name; Position=position; Water=water; Needs=needs; Dying=dying; Living=living; Status=status; WetCells=wetCells; DrainCells=drainCells; }
    }
}
