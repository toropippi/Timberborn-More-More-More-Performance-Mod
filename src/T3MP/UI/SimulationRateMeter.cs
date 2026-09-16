using System.Globalization;
using Bindito.Core;
using Timberborn.InputSystem;
using Timberborn.SingletonSystem;
using Timberborn.TickSystem;
using Timberborn.TimeSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace T3MP.UI;

[Context("Game")]
public sealed class SimulationRateMeterConfigurator : IConfigurator
{
    public void Configure(IContainerDefinition containerDefinition) =>
        containerDefinition.Bind<SimulationRateMeter>().AsSingleton();
}

// One observer per game container. Counts native singleton ticks without
// patching, replacing, or changing the scheduling of any game method.
public sealed class SimulationRateMeter : ITickableSingleton, IPostLoadableSingleton, IUnloadableSingleton
{
    private readonly ITickService _tickService;
    private readonly SpeedManager _speedManager;
    private readonly InputBlocker _inputBlocker;
    private SimulationRateMeterView? _view;
    private SmoothModeView? _smoothMode;
    internal long Ticks { get; private set; }
    internal double GameSeconds { get; private set; }

    internal float RequestedSpeed => _speedManager.CurrentSpeed;
    internal bool SmoothEnabled => _smoothMode && _smoothMode.SmoothEnabled;

    public SimulationRateMeter(ITickService tickService, SpeedManager speedManager, InputBlocker inputBlocker)
    {
        _tickService = tickService;
        _speedManager = speedManager;
        _inputBlocker = inputBlocker;
    }

    public void Tick()
    {
        Ticks++;
        GameSeconds += _tickService.TickIntervalInSeconds;
    }

    public void PostLoad()
    {
        _view = new GameObject("T3MP.SpeedMeter").AddComponent<SimulationRateMeterView>();
        _view.Initialize(this);
        _smoothMode = _view.gameObject.AddComponent<SmoothModeView>();
        _smoothMode.Initialize(_speedManager, _inputBlocker);
        Debug.Log("[T3MP] Speed meter ready (rSPD/iSPD, UPS; game-scoped native tick observer).");
    }

    public void Unload()
    {
        if (_smoothMode) _smoothMode.enabled = false;
        _smoothMode = null;
        if (_view)
        {
            _view.enabled = false;
            Object.Destroy(_view.gameObject);
        }
        _view = null;
    }
}

public sealed class SimulationRateMeterView : MonoBehaviour
{
    private readonly SimulationRateWindow _window = new SimulationRateWindow();
    private SimulationRateMeter _meter = null!;
    private GUIStyle? _style;
    private double _nextSample;
    private bool _paused;
    private string _text = "rSPD/iSPD -- / --\nUPS -- ticks/s";

    internal void Initialize(SimulationRateMeter meter)
    {
        _meter = meter;
        Reset(Time.realtimeSinceStartupAsDouble);
    }

    private void Reset(double now)
    {
        _window.Reset(now, _meter.Ticks, _meter.GameSeconds);
        _nextSample = now;
    }

    private void Update()
    {
        var now = Time.realtimeSinceStartupAsDouble;
        var paused = Time.timeScale == 0;
        if (paused != _paused)
        {
            _paused = paused;
            Reset(now);
        }
        if (now < _nextSample) return;
        _nextSample = now + 0.25;
        _window.SampleAt(now, _meter.Ticks, _meter.GameSeconds);
        _text = _paused
            ? "rSPD/iSPD x0.0 / x0.0\nUPS 0.0 ticks/s"
            : _window.Ready
                ? string.Format(CultureInfo.InvariantCulture, "rSPD/iSPD x{0:F1} / x{1:F1}\nUPS {2:F1} ticks/s",
                    _window.RealSpeed, _meter.RequestedSpeed, _window.UpdatesPerSecond)
                : string.Format(CultureInfo.InvariantCulture, "rSPD/iSPD -- / x{0:F1}\nUPS -- ticks/s", _meter.RequestedSpeed);
    }

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || SceneManager.GetActiveScene().buildIndex != 2) return;
        _style ??= new GUIStyle { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
        var scale = Mathf.Clamp(Screen.height / 1080f, 1f, 2f);
        _style.fontSize = Mathf.RoundToInt(15 * scale);
        var width = Mathf.Min(280 * scale, Screen.width - 18 * scale);
        var rect = new Rect(Screen.width - width - 9 * scale, Screen.height - 70 * scale, width, 40 * scale);
        _style.normal.textColor = new Color(0, 0, 0, 0.75f);
        GUI.Label(new Rect(rect.x + scale, rect.y + scale, rect.width, rect.height), _text, _style);
        _style.normal.textColor = new Color(0.6f, 1, 0.7f, 0.95f);
        GUI.Label(rect, _text, _style);
        if (_meter.SmoothEnabled)
        {
            var modeRect = new Rect(rect.x, rect.y - 22 * scale, rect.width, 22 * scale);
            GUI.Label(modeRect, "Smooth 30fps [Shift+O]", _style);
        }
    }
}
