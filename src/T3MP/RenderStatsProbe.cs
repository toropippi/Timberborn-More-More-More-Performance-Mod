using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MP;

/// <summary>
/// Temporary attribution probe enabled only by -benchRenderStats.
/// It measures Unity render counters in three phases:
/// baseline, vertex-animated MeshRenderers disabled, then restored.
/// </summary>
internal static class RenderStatsProbe
{
    private const float StartupDelaySeconds = 3f;
    private const float PhaseSeconds = 5f;
    private const float SettleSeconds = 1f;
    private const int MaxInstancesPerDraw = 1023;
    private const int TopBatchGroups = 20;
    private const string AnimationTimeProperty = "_AnimationTime";

    private static readonly List<Counter> Counters = new List<Counter>();
    private static RendererState[] _rendererStates = Array.Empty<RendererState>();
    private static bool _initialized;
    private static bool _started;
    private static bool _completed;
    private static float _gameSceneEnteredAt;
    private static float _phaseStartedAt;
    private static int _phase;
    private static int _sampleFrames;
    private static double _frameMilliseconds;

    public static void Update(bool inGameScene)
    {
        if (!BenchmarkSettings.BenchRenderStatsRequested || _completed)
        {
            return;
        }

        if (!inGameScene)
        {
            RestoreRenderers();
            _gameSceneEnteredAt = 0f;
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (_gameSceneEnteredAt <= 0f)
        {
            _gameSceneEnteredAt = now;
            return;
        }

        if (!_initialized)
        {
            InitializeCounters();
            _initialized = true;
        }

        if (!_started)
        {
            if (now - _gameSceneEnteredAt < StartupDelaySeconds)
            {
                return;
            }

            DiscoverVertexRenderers();
            _started = true;
            BeginPhase(0, now);
            return;
        }

        var phaseElapsed = now - _phaseStartedAt;
        if (phaseElapsed >= PhaseSeconds)
        {
            LogPhase();
            if (_phase == 0)
            {
                SetRenderersEnabled(false);
                BeginPhase(1, now);
            }
            else if (_phase == 1)
            {
                RestoreRenderers();
                BeginPhase(2, now);
            }
            else
            {
                RestoreRenderers();
                DisposeCounters();
                _completed = true;
                Debug.Log("[T3MP] RenderStats complete.");
            }

            return;
        }

        if (phaseElapsed >= SettleSeconds)
        {
            RecordFrame();
        }
    }

    private static void InitializeCounters()
    {
        var handles = new List<ProfilerRecorderHandle>();
        try
        {
            ProfilerRecorderHandle.GetAvailable(handles);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[T3MP] RenderStats counter enumeration failed: {exception.Message}");
            return;
        }

        var available = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            try
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                var name = description.Name;
                var category = description.Category.Name;
                if (!IsInterestingCounter(name, category))
                {
                    continue;
                }

                available.Add($"{category}/{name}[{description.UnitType}]");
                var key = category + "\0" + name;
                if (!seen.Add(key))
                {
                    continue;
                }

                var recorder = new ProfilerRecorder(
                    handle,
                    1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                if (recorder.Valid)
                {
                    Counters.Add(new Counter(category, name, description.UnitType, recorder));
                }
                else
                {
                    recorder.Dispose();
                }
            }
            catch (Exception)
            {
                // Counter availability differs between Unity player builds.
            }
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RenderStats initialized. availableHandles={0}, selected={1}, counters={2}",
            handles.Count,
            Counters.Count,
            available.Count == 0 ? "<none>" : string.Join(";", available.OrderBy(value => value, StringComparer.Ordinal))));
    }

    private static bool IsInterestingCounter(string name, string category)
    {
        return name.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Batch", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("SetPass", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Triangle", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Vert", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Render Thread", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.Equals("Camera.Render", StringComparison.Ordinal) ||
               name.Equals("RenderLoop.Draw", StringComparison.Ordinal) ||
               name.Equals("Gfx.WaitForPresentOnGfxThread", StringComparison.Ordinal) ||
               category.Equals("Render", StringComparison.OrdinalIgnoreCase) &&
               name.IndexOf("Pass", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void DiscoverVertexRenderers()
    {
        var updaterType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Timberborn.TimbermeshAnimations.VertexAnimationUpdater", false))
            .FirstOrDefault(type => type is not null);
        if (updaterType is null)
        {
            Debug.LogWarning("[T3MP] RenderStats VertexAnimationUpdater type not found.");
            return;
        }

        var states = new List<RendererState>();
        var seen = new HashSet<MeshRenderer>();
        foreach (var found in Object.FindObjectsByType(updaterType, FindObjectsInactive.Include))
        {
            if (found is not Component component ||
                component.gameObject.scene.buildIndex != 2 ||
                component.GetComponent<MeshRenderer>() is not MeshRenderer renderer ||
                !seen.Add(renderer))
            {
                continue;
            }

            states.Add(new RendererState(renderer, renderer.enabled));
        }

        _rendererStates = states.ToArray();
        var active = _rendererStates.Count(state => state.Renderer != null && state.Renderer.enabled && state.Renderer.gameObject.activeInHierarchy);
        var uniqueMaterials = new HashSet<Material>();
        var uniqueMeshes = new HashSet<Mesh>();
        var instancingEnabled = 0;
        var staticBatched = 0;
        var visible = 0;
        var roots = new Dictionary<string, int>(StringComparer.Ordinal);
        var rendererCountsByRoot = new Dictionary<Transform, int>();
        var visibleRoots = new HashSet<Transform>();
        var visibleRootNames = new HashSet<string>(StringComparer.Ordinal);
        var rootKinds = new Dictionary<string, RootKindStats>(StringComparer.Ordinal);
        var shaders = new Dictionary<string, int>(StringComparer.Ordinal);
        var materialNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var shadowModes = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeBatchGroups = new Dictionary<string, BatchGroup>(StringComparer.Ordinal);
        var visibleBatchGroups = new Dictionary<string, BatchGroup>(StringComparer.Ordinal);
        var activeDrawSlots = 0;
        var visibleDrawSlots = 0;
        foreach (var state in _rendererStates)
        {
            var renderer = state.Renderer;
            if (renderer == null)
            {
                continue;
            }

            var material = renderer.sharedMaterial;
            if (material != null)
            {
                uniqueMaterials.Add(material);
                var shaderName = material.shader != null ? material.shader.name : "<null>";
                shaders.TryGetValue(shaderName, out var shaderCount);
                shaders[shaderName] = shaderCount + 1;
                var materialName = material.name.Replace(" (Instance)", string.Empty);
                materialNames.TryGetValue(materialName, out var materialNameCount);
                materialNames[materialName] = materialNameCount + 1;
                if (material.enableInstancing)
                {
                    instancingEnabled++;
                }
            }

            var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh != null)
            {
                uniqueMeshes.Add(mesh);
                if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                {
                    activeDrawSlots += AddBatchGroups(renderer, mesh, activeBatchGroups);
                    if (renderer.isVisible)
                    {
                        visibleDrawSlots += AddBatchGroups(renderer, mesh, visibleBatchGroups);
                    }
                }
            }

            if (renderer.isPartOfStaticBatch)
            {
                staticBatched++;
            }

            var shadowMode = renderer.shadowCastingMode.ToString();
            shadowModes.TryGetValue(shadowMode, out var shadowModeCount);
            shadowModes[shadowMode] = shadowModeCount + 1;

            var root = renderer.transform.root;
            var rootName = root.name;
            roots.TryGetValue(rootName, out var count);
            roots[rootName] = count + 1;
            rendererCountsByRoot.TryGetValue(root, out var rootRendererCount);
            rendererCountsByRoot[root] = rootRendererCount + 1;

            var kind = RootKind(rootName);
            if (!rootKinds.TryGetValue(kind, out var kindStats))
            {
                kindStats = new RootKindStats();
                rootKinds.Add(kind, kindStats);
            }

            kindStats.Roots.Add(root);
            kindStats.Names.Add(rootName);
            kindStats.Renderers++;
            if (renderer.isVisible)
            {
                visible++;
                visibleRoots.Add(root);
                visibleRootNames.Add(rootName);
                kindStats.VisibleRenderers++;
                kindStats.VisibleRoots.Add(root);
                kindStats.VisibleNames.Add(rootName);
            }
        }

        var topRoots = string.Join("|", roots
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(12)
            .Select(pair => pair.Key + ":" + pair.Value));
        var renderersPerRoot = string.Join("|", rendererCountsByRoot.Values
            .GroupBy(value => value)
            .OrderBy(group => group.Key)
            .Select(group => group.Key + ":" + group.Count()));
        var renderersPerNamedRoot = string.Join("|", roots.Values
            .GroupBy(value => value)
            .OrderBy(group => group.Key)
            .Select(group => group.Key + ":" + group.Count()));
        var kinds = string.Join("|", rootKinds
            .OrderByDescending(pair => pair.Value.Renderers)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => string.Format(
                CultureInfo.InvariantCulture,
                "{0}:names={1},renderers={2},visibleNames={3},visibleRenderers={4}",
                pair.Key,
                pair.Value.Names.Count,
                pair.Value.Renderers,
                pair.Value.VisibleNames.Count,
                pair.Value.VisibleRenderers)));
        var shaderSummary = FormatCounts(shaders);
        var materialSummary = FormatCounts(materialNames);
        var shadowSummary = FormatCounts(shadowModes);
        var activeBatchSummary = SummarizeBatchGroups(activeBatchGroups, activeDrawSlots);
        var visibleBatchSummary = SummarizeBatchGroups(visibleBatchGroups, visibleDrawSlots);
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RenderStats renderers={0}, active={1}, visible={2}, transformRoots={3}, visibleTransformRoots={4}, namedRoots={5}, visibleNamedRoots={6}, uniqueMaterials={7}, uniqueMeshes={8}, instancingEnabled={9}, staticBatched={10}, renderersPerTransformRoot={11}, renderersPerNamedRoot={12}, kinds={13}, shaders={14}, materials={15}, shadows={16}, activeBatchCensus={17}, visibleBatchCensus={18}, topRoots={19}",
            _rendererStates.Length,
            active,
            visible,
            rendererCountsByRoot.Count,
            visibleRoots.Count,
            roots.Count,
            visibleRootNames.Count,
            uniqueMaterials.Count,
            uniqueMeshes.Count,
            instancingEnabled,
            staticBatched,
            renderersPerRoot,
            renderersPerNamedRoot,
            kinds,
            shaderSummary,
            materialSummary,
            shadowSummary,
            activeBatchSummary,
            visibleBatchSummary,
            topRoots.Length == 0 ? "<none>" : topRoots));
    }

    private static int AddBatchGroups(
        MeshRenderer renderer,
        Mesh mesh,
        Dictionary<string, BatchGroup> groups)
    {
        var materials = renderer.sharedMaterials;
        if (mesh.subMeshCount <= 0 || materials.Length == 0)
        {
            return 0;
        }

        // Unity draws extra materials on the final submesh. If there are fewer
        // materials than submeshes, the final material is reused. Counting the
        // larger side therefore approximates the actual renderer draw slots.
        var drawSlots = Math.Max(mesh.subMeshCount, materials.Length);
        for (var slot = 0; slot < drawSlots; slot++)
        {
            var subMesh = Math.Min(slot, mesh.subMeshCount - 1);
            var material = materials[Math.Min(slot, materials.Length - 1)];
            if (material == null)
            {
                continue;
            }

            var key = CreateInstancingKey(renderer, mesh, subMesh, material, out var description);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new BatchGroup(description);
                groups.Add(key, group);
            }

            group.Count++;
        }

        return drawSlots;
    }

    internal static string CreateInstancingKey(
        MeshRenderer renderer,
        Mesh mesh,
        int subMesh,
        Material material,
        out string description)
    {
        var shader = material.shader;
        var builder = new System.Text.StringBuilder(1024);
        builder.Append("mesh=").Append(mesh.GetEntityId().ToString());
        builder.Append(";sub=").Append(subMesh);
        builder.Append(";shader=").Append(shader != null ? shader.GetEntityId().ToString() : "0");
        builder.Append(";queue=").Append(material.renderQueue);
        builder.Append(";passCount=").Append(material.passCount);
        builder.Append(";keywords=");
        AppendSorted(builder, material.shaderKeywords);
        builder.Append(";shadow=").Append((int)renderer.shadowCastingMode);
        builder.Append(";receiveShadow=").Append(renderer.receiveShadows ? 1 : 0);
        builder.Append(";motionVectors=").Append((int)renderer.motionVectorGenerationMode);
        builder.Append(";lightProbe=").Append((int)renderer.lightProbeUsage);
        builder.Append(";reflectionProbe=").Append((int)renderer.reflectionProbeUsage);
        builder.Append(";renderingLayerMask=").Append(renderer.renderingLayerMask);
        builder.Append(";layer=").Append(renderer.gameObject.layer);
        builder.Append(";sortingLayer=").Append(renderer.sortingLayerID);
        builder.Append(";sortingOrder=").Append(renderer.sortingOrder);

        var textureSummary = new List<string>();
        if (shader != null)
        {
            try
            {
                var propertyCount = shader.GetPropertyCount();
                for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                {
                    var propertyName = shader.GetPropertyName(propertyIndex);
                    if (propertyName.Equals(AnimationTimeProperty, StringComparison.Ordinal))
                    {
                        // This is deliberately per-instance in the proposed
                        // renderer replacement and must not split a batch.
                        continue;
                    }

                    var propertyType = shader.GetPropertyType(propertyIndex);
                    builder.Append(";p=").Append(propertyName).Append(':').Append((int)propertyType).Append('=');
                    AppendMaterialProperty(builder, material, propertyName, propertyType, textureSummary);
                }
            }
            catch (Exception exception)
            {
                // Preserve a conservative split if a stripped/runtime shader
                // refuses property enumeration instead of merging unknown state.
                builder.Append(";propertyReadError=").Append(exception.GetType().Name);
                builder.Append(";materialId=").Append(material.GetEntityId().ToString());
            }
        }

        var materialName = material.name.Replace(" (Instance)", string.Empty);
        description = string.Format(
            CultureInfo.InvariantCulture,
            "mesh={0},sub={1},shader={2},material={3},textures={4},shadow={5}",
            Clean(mesh.name),
            subMesh,
            shader != null ? Clean(shader.name) : "<null>",
            Clean(materialName),
            textureSummary.Count == 0 ? "<none>" : string.Join("+", textureSummary),
            renderer.shadowCastingMode);
        return builder.ToString();
    }

    private static void AppendMaterialProperty(
        System.Text.StringBuilder builder,
        Material material,
        string propertyName,
        ShaderPropertyType propertyType,
        List<string> textureSummary)
    {
        switch (propertyType)
        {
            case ShaderPropertyType.Texture:
            {
                var texture = material.GetTexture(propertyName);
                builder.Append(texture != null ? texture.GetEntityId().ToString() : "0");
                var scale = material.GetTextureScale(propertyName);
                var offset = material.GetTextureOffset(propertyName);
                AppendVector2(builder, scale);
                AppendVector2(builder, offset);
                if (texture != null)
                {
                    textureSummary.Add(Clean(propertyName) + "=" + Clean(texture.name));
                }

                break;
            }
            case ShaderPropertyType.Color:
            {
                var color = material.GetColor(propertyName);
                AppendFloat(builder, color.r);
                AppendFloat(builder, color.g);
                AppendFloat(builder, color.b);
                AppendFloat(builder, color.a);
                break;
            }
            case ShaderPropertyType.Vector:
                AppendVector4(builder, material.GetVector(propertyName));
                break;
            case ShaderPropertyType.Float:
            case ShaderPropertyType.Range:
            case ShaderPropertyType.Int:
                AppendFloat(builder, material.GetFloat(propertyName));
                break;
            default:
                builder.Append("unsupported");
                break;
        }
    }

    private static string SummarizeBatchGroups(Dictionary<string, BatchGroup> groups, int drawSlots)
    {
        if (groups.Count == 0)
        {
            return "<none>";
        }

        var counts = groups.Values.Select(group => group.Count).ToArray();
        var instancedCalls = counts.Sum(count => (count + MaxInstancesPerDraw - 1) / MaxInstancesPerDraw);
        var groupedSlots = counts.Sum();
        var singletons = counts.Count(count => count == 1);
        var reusableGroups = counts.Count(count => count > 1);
        var reusableSlots = counts.Where(count => count > 1).Sum();
        var top = string.Join("|", groups.Values
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Description, StringComparer.Ordinal)
            .Take(TopBatchGroups)
            .Select(group => group.Count + "x{" + group.Description + "}"));
        return string.Format(
            CultureInfo.InvariantCulture,
            "drawSlots={0},groupedSlots={1},uniqueKeys={2},singletons={3},reusableGroups={4},reusableSlots={5},estimatedInstancedCalls={6},estimatedDrawReduction={7},largest={8},top={9}",
            drawSlots,
            groupedSlots,
            groups.Count,
            singletons,
            reusableGroups,
            reusableSlots,
            instancedCalls,
            Math.Max(0, groupedSlots - instancedCalls),
            counts.Max(),
            top);
    }

    private static void AppendSorted(System.Text.StringBuilder builder, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(value);
            first = false;
        }
    }

    private static void AppendVector2(System.Text.StringBuilder builder, Vector2 value)
    {
        builder.Append('[');
        AppendFloat(builder, value.x);
        AppendFloat(builder, value.y);
        builder.Append(']');
    }

    private static void AppendVector4(System.Text.StringBuilder builder, Vector4 value)
    {
        builder.Append('[');
        AppendFloat(builder, value.x);
        AppendFloat(builder, value.y);
        AppendFloat(builder, value.z);
        AppendFloat(builder, value.w);
        builder.Append(']');
    }

    private static void AppendFloat(System.Text.StringBuilder builder, float value)
    {
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
    }

    private static string Clean(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : value.Replace(';', '_').Replace('|', '_').Replace(',', '_').Replace('{', '_').Replace('}', '_').Trim();
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        return counts.Count == 0
            ? "<none>"
            : string.Join("|", counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(12)
                .Select(pair => pair.Key + ":" + pair.Value));
    }

    private static string RootKind(string rootName)
    {
        var separator = rootName.IndexOf(' ');
        return separator > 0 ? rootName.Substring(0, separator) : rootName;
    }

    private static void BeginPhase(int phase, float now)
    {
        _phase = phase;
        _phaseStartedAt = now;
        _sampleFrames = 0;
        _frameMilliseconds = 0;
        foreach (var counter in Counters)
        {
            counter.ResetSamples();
        }

        Debug.Log($"[T3MP] RenderStats phase={PhaseName(phase)} started.");
    }

    private static void RecordFrame()
    {
        _sampleFrames++;
        _frameMilliseconds += Time.unscaledDeltaTime * 1000.0;
        foreach (var counter in Counters)
        {
            counter.Record();
        }
    }

    private static void LogPhase()
    {
        var averageFrameMilliseconds = _sampleFrames > 0 ? _frameMilliseconds / _sampleFrames : 0.0;
        var values = Counters.Count == 0
            ? "<no-counters>"
            : string.Join(";", Counters.Select(counter => counter.Format()));
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] RenderStats phase={0}, sampleFrames={1}, frameMsAvg={2:F3}, fps={3:F2}, values={4}",
            PhaseName(_phase),
            _sampleFrames,
            averageFrameMilliseconds,
            averageFrameMilliseconds > 0 ? 1000.0 / averageFrameMilliseconds : 0.0,
            values));
    }

    private static string PhaseName(int phase)
    {
        return phase switch
        {
            0 => "baseline",
            1 => "vertexRenderersDisabled",
            2 => "restored",
            _ => "unknown"
        };
    }

    private static void SetRenderersEnabled(bool enabled)
    {
        foreach (var state in _rendererStates)
        {
            if (state.Renderer != null && state.OriginallyEnabled)
            {
                state.Renderer.enabled = enabled;
            }
        }
    }

    private static void RestoreRenderers()
    {
        foreach (var state in _rendererStates)
        {
            if (state.Renderer != null)
            {
                state.Renderer.enabled = state.OriginallyEnabled;
            }
        }
    }

    private static void DisposeCounters()
    {
        foreach (var counter in Counters)
        {
            counter.Dispose();
        }

        Counters.Clear();
    }

    private readonly struct RendererState
    {
        public RendererState(MeshRenderer renderer, bool originallyEnabled)
        {
            Renderer = renderer;
            OriginallyEnabled = originallyEnabled;
        }

        public MeshRenderer Renderer { get; }
        public bool OriginallyEnabled { get; }
    }

    private sealed class RootKindStats
    {
        public HashSet<Transform> Roots { get; } = new HashSet<Transform>();
        public HashSet<Transform> VisibleRoots { get; } = new HashSet<Transform>();
        public HashSet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> VisibleNames { get; } = new HashSet<string>(StringComparer.Ordinal);
        public int Renderers { get; set; }
        public int VisibleRenderers { get; set; }
    }

    private sealed class BatchGroup
    {
        public BatchGroup(string description)
        {
            Description = description;
        }

        public string Description { get; }
        public int Count { get; set; }
    }

    private sealed class Counter : IDisposable
    {
        private readonly ProfilerRecorder _recorder;
        private double _sum;
        private double _minimum = double.PositiveInfinity;
        private double _maximum = double.NegativeInfinity;
        private int _samples;

        public Counter(string category, string name, ProfilerMarkerDataUnit unit, ProfilerRecorder recorder)
        {
            Category = category;
            Name = name;
            Unit = unit;
            _recorder = recorder;
        }

        private string Category { get; }
        private string Name { get; }
        private ProfilerMarkerDataUnit Unit { get; }

        public void Record()
        {
            try
            {
                if (!_recorder.Valid || _recorder.Count == 0)
                {
                    return;
                }

                var value = _recorder.LastValueAsDouble;
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    return;
                }

                _sum += value;
                _minimum = Math.Min(_minimum, value);
                _maximum = Math.Max(_maximum, value);
                _samples++;
            }
            catch (Exception)
            {
                // Ignore counters that stop being available during a scene transition.
            }
        }

        public string Format()
        {
            if (_samples == 0)
            {
                return $"{Category}/{Name}=no-samples";
            }

            var average = _sum / _samples;
            if (Unit == ProfilerMarkerDataUnit.TimeNanoseconds)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/{1}=avg{2:F3}ms,min{3:F3},max{4:F3}",
                    Category,
                    Name,
                    average / 1000000.0,
                    _minimum / 1000000.0,
                    _maximum / 1000000.0);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}=avg{2:F2},min{3:F0},max{4:F0},{5}",
                Category,
                Name,
                average,
                _minimum,
                _maximum,
                Unit);
        }

        public void ResetSamples()
        {
            _sum = 0;
            _minimum = double.PositiveInfinity;
            _maximum = double.NegativeInfinity;
            _samples = 0;
        }

        public void Dispose()
        {
            _recorder.Dispose();
        }
    }
}
