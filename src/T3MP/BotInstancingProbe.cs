using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace T3MP;

/// <summary>
/// Benchmark-only proof of concept for replacing the visible Iron Teeth bot
/// MeshRenderers with explicit GPU-instanced submissions.
///
/// This intentionally does not require a custom shader: renderers are grouped
/// by the full static render key plus the bit-exact current _AnimationTime.
/// Consequently every instance in a draw uses the same ordinary material
/// uniform and remains animation-equivalent to its disabled source renderer.
/// The per-instance mode loads a BotURP clone whose _AnimationTime property is
/// backed by a classic Unity instancing buffer, removing the exact-time split
/// without synchronizing individual animation phases.
/// </summary>
internal static class BotInstancingProbe
{
    private const float StartupDelaySeconds = 3f;
    private const float PhaseSeconds = 5f;
    private const float SettleSeconds = 1f;
    private const string BotRootPrefix = "Bot.IronTeeth";
    private const string SourceBotShaderSuffix = "/BotURP";
    private const string AnimationTimeProperty = "_AnimationTime";
    private const string InstancedShaderName = "T3MP/BotURP";
    private const string ShaderBundleFileName = "t3mp-bot-instancing";

    private static readonly int AnimationTimeId = Shader.PropertyToID(AnimationTimeProperty);
    private static readonly List<Counter> Counters = new List<Counter>();
    private static readonly Dictionary<FrameKey, int> BatchIndices = new Dictionary<FrameKey, int>();
    private static readonly List<FrameBatch> Batches = new List<FrameBatch>();
    private static readonly Dictionary<object, AnimationTimeSlot> AnimationTimeSlots =
        new Dictionary<object, AnimationTimeSlot>(ReferenceComparer.Instance);

    /// <summary>
    /// Set by BenchmarkProbe when the VertexAnimationUpdater.UpdateAnimation
    /// prefix was successfully installed (benchmark runs only).
    /// </summary>
    internal static bool AnimationCapturePatched;
    private static volatile bool _captureAnimationTimes;

    private static Candidate[] _candidates = Array.Empty<Candidate>();
    private static Bounds[] _staticGroupBounds = Array.Empty<Bounds>();
    private static Material?[] _groupMaterials = Array.Empty<Material?>();
    private static StaticBatch[]? _staticBatches;
    private static string _modPath = string.Empty;
    private static AssetBundle? _shaderBundle;
    private static Shader? _instancedShader;
    private static bool _perInstanceAnimationTime;
    private static bool _initialized;
    private static bool _started;
    private static bool _completed;
    private static bool _replacementActive;
    private static float _gameSceneEnteredAt;
    private static float _phaseStartedAt;
    private static int _phase;
    private static int _sampleFrames;
    private static double _frameMilliseconds;
    private static long _drawnInstances;
    private static long _instancedSubmissions;
    private static long _batchBuildTicks;
    private static long _renderSubmitTicks;
    private static int _instancedFrames;
    private static int _largestBatch;

    public static void Configure(string modPath)
    {
        _modPath = modPath ?? string.Empty;
    }

    public static void LateUpdate(bool inGameScene)
    {
        if (!BenchmarkSettings.BenchBotInstancingRequested || _completed)
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
            AutoRuntimeControl.RequestBenchmarkNormalSpeed();
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

            DiscoverCandidates();
            if (_candidates.Length == 0)
            {
                Abort("no eligible initially-visible Iron Teeth bot renderers were found");
                return;
            }

            _started = true;
            BeginPhase(0, now);
        }

        if (_replacementActive && !DrawInstancedFrame())
        {
            return;
        }

        var phaseElapsed = now - _phaseStartedAt;
        if (phaseElapsed >= SettleSeconds)
        {
            RecordFrame();
        }

        if (phaseElapsed < PhaseSeconds)
        {
            return;
        }

