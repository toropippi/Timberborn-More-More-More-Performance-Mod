using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// Exact fast removal from the game's append-only-ordered registry lists.
///
/// Both EntityRegistry._entitiesInInstantiationOrder and every
/// EntityComponentRegistry._registeredComponents[type] list are appended in
/// chronological registration order and removed with List.Remove - a LINEAR
/// reference scan over the whole list (measured 435us per entity delete for
/// the ~100k-entity instantiation-order list, and ~1ms combined for the
/// per-type component lists on n10c). List enumeration order is load-bearing
/// (save order, GetEnabled iteration feeding RNG picks), so the list object
/// and its ordering must be preserved exactly.
///
/// Trick: stamp every registered object with a global monotonic sequence
/// number at registration time. Appends happen in stamp order, removals keep
/// relative order, so every list stays sorted by stamp - the index of an
/// element can be found by binary search over stamps (O(log n) weak-table
/// lookups) followed by List.RemoveAt, which is state-identical to
/// List.Remove of a unique element. Any missing stamp falls back to the
/// vanilla path for that call.
/// </summary>
internal static class OrderedListFastRemove
{
    private sealed class SeqBox
    {
        public long Value;
    }

    private static readonly ConditionalWeakTable<object, SeqBox> Stamps = new ConditionalWeakTable<object, SeqBox>();
    private static long _nextStamp;
    private static long _fastRemoves;
    private static long _fallbacks;
    private static int _warned;

    public static void Stamp(object registered)
    {
        if (registered is null)
        {
            return;
        }

        // Re-registration gets a fresh stamp: the object is re-appended at
        // the tail, so its stamp must again exceed every earlier element.
        Stamps.GetOrCreateValue(registered).Value = ++_nextStamp;
    }

    /// <summary>
    /// Removes the first (unique) occurrence of <paramref name="item"/> from
    /// <paramref name="list"/> by stamp binary search. Returns false when the
    /// caller must fall back to the vanilla linear removal (missing stamps or
    /// unexpected ordering) - never partially mutates on failure.
    /// </summary>
    public static bool TryRemove<T>(List<T> list, T item) where T : class
    {
        if (item is null || !Stamps.TryGetValue(item, out var targetBox))
        {
            CountFallback();
            return false;
        }

        var target = targetBox.Value;
        var lo = 0;
        var hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var candidate = list[mid];
            if (ReferenceEquals(candidate, item))
            {
                list.RemoveAt(mid);
                Interlocked.Increment(ref _fastRemoves);
                return true;
            }

            if (candidate is null || !Stamps.TryGetValue(candidate, out var candidateBox))
            {
                CountFallback();
                return false;
            }

            if (candidateBox.Value < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Not found by stamp search. That normally means "not present"
        // (vanilla no-op), but it could also mean an ordering violation -
        // stay conservative and let the vanilla linear scan decide.
        CountFallback();
        return false;
    }

    private static void CountFallback()
    {
        Interlocked.Increment(ref _fallbacks);
        if (_fallbacks > 1000 && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            Debug.LogWarning($"[T3MP] OrderedListFastRemove saw {_fallbacks} fallbacks - stamps may not be installed on a registration path.");
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var fast = Interlocked.Exchange(ref _fastRemoves, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        if (fast == 0 && fallbacks == 0)
        {
            return;
        }

        Debug.Log($"[T3MP] OrderedListFastRemove aggregate={aggregateId}, fastRemoves={fast}, fallbacks={fallbacks}");
    }
}
