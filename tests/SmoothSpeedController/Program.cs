using System;
using T3MP.UI;

var env = new Clock();
var mode = new SmoothSpeedController(env);
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
}
void Frames(int count, float seconds)
{
    for (int i = 0; i < count; i++) mode.Step(seconds, true);
}

Frames(100, .1f);
Check(!mode.Enabled && env.TimeScale == 7 && env.VSyncCount == 1 && env.TargetFrameRate == 60, "default off");
mode.Toggle();
Frames(150, .1f);
Check(mode.Active && env.TimeScale == 1 && env.VSyncCount == 0 && env.TargetFrameRate == -1, "heavy load floor");
Frames(300, .01f);
Check(env.TimeScale == 7, "headroom recovers to selected x7");
env.RequestedSpeed = env.TimeScale = 3;
Frames(300, .01f);
Check(env.TimeScale == 3, "button x3 ceiling");
mode.Toggle();
Check(env.TimeScale == 3 && env.VSyncCount == 1 && env.TargetFrameRate == 60, "toggle off restores limits");
env.RequestedSpeed = env.TimeScale = 7;
mode.Toggle();
Frames(10, .1f);
env.TimeScale = 0; // Native pause can set the clock before its selected-speed event.
mode.Step(.1f, true);
Check(env.TimeScale == 0 && !mode.Active && mode.Enabled && env.VSyncCount == 1, "pause stays paused");
env.RequestedSpeed = 0;
mode.Toggle();
Check(env.TimeScale == 0, "off while paused");
mode.Toggle();
env.RequestedSpeed = env.TimeScale = 7;
Frames(10, .1f);
env.RequestedSpeed = env.TimeScale = 1;
mode.Step(.1f, true);
Check(env.TimeScale == 1 && !mode.Active && env.TargetFrameRate == 60, "x1 suspension");
env.RequestedSpeed = env.TimeScale = 7;
Frames(10, .1f);
mode.Step(.1f, false);
Check(env.TimeScale == 7 && !mode.Active && mode.Enabled && env.VSyncCount == 1, "focus/scene suspension");
Frames(10, .1f);
env.TimeScale = 2.5f;
env.VSyncCount = 2;
env.TargetFrameRate = 144;
mode.Stop();
Check(env.TimeScale == 2.5f && env.VSyncCount == 2 && env.TargetFrameRate == 144, "external changes preserved on release");
env.RequestedSpeed = env.TimeScale = 7;
mode.Toggle();
Frames(10, .1f);
mode.Stop();
Check(!mode.Enabled && env.TimeScale == 7 && env.VSyncCount == 2 && env.TargetFrameRate == 144, "unload releases global settings");
var newWorld = new SmoothSpeedController(env);
Check(!newWorld.Enabled && !newWorld.Active, "new world starts off");
mode.Toggle();
Frames(10, .1f);
env.VSyncCount = 3;
env.TargetFrameRate = 90;
Frames(1, .1f);
mode.Stop();
Check(env.VSyncCount == 3 && env.TargetFrameRate == 90, "options changes restored");
foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, 0f, -1f })
{
    mode.Toggle();
    mode.Step(invalid, true);
    Check(!mode.Active && env.TimeScale == 7, "invalid sample");
    mode.Stop();
}
Console.WriteLine("PASS: default off, x1/x3/x7 bounds, load/headroom response, pause/resume, toggle, focus/scene, unload/new world, settings ownership, invalid samples.");

sealed class Clock : ISmoothSpeedEnvironment
{
    public float RequestedSpeed { get; set; } = 7;
    public float TimeScale { get; set; } = 7;
    public int VSyncCount { get; set; } = 1;
    public int TargetFrameRate { get; set; } = 60;
}
