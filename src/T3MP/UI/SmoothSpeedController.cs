using System;

namespace T3MP.UI;

internal interface ISmoothSpeedEnvironment
{
    float RequestedSpeed { get; }
    float TimeScale { get; set; }
    int VSyncCount { get; set; }
    int TargetFrameRate { get; set; }
}

// Optional whole-game speed control. These constants reproduce the shipped
// Shift+O governor: 30 fps, x1 floor, selected-speed ceiling (at most x50).
internal sealed class SmoothSpeedController
{
    private readonly ISmoothSpeedEnvironment _environment;
    private float _scale;
    private float _frameSeconds;
    private float _lastAppliedScale;
    private int _savedVSync;
    private int _savedFrameRate;

    internal bool Enabled { get; private set; }
    internal bool Active { get; private set; }

    internal SmoothSpeedController(ISmoothSpeedEnvironment environment) => _environment = environment;

    internal void Toggle()
    {
        Enabled = !Enabled;
        if (!Enabled) Suspend();
    }

    internal void Step(float frameSeconds, bool canGovern)
    {
        var requested = _environment.RequestedSpeed;
        var current = _environment.TimeScale;
        if (!Enabled || !canGovern || !Finite(requested) || requested <= 1f ||
            !Finite(current) || current <= 0f || !Finite(frameSeconds) || frameSeconds <= 0f)
        {
            Suspend();
            return;
        }

        if (!Active)
        {
            _scale = Math.Max(1f, current);
            _frameSeconds = Math.Max(frameSeconds, 1f / 240f);
            _savedVSync = _environment.VSyncCount;
            _savedFrameRate = _environment.TargetFrameRate;
            Active = true;
        }
        _frameSeconds += (frameSeconds - _frameSeconds) * 0.1f;
        const float targetSeconds = 1f / 30f;
        if (_frameSeconds > targetSeconds * 1.06f) _scale *= 0.95f;
        else if (_frameSeconds < targetSeconds * 0.94f) _scale *= 1.02f;
        _scale = Math.Max(1f, Math.Min(_scale, Math.Min(requested, 50f)));

        // Remember settings changed by the options menu while this mode runs.
        if (_environment.VSyncCount != 0) _savedVSync = _environment.VSyncCount;
        if (_environment.TargetFrameRate != -1) _savedFrameRate = _environment.TargetFrameRate;
        _environment.VSyncCount = 0;
        _environment.TargetFrameRate = -1;
        _environment.TimeScale = _lastAppliedScale = _scale;
    }

    internal void Suspend()
    {
        if (!Active) return;
        Active = false;
        // Only release values we still own. A native pause or another clock
        // writer takes precedence, even before CurrentSpeed catches up.
        var requested = _environment.RequestedSpeed;
        if (_environment.TimeScale == _lastAppliedScale && Finite(requested) && requested > 0f)
            _environment.TimeScale = requested;
        if (_environment.VSyncCount == 0) _environment.VSyncCount = _savedVSync;
        if (_environment.TargetFrameRate == -1) _environment.TargetFrameRate = _savedFrameRate;
    }

    internal void Stop()
    {
        Enabled = false;
        Suspend();
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
