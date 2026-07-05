using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Timberborn.TickSystem;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// TEMP ATTRIBUTION PROBE, arg-gated by '-benchHotspot' (inert otherwise):
/// accumulates per-component-type Tick() time inside the flat dispatch sweep
/// and logs a ranked table every window. Measurement only - the stopwatch
/// pair around every component tick adds real overhead, so ticks/s from a
/// probed run must never be compared against clean runs (only shares within
/// the same run are meaningful).
/// </summary>
internal static class TickHotspotProbe
{
    public static readonly bool Enabled = BenchmarkSettings.BenchHotspotRequested;

    private const double WindowSeconds = 20.0;
    private const int ReportTopCount = 40;

    private sealed class Slot
    {
        public long Calls;
        public long ElapsedTicks;
    }

    private static readonly Dictionary<Type, Slot> Slots = new Dictionary<Type, Slot>(256);
    private static readonly Stopwatch WindowClock = Stopwatch.StartNew();
    private static int _windowIndex;

    public static void Record(TickableComponent component, long elapsedStopwatchTicks)
    {
        var type = component.GetType();
        if (!Slots.TryGetValue(type, out var slot))
        {
            slot = new Slot();
            Slots.Add(type, slot);
        }

        slot.Calls++;
        slot.ElapsedTicks += elapsedStopwatchTicks;
    }

    public static void MaybeReport()
    {
        if (WindowClock.Elapsed.TotalSeconds < WindowSeconds)
        {
            return;
        }

        var windowSeconds = WindowClock.Elapsed.TotalSeconds;
        WindowClock.Restart();
        _windowIndex++;

        var ranked = new List<KeyValuePair<Type, Slot>>(Slots.Count);
        long totalTicks = 0;
        long totalCalls = 0;
        foreach (var pair in Slots)
        {
            if (pair.Value.Calls == 0)
            {
                continue;
            }

            ranked.Add(pair);
            totalTicks += pair.Value.ElapsedTicks;
            totalCalls += pair.Value.Calls;
        }

        ranked.Sort((a, b) => b.Value.ElapsedTicks.CompareTo(a.Value.ElapsedTicks));

        var toMs = 1000.0 / Stopwatch.Frequency;
        var builder = new StringBuilder(4096);
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "[T3MP] Hotspot window {0} ({1:0.0}s realtime): componentTickTotal={2:0}ms calls={3} types={4}",
            _windowIndex,
            windowSeconds,
            totalTicks * toMs,
            totalCalls,
            ranked.Count);
        var limit = Math.Min(ReportTopCount, ranked.Count);
        for (var i = 0; i < limit; i++)
        {
            var slot = ranked[i].Value;
            var totalMs = slot.ElapsedTicks * toMs;
            builder.AppendLine();
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "[T3MP] Hotspot | {0} totalMs={1:0.0} share={2:0.0}% calls={3} avgUs={4:0.00}",
                ranked[i].Key.Name,
                totalMs,
                totalTicks > 0 ? 100.0 * slot.ElapsedTicks / totalTicks : 0.0,
                slot.Calls,
                slot.Calls > 0 ? totalMs * 1000.0 / slot.Calls : 0.0);
        }

        Debug.Log(builder.ToString());

        foreach (var pair in Slots)
        {
            pair.Value.Calls = 0;
            pair.Value.ElapsedTicks = 0;
        }
    }
}
