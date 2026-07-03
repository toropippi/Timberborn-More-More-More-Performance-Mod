using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class NavigationCallerSampler
{
    private const int PathUnlimitedSampleRate = 128;
    private const int RoadSampleRate = 512;
    private const int ReachableSampleRate = 512;
    private const int MaxTopCallers = 6;

    private static readonly object Lock = new object();
    private static readonly Dictionary<SampleKey, int> Samples = new Dictionary<SampleKey, int>();
    private static int _pathUnlimitedCounter;
    private static int _roadCounter;
    private static int _reachableCounter;

    public static void TryRecord(BenchmarkMode mode, string methodName)
    {
        if (!ShouldSample(methodName))
        {
            return;
        }

        var caller = FindCaller();
        lock (Lock)
        {
            var key = new SampleKey(mode, methodName, caller);
            Samples.TryGetValue(key, out var count);
            Samples[key] = count + 1;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        List<KeyValuePair<SampleKey, int>> snapshot;
        lock (Lock)
        {
            if (Samples.Count == 0)
            {
                return;
            }

            snapshot = Samples.ToList();
            Samples.Clear();
        }

        foreach (var group in snapshot.GroupBy(pair => new MethodGroupKey(pair.Key.Mode, pair.Key.MethodName)))
        {
            var totalSamples = group.Sum(pair => pair.Value);
            var top = group
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key.Caller, StringComparer.Ordinal)
                .Take(MaxTopCallers)
                .Select(pair => $"{pair.Key.Caller}:{pair.Value}");

            Debug.Log(
                $"[T3MP] NavCallerSamples aggregate={aggregateId}, mode={group.Key.Mode}, method={group.Key.MethodName}, samples={totalSamples}, top={string.Join(" | ", top)}");
        }
    }

    private static bool ShouldSample(string methodName)
    {
        switch (methodName)
        {
            case "FindPathUnlimitedRange":
                return (Interlocked.Increment(ref _pathUnlimitedCounter) % PathUnlimitedSampleRate) == 0;
            case "FindRoadPath":
                return (Interlocked.Increment(ref _roadCounter) % RoadSampleRate) == 0;
            case "DestinationIsReachableUnlimitedRange":
                return (Interlocked.Increment(ref _reachableCounter) % ReachableSampleRate) == 0;
            default:
                return false;
        }
    }

    private static string FindCaller()
    {
        var stackTrace = new StackTrace(false);
        for (var index = 0; index < stackTrace.FrameCount; index++)
        {
            var method = stackTrace.GetFrame(index)?.GetMethod();
            var declaringType = method?.DeclaringType;
            var typeName = declaringType?.FullName;
            if (method == null || string.IsNullOrEmpty(typeName))
            {
                continue;
            }

            if (ShouldSkip(typeName, method.Name))
            {
                continue;
            }

            return $"{typeName}.{method.Name}";
        }

        return "unknown";
    }

    private static bool ShouldSkip(string typeName, string methodName)
    {
        return typeName.StartsWith("T3MP", StringComparison.Ordinal)
            || typeName.StartsWith("HarmonyLib", StringComparison.Ordinal)
            || typeName.StartsWith("MonoMod.", StringComparison.Ordinal)
            || typeName.StartsWith("System.", StringComparison.Ordinal)
            || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
            || typeName == "Timberborn.Navigation.NavigationService"
            || typeName.Contains("DynamicMethodDefinition")
            || methodName.Contains("_Patch")
            || methodName.StartsWith("lambda_method", StringComparison.Ordinal);
    }

    private readonly struct SampleKey : IEquatable<SampleKey>
    {
        public SampleKey(BenchmarkMode mode, string methodName, string caller)
        {
            Mode = mode;
            MethodName = methodName;
            Caller = caller;
        }

        public BenchmarkMode Mode { get; }
        public string MethodName { get; }
        public string Caller { get; }

        public bool Equals(SampleKey other)
        {
            return Mode == other.Mode
                && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal)
                && string.Equals(Caller, other.Caller, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is SampleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Mode;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Caller);
                return hash;
            }
        }
    }

    private readonly struct MethodGroupKey : IEquatable<MethodGroupKey>
    {
        public MethodGroupKey(BenchmarkMode mode, string methodName)
        {
            Mode = mode;
            MethodName = methodName;
        }

        public BenchmarkMode Mode { get; }
        public string MethodName { get; }

        public bool Equals(MethodGroupKey other)
        {
            return Mode == other.Mode
                && string.Equals(MethodName, other.MethodName, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is MethodGroupKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)Mode * 397) ^ StringComparer.Ordinal.GetHashCode(MethodName);
            }
        }
    }
}
