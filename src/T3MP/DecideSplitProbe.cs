using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// TEMP ATTRIBUTION PROBE, arg-gated by '-benchDecide' (inert otherwise):
/// times every concrete Behavior.Decide override (per-type calls / total ms /
/// no-action release rate), BehaviorManager.ProcessBehavior per root-behavior
/// type, and BehaviorManager.TickRunningExecutor per executor type. Reports a
/// ranked table every window. Measurement only - stopwatch overhead
/// contaminates ticks/s, so probed runs are attribution-only (shares within
/// the same run are meaningful, absolute ticks/s are not).
/// </summary>
internal static class DecideSplitProbe
{
    public static readonly bool Enabled = BenchmarkSettings.BenchDecideRequested;

    private const double WindowSeconds = 20.0;
    private const int ReportTopCount = 48;
    private const int ReportCheckInterval = 2048;

    internal readonly struct ExecState
    {
        public readonly long Timestamp;
        public readonly Type? ExecutorType;

        public ExecState(long timestamp, Type? executorType)
        {
            Timestamp = timestamp;
            ExecutorType = executorType;
        }
    }

    private sealed class Slot
    {
        public long Calls;
        public long ElapsedTicks;
        public long Releases;
    }

    private static readonly Dictionary<MethodBase, Slot> DecideSlots = new Dictionary<MethodBase, Slot>(256);
    private static readonly Dictionary<Type, Slot> RootSlots = new Dictionary<Type, Slot>(64);
    private static readonly Dictionary<Type, Slot> ExecutorSlots = new Dictionary<Type, Slot>(64);
    private static readonly Stopwatch WindowClock = Stopwatch.StartNew();
    private static int _windowIndex;
    private static int _recordsSinceReportCheck;

    public static void RecordDecide(MethodBase method, long elapsedStopwatchTicks, bool releasedNow)
    {
        if (!DecideSlots.TryGetValue(method, out var slot))
        {
            slot = new Slot();
            DecideSlots.Add(method, slot);
        }

        slot.Calls++;
        slot.ElapsedTicks += elapsedStopwatchTicks;
        if (releasedNow)
        {
            slot.Releases++;
        }
    }

    public static void RecordRoot(Type behaviorType, long elapsedStopwatchTicks, bool handled)
    {
        if (!RootSlots.TryGetValue(behaviorType, out var slot))
        {
            slot = new Slot();
            RootSlots.Add(behaviorType, slot);
        }

        slot.Calls++;
        slot.ElapsedTicks += elapsedStopwatchTicks;
        if (!handled)
        {
            slot.Releases++;
        }

        if (++_recordsSinceReportCheck >= ReportCheckInterval)
        {
            _recordsSinceReportCheck = 0;
            MaybeReport();
        }
    }

    public static void RecordExecutor(Type? executorType, long elapsedStopwatchTicks)
    {
        var key = executorType ?? typeof(object);
        if (!ExecutorSlots.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            ExecutorSlots.Add(key, slot);
        }

        slot.Calls++;
        slot.ElapsedTicks += elapsedStopwatchTicks;
    }

    private static void MaybeReport()
    {
        if (WindowClock.Elapsed.TotalSeconds < WindowSeconds)
        {
            return;
        }

        var windowSeconds = WindowClock.Elapsed.TotalSeconds;
        WindowClock.Restart();
        _windowIndex++;

        var toMs = 1000.0 / Stopwatch.Frequency;
        var builder = new StringBuilder(8192);
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "[T3MP] DecideSplit window {0} ({1:0.0}s realtime)",
            _windowIndex,
            windowSeconds);

        AppendTypeTable(builder, "DecideRoot", RootSlots, toMs, releaseLabel: "unhandledRate");
        AppendTypeTable(builder, "DecideExec", ExecutorSlots, toMs, releaseLabel: null);
        AppendDecideTable(builder, toMs);

        Debug.Log(builder.ToString());

        ResetSlots(RootSlots);
        ResetSlots(ExecutorSlots);
        ResetSlots(DecideSlots);
    }

    private static void AppendTypeTable(StringBuilder builder, string label, Dictionary<Type, Slot> slots, double toMs, string? releaseLabel)
    {
        var ranked = new List<KeyValuePair<Type, Slot>>(slots.Count);
        long totalTicks = 0;
        foreach (var pair in slots)
        {
            if (pair.Value.Calls == 0)
            {
                continue;
            }

            ranked.Add(pair);
            totalTicks += pair.Value.ElapsedTicks;
        }

        ranked.Sort((a, b) => b.Value.ElapsedTicks.CompareTo(a.Value.ElapsedTicks));
        builder.AppendLine();
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "[T3MP] {0} totalMs={1:0.0} types={2}",
            label,
            totalTicks * toMs,
            ranked.Count);
        var limit = Math.Min(ReportTopCount, ranked.Count);
        for (var i = 0; i < limit; i++)
        {
            var slot = ranked[i].Value;
            var totalMs = slot.ElapsedTicks * toMs;
            builder.AppendLine();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "[T3MP] {0} | {1} totalMs={2:0.0} share={3:0.0}% calls={4} avgUs={5:0.00}",
                label,
                ranked[i].Key.Name,
                totalMs,
                totalTicks > 0 ? 100.0 * slot.ElapsedTicks / totalTicks : 0.0,
                slot.Calls,
                slot.Calls > 0 ? totalMs * 1000.0 / slot.Calls : 0.0);
            if (releaseLabel is not null)
            {
                builder.AppendFormat(
                    CultureInfo.InvariantCulture,
                    " {0}={1:0.000}",
                    releaseLabel,
                    slot.Calls > 0 ? (double)slot.Releases / slot.Calls : 0.0);
            }
        }
    }

    private static void AppendDecideTable(StringBuilder builder, double toMs)
    {
        var ranked = new List<KeyValuePair<MethodBase, Slot>>(DecideSlots.Count);
        long totalTicks = 0;
        foreach (var pair in DecideSlots)
        {
            if (pair.Value.Calls == 0)
            {
                continue;
            }

            ranked.Add(pair);
            totalTicks += pair.Value.ElapsedTicks;
        }

        ranked.Sort((a, b) => b.Value.ElapsedTicks.CompareTo(a.Value.ElapsedTicks));
        builder.AppendLine();
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "[T3MP] Decide totalMsInclusive={0:0.0} methods={1} (nested decides double-count)",
            totalTicks * toMs,
            ranked.Count);
        var limit = Math.Min(ReportTopCount, ranked.Count);
        for (var i = 0; i < limit; i++)
        {
            var slot = ranked[i].Value;
            var totalMs = slot.ElapsedTicks * toMs;
            builder.AppendLine();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "[T3MP] Decide | {0} totalMs={1:0.0} calls={2} avgUs={3:0.00} releaseRate={4:0.000}",
                ranked[i].Key.DeclaringType?.Name ?? "?",
                totalMs,
                slot.Calls,
                slot.Calls > 0 ? totalMs * 1000.0 / slot.Calls : 0.0,
                slot.Calls > 0 ? (double)slot.Releases / slot.Calls : 0.0);
        }
    }

    private static void ResetSlots<TKey>(Dictionary<TKey, Slot> slots)
    {
        foreach (var pair in slots)
        {
            pair.Value.Calls = 0;
            pair.Value.ElapsedTicks = 0;
            pair.Value.Releases = 0;
        }
    }
}
