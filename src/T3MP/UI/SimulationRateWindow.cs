using System;
using System.Collections.Generic;

namespace T3MP.UI;

// Display-only samples of cumulative counters, using real elapsed time.
internal sealed class SimulationRateWindow
{
    private readonly Queue<Sample> _samples = new Queue<Sample>();
    private Sample _start, _last;
    internal double UpdatesPerSecond { get; private set; }
    internal double RealSpeed { get; private set; }
    internal bool Ready { get; private set; }

    internal void Reset(double now, long ticks, double gameSeconds)
    {
        _samples.Clear();
        _start = _last = new Sample(now, ticks, gameSeconds);
        UpdatesPerSecond = RealSpeed = 0;
        Ready = false;
    }

    // The view samples at most four times per second. Keep a measured window
    // of about two seconds, including the complete elapsed time after stalls.
    internal void SampleAt(double now, long ticks, double gameSeconds)
    {
        if (now <= _last.Time || ticks < _last.Ticks || gameSeconds < _last.GameSeconds)
        {
            Reset(now, ticks, gameSeconds);
            return;
        }
        while (_samples.Count > 0 && _samples.Peek().Time <= now - 2)
            _start = _samples.Dequeue();
        var elapsed = now - _start.Time;
        Ready = elapsed >= 1;
        UpdatesPerSecond = (ticks - _start.Ticks) / elapsed;
        RealSpeed = (gameSeconds - _start.GameSeconds) / elapsed;
        _last = new Sample(now, ticks, gameSeconds);
        _samples.Enqueue(_last);
    }

    private readonly struct Sample
    {
        internal readonly double Time, GameSeconds;
        internal readonly long Ticks;
        internal Sample(double time, long ticks, double gameSeconds)
        { Time = time; Ticks = ticks; GameSeconds = gameSeconds; }
    }
}
