using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

if (args.Length != 2) throw new ArgumentException("managed-directory new-output-directory");
var managed = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Preserve existing output: " + output);
Directory.CreateDirectory(output);
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var corePath = Path.Combine(managed,"UnityEngine.CoreModule.dll");
if (Hash(typeof(Vector3).Assembly.Location) != Hash(corePath)) throw new InvalidOperationException("Build and test Unity modules differ");
var input = Path.Combine(managed,"Timberborn.CharacterMovementSystem.dll");
var inputHash = Hash(input);
using var module = ModuleDefinition.ReadModule(input);
using var unity = ModuleDefinition.ReadModule(corePath);
var follower = module.Types.Single(t => t.FullName == "Timberborn.CharacterMovementSystem.PathFollower");
var method = follower.Methods.Single(m => m.Name == "MoveInDirection");
if (!method.IsStatic || method.Parameters.Count != 5 || method.Body.ExceptionHandlers.Count != 0) throw new InvalidOperationException("Unexpected movement method");
var originalIl = string.Join("\n",method.Body.Instructions);
File.WriteAllText(Path.Combine(output,"native.il.txt"),originalIl);
var instructions = method.Body.Instructions;
bool Calls(Instruction i, string name) => i.OpCode == OpCodes.Call && i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.Vector3" && m.Name == name;
var magnitudeCall = instructions.Single(i => Calls(i,"get_magnitude"));
var normalizedCall = instructions.Single(i => Calls(i,"get_normalized"));
int LocalIndex(Instruction i) => i.OpCode.Code switch {
    Code.Stloc_0 => 0, Code.Stloc_1 => 1, Code.Stloc_2 => 2, Code.Stloc_3 => 3,
    Code.Stloc or Code.Stloc_S => ((VariableDefinition)i.Operand).Index,
    _ => throw new InvalidOperationException("Magnitude is not stored in a local")
};
var magnitude = method.Body.Variables[LocalIndex(magnitudeCall.Next)];
if (magnitude.VariableType.FullName != "System.Single" ||
    magnitudeCall.Previous.OpCode != OpCodes.Ldloca_S || normalizedCall.Previous.OpCode != OpCodes.Ldloca_S ||
    !ReferenceEquals(magnitudeCall.Previous.Operand, normalizedCall.Previous.Operand))
    throw new InvalidOperationException("Magnitude and normalization do not read the same vector local");
var vector = unity.Types.Single(t => t.FullName == "UnityEngine.Vector3");
var divide = module.ImportReference(vector.Methods.Single(m => m.Name == "op_Division"));
var zero = module.ImportReference(vector.Methods.Single(m => m.Name == "get_zero"));
var vectorType = module.ImportReference(vector);
// Preserve the original instruction object, so native incoming branches still
// target the start of the replacement. The stack already contains Vector3&.
var next = normalizedCall.Next;
normalizedCall.OpCode = OpCodes.Ldloc;
normalizedCall.Operand = magnitude;
var divideLabel = Instruction.Create(OpCodes.Ldobj,vectorType);
var done = Instruction.Create(OpCodes.Nop);
var replacement = new[] {
    Instruction.Create(OpCodes.Ldc_R4,1e-5f),
    Instruction.Create(OpCodes.Bgt,divideLabel),
    Instruction.Create(OpCodes.Pop),
    Instruction.Create(OpCodes.Call,zero),
    Instruction.Create(OpCodes.Br,done),
    divideLabel,
    Instruction.Create(OpCodes.Ldloc,magnitude),
    Instruction.Create(OpCodes.Call,divide),
    done
};
var il = method.Body.GetILProcessor();
foreach (var instruction in replacement) il.InsertBefore(next,instruction);
var candidatePath = Path.Combine(output,Path.GetFileName(input));
module.Write(candidatePath);
using (var serialized = ModuleDefinition.ReadModule(candidatePath))
    File.WriteAllText(Path.Combine(output,"candidate.il.txt"), string.Join("\n", serialized.Types.Single(t => t.FullName == follower.FullName).Methods.Single(m => m.Name == method.Name).Body.Instructions));
