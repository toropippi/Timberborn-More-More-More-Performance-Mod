using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

// Timing probe around the road-network / mechanical-graph UI hot paths that
// make path/gear placement and selection heavy on large colonies:
// preview-navmesh churn -> full district flow-field recomputes -> overlay
// mesh rebuild on the road side, and full-network DFS + rehighlight on the
// mechanical side. Pure measurement: a stopwatch around each patched call,
// with per-method totals logged every few seconds as one "TopoUI" line.
internal static class TopologyUiProbe
{
    public readonly struct State
    {
        public readonly long Timestamp;
        public readonly Slot? ProbeSlot;

        public State(long timestamp, Slot? probeSlot)
        {
            Timestamp = timestamp;
            ProbeSlot = probeSlot;
        }
    }

    public sealed class Slot
    {
        public readonly string Name;
        public long Count;
        public double TotalMilliseconds;
        public double MaxMilliseconds;

        public Slot(string name)
        {
            Name = name;
        }
    }

    // Main-thread only (every patched method is game main-loop code), so
    // plain dictionaries are safe.
    private static readonly Dictionary<MethodBase, Slot> SimpleSlots = new Dictionary<MethodBase, Slot>();
    private static readonly Dictionary<MethodBase, Dictionary<Type, Slot>> PerInstanceTypeSlots =
        new Dictionary<MethodBase, Dictionary<Type, Slot>>();
    private static readonly List<Slot> AllSlots = new List<Slot>();
    private static float _nextReportRealtime;

    public static void Begin(object? instance, MethodBase method, out State state)
    {
        var slot = ResolveSlot(instance, method);
        state = new State(Stopwatch.GetTimestamp(), slot);
    }

    public static void End(in State state)
    {
        var slot = state.ProbeSlot;
        if (slot is null)
        {
            return;
        }

        var elapsedMilliseconds = (Stopwatch.GetTimestamp() - state.Timestamp) * 1000.0 / Stopwatch.Frequency;
        slot.Count++;
        slot.TotalMilliseconds += elapsedMilliseconds;
        if (elapsedMilliseconds > slot.MaxMilliseconds)
        {
            slot.MaxMilliseconds = elapsedMilliseconds;
        }

        MaybeReport();
    }

    private static Slot ResolveSlot(object? instance, MethodBase method)
    {
        // DistrictMap has three singleton instances (regular / Preview /
        // Instant) sharing the same methods; split them by instance type so
        // the report shows which map was recomputed.
        var declaringName = method.DeclaringType?.Name ?? "<unknown>";
        var instanceType = instance?.GetType();
        if (instanceType is null || instanceType == method.DeclaringType)
        {
            if (!SimpleSlots.TryGetValue(method, out var slot))
            {
                slot = new Slot($"{declaringName}.{method.Name}");
                SimpleSlots[method] = slot;
                AllSlots.Add(slot);
            }

            return slot;
        }

        if (!PerInstanceTypeSlots.TryGetValue(method, out var byType))
        {
            byType = new Dictionary<Type, Slot>();
            PerInstanceTypeSlots[method] = byType;
        }

        if (!byType.TryGetValue(instanceType, out var typedSlot))
        {
            typedSlot = new Slot($"{instanceType.Name}.{method.Name}");
            byType[instanceType] = typedSlot;
            AllSlots.Add(typedSlot);
        }

        return typedSlot;
    }

    private static void MaybeReport()
    {
        var now = Time.realtimeSinceStartup;
        if (now < _nextReportRealtime)
        {
            return;
        }

        _nextReportRealtime = now + BenchmarkSettings.TopoUiReportWindowSeconds;

        var builder = new StringBuilder(512);
        builder.Append("[T3MP] TopoUI window=");
        builder.Append(BenchmarkSettings.TopoUiReportWindowSeconds.ToString("0.#", CultureInfo.InvariantCulture));
        builder.Append('s');
        var anyActivity = false;

        // Selection sort by total ms, log, then reset. AllSlots stays small
        // (one entry per patched method x instance type).
        AllSlots.Sort(static (left, right) => right.TotalMilliseconds.CompareTo(left.TotalMilliseconds));
        foreach (var slot in AllSlots)
        {
            if (slot.Count == 0)
            {
                continue;
            }

            anyActivity = true;
            builder.Append(" | ");
            builder.Append(slot.Name);
            builder.Append(" n=");
            builder.Append(slot.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(" tot=");
            builder.Append(slot.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append("ms max=");
            builder.Append(slot.MaxMilliseconds.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append("ms");

            slot.Count = 0;
            slot.TotalMilliseconds = 0.0;
            slot.MaxMilliseconds = 0.0;
        }

        if (anyActivity)
        {
            Debug.Log(builder.ToString());
        }
    }
}
