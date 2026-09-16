using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.PrefabOptimization;
using Timberborn.TextureOperations;
using UnityEngine;
using Debug=UnityEngine.Debug;
namespace T3MP.Loading;

// Native atlas generation only reads its source textures and passes pixels to
// native SetPixels32, which copies them. Reuse decoding within one atlas only.
internal static class AtlasPixelsLoad
{
    private const string Owner="t3mp.load.atlas-pixels";
    private const BindingFlags All=LoadPatchBridge.All;
    internal static bool Installed {get;private set;}
    [ThreadStatic] private static Dictionary<Texture2D,Color32[]>? _pixels;
    [ThreadStatic] private static int _hits;
    internal sealed class Scope { internal Dictionary<Texture2D,Color32[]>? Previous;internal int Hits; }
    private static readonly MethodBase[] Protected = typeof(AutoAtlaser).GetMethods(All|BindingFlags.DeclaredOnly)
        .Concat(typeof(TextureFactory).GetMethods(All|BindingFlags.DeclaredOnly))
        .Concat(typeof(EnvironmentMaterialProperties).GetMethods(All|BindingFlags.DeclaredOnly))
        .Concat(typeof(Texture2D).GetMethods(All|BindingFlags.DeclaredOnly).Where(m=>m.Name=="GetPixels32"||m.Name=="SetPixels32"||m.Name=="Apply"))
        .Concat(typeof(Material).GetMethods(All|BindingFlags.DeclaredOnly).Where(m=>m.Name.StartsWith("Get")))
        .Concat(new[]{typeof(Timberborn.BlueprintSystem.AssetRef<Material>).GetProperty("Asset",All)!.GetMethod!}).Cast<MethodBase>().ToArray();
    private static bool Reviewed()=>LoadCompatibility.Reviewed(
        "Timberborn.PrefabOptimization|0c0517bf-185a-46c6-ab97-00a8a5e74af7",
        "Timberborn.TextureOperations|90b26ce4-8697-429b-9b5f-1e15ab552e8d",
        "Timberborn.BlueprintSystem|41363a54-def1-40c7-9ff3-f884cf81cf81",
        "UnityEngine.CoreModule|61dee272-fd45-47db-9c13-40fe43fbdc1a");
    private static bool Compatible()=>Reviewed()&&LoadCompatibility.Unmodified(Protected,Owner);
    internal static void Install()
    {
        if(Installed||Environment.GetCommandLineArgs().Contains("-t3mpTestServiceAtlasBaseline"))return;
        if(!Reviewed()){Debug.Log("[T3MPATLASPIXELS] native fallback: unreviewed modules");return;}
        var ht=LoadPatchBridge.Find("HarmonyLib.Harmony");var hm=LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");var h=Activator.CreateInstance(ht,Owner)!;
        var patch=ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5);
        object Hook(string n)=>Activator.CreateInstance(hm,typeof(AtlasPixelsLoad).GetMethod(n,All))!;
        try
        {
            var rewrite=LoadPatchBridge.Create("T3MPAtlasPixelsRewrite",typeof(AtlasPixelsLoad).GetMethod(nameof(Rewrite),All)!);
            patch.Invoke(h,new object?[]{typeof(AutoAtlaser).GetMethod("CombineFragments",All),null,null,Activator.CreateInstance(hm,rewrite),null});
            patch.Invoke(h,new object?[]{typeof(AutoAtlaser).GetMethod("GenerateAutoAtlasFragments",All),Hook(nameof(Begin)),null,null,Hook(nameof(End))});
            Installed=true;Debug.Log("[T3MPATLASPIXELS] installed; one-atlas decode reuse");
        }
        catch(Exception e){ht.GetMethod("UnpatchAll",All)!.Invoke(h,new object[]{Owner});Debug.LogWarning("[T3MPATLASPIXELS] disabled: "+e.GetBaseException().Message);}
    }
    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> input)
    {
        var operand=typeof(T).GetField("operand")!;var opcode=typeof(T).GetField("opcode")!;var count=0;
        foreach(var i in input)
        {
            if(operand.GetValue(i) is MethodInfo m && m.DeclaringType==typeof(Texture2D)&&m.Name=="GetPixels32"&&m.GetParameters().Length==0)
            {operand.SetValue(i,typeof(AtlasPixelsLoad).GetMethod(nameof(Pixels),All));opcode.SetValue(i,OpCodes.Call);count++;}
            yield return i;
        }
        if(count!=1)throw new Exception("Unexpected atlas pixel call count: "+count);
    }
    private static void Begin(out Scope __state)
    {
        __state=new Scope{Previous=_pixels,Hits=_hits};_hits=0;
        _pixels=Compatible()?new Dictionary<Texture2D,Color32[]>():null;
    }
    private static void End(Scope __state)
    {
        if(__state==null)return;
        Debug.Log($"[T3MPATLASPIXELS] reads={_pixels?.Count??0} hits={_hits} compatible={_pixels!=null}");
        _pixels=__state.Previous;_hits=__state.Hits;
    }
    private static Color32[] Pixels(Texture2D texture)
    {
        if(_pixels==null)return texture.GetPixels32();
        if(_pixels.TryGetValue(texture,out var pixels)){_hits++;return pixels;}
        pixels=texture.GetPixels32();_pixels.Add(texture,pixels);return pixels;
    }
}
