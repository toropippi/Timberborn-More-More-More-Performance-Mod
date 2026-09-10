using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static class InitialNavigationValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type Target => Find("T3MP.Loading.InitialNavigationLoad");
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    private static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestInitialNavValidate");
    private static int _real, _foreignCalls;
    private sealed class SmallMap : INavMeshSizeProvider { public Vector3Int Size => new(4, 4, 4); }
    private sealed class Result { internal NavMeshSource Source = null!; internal NavMeshUpdate.Builder Builder = null!; }
    private static object? Call(string name, params object[] args)
    {
        try { return Target.GetMethod(name, All)!.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    internal static void Install()
    {
        if (!Requested || !(bool)Target.GetProperty("Installed", All)!.GetValue(null)!) return;
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.initial-nav-validation");
        ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5).Invoke(harmony, new object?[] {
            Target.GetMethod("ReportApplied", All), null, Activator.CreateInstance(hm, typeof(InitialNavigationValidation).GetMethod(nameof(ReplayReal), All)), null, null });
    }
    private static void ReplayReal(NavMeshSource[] sources, NavMeshUpdate.Builder[] builders, object[] plans)
    {
        for (var i = 0; i < sources.Length; i++)
        {
            var input = (NavMeshChange[])plans[i].GetType().GetField("Input", All)!.GetValue(plans[i])!;
            var reference = Create(sources[i]._nodeIdService, sources[i] is RoadNavMeshSource);
            foreach (var c in input) Native(c, reference);
            Equal(reference, new Result { Source = sources[i], Builder = builders[i] }, "real " + sources[i].GetType().Name);
            _real++;
            Debug.Log("[T3MPINITIALNAVTEST] real PASS " + sources[i].GetType().Name + " changes=" + input.Length);
        }
    }
    private static Result Create(NodeIdService ids, bool road)
    {
        NavMeshSource source;
        if (road) { var graph = new RoadNavMeshGraph(ids); graph.Load(); source = new RoadNavMeshSource(ids, graph); }
        else { var graph = new TerrainNavMeshGraph(ids, new NavMeshGroupService()); graph.Load(); source = new TerrainNavMeshSource(ids, graph); }
        source.Load(); return new Result { Source = source, Builder = new NavMeshUpdate.Builder(ids) };
    }
    private static void Native(NavMeshChange change, Result result)
    { if (result.Source is RoadNavMeshSource r) change.Apply(r, result.Builder); else change.Apply((TerrainNavMeshSource)result.Source, result.Builder); }
    private static void Equal(Result a, Result b, string context)
    { if (Hash(a) != Hash(b)) throw new Exception("Initial navigation differs: " + context); }
    private static string Hash(Result result)
    {
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data);
        void Edges(List<NavMeshNode> edges) { writer.Write(edges.Count); foreach (var e in edges) { writer.Write(e.Id); writer.Write(e.GroupId); writer.Write(e.Cost); } }
        void Ids(List<int> ids) { writer.Write(ids.Count); foreach (var id in ids) writer.Write(id); }
        for (var i = 0; i < result.Source._nodes.Length; i++)
        {
            var n = result.Source._nodes[i]; writer.Write(n != null);
            if (n != null) { writer.Write(n.IsEmpty); Edges(n._edges); Ids(n._blockages); }
            if (result.Source._navMeshGraph is TerrainNavMeshGraph t) { Edges(t._allNeighbors[i]); Ids(t._cheapNeighbors[i]); }
            else Edges(((RoadNavMeshGraph)result.Source._navMeshGraph)._neighbors[i]);
        }
        Ids(result.Builder._terrainNodeIds); Ids(result.Builder._roadNodeIds);
        foreach (var c in result.Builder._terrainCoordinates) { writer.Write(c.x); writer.Write(c.y); writer.Write(c.z); }
        writer.Write(result.Builder.IsEmpty);
        if (!result.Builder.IsEmpty)
        {
            var bounds = result.Builder.Build().Bounds;
            foreach (var f in bounds.GetType().GetFields(All).OrderBy(f => f.Name)) writer.Write((int)f.GetValue(bounds)!);
        }
        writer.Flush(); data.Position = 0;
        using var sha = SHA256.Create(); return Convert.ToBase64String(sha.ComputeHash(data));
    }
    private static string Error(Action action)
    {
        try { action(); return "ok"; }
        catch (Exception e) { return e.GetType().FullName + ":" + e.Message; }
    }
    internal static void Validate()
    {
        if (!Requested || !(bool)Target.GetProperty("Installed", All)!.GetValue(null)!) return;
        if (_real != 6) throw new Exception("Expected six initial graph comparisons, got " + _real);
        var plans = (IList)Target.GetField("Plans", All)!.GetValue(null)!;
        if (plans.Count != 0) throw new Exception("Initial navigation plans survived LoadAll");
        var ids = new NodeIdService(new SmallMap()); ids.Load(); var random = new System.Random(82917);
        var cases = 0; var updates = 0;
        try
        {
            for (var k = 0; k < 32; k++)
            {
                var input = new List<NavMeshChange>();
                for (var j = 0; j < 512; j++)
                {
                    var a = random.Next(1, 12); var b = a % 11 + 1;
                    input.Add(new NavMeshChange(j % 13 == 0 ? NavMeshChangeType.None : random.Next(2) == 0 ? NavMeshChangeType.AddEdge : NavMeshChangeType.BlockEdge,
                        a, b, random.Next(3), random.Next(5) / 2f));
                }
                var road = k % 2 != 0; var changes = input.ToArray();
                var plan = Call("Prepare", changes, ids, road)!;
                var aResult = Create(ids, road); var bResult = Create(ids, road); var copy = Create(ids, road);
                foreach (var c in changes) Native(c, aResult);
                Call("Apply", plan, bResult.Source, bResult.Builder); Call("Apply", plan, copy.Source, copy.Builder);
                Equal(aResult, bResult, "initial " + k); Equal(aResult, copy, "copy " + k);
                CheckOwnership(bResult, copy);
                if ((bool)Call("Eligible", plan, bResult.Source)!) throw new Exception("Populated source accepted");
                var before = Hash(copy);
                for (var j = 0; j < 96; j++)
                {
                    var a = random.Next(1, 12); var b = a % 11 + 1;
                    var change = new NavMeshChange((NavMeshChangeType)(j % 4 + 1), a, b, random.Next(3), random.Next(5) / 2f);
                    var ea = Error(() => Native(change, aResult)); var eb = Error(() => Native(change, bResult));
                    if (ea != eb) throw new Exception("Native mutation exception changed");
                    Equal(aResult, bResult, "runtime " + k + "/" + j); updates++;
                }
                if (Hash(copy) != before) throw new Exception("Native mutation leaked to independent copy");
                cases++;
            }
            foreach (var road in new[] { false, true })
            {
                var initial = new[] {
                    new NavMeshChange(NavMeshChangeType.AddEdge,1,2,0,1f), new NavMeshChange(NavMeshChangeType.AddEdge,1,2,0,1f),
                    new NavMeshChange(NavMeshChangeType.AddEdge,2,1,0,1f), new NavMeshChange(NavMeshChangeType.AddEdge,1,2,1,float.NaN),
                    new NavMeshChange(NavMeshChangeType.AddEdge,1,2,1,float.PositiveInfinity), new NavMeshChange(NavMeshChangeType.AddEdge,1,2,1,-0f),
                    new NavMeshChange(NavMeshChangeType.BlockEdge,1,2,0,0), new NavMeshChange(NavMeshChangeType.BlockEdge,1,2,0,0),
                    new NavMeshChange(NavMeshChangeType.AddEdge,4,5,0,float.NegativeInfinity), new NavMeshChange(NavMeshChangeType.AddEdge,5,4,0,float.NegativeInfinity)
                };
                var native = Create(ids, road); var fast = Create(ids, road); foreach (var c in initial) Native(c, native);
                var plan = Call("Prepare", initial, ids, road)!; Call("Apply", plan, fast.Source, fast.Builder);
                Equal(native, fast, "special initial");
                foreach (var c in new[] {
                    new NavMeshChange(NavMeshChangeType.UnblockEdge,1,2,0,0), new NavMeshChange(NavMeshChangeType.UnblockEdge,1,2,0,0),
                    new NavMeshChange(NavMeshChangeType.UnblockEdge,1,2,0,0), new NavMeshChange(NavMeshChangeType.RemoveEdge,1,2,1,float.NaN),
                    new NavMeshChange(NavMeshChangeType.AddEdge,1,1,0,0), new NavMeshChange(NavMeshChangeType.RemoveEdge,1,1,0,0),
                    new NavMeshChange(NavMeshChangeType.AddEdge,-1,2,0,0), new NavMeshChange(NavMeshChangeType.AddEdge,7,ids.NumberOfNodes,0,0) })
                {
                    if (Error(() => Native(c,native)) != Error(() => Native(c,fast))) throw new Exception("Special exception changed");
                    Equal(native,fast,"special mutation"); updates++;
                }
                native.Source.Load(); fast.Source.Load(); Equal(native,fast,"reset"); cases++;
            }
            foreach (var c in new[] { new NavMeshChange(NavMeshChangeType.RemoveEdge,1,2,0,0), new NavMeshChange(NavMeshChangeType.UnblockEdge,1,2,0,0),
                new NavMeshChange(NavMeshChangeType.AddEdge,1,1,0,0), new NavMeshChange(NavMeshChangeType.AddEdge,-1,2,0,0),
                new NavMeshChange(NavMeshChangeType.AddEdge,1,ids.NumberOfNodes,0,0), new NavMeshChange((NavMeshChangeType)99,1,2,0,0) })
                if (Call("Prepare",new[]{c},ids,false) != null) throw new Exception("Invalid initial queue accepted");
            // The final source blocks 1->2, but native graph keeps that
            // connection because the last operation only revisits pair 1->3.
            var collision = new[] { new NavMeshChange(NavMeshChangeType.AddEdge,1,2,0,1),
                new NavMeshChange(NavMeshChangeType.AddEdge,2,1,0,1),
                new NavMeshChange(NavMeshChangeType.BlockEdge,1,3,(3*397)^(2*397),0) };
            var original = Create(ids,false); foreach(var c in collision) Native(c,original);
            if (!((TerrainNavMeshGraph)original.Source._navMeshGraph).AreConnected(1,2)) throw new Exception("Collision fixture invalid");
            if (Call("Prepare",collision,ids,false) != null) throw new Exception("Cross-end blockage collision accepted");
            ValidateGuard();
            Debug.Log($"[T3MPINITIALNAVTEST] VALIDATE PASS real={_real} initialCases={cases} runtimeUpdates={updates} invalidQueues=7 guard=1");
        }
        finally { plans.Clear(); }
    }
    private static void CheckOwnership(Result a, Result b)
    {
        void Lists<T>(List<T> x,List<T> y) { if (x.Count>0 && ReferenceEquals(x,y)) throw new Exception("Mutable navigation list shared"); }
        for(var i=0;i<a.Source._nodes.Length;i++)
        {
            var x=a.Source._nodes[i]; var y=b.Source._nodes[i];
            if(x!=null) { if(ReferenceEquals(x,y))throw new Exception("Source node shared"); Lists(x._edges,y._edges); Lists(x._blockages,y._blockages); }
            if(a.Source._navMeshGraph is TerrainNavMeshGraph t) { var u=(TerrainNavMeshGraph)b.Source._navMeshGraph; Lists(t._allNeighbors[i],u._allNeighbors[i]); Lists(t._cheapNeighbors[i],u._cheapNeighbors[i]); }
            else Lists(((RoadNavMeshGraph)a.Source._navMeshGraph)._neighbors[i],((RoadNavMeshGraph)b.Source._navMeshGraph)._neighbors[i]);
        }
    }
    private static void Foreign() => _foreignCalls++;
    private static void ValidateGuard()
    {
        var ht=Find("HarmonyLib.Harmony"); var hm=Find("HarmonyLib.HarmonyMethod"); const string id="t3mp.test.initial-nav-foreign";
        var harmony=Activator.CreateInstance(ht,id)!;
        try
        {
            ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(harmony,new object?[]{typeof(NavMeshSource).GetMethod("AddEdge",All),
                Activator.CreateInstance(hm,typeof(InitialNavigationValidation).GetMethod(nameof(Foreign),All)),null,null,null});
            if((bool)Call("CheckCompatibility")!)throw new Exception("Foreign source patch accepted");
            var depth=Target.GetField("_depth",All)!; var compatible=Target.GetField("_compatible",All)!;
            var old=compatible.GetValue(null);
            try { depth.SetValue(null,1); compatible.SetValue(null,false);
                if(!(bool)Call("Live",null!,null!,true)!)throw new Exception("Foreign fallback did not use native route");
                var ids=new NodeIdService(new SmallMap());ids.Load();var result=Create(ids,false);_foreignCalls=0;
                Native(new NavMeshChange(NavMeshChangeType.AddEdge,1,2,0,1f),result);if(_foreignCalls!=1)throw new Exception("Foreign callback lost");
            } finally { depth.SetValue(null,0); compatible.SetValue(null,old); }
        }
        finally { ht.GetMethod("UnpatchAll",All)!.Invoke(harmony,new object[]{id}); }
        if(!(bool)Call("CheckCompatibility")!)throw new Exception("Guard fixture cleanup failed");
    }
}
