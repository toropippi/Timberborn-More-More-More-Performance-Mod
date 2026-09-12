using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Bindito.Core;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only observer. "Stuck" means the same nonempty visitor sets at
// three successive samples, not proof that no visits occurred between samples.
// No patches, subscriptions, UpdateVisit calls, or repairs. Enabled by -t3mpTestTubeLights.
public sealed class TubeLightMonitor : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const float IntervalSeconds = 10f;
    private const int MaxExamples = 5;
    private static readonly object ReadError = new object();
    private EntityRegistry _registry = null!;
    private object _visitorRegistry = null!;
    private MethodInfo _getTube = null!, _getMovementAnimator = null!;
    private MethodInfo _getTubeIlluminator = null!, _getIlluminator = null!, _getLightingRenderers = null!, _getTubeModel = null!;
    private MethodInfo? _getShaderUserValue;
    private object[]? _shaderUserValueArguments;
    private MethodInfo? _getCharacter;
    private MethodInfo _worldToGrid = null!;
    private FieldInfo _beavers = null!, _bots = null!, _registryVisitors = null!;
    private FieldInfo _lastGrid = null!, _currentTube = null!, _enterer = null!, _model = null!;
    private FieldInfo _block = null!, _pathFollower = null!;
    private FieldInfo _illuminatorToggle = null!, _toggleOn = null!, _illumOn = null!, _turnedOnToggles = null!, _disabledToggles = null!;
    private FieldInfo _renderers = null!, _currentModel = null!;
    private PropertyInfo _hasVisitor = null!, _coordinates = null!, _finished = null!;
    private PropertyInfo _inside = null!, _modelPosition = null!, _stopped = null!;
    private PropertyInfo? _alive;
    private Dictionary<object, History> _previous = new Dictionary<object, History>(Identity.Instance);
    private Dictionary<object, int> _previousVisual = new Dictionary<object, int>(Identity.Instance);
    private readonly HashSet<object> _cachedRenderers = new HashSet<object>(Identity.Instance);
    private readonly List<MeshRenderer> _modelRenderers = new List<MeshRenderer>();
    private readonly HashSet<object> _entities = new HashSet<object>(Identity.Instance);
    private readonly HashSet<object> _registered = new HashSet<object>(Identity.Instance);
    private readonly Dictionary<object, VisitorState> _visitorStates = new Dictionary<object, VisitorState>(Identity.Instance);
    private readonly List<string> _lines = new List<string>();
    private float _next;
    private int _sample, _errors;
    private bool _ready;

    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestTubeLights", StringComparison.OrdinalIgnoreCase));

    internal void Initialize(EntityRegistry registry, IContainer container)
    {
        try
        {
            _registry = registry;
            Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
            var tube = Find("Timberborn.TubeSystem.Tube");
            var visitor = Find("Timberborn.TubeSystem.TubeVisitor");
            var visitorRegistry = Find("Timberborn.TubeSystem.TubeVisitorRegistry");
            var movement = Find("Timberborn.CharacterMovementSystem.MovementAnimator");
            var getComponent = typeof(BaseComponent).GetMethod("GetComponent", All)!;
            _getTube = getComponent.MakeGenericMethod(tube);
            _getMovementAnimator = getComponent.MakeGenericMethod(movement);
            var tubeIlluminator = Find("Timberborn.TubeSystem.TubeIlluminator");
            var illuminator = Find("Timberborn.Illumination.Illuminator");
            var lightingRenderers = Find("Timberborn.Rendering.MaterialLightingRenderers");
            var tubeModel = Find("Timberborn.TubeSystem.TubeModel");
            _getTubeIlluminator = getComponent.MakeGenericMethod(tubeIlluminator);
            _getIlluminator = getComponent.MakeGenericMethod(illuminator);
            _getLightingRenderers = getComponent.MakeGenericMethod(lightingRenderers);
            _getTubeModel = getComponent.MakeGenericMethod(tubeModel);
            _illuminatorToggle = Field(tubeIlluminator, "_illuminatorToggle");
            _toggleOn = Field(_illuminatorToggle.FieldType, "_isOn");
            _illumOn = Field(illuminator, "_isOn");
            _turnedOnToggles = Field(illuminator, "_turnedOnToggles");
            _disabledToggles = Field(illuminator, "_disabledToggles");
            _renderers = Field(lightingRenderers, "_renderers");
            _currentModel = Field(tubeModel, "_currentModel");
            ResolveRendererValueGetter();
            _beavers = Field(tube, "_beaverVisitors");
            _bots = Field(tube, "_botVisitors");
            _hasVisitor = Property(tube, "HasAnyVisitor");
            _block = Field(tube, "_blockObject");
            _coordinates = Property(_block.FieldType, "Coordinates");
            _finished = Property(_block.FieldType, "IsFinished");
            _lastGrid = Field(visitor, "_lastGridPosition");
            _currentTube = Field(visitor, "_currentTube");
            _enterer = Field(visitor, "_enterer");
            _model = Field(visitor, "_characterModel");
            _inside = Property(_enterer.FieldType, "IsInside");
            _modelPosition = Property(_model.FieldType, "Position");
            _registryVisitors = Field(visitorRegistry, "_tubeVisitors");
            _visitorRegistry = container.GetInstance(visitorRegistry);
            _pathFollower = Field(movement, "_animatedPathFollower");
            _stopped = Property(_pathFollower.FieldType, "Stopped");
            _worldToGrid = Find("Timberborn.Navigation.NavigationCoordinateSystem")
                .GetMethod("WorldToGridInt", All, null, new[] { typeof(Vector3) }, null)!;
            if (_worldToGrid == null) throw new MissingMethodException("NavigationCoordinateSystem.WorldToGridInt");
            var character = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Timberborn.Characters.Character")).FirstOrDefault(t => t != null);
            if (character != null)
            {
                _getCharacter = getComponent.MakeGenericMethod(character);
                _alive = character.GetProperty("Alive", All);
            }
            _next = Time.realtimeSinceStartup + IntervalSeconds;
            _ready = true;
            Debug.Log("[T3MPTEST] tubelight started intervalSeconds=10 stuckSamples=3 maxStuckTubes=5");
        }
        catch (Exception exception)
        {
            enabled = false;
            Debug.Log("[T3MPTEST] tubelight disabled reason=" + exception.GetBaseException().GetType().Name + " message=" + OneLine(exception.GetBaseException().Message));
        }
    }

    private void LateUpdate()
    {
        var now = Time.realtimeSinceStartup;
        if (!_ready || now < _next) return;
        _next = now + IntervalSeconds;
        _sample++;
        _errors = 0;
        _lines.Clear();
        try { Sample(); }
        catch (Exception exception)
        {
            // An incomplete scan must not bridge a consecutive-sample streak.
            _previous.Clear();
            _previousVisual.Clear();
            Error("sample", exception);
            Debug.Log($"[T3MPTEST] tubelight sampleFailed sample={_sample} errors={_errors}");
        }
        foreach (var line in _lines) Debug.Log(line);
        _visitorStates.Clear();
        _entities.Clear();
        _registered.Clear();
        _cachedRenderers.Clear();
        _modelRenderers.Clear();
    }

    private void Sample()
    {
        var tubes = new List<(EntityComponent Entity, object Tube)>();
        // One reflected component lookup per entity; membership checks below are O(1).
        foreach (var entity in _registry.Entities)
        {
            if (ReferenceEquals(entity, null)) continue;
            _entities.Add(entity);
            try
            {
                var tube = _getTube.Invoke(entity, null);
                if (tube != null) tubes.Add((entity, tube));
            }
            catch (Exception exception) { Error("entityScan", exception); }
        }

        // Count reference occurrences in each source, not distinct visitors. A
        // stale visitor in both a tube set and the registry contributes twice.
        int registryVisitors = 0, invalidRegistryReferences = 0, invalidTubeReferences = 0;
        foreach (var visitor in (IEnumerable)_registryVisitors.GetValue(_visitorRegistry)!)
        {
            registryVisitors++;
            if (visitor != null) _registered.Add(visitor);
            if (State(visitor).Invalid) invalidRegistryReferences++;
        }

        int lit = 0, stuck = 0, examples = 0, inconsistent = 0;
        int visualLitNoVisitor = 0, visualStuck = 0, toggleMismatch = 0, illuminatorMismatch = 0;
        int rendererMismatch = 0, uncoveredLit = 0, uncoveredRenderers = 0;
        var visualExamples = new int[5];
        var next = new Dictionary<object, History>(Identity.Instance);
        var nextVisual = new Dictionary<object, int>(Identity.Instance);
        foreach (var entry in tubes)
        {
            var tubeId = "unknown";
            try
            {
                tubeId = entry.Entity.EntityId.ToString();
                var isLit = (bool)_hasVisitor.GetValue(entry.Tube)!;
                if (isLit) lit++;
                var beavers = Snapshot(_beavers.GetValue(entry.Tube)!);
                var bots = Snapshot(_bots.GetValue(entry.Tube)!);
                int streak = 0;
                if (isLit)
                {
                    streak = _previous.TryGetValue(entry.Tube, out var old) &&
                             old.Beavers.SetEquals(beavers) && old.Bots.SetEquals(bots)
                        ? old.Streak + 1 : 1;
                    next.Add(entry.Tube, new History(beavers, bots, streak));
                }
                var show = streak >= 3 && examples < MaxExamples;
                if (streak >= 3) stuck++;
                object? coordinates = null;
                if (show)
                {
                    examples++;
                    coordinates = Read(() => _coordinates.GetValue(_block.GetValue(entry.Tube)), "tubeCoordinates");
                    var finished = Read(() => _finished.GetValue(_block.GetValue(entry.Tube)), "tubeFinished");
                    _lines.Add($"[T3MPTEST] tubelight stuck sample={_sample} tube={tubeId} coordinates={Value(coordinates)} finished={Value(finished)} streak={streak} beavers={beavers.Count} bots={bots.Count}");
                }
                Inspect(beavers, "beaver");
                Inspect(bots, "bot");
                try
                {
                    var visual = Visual(entry.Entity);
                    var visualLit = !isLit && (visual.MaxVisibleValue > 0 || visual.MaxUncoveredValue > 0);
                    var visualStreak = visualLit
                        ? (_previousVisual.TryGetValue(entry.Tube, out var previousStreak) ? previousStreak + 1 : 1) : 0;
                    if (visualLit)
                    {
                        visualLitNoVisitor++;
                        nextVisual.Add(entry.Tube, visualStreak);
                    }
                    uncoveredRenderers += visual.Uncovered;
                    if (visualStreak >= 3)
                    {
                        visualStuck++;
                        Example(0, "visualStuck");
                    }
                    if (visual.ToggleOn != isLit)
                    {
                        toggleMismatch++;
                        Example(1, "toggleMismatch");
                    }
                    if (visual.IllumOn != (visual.TurnedOn > 0 && visual.Disabled == 0) || visual.IllumOn != isLit)
                    {
                        illuminatorMismatch++;
                        Example(2, "illuminatorMismatch");
                    }
                    if (_getShaderUserValue != null &&
                        (visual.IllumOn ? visual.VisibleZero : visual.MaxVisibleValue > 0))
                    {
                        rendererMismatch++;
                        Example(3, "rendererMismatch");
                    }
                    if (visual.MaxUncoveredValue > 0)
                    {
                        uncoveredLit++;
                        Example(4, "uncoveredLit");
                    }

                    void Example(int categoryIndex, string category)
                    {
                        if (visualExamples[categoryIndex] >= MaxExamples) return;
                        visualExamples[categoryIndex]++;
                        coordinates ??= Read(() => _coordinates.GetValue(_block.GetValue(entry.Tube)), "tubeCoordinates");
                        var finished = Read(() => _finished.GetValue(_block.GetValue(entry.Tube)), "tubeFinished");
                        _lines.Add($"[T3MPTEST] tubelight visual sample={_sample} tube={tubeId} coordinates={Value(coordinates)} category={category} streak={visualStreak} hasVisitor={Value(isLit)} toggleOn={Value(visual.ToggleOn)} illumOn={Value(visual.IllumOn)} turnedOn={visual.TurnedOn} disabled={visual.Disabled} cached={visual.Cached} visible={visual.Visible} maxVisibleValue={visual.MaxVisibleValue} uncovered={visual.Uncovered} maxUncoveredValue={visual.MaxUncoveredValue} currentModel={visual.CurrentModel} finished={Value(finished)} beavers={beavers.Count} bots={bots.Count}");
                    }
                }
                catch (Exception exception) { Error("visual tube=" + tubeId, exception); }

                void Inspect(HashSet<object> visitors, string kind)
                {
                    foreach (var visitor in visitors)
                    {
                        try
                        {
                            var state = State(visitor);
                            if (state.Invalid) invalidTubeReferences++;
                            var current = visitor == null ? null : Read(() => _currentTube.GetValue(visitor), "currentTube");
                            if (!ReferenceEquals(current, ReadError) && !ReferenceEquals(current, entry.Tube))
                            {
                                inconsistent++;
                                _lines.Add($"[T3MPTEST] tubelight inconsistent sample={_sample} tube={tubeId} kind={kind} visitor={state.Id} currentTube={TubeId(current)} currentTubeIsThis=false registered={Value(visitor != null && _registered.Contains(visitor))}");
                            }
                            if (show) Detail(visitor, state, current, entry.Tube, tubeId, kind, coordinates);
                        }
                        catch (Exception exception) { Error("visitor tube=" + tubeId, exception); }
                    }
                }
            }
            catch (Exception exception) { Error("tube=" + tubeId, exception); }
        }
        _previous = next; // Drop unlit, deleted and unreadable tubes every sample.
        _previousVisual = nextVisual; // Only complete visual reads can extend a streak.
        Debug.Log($"[T3MPTEST] tubelight summary sample={_sample} timeScale={Time.timeScale.ToString("F1", CultureInfo.InvariantCulture)} tubes={tubes.Count} lit={lit} stuck={stuck} registryVisitors={registryVisitors} invalidVisitorReferences={invalidTubeReferences + invalidRegistryReferences} invalidTubeVisitorReferences={invalidTubeReferences} invalidRegistryVisitorReferences={invalidRegistryReferences} inconsistentVisitorReferences={inconsistent} errors={_errors} visualLitNoVisitor={visualLitNoVisitor} visualStuck={visualStuck} toggleMismatch={toggleMismatch} illuminatorMismatch={illuminatorMismatch} rendererMismatch={rendererMismatch} uncoveredLit={uncoveredLit} uncoveredRenderers={uncoveredRenderers} rendererValueChecked={Value(_getShaderUserValue != null)}");
    }

    private void ResolveRendererValueGetter()
    {
        // This Unity build declares the getter on MeshRenderer (and SkinnedMeshRenderer), not on Renderer.
        var getters = typeof(MeshRenderer).GetMethods(All).Where(m => m.Name == "GetShaderUserValue" && !m.IsStatic && !m.ContainsGenericParameters).ToArray();
        _getShaderUserValue = getters.FirstOrDefault(m => m.ReturnType == typeof(uint) && m.GetParameters().Length == 0) ??
            getters.FirstOrDefault(m =>
            {
                var parameters = m.GetParameters();
                return m.ReturnType == typeof(void) && parameters.Length == 1 &&
                       parameters[0].IsOut && parameters[0].ParameterType == typeof(uint).MakeByRefType();
            });
        if (_getShaderUserValue == null)
        {
            Debug.Log("[T3MPTEST] tubelight rendererValueUnavailable");
            return;
        }
        if (_getShaderUserValue.GetParameters().Length == 1) _shaderUserValueArguments = new object[] { 0u };
        Debug.Log("[T3MPTEST] tubelight rendererValueGetter signature=" + OneLine(_getShaderUserValue.ToString()!));
    }

    private uint RendererValue(MeshRenderer renderer)
    {
        if (_getShaderUserValue == null) return 0;
        if (_shaderUserValueArguments == null) return (uint)_getShaderUserValue.Invoke(renderer, null)!;
        _shaderUserValueArguments[0] = 0u;
        _getShaderUserValue.Invoke(renderer, _shaderUserValueArguments);
        return (uint)_shaderUserValueArguments[0];
    }

    private VisualState Visual(EntityComponent entity)
    {
        var toggle = _illuminatorToggle.GetValue(_getTubeIlluminator.Invoke(entity, null));
        var illuminator = _getIlluminator.Invoke(entity, null);
        var renderers = (List<MeshRenderer>)_renderers.GetValue(_getLightingRenderers.Invoke(entity, null))!;
        var model = (GameObject?)_currentModel.GetValue(_getTubeModel.Invoke(entity, null));
        var state = new VisualState
        {
            ToggleOn = (bool)_toggleOn.GetValue(toggle)!,
            IllumOn = (bool)_illumOn.GetValue(illuminator)!,
            TurnedOn = (int)_turnedOnToggles.GetValue(illuminator)!,
            Disabled = (int)_disabledToggles.GetValue(illuminator)!,
            Cached = renderers.Count,
            CurrentModel = model == null ? "null" : OneLine(model.name)
        };
        // Reuse scratch buffers, but refresh membership and the current hierarchy
        // every sample so model changes and renderer recollection are observable.
        _cachedRenderers.Clear();
        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            _cachedRenderers.Add(renderer);
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            state.Visible++;
            if (_getShaderUserValue == null) continue;
            var value = RendererValue(renderer);
            state.MaxVisibleValue = Math.Max(state.MaxVisibleValue, value);
            if (value == 0) state.VisibleZero = true;
        }
        _modelRenderers.Clear();
        if (model != null) model.GetComponentsInChildren(true, _modelRenderers);
        foreach (var renderer in _modelRenderers)
        {
            if (renderer == null || _cachedRenderers.Contains(renderer)) continue;
            state.Uncovered++;
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || _getShaderUserValue == null) continue;
            state.MaxUncoveredValue = Math.Max(state.MaxUncoveredValue, RendererValue(renderer));
        }
        return state;
    }

    private VisitorState State(object? visitor)
    {
        if (visitor == null) return new VisitorState("null", false, true);
        if (_visitorStates.TryGetValue(visitor, out var cached)) return cached;
        var component = (BaseComponent)visitor;
        var entity = Read(() => component.GetComponent<EntityComponent>(), "visitorEntity");
        var id = entity is EntityComponent e ? Read(() => e.EntityId.ToString(), "visitorId") : entity;
        var inRegistry = ReferenceEquals(entity, ReadError) ? (object)ReadError : entity != null && _entities.Contains(entity);
        // BaseComponent is a managed object: test its Unity GameObject explicitly.
        var destroyed = Read(() => component.GameObject == null, "visitorGameObject");
        var state = new VisitorState(Value(id), inRegistry, destroyed);
        _visitorStates.Add(visitor, state);
        return state;
    }

    private void Detail(object? visitor, VisitorState state, object? current, object tube, string tubeId, string kind, object? coordinates)
    {
        var component = visitor as BaseComponent;
        var inside = visitor == null ? null : Read(() => Get(_inside, _enterer.GetValue(visitor)), "inside");
        var model = visitor == null ? null : Read(() => Get(_modelPosition, _model.GetValue(visitor)), "modelPosition");
        var grid = model is Vector3 position ? Read(() => _worldToGrid.Invoke(null, new object[] { position }), "modelGrid") : model;
        var lastGrid = visitor == null ? null : Read(() => _lastGrid.GetValue(visitor), "lastGrid");
        var transform = component == null ? null : Read(() => component.Transform == null ? null : (object)component.Transform.position, "entityPosition");
        var alive = component == null || _getCharacter == null || _alive == null ? "unavailable" :
            Read(() => Get(_alive, _getCharacter.Invoke(component, null)), "alive");
        var stopped = component == null ? null : Read(() =>
        {
            var animator = _getMovementAnimator.Invoke(component, null);
            return animator == null ? null : Get(_stopped, _pathFollower.GetValue(animator));
        }, "animatedPathStopped");
        var matches = grid is Vector3Int cell && coordinates is Vector3Int tubeCell ? (object)(cell == tubeCell) : "unavailable";
        var isThis = ReferenceEquals(current, ReadError) ? ReadError : (object)ReferenceEquals(current, tube);
        _lines.Add($"[T3MPTEST] tubelight visitor sample={_sample} tube={tubeId} kind={kind} visitor={state.Id} entityInRegistry={Value(state.InRegistry)} gameObjectDestroyed={Value(state.Destroyed)} alive={Value(alive)} inside={Value(inside)} model={Value(model)} lastGrid={Value(lastGrid)} currentTube={TubeId(current)} currentTubeIsThis={Value(isThis)} entityPosition={Value(transform)} modelGrid={Value(grid)} tubeCoordinates={Value(coordinates)} modelGridIsTube={Value(matches)} registered={Value(visitor != null && _registered.Contains(visitor))} animatedPathStopped={Value(stopped)}");
    }

    private string TubeId(object? tube) => tube == null || ReferenceEquals(tube, ReadError) ? Value(tube) :
        Value(Read(() => ((BaseComponent)tube).GetComponent<EntityComponent>()?.EntityId.ToString(), "currentTubeId"));

    private object? Read(Func<object?> read, string stage)
    {
        try { return read(); }
        catch (Exception exception) { Error(stage, exception); return ReadError; }
    }

    private void Error(string stage, Exception exception)
    {
        _errors++;
        if (_errors <= MaxExamples)
            _lines.Add($"[T3MPTEST] tubelight error sample={_sample} stage={stage} type={exception.GetBaseException().GetType().Name} message={OneLine(exception.GetBaseException().Message)}");
    }

    private static object? Get(PropertyInfo property, object? instance) => instance == null ? null : property.GetValue(instance);
    private static FieldInfo Field(Type type, string name) => type.GetField(name, All) ?? throw new MissingFieldException(type.FullName, name);
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name, All) ?? throw new MissingMemberException(type.FullName, name);
    private static HashSet<object> Snapshot(object visitors)
    {
        var snapshot = new HashSet<object>(Identity.Instance);
        foreach (var visitor in (IEnumerable)visitors) snapshot.Add(visitor!);
        return snapshot;
    }

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
    private static string Value(object? value)
    {
        if (value == null) return "null";
        if (ReferenceEquals(value, ReadError)) return "error";
        if (value is bool flag) return flag ? "true" : "false";
        if (value is Vector3 v) return string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", v.x, v.y, v.z);
        if (value is Vector3Int c) return string.Format(CultureInfo.InvariantCulture, "({0},{1},{2})", c.x, c.y, c.z);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
    }

    private struct VisualState
    {
        public bool ToggleOn, IllumOn, VisibleZero;
        public int TurnedOn, Disabled, Cached, Visible, Uncovered;
        public uint MaxVisibleValue, MaxUncoveredValue;
        public string CurrentModel;
    }

    private sealed class VisitorState
    {
        public readonly string Id;
        public readonly object? InRegistry, Destroyed;
        public bool Invalid => InRegistry is false || Destroyed is true;
        public VisitorState(string id, object? inRegistry, object? destroyed)
        { Id = id; InRegistry = inRegistry; Destroyed = destroyed; }
    }

    private sealed class History
    {
        public readonly HashSet<object> Beavers, Bots;
        public readonly int Streak;
        public History(HashSet<object> beavers, HashSet<object> bots, int streak)
        { Beavers = beavers; Bots = bots; Streak = streak; }
    }

    // Reference identity keeps stale managed components distinct even after their
    // Unity objects are destroyed, and avoids order/hash-only streak comparisons.
    private sealed class Identity : IEqualityComparer<object>
    {
        public static readonly Identity Instance = new Identity();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
