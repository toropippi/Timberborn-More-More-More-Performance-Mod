using System;
using System.Linq;
using T3MPTestDriver;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var window = new FixedTickWindow(100, 2, 3, 0);
window.Frame(0.5);
window.Tick(101, 1);
Check(!window.Started, "Warmup must not count as measured time");
window.Tick(102, 2);
window.Frame(2.2); // crosses start: excluded
window.Tick(103, 2.3);
window.Tick(104, 2.4); // several ticks before the next rendered frame
window.Frame(2.5);
window.Tick(105, 3);
window.Frame(3.2); // crosses end: excluded
Check(window.Complete && window.StartTick == 102 && window.EndTick == 105, "Exact tick boundaries");
Check(window.EndSeconds - window.StartSeconds == 1, "Elapsed time at tick boundaries");
Check(window.Frames.Count == 1 && Math.Abs(window.Frames[0] - 0.3) < 1e-10, "Only fully contained frames");
window.Tick(106, 99);
window.Frame(100);
Check(window.EndTick == 105 && window.EndSeconds == 3 && window.Frames.Count == 1, "Completion is immutable");
var immediate = new FixedTickWindow(12, 0, 1, 7);
Check(immediate.Started && immediate.StartTick == 12 && immediate.StartSeconds == 7, "Zero warmup");
immediate.Tick(13, 9);
Check(immediate.Complete && immediate.EndSeconds - immediate.StartSeconds == 2, "Single tick interval");
var samples = Enumerable.Range(1, 100).Select(x => (double)x).ToArray();
Check(FixedTickWindow.Percentile(samples, .5) == 50 && FixedTickWindow.Percentile(samples, .95) == 95 && FixedTickWindow.Percentile(samples, .99) == 99, "Nearest-rank percentiles");
Check(FixedTickWindow.Percentile(new[] { 7d }, .99) == 7, "Single frame");
try { FixedTickWindow.Percentile(Array.Empty<double>(), .5); throw new Exception("Empty samples accepted"); } catch (ArgumentException) { }
try { new FixedTickWindow(0, -1, 1, 0); throw new Exception("Negative warmup accepted"); } catch (ArgumentOutOfRangeException) { }
Console.WriteLine("PASS: fixed ticks, warmup, multi-tick frames, boundary exclusion, immutable completion, percentiles.");
