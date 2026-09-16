using System.Diagnostics;
using System.Text.Json;
using T3MP.Runtime;

readonly record struct Candidate(float Distance, int Order, int Identity) : IComparable<Candidate>
{
    public int CompareTo(Candidate other) { var n=Distance.CompareTo(other.Distance); return n!=0?n:Order.CompareTo(other.Order); }
}

internal static class Program
{
    private static long sink;
    private static void Main(string[] args)
    {
        if(args.Length>0 && (args[0]=="--replay" || args[0]=="--index-cases"))
        {
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(_,name)=>{
                var path=Path.Combine(args[1],name.Name+".dll");
                return File.Exists(path)?System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)):null;
            };
            if(args[0]=="--index-cases")NativeIndexCases.Run(args[2]);else NativeReplay.Run(args[2],args.Length>3?args[3]:null);return;
        }
        var random=new Random(61283);
        var tree=new CandidateMinTree<Candidate>();
        var distances=new[]{float.NaN,float.NegativeInfinity,-1f,-0f,0f,0.01f,1f,2f,float.PositiveInfinity};
        for(var i=0;i<10000;i++)
        {
            var input=Enumerable.Range(0,random.Next(0,129)).Select(n=>new Candidate(distances[random.Next(distances.Length)],random.Next(16),n)).ToArray();
            var expected=new SortedSet<Candidate>(); foreach(var c in input) expected.Add(c);
            tree.Build(input); var got=new List<Candidate>(); while(tree.TryPop(out var c)) got.Add(c);
            if(!expected.SequenceEqual(got)) throw new Exception("Ordered identity mismatch: "+i);
            tree.Clear(); if(tree.Count!=0 || tree.TryPop(out _)) throw new Exception("Stale buffer");
        }
        Console.WriteLine("PASS 10000 complete ordered identity comparisons, equal keys, NaN/infinities, empty inputs, buffer reuse");
        var rows=new List<object>();
        // Synthetic group counts, not save-derived positions or whole-game gains.
        foreach(var count in new[]{1,2,4,8,16,64})
        {
            var input=Enumerable.Range(0,count).Select(i=>new Candidate(random.NextSingle()*100,i,i)).ToArray();
            var set=new SortedSet<Candidate>();
            long Native(int loops) { long sum=0; for(int i=0;i<loops;i++){foreach(var x in input)set.Add(x); foreach(var x in set){sum+=x.Identity;break;}set.Clear();}return sum; }
            long Trial(int loops) { long sum=0; for(int i=0;i<loops;i++){tree.Build(input);if(tree.TryPop(out var x))sum+=x.Identity;tree.Clear();}return sum; }
            Native(20000); Trial(20000);
            var nTimes=new List<double>();var tTimes=new List<double>();
            const int iterations=200000;
            for(int pair=0;pair<8;pair++) foreach(var arm in (pair%2==0?"NT":"TN"))
            {var sw=Stopwatch.StartNew();sink+=arm=='N'?Native(iterations):Trial(iterations);sw.Stop();(arm=='N'?nTimes:tTimes).Add(sw.Elapsed.TotalMilliseconds);}
            rows.Add(new{groups=count,iterations,sorted_set_ms=nTimes,segment_tree_ms=tTimes,time_ratio=tTimes.Sum()/nTimes.Sum()});
        }
        Console.WriteLine(JsonSerializer.Serialize(rows));GC.KeepAlive(sink);
    }
}
