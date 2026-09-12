using System;
using System.Linq;
using Bindito.Core;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.MapRepositorySystem;
using Timberborn.ModManagerScene;
using Timberborn.SingletonSystem;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Development-only scenario driver used by scripts/verify_release.ps1. It is
// deployed to Mods\T3MPTestDriver only for the duration of a verification run
// and is never part of the shipped T3MP package. Without a -t3mpTest* command
// line flag every entry point below is a no-op, so a stray leftover install
// cannot affect normal play.
//
// Scenarios (all driven through the same GameSceneLoader API the main menu
// buttons use, so they exercise the real menu code paths without fragile UI
// coordinate clicking):
//   -t3mpTestMenuLoad -t3mpTestSettlement X -t3mpTestSave Y
//       "load a save from the main menu"
//   -t3mpTestNewGame [-t3mpTestMap MapName] [-t3mpTestFaction Folktails]
//       "start a brand-new game" (defaults: first built-in map, Folktails)

public sealed class TestDriverModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        Debug.Log("[T3MPTEST] Test driver loaded. " + TestArguments.Describe());
        FullTickCounter.Install();
        TickProfiler.Install();
        if (TestArguments.LoadRoutingRequested) LoadRoutingTestDriver.Configure();
    }
}

internal static class TestArguments
{
    public static bool LoadRoutingRequested => HasFlag("-t3mpTestLoadRouting");
    public static bool MenuLoadRequested => HasFlag("-t3mpTestMenuLoad");

    public static bool NewGameRequested => HasFlag("-t3mpTestNewGame");

    public static bool AnyScenarioRequested => MenuLoadRequested || NewGameRequested;

    public static string? Settlement => GetValue("-t3mpTestSettlement");

    public static string? Save => GetValue("-t3mpTestSave");

    public static string? Map => GetValue("-t3mpTestMap");

    public static string NewSettlement => GetValue("-t3mpTestNewSettlement") ?? "t3mp-test";

    public static string Faction => GetValue("-t3mpTestFaction") ?? "Folktails";

