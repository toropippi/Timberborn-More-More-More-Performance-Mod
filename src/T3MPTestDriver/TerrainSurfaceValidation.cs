using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Debug=UnityEngine.Debug;
namespace T3MPTestDriver;

internal static class TerrainSurfaceValidation
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Find(string n)=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(n)).First(t=>t!=null)!;
    private static Type Target=>Find("T3MP.Loading.TerrainSurfaceLoad");
    private static int _real,_cases,_foreign;
    private static object Make(string n)=>FormatterServices.GetUninitializedObject(Find(n));
    private static void Set(object o,string f,object v)=>o.GetType().GetField(f,All)!.SetValue(o,v);
    private static object Get(object o,string f)=>o.GetType().GetField(f,All)!.GetValue(o)!;
    internal static void Install()
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestServiceTerrainValidate"))return;
        if(!(bool)Target.GetProperty("Installed",All)!.GetValue(null)!)throw new Exception("TerrainSurfaceLoad not installed");
        var ht=Find("HarmonyLib.Harmony");var hm=Find("HarmonyLib.HarmonyMethod");var h=Activator.CreateInstance(ht,"t3mp.test.terrain-surface");
        ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(h,new object?[]{Target.GetMethod("Fill",All),null,Activator.CreateInstance(hm,typeof(TerrainSurfaceValidation).GetMethod(nameof(AfterFill),All)),null,null});
    }
    private static void Equal(object manager,byte[,,] fast)
    {
        var s=new Vector3Int(fast.GetLength(0)-2,fast.GetLength(1)-2,fast.GetLength(2)-3);
        var native=new byte[s.x+2,s.y+2,s.z+3];
        var call=(Action<Vector3Int>)Delegate.CreateDelegate(typeof(Action<Vector3Int>),manager,manager.GetType().GetMethod("UpdateCodeForCoords",All)!);
        Set(manager,"_surfaceShapeCodes",native);
        try
        {
            for(var z=-2;z<=s.z;z++)for(var y=-1;y<=s.y;y++)for(var x=-1;x<=s.x;x++)call(new Vector3Int(x,y,z));
            for(var x=0;x<s.x+2;x++)for(var y=0;y<s.y+2;y++)for(var z=0;z<s.z+3;z++)
                if(native[x,y,z]!=fast[x,y,z])throw new Exception($"Surface mismatch {x},{y},{z}: {native[x,y,z]} != {fast[x,y,z]}");
        }
        finally{Set(manager,"_surfaceShapeCodes",fast);}
    }
    private static void AfterFill(object manager)
    {
        Equal(manager,(byte[,,])Get(manager,"_surfaceShapeCodes"));_real++;
        var eligible=Target.GetMethod("Eligible",All)!;
        if(!(bool)eligible.Invoke(null,new[]{manager})!)throw new Exception("Real terrain rejected");
        var ht=Find("HarmonyLib.Harmony");var hm=Find("HarmonyLib.HarmonyMethod");var h=Activator.CreateInstance(ht,"t3mp.test.terrain-foreign");
        var method=Find("Timberborn.TerrainSystem.TerrainService").GetMethod("Underground",All)!;
        try
        {
            ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(h,new object?[]{method,Activator.CreateInstance(hm,typeof(TerrainSurfaceValidation).GetMethod(nameof(Foreign),All)),null,null,null});
            if((bool)eligible.Invoke(null,new[]{manager})!)throw new Exception("Foreign guard failed");
            method.Invoke(Get(manager,"_terrainService"),new object[]{Vector3Int.zero});
        }
        finally{ht.GetMethod("UnpatchAll",All)!.Invoke(h,new object[]{"t3mp.test.terrain-foreign"});}
        if(_foreign!=1||!(bool)eligible.Invoke(null,new[]{manager})!)throw new Exception("Foreign guard recovery failed");
        var saved=Get(manager,"_surfaceShapeCodes");Set(manager,"_surfaceShapeCodes",new byte[1,1,1]);
        try{if((bool)eligible.Invoke(null,new[]{manager})!)throw new Exception("Invalid shape accepted");}
        finally{Set(manager,"_surfaceShapeCodes",saved);}
        Debug.Log("[T3MPTERRAINSURFACETEST] real codes PASS count="+((Array)saved).Length+" guard=1 invalid=1");
    }
    private static void Foreign(){_foreign++;}
    internal static void Validate()
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestServiceTerrainValidate"))return;
        var random=new System.Random(8193);
        for(var n=0;n<80;n++)
        {
            var size=new Vector3Int(1+n%9,1+n%7,1+n%13);var stride=size.x+2;var plane=stride*(size.y+2);
            var voxels=new bool[plane*size.z];for(var i=0;i<voxels.Length;i++)voxels[i]=n%4==0||n%4!=1&&random.Next(3)==0;
            var mapSize=Make("Timberborn.MapStateSystem.MapSize");Set(mapSize,"<TerrainSize>k__BackingField",size);
            Set(mapSize,"<TerrainSize2D>k__BackingField",new Vector2Int(size.x,size.y));
            var ids=Make("Timberborn.MapIndexSystem.MapIndexService");Set(ids,"_mapSize",mapSize);Set(ids,"<Stride>k__BackingField",stride);Set(ids,"<VerticalStride>k__BackingField",plane);
            var map=Make("Timberborn.TerrainSystem.TerrainMap");Set(map,"_mapSize",mapSize);Set(map,"_mapIndexService",ids);Set(map,"_terrainVoxels",voxels);
            var service=Make("Timberborn.TerrainSystem.TerrainService");Set(service,"_mapSize",mapSize);Set(service,"_terrainMap",map);
            var manager=Make("Timberborn.TerrainSystemRendering.TerrainMeshManager");Set(manager,"_terrainService",service);
            var output=new byte[size.x+2,size.y+2,size.z+3];
            Target.GetMethod("FillCodes",All)!.Invoke(null,new object[]{voxels,size,output});Equal(manager,output);_cases++;
            // Toggle terrain after initialization and compare a fresh batch with native updates.
            for(var i=0;i<voxels.Length;i+=7)voxels[i]=!voxels[i];
            Target.GetMethod("FillCodes",All)!.Invoke(null,new object[]{voxels,size,output});Equal(manager,output);_cases++;
        }
        if(_real!=1||_cases!=160)throw new Exception("Missing surface validation");
        Debug.Log($"[T3MPTERRAINSURFACETEST] VALIDATE PASS real={_real} synthetic={_cases}");
    }
}
