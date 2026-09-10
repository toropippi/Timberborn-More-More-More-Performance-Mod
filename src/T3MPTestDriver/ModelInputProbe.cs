using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static class ModelInputProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal sealed class Row { internal string Hash = ""; internal int Count, Bytes; internal double Ms, RepeatMs, Heap, RepeatHeap; }
    internal sealed class State { internal Row Row = null!; internal long Start, Heap; }
    [ThreadStatic] private static int _depth;
    private static readonly Dictionary<string, Row> Rows = new Dictionary<string, Row>(StringComparer.Ordinal);
    private static int _unsupported;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestModelInputProfile")) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.model-inputs");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        object Hook(string name)
        {
            var hook = Activator.CreateInstance(hm, typeof(ModelInputProbe).GetMethod(name, All))!;
            if (name == nameof(Stop)) hm.GetField("priority")!.SetValue(hook, 900);
            return hook;
        }
        patch.Invoke(harmony, new object?[] { Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", All), Hook(nameof(Start)), null, null, Hook(nameof(Stop)) });
        patch.Invoke(harmony, new object?[] { Find("Timberborn.Timbermesh.TimbermeshReader").GetMethod("ReadFromStream", All), Hook(nameof(BeginRead)), Hook(nameof(EndRead)), null, null });
        Debug.Log("[T3MPMODELINPUT] installed; SHA256 of remaining memory-stream bytes, attribution only");
    }
    private static void Start() { if (_depth++ == 0) { Rows.Clear(); _unsupported = 0; } }
    private static void Stop()
    {
        if (--_depth != 0) return;
        if (Rows.Count != 0)
        {
            var args = Environment.GetCommandLineArgs();
            var dir = Path.GetDirectoryName(args[Array.IndexOf(args, "-logFile") + 1])!;
            using var writer = new StreamWriter(Path.Combine(dir, "model-inputs.tsv"));
            writer.WriteLine("sha256\tcompressedBytes\tcalls\tdecodeMs\trepeatDecodeMs\theapMB\trepeatHeapMB");
            foreach (var row in Rows.Values.OrderByDescending(r => r.RepeatMs))
                writer.WriteLine(FormattableString.Invariant($"{row.Hash}\t{row.Bytes}\t{row.Count}\t{row.Ms:F3}\t{row.RepeatMs:F3}\t{row.Heap:F3}\t{row.RepeatHeap:F3}"));
            Debug.Log(FormattableString.Invariant($"[T3MPMODELINPUT] calls={Rows.Values.Sum(r => r.Count)} unique={Rows.Count} unsupported={_unsupported} decodeMs={Rows.Values.Sum(r => r.Ms):F3} repeatDecodeMs={Rows.Values.Sum(r => r.RepeatMs):F3} heapMB={Rows.Values.Sum(r => r.Heap):F3} repeatHeapMB={Rows.Values.Sum(r => r.RepeatHeap):F3}"));
        }
        Rows.Clear();
    }
    private static void BeginRead(Stream stream, out State? __state)
    {
        __state = null;
        if (_depth == 0) return;
        if (!(stream is MemoryStream memory) || memory.Position < 0 || memory.Position > memory.Length || memory.Length > int.MaxValue)
        { _unsupported++; return; }
        var bytes = memory.ToArray(); var start = (int)memory.Position;
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(bytes, start, bytes.Length - start)).Replace("-", "");
        if (!Rows.TryGetValue(hash, out var row)) Rows.Add(hash, row = new Row { Hash = hash, Bytes = bytes.Length - start });
        __state = new State { Row = row, Heap = GC.GetTotalMemory(false), Start = Stopwatch.GetTimestamp() };
    }
    private static void EndRead(State? __state)
    {
        if (__state == null) return;
        var ms = (Stopwatch.GetTimestamp() - __state.Start) * 1000.0 / Stopwatch.Frequency;
        var heap = (GC.GetTotalMemory(false) - __state.Heap) / 1048576.0;
        var row = __state.Row;
        if (row.Count++ != 0) { row.RepeatMs += ms; row.RepeatHeap += heap; }
        row.Ms += ms; row.Heap += heap;
    }
}
