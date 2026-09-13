using System;

namespace T3MPTestDriver;

// Dev-only endpoint limiter. A cycle consists of the singleton bucket followed
// by every entity bucket. The native next-index wrap identifies its completion.
internal sealed class StairsTickBoundary
{
    private readonly int _buckets, _target;
    internal int CompletedTicks { get; private set; }
    internal StairsTickBoundary(int buckets, int target)
    {
        if (buckets < 1 || target < 1) throw new ArgumentOutOfRangeException();
        _buckets = buckets; _target = target;
    }
    internal int LimitBatch(int requested, int nextIndex)
    {
        var remaining = checked((_target - CompletedTicks) * _buckets - nextIndex);
        if (remaining < 0 || nextIndex < 0 || nextIndex >= _buckets) throw new InvalidOperationException("Invalid bucket boundary");
        return Math.Min(requested, remaining);
    }
    internal bool AfterBucket(int nextIndex)
    {
        if (nextIndex != 0) return false;
        if (++CompletedTicks > _target) throw new InvalidOperationException("Completed too many cycles");
        return true;
    }
}
