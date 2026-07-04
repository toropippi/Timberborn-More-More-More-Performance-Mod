using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.MechanicalSystem;
using Timberborn.SelectionSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Automated, fully reproducible driver for the topology-UI investigation
// (game arg '-benchTopoUi'): after the save loads it forces speed x50
// (rendered, no blackout), then alternately selects a node of the LARGEST
// mechanical graph and the deterministically-first finished path, holding
// each selection for a few seconds. Selecting a gear runs the full network
// DFS + rehighlight; selecting a path runs the district flow-field + overlay
// rebuild - the same code paths that hitch when connecting gears/paths by
// hand. Targets are picked from the save data itself (largest graph, lowest
// coordinates), so the same save always selects the same objects.
internal sealed class TopologyUiScenario : MonoBehaviour
{
    private enum Phase
    {
        WaitingForLoad,
        Settling,
        SelectGear,
        HoldGear,
        IdleAfterGear,
        SelectPath,
        HoldPath,
        IdleAfterPath,
        Complete
    }

    private static TopologyUiScenario? _instance;
    private static EntitySelectionService? _entitySelectionService;
    private static MechanicalGraphRegistry? _mechanicalGraphRegistry;
    private static object? _speedManager;
    private static readonly List<BaseComponent> PathDrawerOwners = new List<BaseComponent>();
    private static float _selectionServiceCapturedRealtime;

    private static Type? _speedManagerType;
    private static MethodInfo? _unlockSpeedMethod;
    private static MethodInfo? _changeSpeedMethod;

    private Phase _phase = Phase.WaitingForLoad;
    private float _phaseUntilRealtime;
    private int _completedCycles;
    private bool _speedForced;

    public static void Install()
    {
        if (_instance is not null)
        {
            return;
        }

        var gameObject = new GameObject("T3MP_TopologyUiScenario");
        DontDestroyOnLoad(gameObject);
        _instance = gameObject.AddComponent<TopologyUiScenario>();
        Debug.Log("[T3MP] TopoUI scenario installed (waiting for save load).");
    }

    public static void RecordEntitySelectionService(object instance)
    {
        if (instance is EntitySelectionService service && !ReferenceEquals(_entitySelectionService, service))
        {
            _entitySelectionService = service;
            _selectionServiceCapturedRealtime = Time.realtimeSinceStartup;
        }
    }

    public static void RecordMechanicalGraphRegistry(object instance)
    {
        if (instance is MechanicalGraphRegistry registry)
        {
            _mechanicalGraphRegistry = registry;
        }
    }

    public static void RecordPathRangeDrawerOwner(object instance)
    {
        if (instance is BaseComponent component)
        {
            PathDrawerOwners.Add(component);
        }
    }

    public static void RecordSpeedManager(object instance)
    {
        if (!BenchmarkSettings.BenchTopoUiRequested)
        {
            return;
        }

        _speedManager = instance;
    }

