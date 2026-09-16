using System;
using System.Collections.Generic;

namespace T3MPTestDriver;

// Measurement only: boundaries are consecutive TickProgressService postfixes.
// Frames crossing either boundary are excluded from frame-time statistics.
internal sealed class FixedTickWindow
{
    private readonly long _startTarget, _endTarget;
    private readonly List<double> _frames = new List<double>(16384);
    private double _previousFrame = double.NaN;
    internal bool Started { get; private set; }
    internal bool Complete { get; private set; }
    internal long StartTick { get; private set; }
    internal long EndTick { get; private set; }
    internal double StartSeconds { get; private set; }
    internal double EndSeconds { get; private set; }
    internal IReadOnlyList<double> Frames => _frames;

    internal FixedTickWindow(long originTick, int warmupTicks, int measuredTicks, double now)
    {
        if (warmupTicks < 0 || measuredTicks <= 0) throw new ArgumentOutOfRangeException();
        _startTarget = checked(originTick + warmupTicks);
        _endTarget = checked(_startTarget + measuredTicks);
        if (warmupTicks == 0) Start(originTick, now);
    }

    internal void Tick(long tick, double now)
    {
        if (Complete) return;
        if (!Started && tick >= _startTarget) Start(tick, now);
        if (tick < _endTarget) return;
        EndTick = tick;
        EndSeconds = now;
        Complete = true;
    }

    private void Start(long tick, double now)
    {
        StartTick = tick;
        StartSeconds = now;
        Started = true;
    }

    internal void Frame(double now)
    {
        if (Started && _previousFrame >= StartSeconds && (!Complete || now <= EndSeconds))
            _frames.Add(now - _previousFrame);
        _previousFrame = now;
    }

    internal static double Percentile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0) throw new ArgumentException("No complete frame samples");
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * quantile) - 1)];
    }
}
