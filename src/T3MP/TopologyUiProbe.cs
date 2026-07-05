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

    // Source sampler for mass-called methods: every 64th call records the
    // owning GameObject's prefab name, so the report shows WHAT is calling
    // (e.g. which template's models churn), not just how much.
    private static readonly Dictionary<string, int> ModelUpdateSources = new Dictionary<string, int>();
    private static int _modelUpdateSampleCounter;

    private static readonly Dictionary<string, int> ModelUpdateCallers = new Dictionary<string, int>();

    public static void SampleModelUpdateSource(object instance)
    {
        if ((_modelUpdateSampleCounter & 1023) == 1)
        {
            // Ground-truth caller attribution: one stack trace per 1024 calls.
            try
            {
                var stackTrace = new System.Diagnostics.StackTrace(1, false);
                var caller = "<unknown>";
                for (var i = 0; i < stackTrace.FrameCount; i++)
                {
                    var frame = stackTrace.GetFrame(i)?.GetMethod();
                    if (frame is null)
                    {
                        continue;
                    }

                    var typeName = frame.DeclaringType?.Name ?? "";
                    var methodName = frame.Name;
                    if (typeName.Length == 0 ||
                        typeName.StartsWith("T3MP", StringComparison.Ordinal) ||
                        typeName.Contains("BenchmarkProbe") ||
                        typeName.Contains("TopologyUiProbe") ||
                        typeName.Contains("DynamicMethodDefinition") ||
                        typeName.Contains("Harmony") ||
                        typeName.Contains("MonoMod") ||
                        methodName.Contains("_Patch") ||
                        methodName.Contains("UpdateModel"))
                    {
                        continue;
                    }

                    caller = $"{typeName}.{methodName}";
                    break;
                }

                ModelUpdateCallers.TryGetValue(caller, out var callerCount);
                ModelUpdateCallers[caller] = callerCount + 1;
            }
            catch (Exception)
            {
                // Sampling only; never let it disturb the game.
            }
        }

        if ((_modelUpdateSampleCounter++ & 63) != 0)
        {
            return;
        }

        string name;
        try
        {
            name = instance is Timberborn.BaseComponentSystem.BaseComponent component
                ? component.GameObject.name
                : instance.GetType().Name;
        }
        catch (Exception)
        {
            return;
        }

        var cloneIndex = name.IndexOf('(');
        if (cloneIndex > 0)
        {
            name = name.Substring(0, cloneIndex).TrimEnd();
        }

        ModelUpdateSources.TryGetValue(name, out var count);
        ModelUpdateSources[name] = count + 1;
    }

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

        ReportModelUpdateSources();
    }

    private static void ReportModelUpdateSources()
    {
        if (ModelUpdateSources.Count == 0)
        {
            return;
        }

        var entries = new List<KeyValuePair<string, int>>(ModelUpdateSources);
        entries.Sort(static (left, right) => right.Value.CompareTo(left.Value));
        var builder = new StringBuilder(256);
        builder.Append("[T3MP] TopoUI modelSources (x64 sampled)");
        var shown = 0;
        foreach (var entry in entries)
        {
            builder.Append(" | ");
            builder.Append(entry.Key);
            builder.Append(" n~");
            builder.Append((entry.Value * 64L).ToString(CultureInfo.InvariantCulture));
            if (++shown >= 8)
            {
                break;
            }
        }

        Debug.Log(builder.ToString());
        ModelUpdateSources.Clear();

        if (ModelUpdateCallers.Count > 0)
        {
            var callers = new List<KeyValuePair<string, int>>(ModelUpdateCallers);
            callers.Sort(static (left, right) => right.Value.CompareTo(left.Value));
            var callerBuilder = new StringBuilder(256);
            callerBuilder.Append("[T3MP] TopoUI modelCallers (x1024 sampled)");
            var shownCallers = 0;
            foreach (var entry in callers)
            {
                callerBuilder.Append(" | ");
                callerBuilder.Append(entry.Key);
                callerBuilder.Append(" n~");
                callerBuilder.Append((entry.Value * 1024L).ToString(CultureInfo.InvariantCulture));
                if (++shownCallers >= 6)
                {
                    break;
                }
            }

            Debug.Log(callerBuilder.ToString());
            ModelUpdateCallers.Clear();
        }
    }
}