    private void Update()
    {
        try
        {
            Step();
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] TopoUI scenario step failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void Step()
    {
        var now = Time.realtimeSinceStartup;
        switch (_phase)
        {
            case Phase.WaitingForLoad:
                if (_entitySelectionService is not null && _mechanicalGraphRegistry is not null)
                {
                    _phase = Phase.Settling;
                    _phaseUntilRealtime = _selectionServiceCapturedRealtime + BenchmarkSettings.TopoUiScenarioSettleSeconds;
                    Debug.Log(string.Format(
                        CultureInfo.InvariantCulture,
                        "[T3MP] TopoUI scenario: save loaded, settling until t={0:0.0}s.",
                        _phaseUntilRealtime));
                }

                break;

            case Phase.Settling:
                if (!_speedForced && _speedManager is not null)
                {
                    ForceTargetSpeed();
                }

                if (now >= _phaseUntilRealtime)
                {
                    LogScenarioTargets();
                    _phase = Phase.SelectGear;
                }

                break;

            case Phase.SelectGear:
                SelectGearNode();
                _phase = Phase.HoldGear;
                _phaseUntilRealtime = now + BenchmarkSettings.TopoUiScenarioHoldSeconds;
                break;

            case Phase.HoldGear:
                if (now >= _phaseUntilRealtime)
                {
                    Unselect();
                    _phase = Phase.IdleAfterGear;
                    _phaseUntilRealtime = now + BenchmarkSettings.TopoUiScenarioIdleSeconds;
                }

                break;

            case Phase.IdleAfterGear:
                if (now >= _phaseUntilRealtime)
                {
                    _phase = Phase.SelectPath;
                }

                break;

            case Phase.SelectPath:
                SelectPathObject();
                _phase = Phase.HoldPath;
                _phaseUntilRealtime = now + BenchmarkSettings.TopoUiScenarioHoldSeconds;
                break;

            case Phase.HoldPath:
                if (now >= _phaseUntilRealtime)
                {
                    Unselect();
                    _phase = Phase.IdleAfterPath;
                    _phaseUntilRealtime = now + BenchmarkSettings.TopoUiScenarioIdleSeconds;
                }

                break;

            case Phase.IdleAfterPath:
                if (now >= _phaseUntilRealtime)
                {
                    _completedCycles++;
                    if (_completedCycles >= BenchmarkSettings.TopoUiScenarioCycles)
                    {
                        Debug.Log(string.Format(
                            CultureInfo.InvariantCulture,
                            "[T3MP] TopoUI scenario complete. cycles={0}",
                            _completedCycles));
                        _phase = Phase.Complete;
                    }
                    else
                    {
                        _phase = Phase.SelectGear;
                    }
                }

                break;

            case Phase.Complete:
                break;
        }
    }

    private void ForceTargetSpeed()
    {
        var speedManager = _speedManager;
        if (speedManager is null)
        {
            return;
        }

        EnsureSpeedManagerMembers(speedManager.GetType());
        try
        {
            _unlockSpeedMethod?.Invoke(speedManager, null);
            _changeSpeedMethod?.Invoke(speedManager, new object[] { BenchmarkSettings.TopoUiTargetSpeed });
            _speedForced = true;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] TopoUI scenario forced speed x{0}.",
                BenchmarkSettings.TopoUiTargetSpeed));
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] TopoUI scenario speed force failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void EnsureSpeedManagerMembers(Type speedManagerType)
    {
        if (_speedManagerType == speedManagerType)
        {
            return;
        }

        _speedManagerType = speedManagerType;
        _unlockSpeedMethod = speedManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "UnlockSpeed" && method.GetParameters().Length == 0);
        _changeSpeedMethod = speedManagerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "ChangeSpeed" &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(float);
            });
    }

    private void LogScenarioTargets()
    {
        var graphCount = 0;
        var largestGraphNodes = 0;
        if (_mechanicalGraphRegistry is not null)
        {
            foreach (var graph in _mechanicalGraphRegistry.MechanicalGraphs)
            {
                graphCount++;
                var nodes = graph.Nodes.Count();
                if (nodes > largestGraphNodes)
                {
                    largestGraphNodes = nodes;
                }
            }
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] TopoUI scenario targets: mechanicalGraphs={0}, largestGraphNodes={1}, pathDrawerOwners={2}.",
            graphCount,
            largestGraphNodes,
            PathDrawerOwners.Count));
    }

    private void SelectGearNode()
    {
        var node = FindGearNode();
        if (node is null)
        {
            Debug.LogWarning("[T3MP] TopoUI scenario: no finished mechanical node found to select.");
            return;
        }

        var coordinates = node.GetComponent<BlockObject>()?.Coordinates ?? default;
        _entitySelectionService?.Select(node);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] TopoUI scenario: selected gear node at ({0},{1},{2}). selected={3}",
            coordinates.x,
            coordinates.y,
            coordinates.z,
            _entitySelectionService?.IsAnythingSelected ?? false));
    }

    private MechanicalNode? FindGearNode()
    {
        if (_mechanicalGraphRegistry is null)
        {
            return null;
        }

        MechanicalGraph? largestGraph = null;
        var largestCount = 0;
        foreach (var graph in _mechanicalGraphRegistry.MechanicalGraphs)
        {
            var count = graph.Nodes.Count();
            if (count > largestCount)
            {
                largestCount = count;
                largestGraph = graph;
            }
        }

        if (largestGraph is null)
        {
            return null;
        }

        // Deterministic pick: the finished node with the lowest (z,y,x)
        // coordinates, independent of load order.
        MechanicalNode? best = null;
        Vector3Int bestCoordinates = default;
        foreach (var node in largestGraph.Nodes)
        {
            var blockObject = node.GetComponent<BlockObject>();
            if (blockObject is null || !blockObject.IsFinished)
            {
                continue;
            }

            var coordinates = blockObject.Coordinates;
            if (best is null || CompareCoordinates(coordinates, bestCoordinates) < 0)
            {
                best = node;
                bestCoordinates = coordinates;
            }
        }

        return best;
    }

    private void SelectPathObject()
    {
        BaseComponent? best = null;
        Vector3Int bestCoordinates = default;
        foreach (var owner in PathDrawerOwners)
        {
            BlockObject? blockObject;
            try
            {
                blockObject = owner.GetComponent<BlockObject>();
            }
            catch (Exception)
            {
                continue;
            }

            if (blockObject is null || blockObject.IsPreview || !blockObject.IsFinished)
            {
                continue;
            }

            var coordinates = blockObject.Coordinates;
            if (best is null || CompareCoordinates(coordinates, bestCoordinates) < 0)
            {
                best = owner;
                bestCoordinates = coordinates;
            }
        }

        if (best is null)
        {
            Debug.LogWarning("[T3MP] TopoUI scenario: no finished path found to select.");
            return;
        }

        _entitySelectionService?.Select(best);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] TopoUI scenario: selected path at ({0},{1},{2}). selected={3}",
            bestCoordinates.x,
            bestCoordinates.y,
            bestCoordinates.z,
            _entitySelectionService?.IsAnythingSelected ?? false));
    }

    private void Unselect()
    {
        _entitySelectionService?.Unselect();
    }

    private static int CompareCoordinates(Vector3Int left, Vector3Int right)
    {
        var byZ = left.z.CompareTo(right.z);
        if (byZ != 0)
        {
            return byZ;
        }

        var byY = left.y.CompareTo(right.y);
        if (byY != 0)
        {
            return byY;
        }

        return left.x.CompareTo(right.x);
    }
}
