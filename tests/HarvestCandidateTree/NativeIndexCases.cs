using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.Goods;
using Timberborn.YielderFinding;
using Timberborn.Yielding;
using Timberborn.InventorySystem;

internal static class NativeIndexCases
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string codePath)
    {
        var type=Assembly.LoadFrom(Path.GetFullPath(codePath)).GetType("T3MP.Runtime.HarvestIndex",true)!;
        type.GetField("<Installed>k__BackingField",Flags)!.SetValue(null,true);
        var begin=type.GetMethod("Begin",Flags)!;var capture=type.GetMethod("Capture",Flags)!;var end=type.GetMethod("End",Flags)!;var finish=type.GetMethod("Finish",Flags)!;
        var add=typeof(ClosestYielderFinder).GetMethod("AddCloserYielder",Flags)!;
        var field=typeof(ClosestYielderFinder).GetField("_yielders",Flags)!;
        var a=new ClosestYielderFinder(null!);var b=new ClosestYielderFinder(null!);
        var ad=(Dictionary<string,ReachableYielder>)field.GetValue(a)!;var bd=(Dictionary<string,ReachableYielder>)field.GetValue(b)!;
        var shadow=Environment.GetCommandLineArgs().Contains("-t3mpTestHarvestIndexValidate");
        var before=type.GetMethod("BeforeParent",Flags)!;var after=type.GetMethod("AfterParent",Flags)!;
        var inventory=(Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        var other=(Inventory)RuntimeHelpers.GetUninitializedObject(typeof(Inventory));
        var parentArgs=new object?[]{inventory,null};before.Invoke(null,parentArgs);
        var nestedArgs=new object?[]{other,null};before.Invoke(null,nestedArgs);
        after.Invoke(null,new[]{nestedArgs[1]});
        if(!ReferenceEquals(type.GetField("_inventoryContext",Flags)!.GetValue(null),inventory))throw new Exception("Parent context restoration");
        ReachableYielder Make(string? good,float distance){var y=new Yielder(null!);typeof(Yielder).GetField("_yield",Flags)!.SetValue(y,new GoodAmount(good!,5));return new ReachableYielder(y,distance);}
        object? Begin(){var args=new object?[]{b,null};begin.Invoke(null,args);return args[1];}
        void Push(ReachableYielder y){add.Invoke(a,new object[]{y});capture.Invoke(null,new object[]{b,y});}
        void Check(){if(ad.Count!=bd.Count||!ad.Keys.SequenceEqual(bd.Keys))throw new Exception("Dictionary shape");foreach(var p in ad)if(!ReferenceEquals(p.Value.Yielder,bd[p.Key].Yielder)||!p.Value.Distance.Equals(bd[p.Key].Distance))throw new Exception("Selected identity");}
        var x=Make("x",5);var y=Make("y",3);var z=Make("x",1);
        if(!shadow)
        {
            for(var round=0;round<3;round++)
            {ad.Clear();bd.Clear();var scope=Begin();foreach(var value in (round==1?new[]{z,y,x}:new[]{x,y,z}))Push(value);end.Invoke(null,new[]{b,scope});Check();finish.Invoke(null,new object?[]{b,scope,null});}
            ad.Clear();bd.Clear();var failureScope=Begin();Push(x);Push(y);
            finish.Invoke(null,new object?[]{b,failureScope,new InvalidOperationException("enumerator failure")});Check();
        }
        ad.Clear();bd.Clear();var outer=Begin();Push(x);
        if(Begin()!=null)throw new Exception("Nested scope should use native reduction");Check();
        // Native outer FindYielder can clear this shared dictionary after a
        // nested full query; a second reentry must not replay old captures.
        ad.Clear();bd.Clear();Push(y);if(Begin()!=null)throw new Exception("Second nested scope");Check();
        ad.Clear();bd.Clear();Push(z);end.Invoke(null,new[]{b,outer});Check();finish.Invoke(null,new object?[]{b,outer,null});
        ad.Clear();bd.Clear();var first=Begin()!;var indexField=first.GetType().GetField("Index",Flags)!;var firstIndex=indexField.GetValue(first);
        finish.Invoke(null,new object?[]{b,first,null});
        before.Invoke(null,nestedArgs);var second=Begin()!;
        if(ReferenceEquals(firstIndex,indexField.GetValue(second)))throw new Exception("Workplace index isolation");
        finish.Invoke(null,new object?[]{b,second,null});after.Invoke(null,new[]{nestedArgs[1]});
        var restored=Begin()!;if(!ReferenceEquals(firstIndex,indexField.GetValue(restored)))throw new Exception("Workplace index reuse");
        finish.Invoke(null,new object?[]{b,restored,null});after.Invoke(null,new[]{parentArgs[1]});
        if(Begin()!=null)throw new Exception("No scope must use native path");
        Console.WriteLine("PASS native reducer hook lifecycle: "+(shadow?"shadow repeated reentry with nonempty/cleared dictionary":"normal, layout changes, exception restoration and repeated reentry"));
    }
}
