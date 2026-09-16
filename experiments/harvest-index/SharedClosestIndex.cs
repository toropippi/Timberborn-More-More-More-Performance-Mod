using System;
using System.Collections.Generic;

namespace T3MP.Runtime;

internal readonly struct HarvestDistance
{
    internal readonly string Good;
    internal readonly float Distance;
    internal HarvestDistance(string good, float distance) { Good=good; Distance=distance; }
}

// Persistent numeric segment layout, freshly synchronized before every query.
// No entity references, eligibility flags, or unvalidated path results retained.
// Inputs preserve native enumeration order. Native < keeps the first equal
// distance and, if the first candidate is NaN, keeps that candidate forever.
internal sealed class SharedClosestIndex
{
    private sealed class Group
    {
        internal readonly string Good;
        internal readonly List<int> Indices=new List<int>();
        internal int[] Winners=Array.Empty<int>();
        internal int Size;
        internal Group(string good) { Good=good; }
    }
    private readonly List<Group> _groups=new List<Group>();
    private readonly Dictionary<string,Group> _byGood=new Dictionary<string,Group>(StringComparer.Ordinal);
    private string[] _goods=Array.Empty<string>();
    private float[] _distances=Array.Empty<float>();
    private Group[] _owners=Array.Empty<Group>();
    private int[] _local=Array.Empty<int>();
    private int _count=-1;
    internal int GroupCount=>_groups.Count;
    internal string Good(int group)=>_groups[group].Good;
    internal long Rebuilds { get; private set; }
    internal long PointUpdates { get; private set; }

    internal void Synchronize(IReadOnlyList<HarvestDistance> input)
    {
        var rebuild=input.Count!=_count;
        if(!rebuild) for(var i=0;i<_count;i++) if(input[i].Good!=_goods[i]) {rebuild=true;break;}
        if(rebuild)
        {
            _count=input.Count;
            _goods=new string[_count];_distances=new float[_count];_owners=new Group[_count];_local=new int[_count];
            _groups.Clear();_byGood.Clear();
            for(var i=0;i<_count;i++)
            {
                var candidate=input[i];
                _goods[i]=candidate.Good;_distances[i]=candidate.Distance;
                if(!_byGood.TryGetValue(candidate.Good,out var group))
                {group=new Group(candidate.Good);_byGood.Add(candidate.Good,group);_groups.Add(group);}
                _owners[i]=group;_local[i]=group.Indices.Count;group.Indices.Add(i);
            }
            foreach(var group in _groups)
            {
                group.Size=1;while(group.Size<group.Indices.Count)group.Size*=2;
                group.Winners=new int[group.Size*2];
                for(var i=0;i<group.Size;i++)group.Winners[group.Size+i]=i<group.Indices.Count?group.Indices[i]:-1;
                for(var node=group.Size-1;node>0;node--)group.Winners[node]=Better(group.Winners[node*2],group.Winners[node*2+1]);
            }
            Rebuilds++;return;
        }
        for(var i=0;i<_count;i++)
        {
            if(input[i].Distance.Equals(_distances[i]))continue;
            _distances[i]=input[i].Distance;
            var group=_owners[i];var node=(group.Size+_local[i])/2;
            for(;node>0;node/=2)group.Winners[node]=Better(group.Winners[node*2],group.Winners[node*2+1]);
            PointUpdates++;
        }
    }

    private int Better(int left,int right)
    {
        if(left<0)return right;if(right<0)return left;
        if(float.IsNaN(_distances[left]))return float.IsNaN(_distances[right])?left:right;
        if(float.IsNaN(_distances[right]))return left;
        return _distances[right]<_distances[left]?right:left;
    }

    internal int Winner(int groupIndex)
    {
        var group=_groups[groupIndex];var first=group.Indices[0];
        return float.IsNaN(_distances[first])?first:group.Winners[1];
    }
}
