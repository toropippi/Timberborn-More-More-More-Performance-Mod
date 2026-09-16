"""Exercise the actual NextIndex method without starting Unity; optional CoreCLR timing."""
import pathlib
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[1]
source = (root / "src/T3MP/Runtime/TickFrontier.cs").read_text(encoding="utf-8-sig")
start = source.index("internal int NextIndex(")
opening = source.index("{", start)
depth = 1
end = opening + 1
while depth:
    depth += (source[end] == "{") - (source[end] == "}")
    end += 1
method = source[start:end].replace("TickableEntity", "int")
output = root / "testlogs/frontier-search"
output.mkdir(parents=True, exist_ok=True)
(output / "Probe.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
  <EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Program.cs" /></ItemGroup>
</Project>''', encoding="utf-8")
program = r'''
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

class Search {
    internal Dictionary<Guid, int> _members = new();
    internal SortedList<Guid, byte> _eligible = new();
    internal bool Untrusted;
    __METHOD__
    // Previously shipped search, retained only as a differential oracle.
    internal int Previous(SortedList<Guid, int> source, int cursor) {
        if (Untrusted || _members.Count != source.Count || cursor < 0 || cursor >= source.Count) return cursor;
        var lower = source.Keys[cursor]; var keys = _eligible.Keys;
        int low = 0, high = keys.Count;
        while (low < high) {
            var mid = low + (high - low) / 2;
            if (keys[mid].CompareTo(lower) < 0) low = mid + 1; else high = mid;
        }
        if (low >= keys.Count) return source.Count;
        var result = source.IndexOfKey(keys[low]);
        return result < cursor ? cursor : result;
    }
}
class TracingComparer : IComparer<Guid> {
    internal readonly List<(Guid,Guid)> Trace = new();
    internal bool Fail;
    public int Compare(Guid a, Guid b) {
        Trace.Add((a,b));
        if (Fail) throw new InvalidOperationException("comparer failure");
        return a.CompareTo(b);
    }
}
class Program {
    static long checks, sink;
    static Guid Key(int n) => new Guid(n, 0, 0, new byte[8]);
    static void Check(bool b, string message) { checks++; if (!b) throw new Exception(message); }
    static void Compare(Search s, SortedList<Guid,int> source, bool valid) {
        for (int cursor = -1; cursor <= source.Count + 1; cursor++) {
            int actual = s.NextIndex(source,cursor);
            Check(actual == s.Previous(source,cursor), "differential mismatch");
            if (!valid || cursor < 0 || cursor >= source.Count) continue;
            int linear = cursor;
            while (linear < source.Count && !s._eligible.ContainsKey(source.Keys[linear])) linear++;
            Check(actual == linear, "live linear traversal mismatch");
        }
    }
    static void Main(string[] args) {
        var rng = new Random(13092026);
        var source = new SortedList<Guid,int>(); var s = new Search();
        Compare(s,source,true);
        // Exhaustive small populations: every eligibility set and cursor.
        for (int n = 1; n <= 10; n++) {
            source.Clear(); s._members.Clear();
            for (int i = 0; i < n; i++) { source.Add(Key(i),i); s._members.Add(Key(i),i); }
            for (int mask = 0; mask < (1 << n); mask++) {
                s._eligible.Clear();
                for (int i = 0; i < n; i++) if ((mask & (1 << i)) != 0) s._eligible.Add(Key(i),0);
                Compare(s,source,true);
            }
        }
        // Live membership/eligibility changes, clear/reload and same-count replacement.
        for (int step = 0; step < 12000; step++) {
            var bytes = new byte[16]; rng.NextBytes(bytes); var key = new Guid(bytes);
            if (step % 101 == 0) { source.Clear(); s._members.Clear(); s._eligible.Clear(); }
            if (source.Count > 0 && rng.Next(3) == 0) {
                var old = source.Keys[rng.Next(source.Count)];
                source.Remove(old); s._members.Remove(old); s._eligible.Remove(old);
            }
            if (!source.ContainsKey(key)) { source.Add(key,0); s._members.Add(key,0); }
            key = source.Keys[rng.Next(source.Count)];
            if (rng.Next(2) == 0) s._eligible[key] = 0; else s._eligible.Remove(key);
            Compare(s,source,true);
        }
        // Untrusted/count mismatch and stale eligible keys retain the original fallback.
        s.Untrusted = true; Compare(s,source,false); s.Untrusted = false;
        s._members.Clear(); Compare(s,source,false);
        foreach (var k in source.Keys) s._members[k] = 0;
        s._eligible[Key(int.MinValue)] = 0; s._eligible[Key(int.MaxValue)] = 0;
        Compare(s,source,false);
        // Custom comparisons retain both the call trace and thrown exceptions.
        var comparer = new TracingComparer(); source = new SortedList<Guid,int>(comparer); s = new Search();
        for (int i = 0; i < 32; i++) { source.Add(Key(i),i); s._members[Key(i)] = i; s._eligible[Key(i)] = 0; }
        for (int cursor = 0; cursor < source.Count; cursor++) {
            comparer.Trace.Clear(); var previous = s.Previous(source,cursor); var trace = comparer.Trace.ToArray();
            comparer.Trace.Clear(); Check(s.NextIndex(source,cursor) == previous,"custom result");
            Check(trace.SequenceEqual(comparer.Trace),"custom comparison trace");
        }
        comparer.Fail = true;
        string previousFailure = null;
        foreach (Func<int> call in new Func<int>[] { () => s.Previous(source,0), () => s.NextIndex(source,0) }) {
            string failure = null;
            try { call(); } catch (Exception e) {
                Check(e.GetBaseException().Message == "comparer failure", "unexpected exception cause");
                for (; e != null; e = e.InnerException) failure += e.GetType().FullName + ":" + e.Message + "\n";
            }
            Check(failure != null,"custom comparison exception lost");
            if (previousFailure != null) Check(failure == previousFailure,"custom exception wrapping changed");
            previousFailure = failure;
        }
        Console.WriteLine($"PASS: {checks} assertions; exhaustive/live/fallback/custom-comparer cases.");
        if (!args.Contains("--benchmark")) return;
        Console.WriteLine("CoreCLR search-only timings; not Timberborn/Mono performance.");
        foreach (int percent in new[] { 0, 1, 10, 50, 100 }) {
            s = new Search(); source = new SortedList<Guid,int>();
            for (int i = 0; i < 8192; i++) {
                source.Add(Key(i),i); s._members[Key(i)] = i;
                if (i % 100 < percent) s._eligible[Key(i)] = 0;
            }
            double Run(bool candidate) {
                var timer = Stopwatch.StartNew(); long value = 0;
                for (int repeat = 0; repeat < 100; repeat++)
                    for (int cursor = 0; cursor < source.Count; cursor++) {
                        cursor = candidate ? s.NextIndex(source,cursor) : s.Previous(source,cursor);
                        value += cursor;
                    }
                sink = value; return timer.Elapsed.TotalMilliseconds;
            }
            for (int warm = 0; warm < 4; warm++) { Run(false); Run(true); }
            var oldTimes = new List<double>(); var newTimes = new List<double>();
            for (int pair = 0; pair < 6; pair++) {
                if (pair % 2 == 0) { oldTimes.Add(Run(false)); newTimes.Add(Run(true)); }
                else { newTimes.Add(Run(true)); oldTimes.Add(Run(false)); }
            }
            oldTimes.Sort(); newTimes.Sort();
            Console.WriteLine($"eligible={percent}% previous_ms={oldTimes[3]:F3} candidate_ms={newTimes[3]:F3} ratio={oldTimes[3]/newTimes[3]:F3}");
        }
        GC.KeepAlive(sink);
    }
}
'''
(output / "Program.cs").write_text(program.replace("__METHOD__", method), encoding="utf-8")
subprocess.run(["dotnet", "run", "--project", str(output / "Probe.csproj"), "-c", "Release",
                "--", *sys.argv[1:]], cwd=root, check=True)
