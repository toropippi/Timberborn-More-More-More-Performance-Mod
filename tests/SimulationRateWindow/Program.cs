using System;
using T3MP.UI;

static void Equal(double actual, double expected)
{
    if (Math.Abs(actual - expected) > 1e-9) throw new Exception($"Expected {expected}, got {actual}");
}

var window = new SimulationRateWindow();
window.Reset(100, 1000, 600);
window.SampleAt(100.25, 1000, 600);
if (window.Ready) throw new Exception("Startup must wait for a meaningful sample");
window.SampleAt(101, 1010, 606);
Equal(window.UpdatesPerSecond, 10);
Equal(window.RealSpeed, 6);
window.SampleAt(102, 1020, 612);
Equal(window.UpdatesPerSecond, 10);
window.SampleAt(103, 1030, 618);
Equal(window.RealSpeed, 6);

// A long render stall must be included in the denominator, not clamped away.
window.SampleAt(113, 1030, 618);
Equal(window.UpdatesPerSecond, 0);
window.SampleAt(114, 1040, 624);
Equal(window.UpdatesPerSecond, 10.0 / 11);

// Pause/resume establishes a fresh baseline without carrying pre-pause ticks.
window.Reset(120, 1040, 624);
window.SampleAt(121, 1040, 624);
Equal(window.RealSpeed, 0);
window.Reset(130, 1040, 624);
window.SampleAt(131, 1045, 627);
Equal(window.UpdatesPerSecond, 5);
Equal(window.RealSpeed, 3);

// Different native tick durations affect rSPD, not the update count.
window.Reset(200, 0, 0);
window.SampleAt(201, 10, 6);
window.SampleAt(202, 20, 9);
Equal(window.UpdatesPerSecond, 10);
Equal(window.RealSpeed, 4.5);

// World replacement/counter reset and non-monotonic clock cannot leak rates.
window.SampleAt(203, 0, 0);
if (window.Ready) throw new Exception("Counter reset was not detected");
window.SampleAt(199, 0, 0);
Equal(window.UpdatesPerSecond, 0);
Console.WriteLine("PASS: startup, rolling window, stall, pause/resume, tick duration, reset");
