using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug=UnityEngine.Debug;
namespace T3MPTestDriver;

internal static class AtlasPixelsValidation
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Find(string n)=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(n)).First(t=>t!=null)!;
    private static Type Target=>Find("T3MP.Loading.AtlasPixelsLoad");
    private static int _real;
    internal static void Install()
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestServiceAtlasValidate"))return;
        if(!(bool)Target.GetProperty("Installed",All)!.GetValue(null)!)throw new Exception("AtlasPixelsLoad not installed");
        var ht=Find("HarmonyLib.Harmony");var hm=Find("HarmonyLib.HarmonyMethod");var h=Activator.CreateInstance(ht,"t3mp.test.atlas-pixels");
        ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(h,new object?[]{Target.GetMethod("Pixels",All),null,Activator.CreateInstance(hm,typeof(AtlasPixelsValidation).GetMethod(nameof(AfterPixels),All)),null,null});
    }
    private static void Equal(Color32[] a,Color32[] b)
    {
        if(a.Length!=b.Length)throw new Exception("Pixel length mismatch");
        for(var i=0;i<a.Length;i++)if(a[i].r!=b[i].r||a[i].g!=b[i].g||a[i].b!=b[i].b||a[i].a!=b[i].a)throw new Exception("Pixel mismatch "+i);
    }
    private static void AfterPixels(Texture2D texture,Color32[] __result){Equal(texture.GetPixels32(),__result);_real++;}
    private static object Begin(){var args=new object?[]{null};Target.GetMethod("Begin",All)!.Invoke(null,args);return args[0]!;}
    private static void End(object state)=>Target.GetMethod("End",All)!.Invoke(null,new[]{state});
    private static Color32[] Pixels(Texture2D t)=>(Color32[])Target.GetMethod("Pixels",All)!.Invoke(null,new object[]{t})!;
    internal static void Validate()
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestServiceAtlasValidate"))return;
        if(_real<60)throw new Exception("Missing atlas pixel comparisons");
        var real=_real;var t=new Texture2D(7,5,TextureFormat.RGBA32,false);
        try
        {
            var values=Enumerable.Range(0,35).Select(i=>new Color32((byte)i,(byte)(i*7),(byte)(i*3),(byte)(255-i))).ToArray();t.SetPixels32(values);t.Apply();
            var outer=Begin();
            try
            {
                var a=Pixels(t);if(!ReferenceEquals(a,Pixels(t)))throw new Exception("Cache miss");
                var inner=Begin();try{Equal(a,Pixels(t));}finally{End(inner);}
                if(!ReferenceEquals(a,Pixels(t)))throw new Exception("Nested scope lost cache");
            }
            finally{End(outer);}
            values[0]=new Color32(99,98,97,96);t.SetPixels32(values);t.Apply();Equal(values,Pixels(t));
            if(Target.GetField("_pixels",All)!.GetValue(null)!=null)throw new Exception("Cache survived scope");
            var ht=Find("HarmonyLib.Harmony");var hm=Find("HarmonyLib.HarmonyMethod");var h=Activator.CreateInstance(ht,"t3mp.test.atlas-foreign");
            try
            {
                ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(h,new object?[]{Find("Timberborn.PrefabOptimization.AutoAtlaser").GetMethod("TryGetAutoAtlasFragment",All),Activator.CreateInstance(hm,typeof(AtlasPixelsValidation).GetMethod(nameof(Foreign),All)),null,null,null});
                var state=Begin();try{if(Target.GetField("_pixels",All)!.GetValue(null)!=null)throw new Exception("Foreign guard failed");Equal(values,Pixels(t));}finally{End(state);}
            }
            finally{ht.GetMethod("UnpatchAll",All)!.Invoke(h,new object[]{"t3mp.test.atlas-foreign"});}
        }
        finally{UnityEngine.Object.Destroy(t);}
        Debug.Log($"[T3MPATLASPIXELSTEST] VALIDATE PASS nativePixelArrays={real} nested=1 runtimeMutation=1 foreign=1");
    }
    private static void Foreign(){}
}
