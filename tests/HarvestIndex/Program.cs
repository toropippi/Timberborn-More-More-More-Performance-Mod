using T3MP.Runtime;
using System.Diagnostics;
using System.Text.Json;

var random=new Random(23094);var index=new SharedClosestIndex();var input=new List<HarvestDistance>();
var floats=new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-0f,0f,1f,2f,3f,5f};
for(var iteration=0;iteration<20000;iteration++)
{
    switch(random.Next(5))
    {
        case 0:if(input.Count<256)input.Insert(random.Next(input.Count+1),new HarvestDistance("g"+random.Next(5),floats[random.Next(floats.Length)]));break;
        case 1:if(input.Count>0)input.RemoveAt(random.Next(input.Count));break;
        case 2:if(input.Count>0){var i=random.Next(input.Count);input[i]=new HarvestDistance(input[i].Good,floats[random.Next(floats.Length)]);}break;
        case 3:if(input.Count>0){var i=random.Next(input.Count);input[i]=new HarvestDistance("g"+random.Next(5),input[i].Distance);}break;
        case 4:if(input.Count>1){var i=random.Next(input.Count);var j=random.Next(input.Count);(input[i],input[j])=(input[j],input[i]);}break;
    }
    var native=new Dictionary<string,int>();
    for(var i=0;i<input.Count;i++)if(!native.TryGetValue(input[i].Good,out var prior)||input[i].Distance<input[prior].Distance)native[input[i].Good]=i;
    index.Synchronize(input);
    if(index.GroupCount!=native.Count)throw new Exception("Group mismatch");
    var group=0;foreach(var pair in native){if(index.Good(group)!=pair.Key||index.Winner(group)!=pair.Value)throw new Exception("Winner mismatch at "+iteration);group++;}
}
Console.WriteLine("PASS 20000 sequential snapshots: insertion, removal, reorder, good/distance change, NaN-first native semantics, ties, empty/full and incremental point updates");
if(args.Length>0)
{
    var rows=File.ReadLines(args[0]).Where(x=>x.StartsWith("[T3MPHARVESTINDEXINPUT] ")).Select(x=>JsonSerializer.Deserialize<Snapshot>(x.Substring("[T3MPHARVESTINDEXINPUT] ".Length))!).ToArray();
    if(rows.Length==0)throw new Exception("No n10c corpus");
    var inputs=rows.Select(r=>r.goods.Select((g,i)=>new HarvestDistance(g,r.distances[i])).ToArray()).ToArray();
    var indexes=new Dictionary<int,SharedClosestIndex>();var dicts=new Dictionary<int,Dictionary<string,int>>();
    for(var i=0;i<rows.Length;i++){indexes.TryAdd(rows[i].finder,new SharedClosestIndex());dicts.TryAdd(rows[i].finder,new Dictionary<string,int>());}
    // Each replay preserves the captured per-finder sequence; no invented coordinates.
    long Native(int repeats){long checksum=0;for(var r=0;r<repeats;r++)for(var q=0;q<rows.Length;q++){var values=inputs[q];var d=dicts[rows[q].finder];d.Clear();for(var j=0;j<values.Length;j++)if(!d.TryGetValue(values[j].Good,out var prior)||values[j].Distance<values[prior].Distance)d[values[j].Good]=j;foreach(var p in d)checksum+=p.Value;}return checksum;}
    long Trial(int repeats){long checksum=0;for(var r=0;r<repeats;r++)for(var q=0;q<rows.Length;q++){var idx=indexes[rows[q].finder];idx.Synchronize(inputs[q]);for(var g=0;g<idx.GroupCount;g++)checksum+=idx.Winner(g);}return checksum;}
    for(var q=0;q<rows.Length;q++)
    {
        var values=inputs[q];var d=dicts[rows[q].finder];d.Clear();
        for(var j=0;j<values.Length;j++)if(!d.TryGetValue(values[j].Good,out var prior)||values[j].Distance<values[prior].Distance)d[values[j].Good]=j;
        var idx=indexes[rows[q].finder];idx.Synchronize(values);var g=0;
        foreach(var p in d){if(idx.Good(g)!=p.Key||idx.Winner(g)!=p.Value)throw new Exception("Corpus ordered identity mismatch");g++;}
        if(g!=idx.GroupCount)throw new Exception("Corpus extra group");
    }
    if(Native(1)!=Trial(1))throw new Exception("Corpus checksum mismatch");Native(100);Trial(100);
    var nt=new List<double>();var tt=new List<double>();long sink=0;const int repeats=1000;
    for(var pair=0;pair<8;pair++)foreach(var arm in(pair%2==0?"NT":"TN")){var sw=Stopwatch.StartNew();sink+=arm=='N'?Native(repeats):Trial(repeats);sw.Stop();(arm=='N'?nt:tt).Add(sw.Elapsed.TotalMilliseconds);}
    Console.WriteLine(JsonSerializer.Serialize(new{snapshots=rows.Length,finders=indexes.Count,inputs=inputs.Sum(x=>x.Length),queries_per_pair=rows.Length*repeats,native_ms=nt,index_ms=tt,time_ratio=tt.Sum()/nt.Sum()}));GC.KeepAlive(sink);
}
internal sealed class Snapshot {public int finder{get;set;}public string[] goods{get;set;}=Array.Empty<string>();public float[] distances{get;set;}=Array.Empty<float>();}