var originalContext = new GameContext(managed);
var candidateContext = new GameContext(managed);
Move Bind(GameContext context,string path) => context.LoadFromAssemblyPath(path)
    .GetType(follower.FullName,true)!.GetMethod("MoveInDirection",BindingFlags.NonPublic|BindingFlags.Static)!.CreateDelegate<Move>();
var native = Bind(originalContext,input);
var candidate = Bind(candidateContext,candidatePath);
long cases=0;
int Bits(float x) => BitConverter.SingleToInt32Bits(x);
void Compare(Vector3 position,Vector3 target,float speed,float seconds) {
    var nativeTime=seconds; var candidateTime=seconds;
    var a=native(position,target,speed,ref nativeTime,out var reachedA);
    var b=candidate(position,target,speed,ref candidateTime,out var reachedB);
    if (Bits(a.x)!=Bits(b.x) || Bits(a.y)!=Bits(b.y) || Bits(a.z)!=Bits(b.z) || Bits(nativeTime)!=Bits(candidateTime) || reachedA!=reachedB)
        throw new InvalidOperationException($"Bitwise mismatch at case {cases}: {position}/{target}/{speed}/{seconds}");
    cases++;
}
var edge = new[] {0f,-0f,float.Epsilon,-float.Epsilon,1e-6f,1e-5f,1e-4f,0.1f,1f,-1f,float.MaxValue,float.MinValue,float.PositiveInfinity,float.NegativeInfinity,float.NaN};
foreach(var value in edge) foreach(var speed in edge) foreach(var time in edge) {
    Compare(Vector3.zero,new Vector3(value,0,0),speed,time);
    Compare(new Vector3(value,value,value),new Vector3(-value,0,value),speed,time);
}
// Include both sides of the native proximity and normalization thresholds.
foreach(var threshold in new[]{1e-5f,1e-4f,0.1f}) for(int offset=-8;offset<=8;offset++) {
    var value=BitConverter.Int32BitsToSingle(Bits(threshold)+offset);
    foreach(var speed in edge) Compare(Vector3.zero,new Vector3(value,0,0),speed,0.6f);
}
var random=new System.Random(193812);
float Typical(float scale) => (float)(random.NextDouble()*scale);
float Raw() => BitConverter.Int32BitsToSingle((int)random.NextInt64(int.MinValue,(long)int.MaxValue+1));
for(var i=0;i<1000000;i++) {
    var position=new Vector3(Typical(512),Typical(100),Typical(512));
    Compare(position,position+new Vector3(Typical(10)-5,Typical(10)-5,Typical(10)-5),Typical(50),Typical(2));
}
for(var i=0;i<100000;i++) Compare(new Vector3(Raw(),Raw(),Raw()),new Vector3(Raw(),Raw(),Raw()),Raw(),Raw());
if(Hash(input)!=inputHash) throw new InvalidOperationException("Source DLL changed");
var report = new {Cases=cases,BitwiseMismatches=0,Input=input,InputHash=inputHash,UnityHash=Hash(corePath),CandidateHash=Hash(candidatePath),
    Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Scope="Actual native and one-site IL-modified MoveInDirection methods under CoreCLR. Compares position, remaining time and reached flag. Not Unity Mono runtime or performance evidence; no game files changed."};
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(report));

delegate Vector3 Move(Vector3 position,Vector3 target,float speed,ref float remainingTime,out bool reached);
sealed class GameContext(string managed) : AssemblyLoadContext(isCollectible:true) {
    protected override Assembly? Load(AssemblyName name) {
        if(name.Name == typeof(Vector3).Assembly.GetName().Name) return typeof(Vector3).Assembly;
        if(name.Name is "mscorlib" or "netstandard" || name.Name!.StartsWith("System")) return null;
        var path=Path.Combine(managed,name.Name+".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
