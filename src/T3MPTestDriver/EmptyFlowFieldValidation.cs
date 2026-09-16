using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Timberborn.Common;
using Timberborn.Navigation;
using Debug = UnityEngine.Debug;
namespace T3MPTestDriver;

internal static class EmptyFlowFieldValidation
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static Type Find(string name)=>AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType(name)).First(t=>t!=null)!;
    private static Type Target=>Find("T3MP.Loading.EmptyFlowFieldLoad");
    private sealed class Comparer:IEqualityComparer<int>
    {
        internal int Calls;
        public bool Equals(int a,int b){Calls++;return a==b;}
        public int GetHashCode(int value){Calls++;return value;}
    }
    private static void Begin(){Target.GetMethod("Begin",All)!.Invoke(null,new object[]{false});}
    private static void End(){Target.GetMethod("End",All)!.Invoke(null,new object[]{true});}
    private static string Error(Action action)
    {try{action();return "ok";}catch(Exception e){return e.GetType().FullName+":"+e.Message;}}
    private static object Make(bool access,int mode,out Comparer? comparer)
    {
        object value=access?new AccessFlowField():new PathFlowField(); comparer=null;
        if((mode&16)!=0)
        {
            comparer=new Comparer();var field=value.GetType().GetField("_nodes",All)!;
            field.SetValue(value,Activator.CreateInstance(field.FieldType,new object[]{comparer}));
        }
        // Allocate dictionary storage even in cleared-empty cases.
        if(value is AccessFlowField a){a.AddNode(7,-1,float.NaN);a.AddNode(11,7,-0f);a.Clear();}
        else{var p=(PathFlowField)value;p.AddNode(7,-1,float.NaN);p.AddNode(11,7,-0f);p.Clear(3);}
        if((mode&1)!=0)
        {
            if(value is AccessFlowField a2){a2.AddNode(7,-1,float.NaN);a2.AddNode(11,7,-0f);}
            else{var p=(PathFlowField)value;p.AddNode(7,-1,float.NaN);p.AddNode(11,7,-0f);}
        }
        if(value is AccessFlowField a3){if((mode&2)!=0)a3.MarkAsFilled();}
        else
        {
            var p=(PathFlowField)value;
            if((mode&2)!=0)p.MarkAsFullyFilled();
            typeof(PathFlowField).GetField("_refreshed",All)!.SetValue(p,(mode&4)!=0);
            typeof(PathFlowField).GetField("_startNodeId",All)!.SetValue(p,(mode&8)!=0?int.MinValue:3);
        }
        if(comparer!=null)comparer.Calls=0;
        return value;
    }
    private static void Update(object value,ReadOnlyList<int> ids)
    {if(value is AccessFlowField a)a.OnNodesChanged(ids);else((PathFlowField)value).OnNodesChanged(ids);}
    private static string Snapshot(object value)
    {
        var b=new StringBuilder();
        foreach(var f in value.GetType().GetFields(All).Where(f=>!f.IsStatic).OrderBy(f=>f.Name))
        {
            b.Append(f.Name).Append('=');var v=f.GetValue(value);
            if(v is IDictionary dictionary)
            {
                foreach(DictionaryEntry e in dictionary)
                {
                    b.Append(e.Key).Append(':');
                    foreach(var item in e.Value!.GetType().GetFields(All).OrderBy(f=>f.Name))
                    {var x=item.GetValue(e.Value);b.Append(x is float number?BitConverter.SingleToInt32Bits(number):x).Append(',');}
                }
            }
            else b.Append(v);
            b.Append(';');
        }
        return b.ToString();
    }
    internal static void Validate()
    {
        if(!Environment.GetCommandLineArgs().Contains("-t3mpTestEmptyFlowValidate"))return;
        if(!(bool)Target.GetProperty("Installed",All)!.GetValue(null)!)throw new Exception("Empty flow optimization missing");
        var cases=0;
        foreach(var access in new[]{false,true})
        for(var mode=0;mode<32;mode++)
        foreach(var ids in new[]{new List<int>().AsReadOnlyList(),new List<int>{-1,3,99}.AsReadOnlyList(),new List<int>{99,11,7,11}.AsReadOnlyList(),default(ReadOnlyList<int>)})
        {
            var native=Make(access,mode,out var nc);var fast=Make(access,mode,out var fc);
            var expected=Error(()=>Update(native,ids));string actual;
            Begin();try{actual=Error(()=>Update(fast,ids));}finally{End();}
            if(expected!=actual||Snapshot(native)!=Snapshot(fast)||nc?.Calls!=fc?.Calls)throw new Exception("Flow field mismatch "+access+"/"+mode);
            // Subsequent native mutations/queries must still see the same flags.
            var runtime=new List<int>{7,11}.AsReadOnlyList();Update(native,runtime);Update(fast,runtime);
            if(Snapshot(native)!=Snapshot(fast))throw new Exception("Runtime flow state mismatch");cases++;
        }
        Begin();Begin();End();
        if((int)Target.GetField("_depth",All)!.GetValue(null)!=1)throw new Exception("Nested flow scope lost");End();
        if((int)Target.GetField("_depth",All)!.GetValue(null)!=0)throw new Exception("Flow scope leaked");
        ForeignGuard();
        Debug.Log("[T3MPEMPTYFLOWTEST] VALIDATE PASS cases="+cases+" guards=2 nested=1; flags, contents, invalid lists, custom comparers, native runtime updates");
    }
    private static int _foreignCalls;
    private static void Foreign()=>_foreignCalls++;
    private static void ForeignGuard()
    {
        var ht=Find("HarmonyLib.Harmony");var hm=Find("HarmonyLib.HarmonyMethod");const string owner="t3mp.test.empty-flow-foreign";
        var h=Activator.CreateInstance(ht,owner)!;
        foreach(var type in new[]{typeof(AccessFlowField),typeof(PathFlowField)})
        {
            try
            {
                ht.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length==5).Invoke(h,new object?[]{type.GetMethod("HasNode",All),Activator.CreateInstance(hm,typeof(EmptyFlowFieldValidation).GetMethod(nameof(Foreign),All)),null,null,null});
                Begin();try
                {
                    _foreignCalls=0;object field=type==typeof(AccessFlowField)?new AccessFlowField():new PathFlowField();
                    Update(field,new List<int>{1,2,3}.AsReadOnlyList());
                    if(_foreignCalls!=3)throw new Exception("Foreign flow callbacks lost");
                }finally{End();}
            }
            finally{ht.GetMethod("UnpatchAll",All)!.Invoke(h,new object[]{owner});}
        }
        if(!(bool)Target.GetMethod("Compatible",All)!.Invoke(null,null)!)throw new Exception("Flow guard cleanup failed");
    }
}
