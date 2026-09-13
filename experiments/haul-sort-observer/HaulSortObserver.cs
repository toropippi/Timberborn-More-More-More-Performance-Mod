using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using T3MP.Loading;
using Timberborn.Hauling;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Opt-in feasibility measurement. Native candidate evaluation and Sort run once
// on every call. Snapshots are observations, never returned to simulation code.
internal static class HaulSortObserver
{
    private const string Owner = "t3mp.test.haul-sort-observer";
    private const BindingFlags All = LoadPatchBridge.All;
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestHaulSortObserve", StringComparer.OrdinalIgnoreCase);
    private sealed class Snapshot
    {
        internal WeightedBehavior[] Input = Array.Empty<WeightedBehavior>(), Output = Array.Empty<WeightedBehavior>();
        internal Comparison<WeightedBehavior>? Comparison;
        internal bool Valid;
    }
    private static ConditionalWeakTable<List<WeightedBehavior>, Snapshot> _snapshots = new();
    private static bool _installed, _active;
    private static long _calls, _items, _repeated, _repeatedItems, _mismatches, _sortTicks, _repeatSortTicks, _checkTicks;
    private static int _maxItems;

    internal static void Install()
    {
        if (!Requested || _installed) return;
        var mvid = typeof(WeightedBehavior).Module.ModuleVersionId.ToString();
        if (mvid != "2f772520-0086-4a71-bcde-e930227e0b4c" && mvid != "8078a46a-ffdf-4f10-bc85-ad441c52c6eb")
            throw new InvalidOperationException("Haul observer requires a reviewed Hauling module");
        var target = LoadPatchBridge.Find("Timberborn.Hauling.DistrictHaulCandidates").GetMethod("GetWorkplaceBehaviorsOrdered", All)!;
        if (!LoadCompatibility.Unmodified(new[] { target }, Owner))
            throw new InvalidOperationException("Haul observer cannot run with foreign caller patches");
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony");
        var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, Owner)!;
        var rewrite = LoadPatchBridge.Create(Owner, typeof(HaulSortObserver).GetMethod(nameof(Rewrite), All)!);
        ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5)
            .Invoke(harmony, new object?[] { target, null, null, Activator.CreateInstance(hm, rewrite), null });
        _installed = true;
        Debug.Log("[T3MPHAULSORT] installed module=" + mvid);
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions)
    {
        var list = instructions.ToList();
        var operand = typeof(T).GetField("operand", All)!;
        var opcode = typeof(T).GetField("opcode", All)!;
        var native = typeof(List<WeightedBehavior>).GetMethod("Sort", new[] { typeof(Comparison<WeightedBehavior>) })!;
        var calls = list.Where(i => Equals(operand.GetValue(i), native) && (OpCode)opcode.GetValue(i)! == OpCodes.Callvirt).ToArray();
        if (calls.Length != 1) throw new InvalidOperationException("Expected one native haul sort call");
        operand.SetValue(calls[0], typeof(HaulSortObserver).GetMethod(nameof(Observe), All)!);
        opcode.SetValue(calls[0], OpCodes.Call);
        return list;
    }

    private static bool Same(List<WeightedBehavior> values, WeightedBehavior[] previous)
    {
        if (values.Count != previous.Length) return false;
        for (var i = 0; i < values.Count; i++)
            if (!ReferenceEquals(values[i].WorkplaceBehavior, previous[i].WorkplaceBehavior) ||
                BitConverter.SingleToInt32Bits(values[i].Weight) != BitConverter.SingleToInt32Bits(previous[i].Weight)) return false;
        return true;
    }

    private static void Observe(List<WeightedBehavior> values, Comparison<WeightedBehavior> comparison)
    {
        if (!_active) { values.Sort(comparison); return; }
        var snapshot = _snapshots.GetOrCreateValue(values);
        var start = Stopwatch.GetTimestamp();
        var repeated = snapshot.Valid && ReferenceEquals(comparison, snapshot.Comparison) && Same(values, snapshot.Input);
        _checkTicks += Stopwatch.GetTimestamp() - start;
        if (snapshot.Input.Length != values.Count) snapshot.Input = new WeightedBehavior[values.Count];
        values.CopyTo(snapshot.Input);
        // Invalidate before native work so an exception cannot publish an old result.
        snapshot.Valid = false;
        start = Stopwatch.GetTimestamp();
        values.Sort(comparison);
        var elapsed = Stopwatch.GetTimestamp() - start;
        _calls++;
        _items += values.Count;
        _maxItems = Math.Max(_maxItems, values.Count);
        _sortTicks += elapsed;
        if (repeated)
        {
            _repeated++;
            _repeatedItems += values.Count;
            _repeatSortTicks += elapsed;
            if (!Same(values, snapshot.Output)) _mismatches++;
        }
        if (snapshot.Output.Length != values.Count) snapshot.Output = new WeightedBehavior[values.Count];
        values.CopyTo(snapshot.Output);
        snapshot.Comparison = comparison;
        snapshot.Valid = true;
    }

    internal static void Begin()
    {
        if (!Requested) return;
        if (!_installed) throw new InvalidOperationException("Haul observer was not installed");
        _snapshots = new();
        _calls = _items = _repeated = _repeatedItems = _mismatches = _sortTicks = _repeatSortTicks = _checkTicks = 0;
        _maxItems = 0;
        _active = true;
    }
    internal static void End() { _active = false; _snapshots = new(); }
    internal static void Report()
    {
        if (!_active) return;
        var ms = 1000.0 / Stopwatch.Frequency;
        Debug.Log(string.Format(CultureInfo.InvariantCulture,
            "[T3MPHAULSORT] cumulative calls={0} items={1} repeated={2} repeatedItems={3} maxItems={4} mismatches={5} sortMs={6:F6} repeatSortMs={7:F6} checkMs={8:F6}",
            _calls, _items, _repeated, _repeatedItems, _maxItems, _mismatches, _sortTicks * ms, _repeatSortTicks * ms, _checkTicks * ms));
    }
}
