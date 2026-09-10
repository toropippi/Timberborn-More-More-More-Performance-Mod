using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Windows.EventTracing;

if (args.Length < 3) throw new ArgumentException("Usage: LoadTraceAnalysis trace.etl output.json game.log ... [--native-qpc]");
var nativeQpc = args.Contains("--native-qpc");
var phases = new List<Phase>();
foreach (var log in args.Skip(2).Where(a => a != "--native-qpc"))
foreach (var line in File.ReadLines(log))
{
    if (!line.StartsWith("[T3MPRESOURCE] ") || !line.Contains("wallMs=")) continue;
    var values = Regex.Matches(line, @"(\w+)=([^ ]+)").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    var end = DateTimeOffset.Parse(values["utc"], CultureInfo.InvariantCulture);
    var wall = double.Parse(values["wallMs"], CultureInfo.InvariantCulture);
    if (wall < 100) continue;
    phases.Add(new Phase { Log = log, Name = line.Split(' ')[1], End = end, Start = end.AddMilliseconds(-wall),
        Thread = int.Parse(values["tid"]), WallMs = wall,
        QpcStart = long.Parse(values["qpcStart"]), QpcFrequency = long.Parse(values["qpcFrequency"]),
        LoggedThreadCpuMs = double.Parse(values["threadCpuMs"], CultureInfo.InvariantCulture) });
}
using var trace = TraceProcessor.Create(args[0]);
var metadata = trace.UseMetadata();
var counters = trace.UseProcessorCounters();
Console.WriteLine("Processing ETL...");
trace.Process();
if (nativeQpc)
foreach (var phase in phases)
{
    var start = metadata.StartTime.AddSeconds((phase.QpcStart - metadata.ReferenceTimestampValue!.Value.RawValue) / (double)phase.QpcFrequency);
    if (Math.Abs((start - phase.Start).TotalSeconds) > 1)
        throw new InvalidOperationException("Native QPC does not match UTC; older Mono Stopwatch logs cannot use --native-qpc.");
    phase.UtcVsQpcMs = (phase.Start - start).TotalMilliseconds;
    phase.Start = start; phase.End = start.AddMilliseconds(phase.WallMs);
}
var data = counters.Result;
Console.WriteLine("Counters: " + string.Join(",", data.CounterNames) + " deltas=" + data.ContextSwitchCounterDeltas.Count);
var threads = new Dictionary<(int Pid, int Tid), Totals>();
var captureStart = DateTimeOffset.MaxValue;
var captureEnd = DateTimeOffset.MinValue;
foreach (var delta in data.ContextSwitchCounterDeltas)
{
    var start = delta.StartTime.DateTimeOffset; var end = delta.StopTime.DateTimeOffset;
    if (start < captureStart) captureStart = start;
    if (end > captureEnd) captureEnd = end;
    var process = delta.Process;
    if (process == null || !string.Equals(process.ImageName, "Timberborn.exe", StringComparison.OrdinalIgnoreCase)) continue;
    var key = (process.Id, delta.ThreadId);
    if (!threads.TryGetValue(key, out var total)) threads[key] = total = new Totals();
    var ms = (end - start).TotalMilliseconds;
    total.Add(delta.RawCounterDeltas, ms);
    foreach (var phase in phases)
    {
        if (end <= phase.Start || start >= phase.End) continue;
        // Only fully contained slices contribute. Do not fabricate proportional
        // instruction counts at the boundaries of coarse log timestamps.
        if (start < phase.Start || end > phase.End)
        {
            phase.BoundarySlices++;
            phase.BoundaryAllThreads.Add(delta.RawCounterDeltas, ms);
            if (delta.ThreadId == phase.Thread) phase.BoundaryMainThread.Add(delta.RawCounterDeltas, ms);
            continue;
        }
        phase.AllThreads.Add(delta.RawCounterDeltas, ms);
        if (delta.ThreadId == phase.Thread) phase.MainThread.Add(delta.RawCounterDeltas, ms);
    }
}
foreach (var phase in phases)
    phase.FullyWithinCounterCapture = phase.Start >= captureStart && phase.End <= captureEnd;
var output = new { Trace = args[0], Counters = data.CounterNames, data.HasCycleCount, data.HasInstructionCount,
    NativeQpcAlignment = nativeQpc, metadata.LostBufferCount, metadata.LostEventCount,
    BoundaryCountInterpretation = "Contained counts are lower bounds; add entire Boundary counts for conservative upper bounds. No proportional interpolation.",
    CounterCaptureStart = captureStart, CounterCaptureEnd = captureEnd,
    Threads = threads.Select(p => new { Pid = p.Key.Pid, Tid = p.Key.Tid, p.Value }).OrderByDescending(x => x.Value.CpuMs), Phases = phases };
File.WriteAllText(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Saved " + args[1]);

sealed class Phase
{
    public string Log { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public int Thread { get; set; }
    public double WallMs { get; set; }
    public double LoggedThreadCpuMs { get; set; }
    public long QpcStart { get; set; }
    public long QpcFrequency { get; set; }
    public double UtcVsQpcMs { get; set; }
    public bool FullyWithinCounterCapture { get; set; }
    public int BoundarySlices { get; set; }
    public Totals MainThread { get; set; } = new();
    public Totals AllThreads { get; set; } = new();
    public Totals BoundaryMainThread { get; set; } = new();
    public Totals BoundaryAllThreads { get; set; } = new();
}
sealed class Totals
{
    public int Slices { get; set; }
    public double CpuMs { get; set; }
    public Dictionary<string, decimal> Counts { get; set; } = new();
    public void Add(IReadOnlyDictionary<string, ulong> values, double ms)
    {
        Slices++; CpuMs += ms;
        foreach (var pair in values) { Counts.TryGetValue(pair.Key, out var old); Counts[pair.Key] = old + pair.Value; }
    }
}
