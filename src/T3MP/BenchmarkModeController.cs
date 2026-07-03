using System.Globalization;
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal sealed class BenchmarkModeController : MonoBehaviour
{
    private static BenchmarkModeController? _instance;
    private static BenchmarkMode _currentMode;
    private static int _framesInMode;
    private static bool _sampling;
    private static bool _forceOptimized;
    private static bool _renderBlackoutRequested;
    private static int _fullTicksSincePeek;
    private static bool _renderPeekPending;
    private static bool _renderPeekActive;
    private static long _renderPeeks;
    private static long _overlayFullTicks;
    private long _overlayWindowStartTicks;
    private float _overlayWindowStartRealtime;
    private float _speedupMultiplier;
    private float _speedupTicksPerSecond;
    private bool _speedupInitialized;
    private int _speedupLogs;
    private GUIStyle? _speedupStyle;
    private GUIStyle? _blackoutHintStyle;
    private Texture2D? _speedupFillTexture;

    private GUIStyle? _overlayStyle;
    private float _modeStartRealtime;
    private float _aggregateStartRealtime;
    private float _lastUpdateRealtime;
    private float _lastBlackoutKeyRealtime = -10f;
    private long _lastManagedMemory;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;
    private int _originalVSyncCount;
    private int _originalTargetFrameRate;
    private readonly HashSet<Camera> _disabledCameras = new();
    private readonly HashSet<Canvas> _disabledCanvases = new();
    private float _originalShadowDistance;
    private float _originalLodBias;
    private int _originalPixelLightCount;
    private int _originalAntiAliasing;
    private bool _originalRealtimeReflectionProbes;
    private bool _originalSoftParticles;
    private int _lastRenderPolicyFrame = -1000;
    private bool _renderPolicyApplied;
    private bool _blackoutWasActive;
    private bool _autoForceOptimizedApplied;
    private float _gameSceneEnteredRealtime = -1f;

    public static void Install()
    {
        if (_instance is not null)
        {
            return;
        }

        var gameObject = new GameObject("T3MP.BenchmarkModeController");
        DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        _instance = gameObject.AddComponent<BenchmarkModeController>();
    }

    public static bool TryGetSampleMode(out BenchmarkMode mode)
    {
        mode = _currentMode;
        return BenchmarkSettings.EnableBenchmark && BenchmarkSettings.EnableBenchmarkMeasurement && _sampling;
    }

    public static BenchmarkMode CurrentMode => _currentMode;
    public static bool RenderBlackoutActive => BenchmarkSettings.EnableOptimizedRenderBlackout &&
        _renderBlackoutRequested &&
        !_renderPeekActive &&
        _currentMode == BenchmarkMode.Optimized;

    /// <summary>
    /// Called once per completed full tick (from the benchmark probe's full
    /// tick postfix). Drives the render peek and feeds the live speedup meter.
    /// </summary>
    public static void RecordFullTickForRenderPeek()
    {
        Interlocked.Increment(ref _overlayFullTicks);

        if (!BenchmarkSettings.EnableBlackoutRenderPeek ||
            !BenchmarkSettings.EnableOptimizedRenderBlackout ||
            !_renderBlackoutRequested)
        {
            _fullTicksSincePeek = 0;
            return;
        }

        if (++_fullTicksSincePeek >= BenchmarkSettings.RenderPeekIntervalFullTicks)
        {
            _fullTicksSincePeek = 0;
            _renderPeekPending = true;
        }
    }

    private void Awake()
    {
        _currentMode = BenchmarkSettings.ForceOptimizedByDefault ? BenchmarkMode.Optimized : BenchmarkMode.Vanilla;
        _framesInMode = 0;
        _sampling = false;
        _forceOptimized = BenchmarkSettings.ForceOptimizedByDefault;
        _renderBlackoutRequested = false;
        _modeStartRealtime = Time.realtimeSinceStartup;
        _aggregateStartRealtime = _modeStartRealtime;
        _lastUpdateRealtime = _modeStartRealtime;
        _lastManagedMemory = GC.GetTotalMemory(false);
        _lastGc0 = GC.CollectionCount(0);
        _lastGc1 = GC.CollectionCount(1);
        _lastGc2 = GC.CollectionCount(2);
        _originalVSyncCount = QualitySettings.vSyncCount;
        _originalTargetFrameRate = Application.targetFrameRate;
        _originalShadowDistance = QualitySettings.shadowDistance;
        _originalLodBias = QualitySettings.lodBias;
        _originalPixelLightCount = QualitySettings.pixelLightCount;
        _originalAntiAliasing = QualitySettings.antiAliasing;
        _originalRealtimeReflectionProbes = QualitySettings.realtimeReflectionProbes;
        _originalSoftParticles = QualitySettings.softParticles;
        ApplyFrameRatePolicy();

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] Controller installed. build={0}",
            BenchmarkSettings.OptimizedImplementationName));
        Debug.Log("[T3MP] Optimizations auto-enable after load. Shift+P toggles render blackout + animation thinning. Game speed is left to the base game.");
    }

    private void Update()
    {
        var now = Time.realtimeSinceStartup;
        var elapsedSinceLastUpdate = now - _lastUpdateRealtime;
        var managedMemory = GC.GetTotalMemory(false);
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        RuntimePerformanceOverlay.RecordFrame(Time.unscaledTime, Time.unscaledDeltaTime);
        if (BenchmarkSettings.EnableHitchLogging &&
            elapsedSinceLastUpdate >= BenchmarkSettings.HitchLogThresholdSeconds)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] Hitch sec={0:F3}, mode={1}, frame={2}, cache={3}, memDeltaMb={4:F1}, gcDelta={5}/{6}/{7}, metrics={8}, main={9}",
                elapsedSinceLastUpdate,
                _currentMode,
                Time.frameCount,
                NeedBehaviorTravelOptimizer.GlobalShadowCacheEntryCount,
                (managedMemory - _lastManagedMemory) / 1048576.0,
                gc0 - _lastGc0,
                gc1 - _lastGc1,
                gc2 - _lastGc2,
                BenchmarkMetrics.GetCurrentSummary(_currentMode),
                MainLoopProfiler.GetCurrentSummary(_currentMode)));
        }

        if (elapsedSinceLastUpdate > 5f)
        {
            ResetWindows(now);
            _lastUpdateRealtime = now;
            _lastManagedMemory = managedMemory;
            _lastGc0 = gc0;
            _lastGc1 = gc1;
            _lastGc2 = gc2;
            return;
        }

        HandleHotkeys(now);
        TryAutoForceOptimizedAfterLoad(now);
        UpdateRenderPeek();
        UpdateSpeedupMeter(now);
        UpdateSpeedupOverlayUi();
        RefreshOptimizedRenderPolicy();
        _lastUpdateRealtime = now;
        _lastManagedMemory = managedMemory;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
        _framesInMode++;

        // Development-only A/B measurement. In the distributed build the mod
        // never cycles back to Vanilla and emits no benchmark logging; it runs
        // in Optimized mode after the post-load auto-force.
        if (!BenchmarkSettings.EnableBenchmarkMeasurement)
        {
            return;
        }

        _sampling = _framesInMode > BenchmarkSettings.WarmupFramesAfterSwitch;
        if (_sampling)
        {
            BenchmarkMetrics.RecordFrame(_currentMode, Time.unscaledDeltaTime);
            UnityMarkerProfiler.RecordFrame(_currentMode);
        }

        if (!_forceOptimized && now - _modeStartRealtime >= BenchmarkSettings.ModeSegmentSeconds)
        {
            SwitchMode(now);
        }

        if (now - _aggregateStartRealtime >= BenchmarkSettings.AggregateSeconds)
        {
            BenchmarkMetrics.LogAndReset(now - _aggregateStartRealtime);
            _aggregateStartRealtime = now;
        }
    }

    private static BenchmarkMode NextMode(BenchmarkMode mode)
    {
        return mode == BenchmarkMode.Vanilla ? BenchmarkMode.Optimized : BenchmarkMode.Vanilla;
    }

    private void SwitchMode(float now)
    {
        _currentMode = NextMode(_currentMode);
        ApplyFrameRatePolicy();
        _framesInMode = 0;
        _sampling = false;
        _modeStartRealtime = now;
    }

    private void HandleHotkeys(float now)
    {
        // Shift+P toggles the render blackout + animation thinning. This is the
        // mod's only hotkey; game speed is left entirely to the base game.
        var keyboard = Keyboard.current;
        if (BenchmarkSettings.EnableRenderBlackoutToggleKey &&
            keyboard is not null &&
            now - _lastBlackoutKeyRealtime >= 0.5f &&
            keyboard.pKey.wasPressedThisFrame &&
            (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed))
        {
            _lastBlackoutKeyRealtime = now;
            _renderBlackoutRequested = !_renderBlackoutRequested;
            ApplyFrameRatePolicy();
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] Turbo rendering (blackout + animation thinning) {0}.",
                _renderBlackoutRequested ? "ON" : "OFF"));
        }
    }

    private void EnterOptimizedMode(float now)
    {
        // Enables the always-on optimizations (Optimized mode). Does NOT touch
        // the render blackout: that is a separate, independent toggle.
        _forceOptimized = true;
        BenchmarkMetrics.Reset();
        _currentMode = BenchmarkMode.Optimized;
        ApplyFrameRatePolicy();
        _framesInMode = 0;
        _sampling = false;
        _modeStartRealtime = now;
        _aggregateStartRealtime = now;
    }

    private void UpdateSpeedupMeter(float now)
    {
        // Time-weighted (exponential) moving average of the simulation tick
        // rate. Sampling every frame with an EMA keeps the readout stable
        // instead of jumping between the 0/1/2 ticks a fixed 0.5s window
        // happens to catch, and it never drops to a "no data" state that would
        // make the on-screen meter blink.
        var ticksNow = Interlocked.Read(ref _overlayFullTicks);
        if (_overlayWindowStartRealtime <= 0f)
        {
            _overlayWindowStartRealtime = now;
            _overlayWindowStartTicks = ticksNow;
            return;
        }

        var elapsed = now - _overlayWindowStartRealtime;
        if (elapsed < 0.05f)
        {
            return;
        }

        var instantRate = (float)((ticksNow - _overlayWindowStartTicks) / elapsed);
        _overlayWindowStartRealtime = now;
        _overlayWindowStartTicks = ticksNow;

        // Weight past history so ~63% of the average comes from the last Tau
        // seconds; robust to bursty frames at high speed.
        const float smoothingTauSeconds = 3f;
        var alpha = 1f - Mathf.Exp(-elapsed / smoothingTauSeconds);
        if (!_speedupInitialized)
        {
            _speedupTicksPerSecond = instantRate;
            _speedupInitialized = true;
        }
        else
        {
            _speedupTicksPerSecond += (instantRate - _speedupTicksPerSecond) * alpha;
        }

        // Real-time multiplier: each simulation tick advances the world by
        // GameTickIntervalSeconds of in-game time at 1x speed, so in-game
        // seconds per real second = ticks/s * tick interval.
        _speedupMultiplier = _speedupTicksPerSecond * BenchmarkSettings.GameTickIntervalSeconds;

        if (_currentMode == BenchmarkMode.Optimized && _speedupLogs < 3)
        {
            _speedupLogs++;
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] Simulation rate {0:F1} ticks/s ({1:F1}x realtime).",
                _speedupTicksPerSecond,
                _speedupMultiplier));
        }
    }

    private void UpdateRenderPeek()
    {
        if (_renderPeekActive)
        {
            // The peek frame has been rendered; re-engage the blackout.
            _renderPeekActive = false;
            ApplyFrameRatePolicy();
            return;
        }

        if (!_renderPeekPending)
        {
            return;
        }

        _renderPeekPending = false;
        if (!BenchmarkSettings.EnableOptimizedRenderBlackout ||
            !_renderBlackoutRequested ||
            _currentMode != BenchmarkMode.Optimized)
        {
            return;
        }

        _renderPeekActive = true;
        // RenderBlackoutActive is now false, so this restores cameras,
        // canvases, and quality settings; the visual suppression patches all
        // read RenderBlackoutActive live and run normally for this frame.
        ApplyFrameRatePolicy();
        if (++_renderPeeks <= 3)
        {
            Debug.Log(string.Format(
                CultureInfo.InvariantCulture,
                "[T3MP] Render peek frame. intervalFullTicks={0}, peek={1}",
                BenchmarkSettings.RenderPeekIntervalFullTicks,
                _renderPeeks));
        }
    }

    private void TryAutoForceOptimizedAfterLoad(float now)
    {
        if (!BenchmarkSettings.EnableAutoForceOptimizedAfterLoad ||
            _autoForceOptimizedApplied ||
            _forceOptimized)
        {
            return;
        }

        // The game scene loads paused (SceneLoader leaves Time.timeScale=0), so
        // scaled clocks like Time.timeSinceLevelLoad never advance in unattended
        // runs. Measure the post-load delay with the unscaled realtime clock.
        if (SceneManager.GetActiveScene().buildIndex != 2)
        {
            _gameSceneEnteredRealtime = -1f;
            return;
        }

        if (_gameSceneEnteredRealtime < 0f)
        {
            _gameSceneEnteredRealtime = now;
            return;
        }

        if (now - _gameSceneEnteredRealtime < BenchmarkSettings.AutoForceOptimizedAfterLoadSeconds)
        {
            return;
        }

        _autoForceOptimizedApplied = true;

        if (BenchmarkSettings.MeasureAbAtUltra)
        {
            // Measurement only: drive ultra speed but let the controller cycle
            // Vanilla<->Optimized so both are measured at 50x in one run.
            // Optimized segments blackout (real mod behavior); Vanilla renders
            // (raw 50x). Speed 50 persists across both once applied.
            _renderBlackoutRequested = true;
            AutoRuntimeControl.RequestOptimizedUltraSpeed();
            _modeStartRealtime = now;
            _aggregateStartRealtime = now;
            Debug.Log("[T3MP] A/B-at-ultra measurement mode (cycling Vanilla/Optimized at 50x).");
            return;
        }

        if (BenchmarkSettings.MeasureOptimizedVisibleAtUltra)
        {
            // Measurement only: optimizations ON, render blackout OFF (visible
            // 1/2/3 mode), driven at ultra speed as if another mod set 50x.
            _forceOptimized = true;
            _renderBlackoutRequested = false;
            _currentMode = BenchmarkMode.Optimized;
            ApplyFrameRatePolicy();
            _framesInMode = 0;
            _sampling = false;
            _modeStartRealtime = now;
            _aggregateStartRealtime = now;
            AutoRuntimeControl.RequestOptimizedUltraSpeed();
            Debug.Log("[T3MP] Optimized-visible measurement mode at ultra speed (no blackout).");
            return;
        }

        if (BenchmarkSettings.MeasureVanillaBaseline)
        {
            // Measurement only: hold VANILLA (optimizations off) but drive the
            // sim at ultra speed via AutoRuntimeControl, so Vanilla ticks/s can
            // be compared with Optimized at the same speed.
            _forceOptimized = true;
            _renderBlackoutRequested = false;
            _currentMode = BenchmarkMode.Vanilla;
            ApplyFrameRatePolicy();
            _framesInMode = 0;
            _sampling = false;
            _modeStartRealtime = now;
            _aggregateStartRealtime = now;
            AutoRuntimeControl.RequestOptimizedUltraSpeed();
            Debug.Log("[T3MP] Vanilla baseline measurement mode at ultra speed.");
            return;
        }

        // Turn optimizations on automatically a few seconds after the game
        // scene loads (visible, no ultra speed / no blackout). Waiting avoids
        // optimizing during early ticking. Ultra speed is opt-in via key 4 and
        // the render blackout via key 5. The dev benchmark harness can request
        // both on load with the -benchAutoUltra command-line flag.
        var benchAutoUltra = BenchmarkSettings.BenchAutoUltraRequested;
        _renderBlackoutRequested = benchAutoUltra;
        EnterOptimizedMode(now);
        if (benchAutoUltra)
        {
            AutoRuntimeControl.RequestOptimizedUltraSpeed();
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] Optimizations enabled after load. delaySeconds={0:F1}",
            BenchmarkSettings.AutoForceOptimizedAfterLoadSeconds));
    }

    private void OnGUI()
    {
        DrawSpeedupOverlay();

        if (!BenchmarkSettings.EnableRuntimeOverlay)
        {
            return;
        }

        _overlayStyle ??= new GUIStyle(GUI.skin.box)
        {
            alignment = TextAnchor.MiddleRight,
            fontSize = 14,
            padding = new RectOffset(8, 8, 5, 5)
        };
        _overlayStyle.normal.textColor = Color.white;

        var snapshot = RuntimePerformanceOverlay.GetSnapshot();
        var rect = new Rect(Screen.width - 330f, Screen.height - 142f, 320f, 110f);
        GUI.Box(rect, snapshot.ToOverlayText(_currentMode), _overlayStyle);
    }

    private bool _speedupUsingGameUi;

    private void UpdateSpeedupOverlayUi()
    {
        var show = BenchmarkSettings.EnableSpeedupOverlay &&
            _currentMode == BenchmarkMode.Optimized &&
            _speedupInitialized;
        if (!show)
        {
            if (_speedupUsingGameUi)
            {
                SpeedupUiOverlay.Hide();
            }

            return;
        }

        // The IMGUI meter (OnGUI) is the reliable default. The UI Toolkit path
        // (game font) is experimental and off unless explicitly enabled.
        if (BenchmarkSettings.EnableSpeedupOverlayGameUi)
        {
            var text = FormatSpeedupText();
            _speedupUsingGameUi = SpeedupUiOverlay.TrySetText(text);
        }
        else if (_speedupUsingGameUi)
        {
            SpeedupUiOverlay.Hide();
            _speedupUsingGameUi = false;
        }
    }

    private string FormatSpeedupText()
    {
        // rSPD = real speed the sim is ACTUALLY running at (measured ticks/s x
        //        the 0.6s tick interval = game-seconds advanced per real second).
        // iSPD = ideal speed = Time.timeScale, i.e. what the game is TRYING to run
        //        after its population throttle (a big colony caps x50 down to less).
        // When rSPD < iSPD the machine can't keep up (CPU-bound); equal = keeping up.
        // UPS  = raw simulation updates (ticks) per second.
        var idealSpeed = Time.timeScale;
        return string.Format(
            CultureInfo.InvariantCulture,
            "rSPD/iSPD x{0:F1} / x{1:F1}\nUPS {2:F1} ticks/s",
            _speedupMultiplier,
            idealSpeed,
            _speedupTicksPerSecond);
    }

    private void DrawSpeedupOverlay()
    {
        // IMGUI fallback, used only if the UI Toolkit overlay is unavailable.
        if (!BenchmarkSettings.EnableSpeedupOverlay ||
            _currentMode != BenchmarkMode.Optimized ||
            !_speedupInitialized ||
            _speedupUsingGameUi)
        {
            return;
        }

        _speedupStyle ??= new GUIStyle
        {
            alignment = TextAnchor.MiddleRight,
            fontSize = 15,
            fontStyle = FontStyle.Bold
        };

        var text = FormatSpeedupText();
        const float width = 235f;
        const float height = 40f;
        const float hintHeight = 18f;
        // Sit just above the game's bottom-right FPS readout. Normally no box
        // background (avoids the floating-panel look), drawn as a shadow + text.
        var rect = new Rect(Screen.width - width - 9f, Screen.height - height - 30f, width, height);

        // While the render blackout is active, show a persistent reminder line
        // above the meter so an accidental Shift+P is obvious and easy to undo.
        var blackout = RenderBlackoutActive;
        var hintRect = new Rect(rect.x, rect.y - hintHeight, width, hintHeight);

        // During the render blackout the camera is disabled, so the frame buffer
        // is never cleared and successive IMGUI text draws smear on top of each
        // other. Paint an opaque strip behind the text each frame to clear the
        // previous draw. Only needed while blacked out; otherwise the game clears.
        if (blackout)
        {
            _speedupFillTexture ??= CreateFillTexture(new Color(0.06f, 0.07f, 0.06f, 1f));
            var fill = new Rect(rect.x - 6f, hintRect.y - 2f, rect.width + 12f, rect.height + hintHeight + 4f);
            GUI.DrawTexture(fill, _speedupFillTexture);

            _blackoutHintStyle ??= new GUIStyle
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = 12,
                fontStyle = FontStyle.Bold
            };
            const string hint = "(Animation skip mode [Shift+P])";
            _blackoutHintStyle.normal.textColor = new Color(0f, 0f, 0f, 0.65f);
            GUI.Label(new Rect(hintRect.x + 1f, hintRect.y + 1f, hintRect.width, hintRect.height), hint, _blackoutHintStyle);
            _blackoutHintStyle.normal.textColor = new Color(1f, 0.85f, 0.4f, 0.95f);
            GUI.Label(hintRect, hint, _blackoutHintStyle);
        }

        _speedupStyle.normal.textColor = new Color(0f, 0f, 0f, 0.65f);
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, _speedupStyle);
        _speedupStyle.normal.textColor = new Color(0.6f, 1f, 0.7f, 0.95f);
        GUI.Label(rect, text, _speedupStyle);
    }

    private static Texture2D CreateFillTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private void ApplyFrameRatePolicy()
    {
        // Only uncap the frame rate during a Shift+P blackout, where the screen
        // is not rendered and a faster loop directly speeds up the simulation.
        // During normal rendered play, uncapping just drives the GPU at hundreds
        // of fps for zero simulation benefit (the sim tick rate is not fps-bound
        // at normal speeds) and makes the game's "FPS: avg / min" readout jump to
        // 100-600 on a light map. So outside the blackout, leave the game's own
        // vsync / frame cap untouched.
        var uncapFrameRate = BenchmarkSettings.EnableOptimizedFrameRateUncap &&
            _currentMode == BenchmarkMode.Optimized &&
            _renderBlackoutRequested;
        if (uncapFrameRate)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
        }
        else
        {
            QualitySettings.vSyncCount = _originalVSyncCount;
            Application.targetFrameRate = _originalTargetFrameRate;
        }

        ApplyRenderPolicy();
    }

    private void ApplyRenderPolicy()
    {
        if (!RenderBlackoutActive)
        {
            if (_blackoutWasActive)
            {
                _blackoutWasActive = false;
                PathFollowerNoAnimationFastMove.ResyncAfterBlackout();
            }

            RestoreRenderPolicy();
            return;
        }

        _blackoutWasActive = true;
        if (SceneManager.GetActiveScene().buildIndex == 2)
        {
            if (!_renderPolicyApplied)
            {
                QualitySettings.shadowDistance = 0f;
                QualitySettings.lodBias = 0.25f;
                QualitySettings.pixelLightCount = 0;
                QualitySettings.antiAliasing = 0;
                QualitySettings.realtimeReflectionProbes = false;
                QualitySettings.softParticles = false;
                _renderPolicyApplied = true;
            }

            DisableActiveRenderSurfaces();
            return;
        }

        RestoreRenderPolicy();
    }

    private void RefreshOptimizedRenderPolicy()
    {
        if (!RenderBlackoutActive ||
            Time.frameCount - _lastRenderPolicyFrame < 60)
        {
            return;
        }

        ApplyRenderPolicy();
    }

    private void DisableActiveRenderSurfaces()
    {
        _lastRenderPolicyFrame = Time.frameCount;
        foreach (var camera in Camera.allCameras)
        {
            if (TryGetBehaviourEnabled(camera, out var isEnabled) && isEnabled)
            {
                if (!TrySetBehaviourEnabled(camera, false))
                {
                    continue;
                }

                _disabledCameras.Add(camera);
            }
        }

        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (TryGetBehaviourEnabled(canvas, out var isEnabled) && isEnabled)
            {
                if (!TrySetBehaviourEnabled(canvas, false))
                {
                    continue;
                }

                _disabledCanvases.Add(canvas);
            }
        }
    }

    private void RestoreRenderPolicy()
    {
        foreach (var camera in _disabledCameras)
        {
            TrySetBehaviourEnabled(camera, true);
        }

        _disabledCameras.Clear();

        foreach (var canvas in _disabledCanvases)
        {
            TrySetBehaviourEnabled(canvas, true);
        }

        _disabledCanvases.Clear();

        if (!_renderPolicyApplied)
        {
            return;
        }

        QualitySettings.shadowDistance = _originalShadowDistance;
        QualitySettings.lodBias = _originalLodBias;
        QualitySettings.pixelLightCount = _originalPixelLightCount;
        QualitySettings.antiAliasing = _originalAntiAliasing;
        QualitySettings.realtimeReflectionProbes = _originalRealtimeReflectionProbes;
        QualitySettings.softParticles = _originalSoftParticles;
        _renderPolicyApplied = false;
    }

    private static bool TryGetBehaviourEnabled(Behaviour? behaviour, out bool enabled)
    {
        enabled = false;
        if (behaviour == null)
        {
            return false;
        }

        try
        {
            enabled = behaviour.enabled;
            return true;
        }
        catch (MissingReferenceException)
        {
            return false;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    private static bool TrySetBehaviourEnabled(Behaviour? behaviour, bool enabled)
    {
        if (behaviour == null)
        {
            return false;
        }

        try
        {
            behaviour.enabled = enabled;
            return true;
        }
        catch (MissingReferenceException)
        {
            return false;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    private void ResetWindows(float now)
    {
        BenchmarkMetrics.Reset();
        _currentMode = _forceOptimized ? BenchmarkMode.Optimized : BenchmarkMode.Vanilla;
        ApplyFrameRatePolicy();
        _framesInMode = 0;
        _sampling = false;
        _modeStartRealtime = now;
        _aggregateStartRealtime = now;
    }

    private void OnDestroy()
    {
        if (BenchmarkSettings.EnableOptimizedFrameRateUncap)
        {
            QualitySettings.vSyncCount = _originalVSyncCount;
            Application.targetFrameRate = _originalTargetFrameRate;
        }

        RestoreRenderPolicy();
    }
}
