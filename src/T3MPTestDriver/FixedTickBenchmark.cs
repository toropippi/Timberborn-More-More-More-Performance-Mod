using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.TimeSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Installed only by the dev driver and only with an explicit benchmark flag.
// The result is emitted before pausing. No scheduling or rendering writes occur
// during the timed interval; the common observer is present in every arm.
public sealed class FixedTickBenchmark : MonoBehaviour
{
    private static FixedTickBenchmark? _active;
    private FixedTickWindow? _window;
    private SpeedManager _speed = null!;
    private string _initialDisplay = "";
    private string _initialPatches = "";
    private float _initialTimeScale;
    private bool _displayChanged, _speedChanged;
    private bool _focused, _focusChanged;
    private int _startFrame, _endFrame;
    private double _nextProgress;
    private long _origin;
    private int _warmup, _measured;

    internal static bool Requested => Environment.GetCommandLineArgs()
        .Any(a => string.Equals(a, "-t3mpTestBenchmarkTicks", StringComparison.OrdinalIgnoreCase));
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal void Initialize(SpeedManager speed)
    {
        var args = Environment.GetCommandLineArgs();
        if (SearchProfiler.Requested) throw new InvalidOperationException("Fixed benchmark cannot run with search profiling");
        if (FloodRegression.Requested) throw new InvalidOperationException("Fixed benchmark cannot run with flood regression");
        var frontier = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime.TickFrontier")).FirstOrDefault(t => t != null);
        if (frontier?.GetField("PositionValidation", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) is true)
            throw new InvalidOperationException("Fixed benchmark cannot run with frontier positions validation");
        if (BoolInliningExperiment.Validate || BoolInliningExperiment.Synthetic) throw new InvalidOperationException("Fixed benchmark cannot run with bool validation");
        if (RoadReachabilityExperiment.Validate || RoadReachabilityExperiment.Synthetic) throw new InvalidOperationException("Fixed benchmark cannot run with road validation");
        if (new[] { "-t3mpTestTickProfile", "-t3mpTestModelGap", "-t3mpTestTubeLights", "-t3mpTestLoadRouting", "-t3mpTestEventDelegates", "-t3mpTestWaterUpload" }.Any(flag => args.Contains(flag, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Fixed benchmark cannot run with intrusive diagnostics");
        _warmup = ReadInt(args, "-t3mpTestBenchmarkWarmup", 256, 1);
        _measured = ReadInt(args, "-t3mpTestBenchmarkTicks", 0, 1);
        _speed = speed;
        _initialDisplay = Display();
        _initialPatches = Patches();
        _nextProgress = Now + 20;
        _active = this;
        Debug.Log("[T3MPTEST] Fixed benchmark prepared; awaiting first simulation tick.");
    }

    internal static void RecordTick(long tick)
    {
        var active = _active;
        if (active == null) return;
        if (active._window == null)
        {
            // SpeedManager queues ChangeSpeed until LateUpdateSingleton. The
            // first simulation tick observes the applied speed, not the paused
            // value from PostLoad. Initialization/logging precede timed work.
            active._initialTimeScale = Time.timeScale;
            active._focused = Application.isFocused;
            active._origin = tick;
            active._window = new FixedTickWindow(tick, active._warmup, active._measured, Now);
            Debug.Log("[T3MPTEST] Fixed benchmark start " + JsonUtility.ToJson(new StartReport {
                originTick = tick, warmupTicks = active._warmup, measuredTicks = active._measured,
                display = active._initialDisplay, timeScale = active._initialTimeScale, patches = active._initialPatches
            }));
        }
        else if (!active._window.Complete)
        {
            var started = active._window.Started;
            active._window.Tick(tick, Now);
            if (!started && active._window.Started) active._startFrame = Time.frameCount;
            if (active._window.Complete) active._endFrame = Time.frameCount;
        }
    }

    private void LateUpdate()
    {
        if (_window == null) return;
        var now = Now;
        _window.Frame(now);
        // Cheap live checks. Full camera/quality snapshots are outside the interval.
        _speedChanged |= Time.timeScale != _initialTimeScale;
        _focusChanged |= Application.isFocused != _focused;
        _displayChanged |= Screen.width != _width || Screen.height != _height || QualitySettings.vSyncCount != _vSync || Application.targetFrameRate != _frameCap;
        if (!_window.Complete)
        {
            if (now >= _nextProgress)
            {
                Debug.Log("[T3MPTEST] Fixed benchmark progress ticks=" + (FullTickCounter.FullTicks - _origin));
                _nextProgress = now + 20;
            }
            return;
        }
        _active = null;
        enabled = false;
        var frames = _window.Frames.ToArray();
        var sum = frames.Sum();
        var over100 = frames.Count(f => f > 0.1);
        var over250 = frames.Count(f => f > 0.25);
        Array.Sort(frames);
        var elapsed = _window.EndSeconds - _window.StartSeconds;
        var finalDisplay = Display();
        var finalPatches = Patches();
        var valid = frames.Length > 0 && elapsed > 0 && !_speedChanged && !_displayChanged && !_focusChanged &&
            finalDisplay == _initialDisplay && finalPatches == _initialPatches &&
            _window.StartTick == _origin + _warmup && _window.EndTick - _window.StartTick == _measured;
        Debug.Log("[T3MPTEST] Fixed benchmark result " + JsonUtility.ToJson(new ResultReport {
            valid = valid, originTick = _origin, startTick = _window.StartTick, endTick = _window.EndTick,
            elapsedSeconds = elapsed, ticksPerSecond = _measured / elapsed, frames = frames.Length,
            frameSeconds = sum, averageFps = sum > 0 ? frames.Length / sum : 0,
            medianFrameMs = frames.Length > 0 ? 1000 * FixedTickWindow.Percentile(frames, 0.5) : 0,
            p95FrameMs = frames.Length > 0 ? 1000 * FixedTickWindow.Percentile(frames, 0.95) : 0,
            p99FrameMs = frames.Length > 0 ? 1000 * FixedTickWindow.Percentile(frames, 0.99) : 0,
            framesOver100Ms = over100, framesOver250Ms = over250,
            nativeFrames = _endFrame - _startFrame, focused = _focused, focusChanged = _focusChanged,
            speedChanged = _speedChanged, displayChanged = _displayChanged || finalDisplay != _initialDisplay,
            display = finalDisplay, timeScale = _initialTimeScale, patches = finalPatches
        }));
        RoadReachabilityExperiment.Report();
        _speed.ChangeSpeed(0f);
    }

    private int _width, _height, _vSync, _frameCap;
    private string Display()
    {
        _width = Screen.width; _height = Screen.height;
        _vSync = QualitySettings.vSyncCount; _frameCap = Application.targetFrameRate;
        var cameras = Camera.allCameras.OrderBy(c => c.name).Select(c =>
            c.name + ":" + c.transform.position.ToString("R", CultureInfo.InvariantCulture) + ":" +
            c.transform.rotation.ToString("R", CultureInfo.InvariantCulture) + ":" + c.fieldOfView.ToString("R", CultureInfo.InvariantCulture));
        return $"{_width}x{_height};fullscreen={Screen.fullScreenMode};quality={QualitySettings.GetQualityLevel()};vsync={_vSync};cap={_frameCap};api={SystemInfo.graphicsDeviceType};cameras=" + string.Join("|", cameras);
    }

    private static string Patches()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return string.Join(",", new[] { "EventBusFastDelegates", "TickEntityFast", "WaterTextureUpload", "TickFrontier", "TubeVisitFix" }.Select(name => {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.Runtime." + name)).FirstOrDefault(t => t != null);
            return name + "=" + (type?.GetProperty("Installed", flags)?.GetValue(null)?.ToString() ?? "absent");
        })) + ",RoadCacheInstalled=" + RoadReachabilityExperiment.Installed + ",RoadCacheEnabled=" + RoadReachabilityExperiment.Enabled +
            ",BoolInlineInstalled=" + BoolInliningExperiment.Installed + ",BoolInlineEnabled=" + BoolInliningExperiment.Enabled;
    }

    private static int ReadInt(string[] args, string flag, int fallback, int minimum)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && fallback >= minimum) return fallback;
        if (index < 0 || index + 1 == args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
            throw new ArgumentException("Invalid benchmark argument: " + flag);
        return value;
    }

    private void OnDestroy() { if (ReferenceEquals(_active, this)) _active = null; }

    [Serializable] private sealed class StartReport
    {
        public long originTick;
        public int warmupTicks, measuredTicks;
        public string display = "", patches = "";
        public float timeScale;
    }
    [Serializable] private sealed class ResultReport
    {
        public bool valid, speedChanged, displayChanged, focused, focusChanged;
        public long originTick, startTick, endTick;
        public double elapsedSeconds, ticksPerSecond, frameSeconds, averageFps, medianFrameMs, p95FrameMs, p99FrameMs;
        public int frames, framesOver100Ms, framesOver250Ms, nativeFrames;
        public string display = "", patches = "";
        public float timeScale;
    }
}
