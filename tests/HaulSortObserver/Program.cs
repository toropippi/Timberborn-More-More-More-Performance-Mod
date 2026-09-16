using System.Reflection;
using Timberborn.Hauling;
using T3MPTestDriver;

var type = typeof(HaulSortObserver);
const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
var observe = type.GetMethod("Observe", flags)!;
void Observe(List<WeightedBehavior> list, Comparison<WeightedBehavior> comparison)
{
    try { observe.Invoke(null, new object[] {list, comparison}); }
    catch (TargetInvocationException e) { throw e.InnerException!; }
}
long Counter(string name) => (long)type.GetField(name, flags)!.GetValue(null)!;
var version = typeof(List<WeightedBehavior>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)!;
bool Same(IReadOnlyList<WeightedBehavior> a, IReadOnlyList<WeightedBehavior> b) => a.Count == b.Count &&
    Enumerable.Range(0, a.Count).All(i => ReferenceEquals(a[i].WorkplaceBehavior,b[i].WorkplaceBehavior) &&
        BitConverter.SingleToInt32Bits(a[i].Weight)==BitConverter.SingleToInt32Bits(b[i].Weight));
type.GetField("_active", flags)!.SetValue(null, true);
var random = new Random(19847);
var objects = Enumerable.Range(0,20).Select(_=>new object()).ToArray();
var special = new[]{0f,-0f,float.NaN,float.PositiveInfinity,float.NegativeInfinity,1f,1f,-2f};
var list = new List<WeightedBehavior>();
Comparison<WeightedBehavior> compare = (a,b)=>b.Weight.CompareTo(a.Weight);
var cases = 0;
for(var run=0;run<180;run++)
{
    var items = Enumerable.Range(0,random.Next(0,300)).Select(_=>new WeightedBehavior(special[random.Next(special.Length)],objects[random.Next(objects.Length)])).ToArray();
    for(var repeat=0;repeat<2;repeat++)
    {
        list.Clear();list.AddRange(items);
        var native = new List<WeightedBehavior>(items);
        var beforeVersion=(int)version.GetValue(list)!;
        native.Sort(compare);Observe(list,compare);
        if(!Same(native,list) || (int)version.GetValue(list)! != beforeVersion+1) throw new Exception("Native sort sequence/version changed");
        cases++;
    }
}
if(Counter("_repeated")<180 || Counter("_mismatches")!=0) throw new Exception("Repeat accounting or output mismatch");
// Changing one bit of zero must count as a changed input, not a reusable result.
list.Clear();list.Add(new WeightedBehavior(0f,objects[0]));Observe(list,compare);
var repeated=Counter("_repeated");
list.Clear();list.Add(new WeightedBehavior(BitConverter.Int32BitsToSingle(int.MinValue),objects[0]));Observe(list,compare);
if(Counter("_repeated")!=repeated) throw new Exception("Signed zero was merged");
// Native comparer exceptions propagate and leave the same partial sort/version.
var input=Enumerable.Range(0,50).Select(i=>new WeightedBehavior(i,objects[i%20])).ToArray();
list.Clear();list.AddRange(input);
var expected=new List<WeightedBehavior>(input);
var countA=0;var countB=0;
Comparison<WeightedBehavior> failA=(a,b)=>++countA==7?throw new ApplicationException("fixture"):compare(a,b);
Comparison<WeightedBehavior> failB=(a,b)=>++countB==7?throw new ApplicationException("fixture"):compare(a,b);
Exception? aError=null,bError=null;
try{expected.Sort(failA);}catch(Exception e){aError=e;}
var originalVersion=(int)version.GetValue(list)!;
try{Observe(list,failB);}catch(Exception e){bError=e;}
if(aError?.GetType()!=bError?.GetType() || aError?.InnerException?.GetType()!=bError?.InnerException?.GetType() ||
   countA!=countB || !Same(expected,list) || (int)version.GetValue(list)! != originalVersion) throw new Exception("Exception behavior changed");
Console.WriteLine($"PASS: {cases} native order/version cases, signed zero and comparer exception; repeated={Counter("_repeated")}");

// Only the observation wrapper is exercised. Harmony installation is validated in game.
namespace Timberborn.Hauling
{
    public readonly struct WeightedBehavior(float weight, object workplace)
    {
        public float Weight {get;}=weight;
        public object WorkplaceBehavior {get;}=workplace;
    }
}
namespace UnityEngine { public static class Debug { public static void Log(object value){} } }
namespace T3MP.Loading
{
    internal static class LoadPatchBridge
    {
        internal const BindingFlags All=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        internal static Type Find(string name)=>throw new NotSupportedException();
        internal static MethodInfo Create(string owner,MethodInfo method)=>throw new NotSupportedException();
    }
    internal static class LoadCompatibility { internal static bool Unmodified(IEnumerable<MethodBase> methods,string owner)=>throw new NotSupportedException(); }
}
