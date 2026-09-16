using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Timberborn.Common;
using Timberborn.TemplateSystem;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using T3MP.Runtime;

internal static class NativeReplay
{
    private static long sink;
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string logPath,string? codePath)
    {
        var corpus=new List<Dictionary<string,ReachableYielder>>();
        foreach(var line in File.ReadLines(logPath).Where(x=>x.StartsWith("[T3MPHARVESTINPUT] ")))
        {
            using var json=JsonDocument.Parse(line.Substring("[T3MPHARVESTINPUT] ".Length));
            var distances=json.RootElement.GetProperty("distances").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
            var orders=json.RootElement.GetProperty("orders").EnumerateArray().Select(x=>x.GetInt32()).ToArray();
            if(distances.Length!=orders.Length)throw new Exception("Malformed corpus");
            var row=new Dictionary<string,ReachableYielder>();
            for(var i=0;i<distances.Length;i++)
            {
                var template=new InstantiatedTemplate(null!);
                typeof(InstantiatedTemplate).GetProperty("InstantiationOrder")!.SetValue(template,orders[i]);
                var yielder=new Yielder(null!);
                typeof(Yielder).GetField("_instantiatedTemplate",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(yielder,template);
                row.Add(i.ToString(),new ReachableYielder(yielder,distances[i]));
            }
            corpus.Add(row);
        }
        if(corpus.Count==0)throw new Exception("No real-game input snapshots");
        if(codePath!=null) NativeFailureCases.Run(codePath);
        var tree=new CandidateMinTree<ReachableYielder>();var set=new SortedSet<ReachableYielder>();
        foreach(var row in corpus)
        {
            set.AddRange(row.Values);tree.Build(row.Values);
            foreach(var expected in set)
                if(!tree.TryPop(out var got)||!ReferenceEquals(expected.Yielder,got.Yielder)||!expected.Distance.Equals(got.Distance))throw new Exception("Native replay mismatch");
            if(tree.TryPop(out _))throw new Exception("Extra candidate");tree.Clear();set.Clear();
        }
        long Native(int loops){long sum=0;for(var i=0;i<loops;i++)foreach(var row in corpus){set.AddRange(row.Values);foreach(var x in set){sum+=x.Yielder.InstantiationOrder;break;}set.Clear();}return sum;}
        long Trial(int loops){long sum=0;for(var i=0;i<loops;i++)foreach(var row in corpus){tree.Build(row.Values);if(tree.TryPop(out var x))sum+=x.Yielder.InstantiationOrder;tree.Clear();}return sum;}
        Native(200);Trial(200);
        var native=new List<double>();var trial=new List<double>();const int rounds=2000;
        for(var pair=0;pair<8;pair++)foreach(var arm in (pair%2==0?"NT":"TN"))
        {var sw=Stopwatch.StartNew();sink+=arm=='N'?Native(rounds):Trial(rounds);sw.Stop();(arm=='N'?native:trial).Add(sw.Elapsed.TotalMilliseconds);}
        Console.WriteLine(JsonSerializer.Serialize(new{snapshots=corpus.Count,group_histogram=corpus.GroupBy(x=>x.Count).ToDictionary(g=>g.Key,g=>g.Count()),
            game_assembly_mvid=typeof(ReachableYielder).Assembly.ManifestModule.ModuleVersionId,comparisons="All ordered identities, then first-candidate selection timing; buffers reused; actual game comparator and collection extension; CoreCLR",
            queries_per_arm_per_pair=rounds*corpus.Count,sorted_set_ms=native,segment_tree_ms=trial,time_ratio=trial.Sum()/native.Sum()}));
        GC.KeepAlive(sink);
    }
}