        LogPhase();
        if (_phase == 0)
        {
            if (!EnableReplacement())
            {
                return;
            }

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
            Debug.Log("[T3MP] BotInstancing complete.");
        }
    }

    private static void DiscoverCandidates()
    {
        var candidates = new List<Candidate>();
        foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include))
        {
            if (renderer.gameObject.scene.buildIndex != 2 ||
                !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy ||
                !renderer.isVisible ||
                !renderer.transform.root.name.StartsWith(BotRootPrefix, StringComparison.Ordinal) ||
                renderer.HasPropertyBlock())
            {
                continue;
            }

            var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var materials = renderer.sharedMaterials;
            if (mesh == null || mesh.subMeshCount != 1 || materials.Length != 1)
            {
                continue;
            }

            var material = materials[0];
            if (material == null || material.renderQueue > (int)RenderQueue.GeometryLast)
            {
                continue;
            }

            var hasAnimationTime = material.HasProperty(AnimationTimeId);
            var requiresPerInstanceAnimationTime = hasAnimationTime &&
                                                   material.shader != null &&
                                                   material.shader.name.EndsWith(
                                                       SourceBotShaderSuffix,
                                                       StringComparison.Ordinal);
            if (BenchmarkSettings.BenchBotInstancingPerInstanceRequested &&
                hasAnimationTime &&
                !requiresPerInstanceAnimationTime)
            {
                // A shader with some unrelated _AnimationTime property cannot
                // safely be replaced by the BotURP clone.
                continue;
            }

            var baseKey = RenderStatsProbe.CreateInstancingKey(renderer, mesh, 0, material, out _);
            candidates.Add(new Candidate(
                renderer,
                mesh,
                material,
                baseKey,
                renderer.enabled,
                hasAnimationTime,
                requiresPerInstanceAnimationTime));
        }

        _candidates = candidates.ToArray();
        var groupIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupBounds = new List<Bounds>();
        foreach (var candidate in _candidates)
        {
            if (!groupIds.TryGetValue(candidate.BaseKey, out var groupId))
            {
                groupId = groupIds.Count;
                groupIds.Add(candidate.BaseKey, groupId);
                groupBounds.Add(candidate.Renderer.bounds);
            }
            else
            {
                var bounds = groupBounds[groupId];
                bounds.Encapsulate(candidate.Renderer.bounds);
                groupBounds[groupId] = bounds;
            }

            candidate.BaseGroupId = groupId;
        }

        // The benchmark camera is static and the replacement phase lasts only
        // five seconds. A generous cached margin avoids 254 native bounds reads
        // every frame while keeping the group cull substantially tighter than
        // a map-wide catch-all bound.
        for (var i = 0; i < groupBounds.Count; i++)
        {
            var bounds = groupBounds[i];
            bounds.Expand(64f);
            groupBounds[i] = bounds;
        }

        _staticGroupBounds = groupBounds.ToArray();
        var staticGroups = _candidates
            .GroupBy(candidate => candidate.BaseGroupId)
            .Select(group => group.Count())
            .OrderByDescending(count => count)
            .ToArray();
        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] BotInstancing candidates={0}, animatedCandidates={1}, auxiliaryCandidates={2}, staticKeys={3}, largestStaticGroup={4}, reusableCandidates={5}, lightProbeModes={6}, renderingLayerMasks={7}",
            _candidates.Length,
            _candidates.Count(candidate => candidate.HasAnimationTime),
            _candidates.Count(candidate => !candidate.HasAnimationTime),
            staticGroups.Length,
            staticGroups.Length == 0 ? 0 : staticGroups[0],
            staticGroups.Where(count => count > 1).Sum(),
            FormatCounts(_candidates.GroupBy(candidate => candidate.Renderer.lightProbeUsage.ToString()).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)),
            FormatCounts(_candidates.GroupBy(candidate => candidate.Renderer.renderingLayerMask.ToString(CultureInfo.InvariantCulture)).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal))));
    }

    private static bool EnableReplacement()
    {
        try
        {
            _perInstanceAnimationTime = BenchmarkSettings.BenchBotInstancingPerInstanceRequested;
            if (_perInstanceAnimationTime && !TryLoadInstancedShader())
            {
                return false;
            }

            _groupMaterials = new Material?[_staticGroupBounds.Length];
            foreach (var candidate in _candidates)
            {
                if (candidate.Renderer == null || !candidate.Renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                // Outfit/highlight systems may replace a renderer's temporary
                // material while the baseline phase is running. Refresh the
                // native object before cloning and leave vanished candidates
                // on their original renderer.
                candidate.Material = candidate.Renderer.sharedMaterial;
                if (candidate.Material == null)
                {
                    continue;
                }

                if (_groupMaterials[candidate.BaseGroupId] == null)
                {
                    var replacement = _perInstanceAnimationTime && candidate.RequiresPerInstanceAnimationTime
                        ? new Material(_instancedShader!)
                        : new Material(candidate.Material);
                    if (_perInstanceAnimationTime && candidate.RequiresPerInstanceAnimationTime)
                    {
                        replacement.CopyPropertiesFromMaterial(candidate.Material);
                        replacement.shaderKeywords = candidate.Material.shaderKeywords;
                        replacement.renderQueue = candidate.Material.renderQueue;
                    }

                    replacement.enableInstancing = true;
                    replacement.hideFlags = HideFlags.HideAndDontSave;
                    replacement.name = candidate.Material.name.Replace(" (Instance)", string.Empty) +
                                       (_perInstanceAnimationTime
                                           ? " [T3MP BotInstancing PerInstance]"
                                           : " [T3MP BotInstancing]");
                    _groupMaterials[candidate.BaseGroupId] = replacement;
                }
            }

            foreach (var candidate in _candidates)
            {
                var renderer = candidate.Renderer;
                candidate.ReplacementEligible = renderer != null &&
                                                candidate.Material != null &&
                                                _groupMaterials[candidate.BaseGroupId] != null;
                if (candidate.ReplacementEligible && candidate.OriginallyEnabled)
                {
                    renderer!.enabled = false;
                }
            }

            _staticBatches = BuildStaticBatches();
            _replacementActive = true;
            Debug.Log("[T3MP] BotInstancing replacement enabled. perInstanceAnimationTime=" +
                      _perInstanceAnimationTime +
                      ", eligibleCandidates=" +
                      _candidates.Count(candidate => candidate.ReplacementEligible) +
                      ", staticBatches=" +
                      (_staticBatches?.Length.ToString(CultureInfo.InvariantCulture) ?? "<dynamic>"));
            return true;
        }
        catch (Exception exception)
        {
            Abort("replacement setup failed: " + exception);
            return false;
        }
    }

    private static StaticBatch[]? BuildStaticBatches()
    {
        // Exact-animation-time grouping ('-benchBotInstancing') splits batches
        // by the bit pattern of the CURRENT frame's _AnimationTime, so its
        // batches genuinely change every frame - keep the dynamic path there.
        if (!_perInstanceAnimationTime && !BenchmarkSettings.BenchBotInstancingStaticTimeRequested)
        {
            return null;
        }

        var membersByGroup = new List<Candidate>?[_staticGroupBounds.Length];
        foreach (var candidate in _candidates)
        {
            if (!candidate.ReplacementEligible)
            {
                continue;
            }

            var members = membersByGroup[candidate.BaseGroupId] ??= new List<Candidate>();
            members.Add(candidate);
        }

        var batches = new List<StaticBatch>(_staticGroupBounds.Length);
        for (var groupId = 0; groupId < membersByGroup.Length; groupId++)
        {
            var members = membersByGroup[groupId];
            var groupMaterial = _groupMaterials[groupId];
            if (members == null || groupMaterial == null)
            {
                continue;
            }

            if (AnimationCapturePatched)
            {
                foreach (var member in members)
                {
                    if (!member.HasAnimationTime)
                    {
                        continue;
                    }

                    var updater = member.GameObject
                        .GetComponent<Timberborn.TimbermeshAnimations.VertexAnimationUpdater>();
                    if (updater != null)
                    {
                        var slot = new AnimationTimeSlot
                        {
                            Time = member.Material.GetFloat(AnimationTimeId)
                        };
                        member.TimeSlot = slot;
                        AnimationTimeSlots[updater] = slot;
                    }
                }
            }

            var representative = members[0];
            var renderer = representative.Renderer;
            var propertyBlock = new MaterialPropertyBlock();
            var perInstanceTimes = _perInstanceAnimationTime && representative.RequiresPerInstanceAnimationTime;
            var renderParams = new RenderParams(groupMaterial)
            {
                layer = representative.GameObject.layer,
                renderingLayerMask = renderer.renderingLayerMask,
                rendererPriority = renderer.rendererPriority,
                worldBounds = _staticGroupBounds[groupId],
                motionVectorMode = renderer.motionVectorGenerationMode,
                reflectionProbeUsage = renderer.reflectionProbeUsage,
                shadowCastingMode = renderer.shadowCastingMode,
                receiveShadows = renderer.receiveShadows,
                lightProbeUsage = renderer.lightProbeUsage,
                matProps = propertyBlock
            };
            batches.Add(new StaticBatch(
                members.ToArray(),
                representative.Mesh,
                perInstanceTimes,
                !perInstanceTimes && representative.HasAnimationTime,
                renderParams,
                propertyBlock));
        }

        _captureAnimationTimes = AnimationTimeSlots.Count > 0;
        return batches.ToArray();
    }

    /// <summary>
    /// Harmony prefix on VertexAnimationUpdater.UpdateAnimation. While the
    /// replacement is active, the per-frame animation time of a replaced
    /// renderer is captured into a managed slot and the (native) material
    /// SetFloat is skipped - its renderer is disabled anyway, and the next
    /// vanilla UpdateAnimation call after restore re-syncs the material.
    /// Unreplaced renderers always run vanilla.
    /// </summary>
    private static bool CaptureAnimationTimePrefix(object __instance, float normalizedTime)
    {
        if (!_captureAnimationTimes)
        {
            return true;
        }

        if (AnimationTimeSlots.TryGetValue(__instance, out var slot))
        {
            slot.Time = normalizedTime;
            return false;
        }

        return true;
    }

    private static bool TryLoadInstancedShader()
    {
        if (_instancedShader != null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_modPath))
        {
            Abort("mod path is unavailable for the instanced shader bundle");
            return false;
        }

        var bundlePath = Path.Combine(_modPath, "AssetBundles", ShaderBundleFileName);
        if (!File.Exists(bundlePath))
        {
            Abort("instanced shader bundle was not found: " + bundlePath);
            return false;
        }

        // Timberborn eagerly loads every bundle under a mod's AssetBundles
        // directory before StartMod. Reuse that instance; Unity rejects a
        // second LoadFromFile call for the same bundle name.
        _shaderBundle = AssetBundle.GetAllLoadedAssetBundles()
            .FirstOrDefault(bundle => bundle.name == ShaderBundleFileName) ??
            AssetBundle.LoadFromFile(bundlePath);
        if (_shaderBundle == null)
        {
            Abort("instanced shader bundle failed to load: " + bundlePath);
            return false;
        }

        _instancedShader = _shaderBundle.LoadAllAssets<Shader>()
            .FirstOrDefault(shader => shader.name == InstancedShaderName);
        if (_instancedShader == null || !_instancedShader.isSupported)
        {
            Abort("instanced shader is missing or unsupported in bundle: " + bundlePath);
            return false;
        }

        Debug.Log("[T3MP] BotInstancing loaded shader bundle: " + bundlePath);
        return true;
    }

    private static bool DrawInstancedFrame()
    {
        if (_staticBatches != null)
        {
            return DrawStaticBatchesFrame();
        }

        var buildStarted = Stopwatch.GetTimestamp();
        BatchIndices.Clear();
        var activeBatchCount = 0;
        try
        {
            foreach (var candidate in _candidates)
            {
                var renderer = candidate.Renderer;
                var material = candidate.Material;
                if (!candidate.ReplacementEligible ||
                    renderer == null ||
                    material == null ||
                    !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var animationTime = candidate.HasAnimationTime
                    ? material.GetFloat(AnimationTimeId)
                    : 0f;
                var animationTimeBits = !candidate.HasAnimationTime ||
                                        _perInstanceAnimationTime ||
                                        BenchmarkSettings.BenchBotInstancingStaticTimeRequested
                    ? 0
                    : BitConverter.SingleToInt32Bits(animationTime);
                var key = new FrameKey(candidate.BaseGroupId, animationTimeBits);
                if (!BatchIndices.TryGetValue(key, out var batchIndex))
                {
                    batchIndex = activeBatchCount++;
                    BatchIndices.Add(key, batchIndex);
                    if (batchIndex == Batches.Count)
                    {
                        Batches.Add(new FrameBatch());
                    }

                    Batches[batchIndex].Reset(candidate, animationTime);
                }

                Batches[batchIndex].Add(renderer.localToWorldMatrix, animationTime);
            }

            var submitStarted = Stopwatch.GetTimestamp();
            var drawnInstances = 0;
            var submissions = 0;
            var largestBatch = 0;
            for (var i = 0; i < activeBatchCount; i++)
            {
                var batch = Batches[i];
                var representative = batch.Representative;
                var groupMaterial = representative.BaseGroupId >= 0 && representative.BaseGroupId < _groupMaterials.Length
                    ? _groupMaterials[representative.BaseGroupId]
                    : null;
                if (batch.Matrices.Count == 0 || groupMaterial == null || representative.Renderer == null)
                {
                    continue;
                }

                if (_perInstanceAnimationTime && representative.RequiresPerInstanceAnimationTime)
                {
                    batch.PropertyBlock.SetFloatArray(AnimationTimeId, batch.AnimationTimes);
                }
                else
                {
                    batch.PropertyBlock.SetFloat(AnimationTimeId, batch.AnimationTime);
                }
                var renderParams = new RenderParams(groupMaterial)
                {
                    layer = representative.Renderer.gameObject.layer,
                    renderingLayerMask = representative.Renderer.renderingLayerMask,
                    rendererPriority = representative.Renderer.rendererPriority,
                    worldBounds = batch.WorldBounds,
                    motionVectorMode = representative.Renderer.motionVectorGenerationMode,
                    reflectionProbeUsage = representative.Renderer.reflectionProbeUsage,
                    shadowCastingMode = representative.Renderer.shadowCastingMode,
                    receiveShadows = representative.Renderer.receiveShadows,
                    lightProbeUsage = representative.Renderer.lightProbeUsage,
                    matProps = batch.PropertyBlock
                };
                Graphics.RenderMeshInstanced(
                    in renderParams,
                    representative.Mesh,
                    0,
                    batch.Matrices,
                    batch.Matrices.Count,
                    0);
                drawnInstances += batch.Matrices.Count;
                submissions++;
                largestBatch = Math.Max(largestBatch, batch.Matrices.Count);
            }

            _drawnInstances += drawnInstances;
            _instancedSubmissions += submissions;
            _instancedFrames++;
            _largestBatch = Math.Max(_largestBatch, largestBatch);
            _batchBuildTicks += submitStarted - buildStarted;
            _renderSubmitTicks += Stopwatch.GetTimestamp() - submitStarted;
            return true;
        }
        catch (Exception exception)
        {
            Abort("DrawMeshInstanced failed: " + exception);
            return false;
        }
    }

    private static bool DrawStaticBatchesFrame()
    {
        try
        {
            var batches = _staticBatches!;
            var drawnInstances = 0;
            var submissions = 0;
            var largestBatch = 0;
            var buildTicks = 0L;
            var submitTicks = 0L;
            for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
            {
                var batch = batches[batchIndex];
                var members = batch.Members;
                var matrices = batch.Matrices;
                var animationTimes = batch.AnimationTimes;
                var perInstanceTimes = batch.PerInstanceTimes;
                var buildStarted = Stopwatch.GetTimestamp();
                var count = 0;
                var uniformTime = 0f;
                for (var memberIndex = 0; memberIndex < members.Length; memberIndex++)
                {
                    var member = members[memberIndex];
                    var transform = member.Transform;
                    if (transform == null || !member.GameObject.activeInHierarchy)
                    {
                        // Destroyed (bot died) or temporarily inactive (bot
                        // inside a building): exactly what its disabled
                        // MeshRenderer would not have drawn either.
                        continue;
                    }

                    matrices[count] = transform.localToWorldMatrix;
                    if (perInstanceTimes)
                    {
                        var slot = member.TimeSlot;
                        animationTimes[count] = slot != null
                            ? slot.Time
                            : member.Material.GetFloat(AnimationTimeId);
                    }
                    else if (count == 0 && batch.UniformTime)
                    {
                        var slot = member.TimeSlot;
                        uniformTime = slot != null
                            ? slot.Time
                            : member.Material.GetFloat(AnimationTimeId);
                    }

                    count++;
                }

                var submitStarted = Stopwatch.GetTimestamp();
                buildTicks += submitStarted - buildStarted;
                if (count > 0)
                {
                    if (perInstanceTimes)
                    {
                        // The block copies the array on set, so re-set it each
                        // frame after the in-place rewrite above. Slots past
                        // 'count' hold stale values no drawn instance indexes.
                        batch.PropertyBlock.SetFloatArray(AnimationTimeId, animationTimes);
                    }
                    else if (batch.UniformTime)
                    {
                        batch.PropertyBlock.SetFloat(AnimationTimeId, uniformTime);
                    }

                    Graphics.RenderMeshInstanced(
                        in batch.RenderParams,
                        batch.Mesh,
                        0,
                        matrices,
                        count,
                        0);
                    drawnInstances += count;
                    submissions++;
                    largestBatch = Math.Max(largestBatch, count);
                }

                submitTicks += Stopwatch.GetTimestamp() - submitStarted;
            }

            _drawnInstances += drawnInstances;
            _instancedSubmissions += submissions;
            _instancedFrames++;
            _largestBatch = Math.Max(_largestBatch, largestBatch);
            _batchBuildTicks += buildTicks;
            _renderSubmitTicks += submitTicks;
            return true;
        }
        catch (Exception exception)
        {
            Abort("static-batch RenderMeshInstanced failed: " + exception);
            return false;
        }
    }

    private static void RestoreRenderers()
    {
        _captureAnimationTimes = false;
        AnimationTimeSlots.Clear();
        if (_replacementActive)
        {
            foreach (var candidate in _candidates)
            {
                if (candidate.ReplacementEligible && candidate.Renderer != null)
                {
                    candidate.Renderer.enabled = candidate.OriginallyEnabled;
                }

                candidate.ReplacementEligible = false;
                candidate.TimeSlot = null;
            }
        }

        foreach (var material in _groupMaterials)
        {
            if (material != null)
            {
                Object.Destroy(material);
            }
        }

        _groupMaterials = Array.Empty<Material?>();
        _staticBatches = null;
        _replacementActive = false;
    }

    private static void InitializeCounters()
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal)
        {
            "CPU Render Thread Frame Time",
            "SRP Batcher Draw Calls Count",
            "Standard Instanced Draw Calls Count",
            "Standard Draw Calls Count",
            "SetPass Calls Count"
        };
        var handles = new List<ProfilerRecorderHandle>();
        try
        {
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var handle in handles)
            {
                var description = ProfilerRecorderHandle.GetDescription(handle);
                if (!wanted.Contains(description.Name))
                {
                    continue;
                }

                var recorder = new ProfilerRecorder(
                    handle,
                    1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.StartImmediately);
                if (recorder.Valid)
                {
                    Counters.Add(new Counter(description.Category.Name, description.Name, description.UnitType, recorder));
                }
                else
                {
                    recorder.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] BotInstancing counter initialization failed: " + exception.Message);
        }
    }

    private static void BeginPhase(int phase, float now)
    {
        _phase = phase;
        _phaseStartedAt = now;
        _sampleFrames = 0;
        _frameMilliseconds = 0;
        _drawnInstances = 0;
        _instancedSubmissions = 0;
        _batchBuildTicks = 0;
        _renderSubmitTicks = 0;
        _instancedFrames = 0;
        _largestBatch = 0;
        foreach (var counter in Counters)
        {
            counter.ResetSamples();
        }

        Debug.Log("[T3MP] BotInstancing phase=" + PhaseName(phase) + " started.");
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
            "[T3MP] BotInstancing phase={0}, collapseAnimationTime={1}, perInstanceAnimationTime={2}, sampleFrames={3}, frameMsAvg={4:F3}, fps={5:F2}, instancedFrames={6}, instancesPerInstancedFrame={7:F2}, submissionsPerInstancedFrame={8:F2}, largestBatch={9}, batchBuildMsPerInstancedFrame={10:F3}, renderSubmitMsPerInstancedFrame={11:F3}, values={12}",
            PhaseName(_phase),
            BenchmarkSettings.BenchBotInstancingStaticTimeRequested,
            _perInstanceAnimationTime,
            _sampleFrames,
            averageFrameMilliseconds,
            averageFrameMilliseconds > 0 ? 1000.0 / averageFrameMilliseconds : 0.0,
            _instancedFrames,
            _instancedFrames > 0 ? (double)_drawnInstances / _instancedFrames : 0.0,
            _instancedFrames > 0 ? (double)_instancedSubmissions / _instancedFrames : 0.0,
            _largestBatch,
            _instancedFrames > 0 ? ToMilliseconds(_batchBuildTicks) / _instancedFrames : 0.0,
            _instancedFrames > 0 ? ToMilliseconds(_renderSubmitTicks) / _instancedFrames : 0.0,
            values));
    }

    private static string PhaseName(int phase)
    {
        return phase switch
        {
            0 => "baseline",
            1 when BenchmarkSettings.BenchBotInstancingPerInstanceRequested => "botInstancedPerInstance",
            1 => BenchmarkSettings.BenchBotInstancingStaticTimeRequested
                ? "botInstancedStaticTime"
                : "botInstanced",
            2 => "restored",
            _ => "unknown"
        };
    }

    private static string FormatCounts(Dictionary<string, int> counts)
    {
        return counts.Count == 0
            ? "<none>"
            : string.Join("|", counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value));
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private static void Abort(string reason)
    {
        RestoreRenderers();
        DisposeCounters();
        _completed = true;
        Debug.LogWarning("[T3MP] BotInstancing aborted: " + reason);
    }

    private static void DisposeCounters()
    {
        foreach (var counter in Counters)
        {
            counter.Dispose();
        }

        Counters.Clear();
    }

    private sealed class Candidate
    {
        public Candidate(
            MeshRenderer renderer,
            Mesh mesh,
            Material material,
            string baseKey,
            bool originallyEnabled,
            bool hasAnimationTime,
            bool requiresPerInstanceAnimationTime)
        {
            Renderer = renderer;
            // gameObject/transform are native lookups; resolve once so the
            // per-frame batch fill pays one activeInHierarchy + one matrix
            // read per candidate and nothing else.
            GameObject = renderer.gameObject;
            Transform = renderer.transform;
            Mesh = mesh;
            Material = material;
            BaseKey = baseKey;
            OriginallyEnabled = originallyEnabled;
            HasAnimationTime = hasAnimationTime;
            RequiresPerInstanceAnimationTime = requiresPerInstanceAnimationTime;
        }

        public MeshRenderer Renderer { get; }
        public GameObject GameObject { get; }
        public Transform Transform { get; }
        public Mesh Mesh { get; }
        public Material Material { get; set; }
        public string BaseKey { get; }
        public bool OriginallyEnabled { get; }
        public bool HasAnimationTime { get; }
        public bool RequiresPerInstanceAnimationTime { get; }
        public bool ReplacementEligible { get; set; }
        public int BaseGroupId { get; set; }
        public AnimationTimeSlot? TimeSlot { get; set; }
    }

    /// <summary>
    /// Managed mirror of one replaced renderer's _AnimationTime, written by
    /// the UpdateAnimation prefix instead of the material (native) while the
    /// replacement is active.
    /// </summary>
    private sealed class AnimationTimeSlot
    {
        public float Time;
    }

    /// <summary>
    /// Pure reference equality so dictionary lookups in the hot prefix never
    /// touch UnityEngine.Object's native-backed Equals.
    /// </summary>
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        bool IEqualityComparer<object>.Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        int IEqualityComparer<object>.GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    /// <summary>
    /// Precomputed per-group batch for the modes whose grouping does not
    /// depend on the current animation time (per-instance shader or
    /// collapsed static time). Membership, buffers, the property block and
    /// the full RenderParams are all resolved once at replacement time, so
    /// a frame only reads each member's active state + matrix (+ animation
    /// time) and issues one draw per group.
    /// </summary>
    private sealed class StaticBatch
    {
        public StaticBatch(Candidate[] members, Mesh mesh, bool perInstanceTimes, bool uniformTime, RenderParams renderParams, MaterialPropertyBlock propertyBlock)
        {
            Members = members;
            Mesh = mesh;
            PerInstanceTimes = perInstanceTimes;
            UniformTime = uniformTime;
            RenderParams = renderParams;
            PropertyBlock = propertyBlock;
            Matrices = new Matrix4x4[members.Length];
            AnimationTimes = perInstanceTimes ? new float[members.Length] : Array.Empty<float>();
        }

        public Candidate[] Members { get; }
        public Mesh Mesh { get; }
        public bool PerInstanceTimes { get; }
        public bool UniformTime { get; }
        public RenderParams RenderParams;
        public MaterialPropertyBlock PropertyBlock { get; }
        public Matrix4x4[] Matrices { get; }
        public float[] AnimationTimes { get; }
    }

    private readonly struct FrameKey : IEquatable<FrameKey>
    {
        public FrameKey(int baseGroupId, int animationTimeBits)
        {
            BaseGroupId = baseGroupId;
            AnimationTimeBits = animationTimeBits;
        }

        private int BaseGroupId { get; }
        private int AnimationTimeBits { get; }

        public bool Equals(FrameKey other)
        {
            return AnimationTimeBits == other.AnimationTimeBits &&
                   BaseGroupId == other.BaseGroupId;
        }

        public override bool Equals(object? obj)
        {
            return obj is FrameKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (BaseGroupId * 397) ^ AnimationTimeBits;
            }
        }
    }

    private sealed class FrameBatch
    {
        public Candidate Representative { get; private set; } = null!;
        public List<Matrix4x4> Matrices { get; } = new List<Matrix4x4>(128);
        public List<float> AnimationTimes { get; } = new List<float>(128);
        public MaterialPropertyBlock PropertyBlock { get; } = new MaterialPropertyBlock();
        public float AnimationTime { get; private set; }
        public Bounds WorldBounds { get; private set; }

        public void Reset(Candidate representative, float animationTime)
        {
            Representative = representative;
            AnimationTime = animationTime;
            Matrices.Clear();
            AnimationTimes.Clear();
            PropertyBlock.Clear();
            WorldBounds = representative.BaseGroupId >= 0 && representative.BaseGroupId < _staticGroupBounds.Length
                ? _staticGroupBounds[representative.BaseGroupId]
                : default;
        }

        public void Add(Matrix4x4 matrix, float animationTime)
        {
            Matrices.Add(matrix);
            AnimationTimes.Add(animationTime);
        }
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

        public string Format()
        {
            if (_samples == 0)
            {
                return Category + "/" + Name + "=no-samples";
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
