using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;

// dotnet run --project experiments/terrain-visits -c Release -- <Managed> <new output dir>
// Executes native/candidate game methods on in-memory graphs under CoreCLR only.
if (args.Length != 2) throw new ArgumentException("managed-directory new-output-directory");
var managed = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var source = Path.Combine(managed, "Timberborn.Navigation.dll");
if (Directory.Exists(output)) throw new IOException("Output already exists");
Directory.CreateDirectory(output);
string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
var sourceHash = Hash(source);
if (sourceHash is not ("65139131DFD0D633F578F0601ED6F9D8DE8D752CA9260CF4078D938B5099ED27" or
                      "5B4E275A0592A8F6F25A1D6612C7BA8E6056FE17EF0EDA785F8E701CCF74A442"))
    throw new InvalidOperationException("Unreviewed Navigation DLL");
var candidate = Path.Combine(output, Path.GetFileName(source));
using (var resolver = new DefaultAssemblyResolver())
{
    resolver.AddSearchDirectory(managed);
    using var module = ModuleDefinition.ReadModule(source, new ReaderParameters { AssemblyResolver = resolver });
    Rewrite.Apply(module);
    module.Write(candidate);
}
var a = new Harness(managed, source);
var b = new Harness(managed, candidate);
var random = new Random(726631);
var costs = new[] { 0f, 0.5f, 1f, 2f, 5f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
var cases = 0;
void Compare(int start, int range)
{
    var x = a.Run(start, range); var y = b.Run(start, range);
    if (x != y) throw new Exception($"Mismatch at case {cases}: {x} versus {y}");
    cases++;
}
// Repeated searches reuse actual service queues/sets; graphs and restrictions
// change between calls, including cycles, duplicates, range edges and bad IDs.
for (var iteration = 0; iteration < 500; iteration++)
{
    var count = random.Next(1, 100);
    var graph = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, random.Next(0, 9))
        .Select(_ => new Edge(random.Next(count), costs[random.Next(costs.Length)])).ToArray()).ToArray();
    var restricted = Enumerable.Range(0, count).Select(_ => random.Next(4) == 0).ToArray();
    a.SetGraph(graph, restricted); b.SetGraph(graph, restricted);
    for (var j = 0; j < 10; j++) Compare(random.Next(count), random.Next(-1, 8));
}
// Exceptions leave queue/set state intact just as in native; no retry/cleanup.
foreach (var graph in new[] {
    new[] { new[] { new Edge(8, 1), new Edge(1, 1) }, Array.Empty<Edge>() },
    new[] { new[] { new Edge(-1, 1) } },
    new[] { Array.Empty<Edge>() }
})
{
    a = new Harness(managed, source); b = new Harness(managed, candidate);
    a.SetGraph(graph, new bool[graph.Length]); b.SetGraph(graph, new bool[graph.Length]);
    Compare(0, 5); Compare(0, 0); Compare(-1, 5);
}
// Native fallback must retain comparer call order, including exceptions.
foreach (var throwAt in new[] { 0, 1, 4, 12 })
{
    a = new Harness(managed, source); b = new Harness(managed, candidate);
    var graph = new[] { new[] { new Edge(1, 1), new Edge(1, 1), new Edge(0, 1) }, new[] { new Edge(0, 1) } };
    a.SetGraph(graph, new bool[2]); b.SetGraph(graph, new bool[2]);
    var ca = new TracingComparer(throwAt); var cb = new TracingComparer(throwAt);
    a.SetComparer(ca); b.SetComparer(cb); Compare(0, 5);
    if (!ca.Trace.SequenceEqual(cb.Trace)) throw new Exception("Comparer trace differs");
}

// Search-only timing: actual game BFS methods, synthetic regular terrain.
// Reflection is outside the timed loop; each harness binds the native entry.
a = new Harness(managed, source); b = new Harness(managed, candidate);
const int width = 64, repeats = 200000;
var grid = Enumerable.Range(0, width * width).Select(i => new[] { i - width, i + width, i - 1, i + 1 }
    .Where(j => j >= 0 && j < width * width && (j / width == i / width || j % width == i % width))
    .Select(j => new Edge(j, 1)).ToArray()).ToArray();
a.SetGraph(grid, new bool[grid.Length]); b.SetGraph(grid, new bool[grid.Length]);
Compare(width * 32 + 32, 5);
a.Time(1000); b.Time(1000);
var times = new List<object>();
var nativeMs = new List<double>(); var candidateMs = new List<double>();
for (var pair = 0; pair < 6; pair++)
{
    double x, y;
    if (pair % 2 == 0) { x = a.Time(repeats); y = b.Time(repeats); }
    else { y = b.Time(repeats); x = a.Time(repeats); }
    nativeMs.Add(x); candidateMs.Add(y);
    times.Add(new { nativeMs = x, candidateMs = y, ratio = x / y });
}
if (Hash(source) != sourceHash) throw new Exception("Source changed");
var report = new {
    cases, mismatches = 0, sourceHash, candidateHash = Hash(candidate),
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
    repeats, timing = times, pooledSearchRatio = nativeMs.Sum() / candidateMs.Sum(),
    scope = "Actual native and copy-patched TerrainReachabilityService under CoreCLR; generated graphs. Not Unity Mono, n10c throughput, visual regression or MOD compatibility evidence. No installed DLL changed."
};
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

sealed class TracingComparer(int throwAt) : IEqualityComparer<int>
{
    internal readonly List<string> Trace = new();
    void Record(string s) { Trace.Add(s); if (throwAt > 0 && Trace.Count == throwAt) throw new InvalidOperationException("Injected comparer failure"); }
    public bool Equals(int x, int y) { Record($"eq:{x}:{y}"); return x == y; }
    public int GetHashCode(int x) { Record($"hash:{x}"); return x % 2; }
}
