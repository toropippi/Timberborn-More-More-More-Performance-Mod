using NavigationShapePlans = T3MP.Loading.NavigationShapePlans;
using LazyGoodStackExperiment = T3MP.Loading.DeferredGoodStackModels;
using TransputRoutingExperiment = T3MP.Loading.TransputLoadRouting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.SingletonSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Isolated-save load timing, state fingerprint and real EventBus equivalence
// checks. All toggles and synthetic events live in the development mod.
public sealed class LoadRoutingTestDriver : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type Router => Find("T3MP.LoadEventRouter");
    private static FieldInfo Installed => Router.GetField("_installed", All)!;
    private static bool Baseline => Environment.GetCommandLineArgs().Contains("-t3mpTestLoadBaseline");
    private static bool ProfileOnly => Environment.GetCommandLineArgs().Contains("-t3mpTestProfileOnly");
    private EntityRegistry _registry = null!;
    private Bindito.Core.IContainer _container = null!;
    private bool _done;
    private int _captureAfterFrame;
    private Timberborn.TimeSystem.SpeedManager _speed = null!;
    private float _smokeUntil;
    private long _smokeStartTicks;
    private static int _foreignCalls;

    public static void Configure()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-t3mpTestExpectedModMvid");
            if (ProfileOnly)
            {
                var allowed = new HashSet<string> { "-t3mpTestProfileOnly", "-t3mpTestLoadRouting",
                    "-t3mpTestExpectedModMvid", "-t3mpTestResourceProfile", "-t3mpTestSingletonProfile", "-t3mpTestSteamLoadBaseline",
                    "-t3mpTestMenuLoad", "-t3mpTestSettlement", "-t3mpTestSave",
                    "-t3mpTestServiceWorkProfile", "-t3mpTestConstructionProfile", "-t3mpTestEntityCreationProfile", "-t3mpTestInitializationProfile", "-t3mpTestLoadEventProfile" };
                foreach (var argument in args.Where(a => a.StartsWith("-t3mpTest", StringComparison.Ordinal)))
                    if (!allowed.Contains(argument)) throw new Exception("Non-profiling flag in profile-only run: " + argument);
                var actual = Find("T3MP.BenchmarkSettings").Module.ModuleVersionId;
                if (index < 0 || index + 1 >= args.Length || actual.ToString() != args[index + 1])
                    throw new Exception("Unexpected loaded mod MVID");
                Log("CONFIG profileOnly=True experimentsInstalled=False modMvid=" + actual);
                ServiceWorkProbe.Install();
                LoadConstructionProbe.Install();
                EntityCreationProbe.Install();
                LoadResourceProbe.Install();
                InitializationProbe.Install();
                LoadEventProbe.Install();
                return;
            }
            if (index < 0 || Router.Module.ModuleVersionId.ToString() != args[index + 1])
                throw new Exception("Unexpected loaded mod MVID");
            // A native baseline on an unreviewed game build deliberately has
            // no router. Candidate runs must still prove it was installed.
            if (!Baseline && !(bool)Installed.GetValue(null)!) throw new Exception("Router failed to install");
            var settings = Find("T3MP.BenchmarkSettings");
            foreach (var flag in new[] { "EnableLoadComponentProfiler", "EnableLoadSingletonProfiler", "EnableLoadEventProfiler",
                         "EnableBenchmarkMeasurement", "EnableHotOptimizerMetrics", "BenchSpawnRequested" })
                if ((bool)settings.GetField(flag, All)!.GetValue(null)!) throw new Exception("Timing probe enabled: " + flag);
            if (Baseline) Installed.SetValue(null, false);
            Log("CONFIG baseline=" + Baseline + " modMvid=" + Router.Module.ModuleVersionId);
            ServiceWorkProbe.Install();
            TerrainSurfaceValidation.Install();
            AtlasPixelsValidation.Install();
            LoadConstructionProbe.Install();
            EntityCreationProbe.Install();
            EventSubscriptionProbe.Install();
            NavigationNotificationProbe.Install();
            LoadResourceProbe.Install();
            LoadEventProbe.Install();
            WaterColumnIndexExperiment.Install();
            LayeredObstacleIndexExperiment.Install();
            BlockEventRoutingExperiment.Install();
            NavigationLoadExperiment.Install();
            ConstructionPlanExperiment.Install();
            ComponentConstructionExperiment.Install();
            InitializationProbe.Install();
            StatusSpriteCacheExperiment.Install();
            StatusIconStagingExperiment.Install();
            StatusFinalStateExperiment.Install();
            LoadGcExperiment.Install();
            TransputRoutingExperiment.Install();
            ModelLayoutExperiment.Install();
            MeshBuilderCapacityExperiment.Install();
            PrefabConstructionProbe.Install();
            ModelInputProbe.Install();
            ModelVisibilityProbe.Install();
            ModelVisibilityExperiment.Install();
            NaturalModelTransitionExperiment.Install();
            LazyGoodStackExperiment.BeforeSnapshot = SelectionSnapshot.Report;
            LazyGoodStackExperiment.Install();
            T3MP.Loading.PreparedCarriedItems.Install();
            RangedEventExperiment.Install();
            NavigationRemovalView.Install();
            NavigationShapePlans.Install();
            LayeredOccupierProbe.Install();
            LazyBuildingPreviewExperiment.Install();
            LazyPlantablePreviewExperiment.Install();
            LazyPathVariantExperiment.Install();
            AdapterTemplateExperiment.Install();
            DeferredUpdateAdapterExperiment.Install();
            LazyConstructionStageExperiment.Install();
            InitialNavigationValidation.Install();
        }
        catch (Exception exception) { Log("ERROR " + exception); }
    }

    public void Initialize(EntityRegistry registry, Timberborn.TimeSystem.SpeedManager speed, Bindito.Core.IContainer container)
    { _registry = registry; _speed = speed; _container = container; _captureAfterFrame = Time.frameCount + 5; }

    private void Update()
    {
        if (_done) return;
        if (_smokeUntil > 0)
        {
            if (Time.realtimeSinceStartup < _smokeUntil) return;
            _done = true;
            var ticks = (long)Find("T3MP.BenchmarkModeController").GetField("_overlayFullTicks", All)!.GetValue(null)! - _smokeStartTicks;
            NavigationPackedSource.Report();
            Log(ticks > 0 ? "SMOKE PASS ticks=" + ticks : "ERROR simulation did not advance");
            LazyGoodStackExperiment.Report("smoke-end");
            RangedEventExperiment.Report("smoke-end");
            NavigationRemovalView.Report("smoke-end");
            LazyBuildingPreviewExperiment.Report("smoke-end");
            LazyPlantablePreviewExperiment.Report("smoke-end");
            LazyPathVariantExperiment.Report("smoke-end");
            DeferredUpdateAdapterExperiment.Report("smoke-end");
            if (ticks > 0) Log("COMPLETE");
            return;
        }
        if (Time.frameCount < _captureAfterFrame) return;
        _done = true;
        try
        {
            if (ProfileOnly)
            {
                LoadConstructionProbe.Report();
                InitializationProbe.Report();
                Log("PROFILE entities=" + _registry.Entities.Count());
                Log("COMPLETE");
                return;
            }
            LoadConstructionProbe.Report();
            EventSubscriptionProbe.Report(_registry.Entities, _container);
            EventRegistrationPlanValidation.Run();
            LoadOptimizationGuardValidation.Run();
            NavigationPackedSource.Report();
            NavigationLoadExperiment.Run();
            NavigationPackedSource.Report();
            MeshBuilderCapacityExperiment.Validate();
            ModelVisibilityExperiment.Validate();
            ConstructionPlanExperiment.ValidateIfRequested();
            ComponentConstructionExperiment.Validate();
            AdapterTemplateExperiment.Validate();
            DeferredUpdateAdapterExperiment.Validate();
            InitializationProbe.Report();
            WaterColumnIndexExperiment.Validate();
            LayeredObstacleIndexExperiment.Validate();
            BlockEventRoutingExperiment.Validate();
            ProductionBlockRoutingValidation.Validate();
            RangedEventExperiment.Validate();
            NavigationRemovalView.Validate();
            LazyBuildingPreviewExperiment.Validate();
            LazyPlantablePreviewExperiment.Validate();
            var entities = _registry.Entities.ToList();
            DeferredUpdateAdapterExperiment.ExerciseReal(entities);
            AsyncCloneProbe.Run(_container, entities);
            MeshSnapshot.Report(entities);
            ComponentMemoryCensus.Report(entities, _container);
            ModelVariantCensus.Report(entities);
            LazyGoodStackExperiment.Exercise(entities);
            LazyGoodStackExperiment.ValidateBeforeSnapshots(entities);
            LazyPathVariantExperiment.Report("scene-ready");
            LazyPathVariantExperiment.ValidateBeforeSnapshots(entities);
            LazyConstructionStageExperiment.ValidateBeforeSnapshots(entities);
            TubeLightingSnapshot.Report(entities);
            ModelVisualSnapshot.Report(entities);
            FullStateSnapshot.Report(_container);
            SelectionSnapshot.Report(entities);
            LoadGcBudgetValidation.Run();
            ProductionConstructionValidation.Validate();
            ProductionComponentValidation.Validate();
            InitialNavigationValidation.Validate();
            EmptyFlowFieldValidation.Validate();
            TerrainSurfaceValidation.Validate();
            AtlasPixelsValidation.Validate();
            NaturalModelTransitionExperiment.Validate();
            var trackerType = Find("Timberborn.TubeSystem.TubeTracker");
            var tubeType = Find("Timberborn.TubeSystem.Tube");
            var trackers = entities.SelectMany(e => e.AllComponents.Where(c => c.GetType() == trackerType)
                .Select(c => (Entity: e, Tracker: c))).OrderBy(x => x.Entity.EntityId).ToArray();
            var state = trackerType.GetField("_isInTube", All)!;
            var builder = new StringBuilder();
            foreach (var pair in trackers)
            {
                var p = pair.Entity.Transform.position;
                builder.Append(pair.Entity.EntityId).Append('|').Append(state.GetValue(pair.Tracker)).Append('|')
                    .Append(p.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(p.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            using var sha = SHA256.Create();
            var fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", "");
            Log($"STATE entities={entities.Count} trackers={trackers.Length} inTube={trackers.Count(t => (bool)state.GetValue(t.Tracker)!)} sha256={fingerprint}");
            if (Environment.GetCommandLineArgs().Contains("-t3mpTestLoadValidate"))
            {
                var ordinary = entities.First(e => !e.AllComponents.Any(c => c.GetType() == tubeType));
                var tube = entities.FirstOrDefault(e => e.AllComponents.Any(c => c.GetType() == tubeType));
                if (trackers.Length > 0 && tube != null)
                    Validate(ordinary, tube, trackers[0].Tracker, trackerType);
                else Log("TEST no tube/tracker in this scene; state fingerprint only");
            }
            if (Environment.GetCommandLineArgs().Contains("-t3mpTestLoadSmoke"))
            {
                _smokeStartTicks = (long)Find("T3MP.BenchmarkModeController").GetField("_overlayFullTicks", All)!.GetValue(null)!;
                _speed.ChangeSpeed(3f);
                _smokeUntil = Time.realtimeSinceStartup + 12f;
                _done = false;
            }
            else Log("COMPLETE");
        }
        catch (Exception exception) { Log("ERROR " + exception); }
    }

    // Public for vanilla EventBus.Register's normal public-[OnEvent] validation.
    public sealed class Listener
    {
        public Action<EntityInitializedEvent> Action = null!;
        [OnEvent] public void OnInitialized(EntityInitializedEvent e) { Action(e); }
    }

    private static void Validate(EntityComponent ordinary, EntityComponent tube, object exemplar, Type trackerType)
    {
        var enabledBefore = (bool)Installed.GetValue(null)!;
        try
        {
            foreach (var scenario in new[] { "order", "registration", "nested", "exception", "runtime", "failed-registration", "other-event-registration", "missing-removal" })
            {
                var original = Run(scenario, false, ordinary, tube, exemplar, trackerType);
                var optimized = Run(scenario, true, ordinary, tube, exemplar, trackerType);
                if (original != optimized) throw new Exception("Different event trace in " + scenario + "\n" + original + "\n" + optimized);
                Log("TEST " + scenario + " PASS");
            }
            if (Router.GetField("_session", All)!.GetValue(null) != null) throw new Exception("Load scope leaked");
            // A foreign handler patch must be observed before filtering.
            Installed.SetValue(null, true);
            var ht = Find("HarmonyLib.Harmony");
            var hm = Find("HarmonyLib.HarmonyMethod");
            var harmony = Activator.CreateInstance(ht, "t3mp.test.load.compatibility")!;
            try
            {
                var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
                var prefix = Activator.CreateInstance(hm, typeof(LoadRoutingTestDriver).GetMethod(nameof(ForeignPrefix), All))!;
                patch.Invoke(harmony, new[] { (object)trackerType.GetMethod("OnEntityInitialized", All)!, prefix, null, null, null });
                if ((bool)Router.GetMethod("Compatible", All)!.Invoke(null, null)!) throw new Exception("Foreign patch accepted");
                _foreignCalls = 0;
                Run("order", true, ordinary, tube, exemplar, trackerType);
                if (_foreignCalls != 9) throw new Exception("Foreign handler calls were omitted: " + _foreignCalls);
                Log("TEST foreign-patch fallback PASS");
            }
            finally { ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { "t3mp.test.load.compatibility" }); }
        }
        finally { Installed.SetValue(null, enabledBefore); }
    }

    private static void ForeignPrefix() { _foreignCalls++; }

    private static string Run(string scenario, bool enabled, EntityComponent ordinary, EntityComponent tube, object exemplar, Type trackerType)
    {
        Installed.SetValue(null, true); // mark exact vanilla wrappers during registration
        var bus = new EventBus();
        var trace = new List<string>();
        var state = trackerType.GetField("_isInTube", All)!;
        var cache = typeof(BaseComponent).GetField("_componentCache", All)!;
        var map = trackerType.GetField("_tubeMap", All)!.GetValue(exemplar);
        var clones = new List<object>();
        object Tracker(bool inTube)
        {
            var clone = Activator.CreateInstance(trackerType, All, null, new[] { map, bus }, null)!;
            cache.SetValue(clone, cache.GetValue(exemplar));
            state.SetValue(clone, inTube);
            clones.Add(clone);
            return clone;
        }
        string Id(EntityInitializedEvent e) => ReferenceEquals(e.Entity, tube) ? "tube" : "ordinary";
        var late = new Listener { Action = e => trace.Add("late:" + Id(e)) };
        Listener first = null!;
        var changed = false;
        first = new Listener { Action = e =>
        {
            trace.Add("first:" + Id(e));
            if (!changed)
            {
                changed = true;
                if (scenario == "registration") { bus.Register(late); bus.Unregister(first); }
                if (scenario == "nested") bus.Post(new EntityInitializedEvent(tube));
                if (scenario == "exception") throw new InvalidOperationException("test callback");
                var registry = typeof(EventBus).GetField("_subscriptions", All)!.GetValue(bus)!;
                if (scenario == "failed-registration")
                {
                    try { registry.GetType().GetMethod("Add", All)!.Invoke(registry,
                        new object[] { typeof(EntityInitializedEvent), first, new Action<object>(_ => { }) }); }
                    catch (TargetInvocationException) { trace.Add("registration-rejected"); }
                }
                if (scenario == "other-event-registration") registry.GetType().GetMethod("Add", All)!.Invoke(registry,
                    new object[] { typeof(EntityDeletedEvent), late, new Action<object>(_ => { }) });
                if (scenario == "missing-removal") registry.GetType().GetMethod("RemoveAll", All)!.Invoke(registry, new object[] { new object() });
            }
        } };
        bus.Register(first);
        bus.Register(Tracker(false));
        bus.Register(Tracker(true));
        // Predicate must be evaluated again after an unrelated callback.
        bus.Register(new Listener { Action = e => { trace.Add("middle:" + Id(e)); state.SetValue(clones[0], false); } });
        bus.Register(Tracker(false));
        bus.Register(new Listener { Action = e => trace.Add("last:" + Id(e)) });
        var ready = typeof(EventBus).GetField("_ready", All)!;
        var events = (Queue<object>)typeof(EventBus).GetField("_earlyEvents", All)!.GetValue(bus)!;
        events.Enqueue(new EntityInitializedEvent(ordinary));
        events.Enqueue(new EntityInitializedEvent(tube));
        events.Enqueue(new EntityInitializedEvent(ordinary));
        if (scenario == "nested") ready.SetValue(bus, true);
        Installed.SetValue(null, enabled);
        try { bus.PostLoad(); }
        catch (Exception exception) { trace.Add("throw:" + exception.GetType().Name + ":" + exception.InnerException?.GetType().Name); }
        if (scenario == "runtime") bus.Post(new EntityInitializedEvent(ordinary));
        trace.Add("states:" + string.Join(",", clones.Select(c => state.GetValue(c))));
        trace.Add("ready:" + ready.GetValue(bus));
        trace.Add("posting:" + typeof(EventBus).GetField("_posting", All)!.GetValue(bus));
        trace.Add("pending:" + ((Queue<Action>)typeof(EventBus).GetField("_pendingActions", All)!.GetValue(bus)!).Count);
        trace.Add("early:" + events.Count);
        return string.Join(";", trace);
    }

    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null)
        ?? throw new TypeLoadException(name);
    private static void Log(string message) => Debug.Log("[T3MPLOAD] " + message);
}
