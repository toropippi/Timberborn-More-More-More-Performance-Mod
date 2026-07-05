using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// TEMP ATTRIBUTION PROBE, arg-gated by '-benchSpawn' (inert otherwise):
/// splits the entity spawn/delete tax. Times fixed sites (PlantNew,
/// TemplateInstantiator.Instantiate, EntityComponent lifecycle phases,
/// EntityService.Delete), every EventBus.PostNow by event type, and every
/// [OnEvent] handler body by method (via the EventBusFastDelegates wrapper).
/// Measurement only - attribution shares within a run, never absolute ticks/s.
/// </summary>
internal static class SpawnSplitProbe
{
    public static readonly bool Enabled = BenchmarkSettings.BenchSpawnRequested;

    private const double WindowSeconds = 20.0;
    private const int ReportTopCount = 40;
    private const int ReportCheckInterval = 4096;

    private sealed class Slot
    {
        public long Calls;
        public long ElapsedTicks;
    }

    private static readonly Dictionary<string, Slot> SiteSlots = new Dictionary<string, Slot>(32);
    private static readonly Dictionary<Type, Slot> EventSlots = new Dictionary<Type, Slot>(128);
    private static readonly Dictionary<MethodInfo, Slot> HandlerSlots = new Dictionary<MethodInfo, Slot>(1024);
    private static readonly Stopwatch WindowClock = Stopwatch.StartNew();
    private static int _windowIndex;
    private static int _recordsSinceReportCheck;

    public static void RecordSite(string site, long elapsedStopwatchTicks)
    {
        if (!SiteSlots.TryGetValue(site, out var slot))
        {
            slot = new Slot();
            SiteSlots.Add(site, slot);
        }

        slot.Calls++;
        slot.ElapsedTicks += elapsedStopwatchTicks;
    }

    public static void RecordEvent(Type eventType, long elapsedStopwatchTicks)
    {
        if (!EventSlots.TryGetValue(eventType, out var slot))
        {
            slot = new Slot();
            EventSlots.Add(eventType, slot);
        }

        slot.Calls++;
        slot.ElapsedTicks += elapsedStopwatchTicks;

        if (++_recordsSinceReportCheck >= ReportCheckInterval)
        {
            _recordsSinceReportCheck = 0;
            MaybeReport();
        }
    }

    public static void RecordHandler(MethodInfo method, long elapsedStopwatchTicks)
    {
        if (!HandlerSlots.TryGetValue(method, out var slot))
        {
            slot = new Slot();
            HandlerSlots.Add(method, slot);
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
            "[T3MP] SpawnSplit window {0} ({1:0.0}s realtime)",
            _windowIndex,
            windowSeconds);

        builder.AppendLine();
        builder.Append("[T3MP] SpawnSite fixed sites (nested sites double-count):");
        foreach (var pair in SiteSlots)
        {
            if (pair.Value.Calls == 0)
            {
                continue;
            }

            var totalMs = pair.Value.ElapsedTicks * toMs;
            builder.AppendLine();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "[T3MP] SpawnSite | {0} totalMs={1:0.0} calls={2} avgUs={3:0.00}",
                pair.Key,
                totalMs,
                pair.Value.Calls,
                pair.Value.Calls > 0 ? totalMs * 1000.0 / pair.Value.Calls : 0.0);
        }

        AppendRanked(builder, "SpawnEvent", EventSlots, toMs, static type => type.Name);
        AppendRanked(builder, "SpawnHandler", HandlerSlots, toMs, static method => $"{method.DeclaringType?.Name}.{method.Name}({method.GetParameters()[0].ParameterType.Name})");

        Debug.Log(builder.ToString());

        Reset(SiteSlots);
        Reset(EventSlots);
        Reset(HandlerSlots);
    }

    private static void AppendRanked<TKey>(StringBuilder builder, string label, Dictionary<TKey, Slot> slots, double toMs, Func<TKey, string> describe)
    {
        var ranked = new List<KeyValuePair<TKey, Slot>>(slots.Count);
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
            "[T3MP] {0} totalMs={1:0.0} keys={2}",
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
                describe(ranked[i].Key),
                totalMs,
                totalTicks > 0 ? 100.0 * slot.ElapsedTicks / totalTicks : 0.0,
                slot.Calls,
                slot.Calls > 0 ? totalMs * 1000.0 / slot.Calls : 0.0);
        }
    }

    private static void Reset<TKey>(Dictionary<TKey, Slot> slots)
    {
        foreach (var pair in slots)
        {
            pair.Value.Calls = 0;
            pair.Value.ElapsedTicks = 0;
        }
    }
}
