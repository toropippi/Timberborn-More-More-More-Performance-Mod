using System.Reflection;
using Timberborn.Carrying;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.TemplateSystem;
using Timberborn.YielderFinding;
using Timberborn.Yielding;

public class ThrowingGoodService : DispatchProxy
{
    internal static string? RequestedGood;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        RequestedGood=(string)args![0]!;
        throw new InvalidOperationException("fixture-good-service-failure");
    }
}

internal static class NativeFailureCases
{
    private const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run(string codePath)
    {
        var code=Assembly.LoadFrom(Path.GetFullPath(codePath));
        var type=code.GetType("T3MP.Runtime.HarvestCandidateTree",true)!;
        // Exercise the actual helper without installing guards around the test's
        // deliberately substituted throwing good service. Live tests install normally.
        type.GetField("<Installed>k__BackingField",Flags)!.SetValue(null,true);
        var fast=type.GetMethod("Find",Flags)!;
        var native=typeof(ClosestYielderFinder).GetMethod("FindYielder",Flags,null,new[]{typeof(Inventory),typeof(int)},null)!;
        var yieldsField=typeof(ClosestYielderFinder).GetField("_yielders",Flags)!;
        var orderedField=typeof(ClosestYielderFinder).GetField("_orderedYielders",Flags)!;
        for(var scenario=0;scenario<3;scenario++)
        {
            var a=new ClosestYielderFinder(new CarryAmountCalculator(DispatchProxy.Create<IGoodService,ThrowingGoodService>()));
            var b=new ClosestYielderFinder(new CarryAmountCalculator(DispatchProxy.Create<IGoodService,ThrowingGoodService>()));
            foreach(var instance in new[]{a,b})
            {
                var yields=(Dictionary<string,ReachableYielder>)yieldsField.GetValue(instance)!;
                for(var i=0;i<4;i++)
                {
                    var template=new InstantiatedTemplate(null!);
                    typeof(InstantiatedTemplate).GetProperty("InstantiationOrder")!.SetValue(template,i%3);
                    var yielder=new Yielder(null!);
                    typeof(Yielder).GetField("_instantiatedTemplate",Flags)!.SetValue(yielder,template);
                    typeof(Yielder).GetField("_yield",Flags)!.SetValue(yielder,new GoodAmount("g"+i,5));
                    yields.Add("g"+i,new ReachableYielder(yielder,i%2));
                }
                if(scenario==1)orderedField.SetValue(instance,new SortedSet<ReachableYielder>(Comparer<ReachableYielder>.Create((x,y)=>y.CompareTo(x))));
                if(scenario==2)((SortedSet<ReachableYielder>)orderedField.GetValue(instance)!).Add(yields["g3"]);
            }
            string Execute(ClosestYielderFinder instance,bool optimized)
            {
                ThrowingGoodService.RequestedGood=null;string error="none";
                try {if(optimized)fast.Invoke(null,new object?[]{instance,null,10});else native.Invoke(instance,new object?[]{null,10});}
                catch(TargetInvocationException e){error=e.InnerException!.GetType().Name+":"+e.InnerException.Message;}
                if(error!="InvalidOperationException:fixture-good-service-failure")throw new Exception("Unexpected fixture exit: "+error);
                var remaining=(SortedSet<ReachableYielder>)orderedField.GetValue(instance)!;
                return error+"|"+ThrowingGoodService.RequestedGood+"|"+string.Join(",",remaining.Select(x=>x.Yielder.Yield.GoodId));
            }
            if(Execute(a,false)!=Execute(b,true))throw new Exception("Native exception/candidate/set-state mismatch: "+scenario);
            if(Execute(a,false)!=Execute(b,true))throw new Exception("Post-exception retry mismatch: "+scenario);
        }
        Console.WriteLine("PASS actual native/helper exception identity, first carry candidate, retained ordered set, retry, custom comparer and preexisting-set fallbacks (6 comparisons)");
    }
}