    // Optional speed override for the game scene (e.g. 99 to reproduce
    // high-speed visual issues). Also activates the game-scene driver on its
    // own so a plain CLI autoload can be resumed at a chosen speed.
    public static float? Speed
    {
        get
        {
            var raw = GetValue("-t3mpTestSpeed");
            return raw != null &&
                   float.TryParse(raw, System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                   parsed > 0f
                ? parsed
                : null;
        }
    }

    public static string Describe()
    {
        if (MenuLoadRequested)
        {
            return $"scenario=MenuLoad settlement={Settlement} save={Save}";
        }

        if (NewGameRequested)
        {
            return $"scenario=NewGame map={Map ?? "<first built-in>"} faction={Faction}";
        }

        return "scenario=none (driver idle)";
    }

    private static bool HasFlag(string flag)
    {
        return Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetValue(string flag)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length - 1; i++)
        {
            if (string.Equals(arguments[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }
}

[Context("MainMenu")]
public sealed class MainMenuTestConfigurator : IConfigurator
{
    public void Configure(IContainerDefinition containerDefinition)
    {
        containerDefinition.Bind<MainMenuTestDriver>().AsSingleton();
    }
}

public sealed class MainMenuTestDriver : IUpdatableSingleton
{
    // Give the menu a few frames to finish initializing before starting the
    // scenario, mirroring when a human could first click a menu button.
    private const int StartAfterUpdates = 10;

    private readonly GameSceneLoader _gameSceneLoader;
    private readonly GameSaveRepository _gameSaveRepository;
    private readonly MapRepository _mapRepository;
    private int _updates;
    private bool _done;

    public MainMenuTestDriver(GameSceneLoader gameSceneLoader,
                              GameSaveRepository gameSaveRepository,
                              MapRepository mapRepository)
    {
        _gameSceneLoader = gameSceneLoader;
        _gameSaveRepository = gameSaveRepository;
        _mapRepository = mapRepository;
    }

    public void UpdateSingleton()
    {
        if (_done)
        {
            return;
        }

        if (!TestArguments.AnyScenarioRequested)
        {
            _done = true;
            return;
        }

        if (++_updates < StartAfterUpdates)
        {
            return;
        }

        _done = true;
        try
        {
            if (TestArguments.MenuLoadRequested)
            {
                StartMenuLoad();
            }
            else
            {
                StartNewGame();
            }
        }
        catch (Exception exception)
        {
            Debug.LogError("[T3MPTEST] ERROR: scenario failed to start: " + exception);
        }
    }

    private void StartMenuLoad()
    {
        var settlementName = TestArguments.Settlement;
        var saveName = TestArguments.Save;
        if (string.IsNullOrEmpty(settlementName) || string.IsNullOrEmpty(saveName))
        {
            Debug.LogError("[T3MPTEST] ERROR: MenuLoad needs -t3mpTestSettlement and -t3mpTestSave.");
            return;
        }

        var settlement = _gameSaveRepository.GetAllSettlements()
            .FirstOrDefault(reference => reference.SettlementName == settlementName);
        if (settlement == null)
        {
            Debug.LogError("[T3MPTEST] ERROR: settlement not found: " + settlementName + ". Known: " +
                           string.Join(", ", _gameSaveRepository.GetAllSettlements()
                               .Select(reference => reference.SettlementName)));
            return;
        }

        var saveReference = new SaveReference(saveName, settlement);
        if (!_gameSaveRepository.SaveExists(saveReference))
        {
            Debug.LogError("[T3MPTEST] ERROR: save not found: " + saveReference);
            return;
        }

        Debug.Log("[T3MPTEST] MenuLoad: starting " + saveReference);
        _gameSceneLoader.StartSaveGameInstantly(saveReference);
    }

    private void StartNewGame()
    {
        var mapName = TestArguments.Map ?? _mapRepository.GetBuiltinMapNames().FirstOrDefault();
        if (string.IsNullOrEmpty(mapName))
        {
            Debug.LogError("[T3MPTEST] ERROR: no built-in maps found for NewGame.");
            return;
        }

        Debug.Log("[T3MPTEST] NewGame: starting map=" + mapName + " faction=" + TestArguments.Faction);
        _gameSceneLoader.StartNewGameInstantly(
            TestArguments.Faction, MapFileReference.FromResource(mapName), TestArguments.NewSettlement);
    }
}

[Context("Game")]
public sealed class GameTestConfigurator : IConfigurator
{
    public void Configure(IContainerDefinition containerDefinition)
    {
        containerDefinition.Bind<GameTestDriver>().AsSingleton();
    }
}

public sealed class GameTestDriver : IPostLoadableSingleton
{
    private readonly IContainer _container;
    private readonly SpeedManager _speedManager;
    private readonly Timberborn.EntitySystem.EntityRegistry _entityRegistry;

    public GameTestDriver(SpeedManager speedManager, Timberborn.EntitySystem.EntityRegistry entityRegistry, IContainer container)
    {
        _container = container;
        _speedManager = speedManager;
        _entityRegistry = entityRegistry;
    }

    public void PostLoad()
    {
        // Start observers before scenario handling so the tube flag also works
        // alone, without unpausing or changing the game speed.
        if (ModelGapMonitor.Requested && !TestArguments.LoadRoutingRequested &&
            (TestArguments.AnyScenarioRequested || TestArguments.Speed != null))
        {
            new GameObject("T3MPTEST.ModelGap").AddComponent<ModelGapMonitor>().Initialize(_entityRegistry);
        }
        if (TubeLightMonitor.Requested)
        {
            new GameObject("T3MPTEST.TubeLights").AddComponent<TubeLightMonitor>().Initialize(_entityRegistry, _container);
        }
        if (TestArguments.LoadRoutingRequested)
        {
            _speedManager.ChangeSpeed(0f);
            new GameObject("T3MPTEST.LoadRouting").AddComponent<LoadRoutingTestDriver>().Initialize(_entityRegistry, _speedManager, _container);
            return;
        }
        if (!TestArguments.AnyScenarioRequested && TestArguments.Speed == null)
        {
            return;
        }

        var speed = TestArguments.Speed ?? 1f;
        Debug.Log("[T3MPTEST] Game scene loaded OK. Unpausing (speed " + speed + ").");
        _speedManager.ChangeSpeed(speed);
        if (TestArguments.Speed != null)
        {
            new GameObject("T3MPTEST.SimulationRate").AddComponent<SimulationRateLogger>();
        }
    }
}

// Logs full ticks per second in fixed real-time windows so A/B runs of the
// same save can be compared from Player.log. Test driver only.
public sealed class SimulationRateLogger : MonoBehaviour
{
    private const float WindowSeconds = 20f;
    private float _windowStart;
    private long _windowTicks;
    private float _totalStart;
    private long _totalTicks;
    private int _windows;

    private void Start()
    {
        _windowStart = _totalStart = Time.realtimeSinceStartup;
        _windowTicks = _totalTicks = FullTickCounter.FullTicks;
    }

    private void Update()
    {
        var now = Time.realtimeSinceStartup;
        if (now - _windowStart < WindowSeconds) return;
        var ticks = FullTickCounter.FullTicks;
        var rate = (ticks - _windowTicks) / (now - _windowStart);
        var total = (ticks - _totalTicks) / (now - _totalStart);
        _windows++;
        Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[T3MPTEST] Simulation rate {0:F2} ticks/s window={1} ticks={2} timeScale={3:F2} cumulative={4:F2} ticks/s",
            rate, _windows, ticks - _windowTicks, Time.timeScale, total));
        try
        {
            var water = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime.WaterTextureUpload")).FirstOrDefault(t => t != null);
            if (water != null)
            {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                Debug.Log("[T3MPTEST] water calls=" + water.GetField("Calls", all)!.GetValue(null) + " uploaded=" + water.GetField("Uploaded", all)!.GetValue(null) +
                          " identical=" + water.GetField("Reused", all)!.GetValue(null));
            }
            var frontier = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime.TickFrontier")).FirstOrDefault(t => t != null);
            if (frontier != null)
            {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                Debug.Log("[T3MPTEST] frontier sweeps=" + frontier.GetField("Sweeps", all)!.GetValue(null) + " visited=" + frontier.GetField("Visited", all)!.GetValue(null) +
                          " skipped=" + frontier.GetField("Skipped", all)!.GetValue(null));
            }
            var tube = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime.TubeVisitFix")).FirstOrDefault(t => t != null);
            if (tube != null)
            {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                Debug.Log("[T3MPTEST] tube repairs=" + tube.GetField("Repairs", all)!.GetValue(null));
            }
        }
        catch (Exception) { /* diagnostics only */ }
        TickProfiler.Report(now - _windowStart);
        _windowStart = now;
        _windowTicks = ticks;
    }
}
