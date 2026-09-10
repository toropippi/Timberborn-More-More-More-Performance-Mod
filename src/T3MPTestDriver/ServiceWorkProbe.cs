using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.TemplateSystem;
using Timberborn.BlockSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;
namespace T3MPTestDriver;

internal static class ServiceWorkProbe
{
    private const BindingFlags All = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    internal sealed class Frame { internal Frame? Parent; internal long Start, Children; }
    private sealed class Total { internal long Calls, Own, Inclusive; }
    private static readonly Dictionary<MethodBase,Total> Totals=new Dictionary<MethodBase,Total>();
    private static readonly HashSet<Texture2D> ReadTextures=new HashSet<Texture2D>();
    private static double _repeatPixelsMs;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static Frame? _current;
    private static Type Find(string name)=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name)).First(t=>t!=null)!;
    internal static void Install()
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestServiceWorkProfile"))return;
        var ht=Find("HarmonyLib.Harmony");var hm=Find("HarmonyLib.HarmonyMethod");
        var h=Activator.CreateInstance(ht,"t3mp.test.service-work");
        var patch=ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        object Hook(string n)=>Activator.CreateInstance(hm,typeof(ServiceWorkProbe).GetMethod(n,All))!;
        patch.Invoke(h,new object?[]{Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadSingletons",All),Hook(nameof(Start)),null,null,Hook(nameof(Stop))});
        foreach(var entry in new[]{
            "Timberborn.BottomBarSystem.BottomBarPanel|Load",
            "Timberborn.TemplateInstantiation.TemplateInstantiator|GetCachedTemplate,GetInstanceComponents",
            "Timberborn.BlockObjectTools.PreviewFactory|Create",
            "Timberborn.CoreUI.VisualElementLoader|LoadVisualElement",
            "Timberborn.PrefabOptimization.AutoAtlasingPrefabOptimizer|Optimize,OptimizeMeshRenderer",
            "Timberborn.PrefabOptimization.AutoAtlaser|GenerateAutoAtlasFragments,CombineFragments",
            "Timberborn.PrefabOptimization.MergeMeshesByMaterialPrefabOptimizer|Optimize",
            "Timberborn.Timbermesh.TimbermeshReader|ReadFromStream",
            "Timberborn.TerrainSystemRendering.TerrainMeshManager|InitializeTerrainTiles,CollectMeshesForTile,UpdateTileMesh",
            "Timberborn.StockpileVisualization.GoodPileVariantsService|Load,GetMesh,GetRandomPile"})
        {
            var p=entry.Split('|');
            foreach(var m in Find(p[0]).GetMethods(All).Where(m=>p[1].Split(',').Contains(m.Name)&&!m.ContainsGenericParameters))
                patch.Invoke(h,new object?[]{m,Hook(nameof(Begin)),null,null,Hook(nameof(End))});
        }
        var rewrite=typeof(ServiceWorkProbe).GetMethod(nameof(Rewrite),All)!.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
        foreach(var name in new[]{"GetBlockObjects","GetBlockObjectsWithoutValidGroup"})
            patch.Invoke(h,new object?[]{Find("Timberborn.BlockObjectTools.PlaceableBlockObjectSpecService").GetMethod(name,All),null,null,Activator.CreateInstance(hm,rewrite),null});
        var textureRewrite=typeof(ServiceWorkProbe).GetMethod(nameof(RewriteTexture),All)!.MakeGenericMethod(Find("HarmonyLib.CodeInstruction"));
        patch.Invoke(h,new object?[]{Find("Timberborn.PrefabOptimization.AutoAtlaser").GetMethod("CombineFragments",All),null,null,Activator.CreateInstance(hm,textureRewrite),null});
        Debug.Log("[T3MPSERVICEWORK] installed; intrusive service-only attribution");
    }
    private static IEnumerable<T> RewriteTexture<T>(IEnumerable<T> input)
    {
        var operand=typeof(T).GetField("operand")!;var opcode=typeof(T).GetField("opcode")!;var count=0;
        foreach(var i in input)
        {
            if(operand.GetValue(i) is MethodInfo m && m.DeclaringType==typeof(Texture2D))
            {
                var name=m.Name=="GetPixels32"&&m.GetParameters().Length==0?nameof(ReadPixels):
                    m.Name=="SetPixels32"&&m.GetParameters().Length==5?nameof(WritePixels):
                    m.Name=="Apply"&&m.GetParameters().Length==2?nameof(ApplyPixels):null;
                if(name!=null){operand.SetValue(i,typeof(ServiceWorkProbe).GetMethod(name,All));opcode.SetValue(i,OpCodes.Call);count++;}
            }
            yield return i;
        }
        if(count!=3)throw new Exception("Unexpected texture calls: "+count);
    }
    private static Color32[] ReadPixels(Texture2D t)
    {
        var repeat=!ReadTextures.Add(t);var start=Stopwatch.GetTimestamp();
        Begin(out var s);try{return t.GetPixels32();}finally{End(typeof(ServiceWorkProbe).GetMethod(nameof(ReadPixels),All)!,s);if(repeat)_repeatPixelsMs+=(Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency;}
    }
    private static void WritePixels(Texture2D t,int x,int y,int w,int h,Color32[] colors)
    {
        Begin(out var s);try{t.SetPixels32(x,y,w,h,colors);}finally{End(typeof(ServiceWorkProbe).GetMethod(nameof(WritePixels),All)!,s);}
    }
    private static void ApplyPixels(Texture2D t,bool mip,bool unreadable)
    {
        Begin(out var s);try{t.Apply(mip,unreadable);}finally{End(typeof(ServiceWorkProbe).GetMethod(nameof(ApplyPixels),All)!,s);}
        Debug.Log($"[T3MPSERVICEWORK] atlas={t.name} size={t.width}x{t.height} format={t.format} mips={t.mipmapCount}");
    }
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> input)
    {
        var operand=typeof(T).GetField("operand")!;var opcode=typeof(T).GetField("opcode")!;var count=0;
        foreach(var i in input)
        {
            if(operand.GetValue(i) is MethodInfo m && m.DeclaringType==typeof(TemplateService)&&m.Name=="GetAll")
            {operand.SetValue(i,typeof(ServiceWorkProbe).GetMethod(nameof(GetPlaceable),All));opcode.SetValue(i,OpCodes.Call);count++;}
            yield return i;
        }
        if(count!=1)throw new Exception("Unexpected template call sites: "+count);
    }
    private static IEnumerable<PlaceableBlockObjectSpec> GetPlaceable(TemplateService service)
    {
        Begin(out var state);
        try{return service.GetAll<PlaceableBlockObjectSpec>();}
        finally{End(typeof(ServiceWorkProbe).GetMethod(nameof(GetPlaceable),All)!,state);}
    }
    private static void Start(){if(_depth++==0){Totals.Clear();ReadTextures.Clear();_repeatPixelsMs=0;}}
    private static void Stop()
    {
        if(--_depth!=0)return;
        Debug.Log(FormattableString.Invariant($"[T3MPSERVICEWORK] uniqueTextureReads={ReadTextures.Count} repeatedPixelMs={_repeatPixelsMs:F3}"));
        foreach(var p in Totals.OrderByDescending(p=>p.Value.Own))
            Debug.Log(FormattableString.Invariant($"[T3MPSERVICEWORK] {p.Key.DeclaringType!.Name}.{p.Key.Name} calls={p.Value.Calls} ownMs={p.Value.Own*1000.0/Stopwatch.Frequency:F3} inclusiveMs={p.Value.Inclusive*1000.0/Stopwatch.Frequency:F3}"));
    }
    private static void Begin(out Frame? __state)
    {
        __state=null;if(_depth==0)return;
        __state=new Frame{Parent=_current,Start=Stopwatch.GetTimestamp()};_current=__state;
    }
    private static void End(MethodBase __originalMethod,Frame? __state)
    {
        if(__state==null)return;
        var elapsed=Stopwatch.GetTimestamp()-__state.Start;_current=__state.Parent;
        if(_current!=null)_current.Children+=elapsed;
        if(!Totals.TryGetValue(__originalMethod,out var t))Totals.Add(__originalMethod,t=new Total());
        t.Calls++;t.Inclusive+=elapsed;t.Own+=elapsed-__state.Children;
    }
}
