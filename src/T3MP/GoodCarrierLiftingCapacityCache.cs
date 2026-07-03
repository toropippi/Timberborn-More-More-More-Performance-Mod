using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.Carrying;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class GoodCarrierLiftingCapacityCache
{
    private static readonly ConditionalWeakTable<GoodCarrier, CacheEntry> Entries = new();

    private static long _attempts;
    private static long _hits;
    private static long _stores;

    public static bool TryGet(GoodCarrier carrier, ref int result, out CallState state)
    {
        state = CallState.Inactive;
        if (!BenchmarkSettings.EnableGoodCarrierLiftingCapacityFrameCache ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            carrier is null)
        {
            return true;
        }

        Interlocked.Increment(ref _attempts);
        var frame = Time.frameCount;
        var entry = Entries.GetOrCreateValue(carrier);
        if (entry.Frame == frame)
        {
            result = entry.Value;
            Interlocked.Increment(ref _hits);
            state = CallState.Skipped;
            return false;
        }

        state = new CallState(true, carrier, frame);
        return true;
    }

    public static void Store(CallState state, int result)
    {
        if (!state.Active)
        {
            return;
        }

        var entry = Entries.GetOrCreateValue(state.Carrier);
        entry.Frame = state.Frame;
        entry.Value = result;
        Interlocked.Increment(ref _stores);
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var hits = Interlocked.Exchange(ref _hits, 0);
        var stores = Interlocked.Exchange(ref _stores, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] GoodCarrierLiftingCapacityCache aggregate={0}, enabled={1}, attempts={2}, hits={3}, hitRate={4:F3}, stores={5}",
            aggregateId,
            BenchmarkSettings.EnableGoodCarrierLiftingCapacityFrameCache,
            attempts,
            hits,
            attempts > 0 ? (double)hits / attempts : 0.0,
            stores));
    }

    public readonly struct CallState
    {
        public static readonly CallState Inactive = new CallState(false, null!, 0);
        public static readonly CallState Skipped = new CallState(false, null!, 0);

        public CallState(bool active, GoodCarrier carrier, int frame)
        {
            Active = active;
            Carrier = carrier;
            Frame = frame;
        }

        public bool Active { get; }
        public GoodCarrier Carrier { get; }
        public int Frame { get; }
    }

    private sealed class CacheEntry
    {
        public int Frame = -1;
        public int Value;
    }
}
