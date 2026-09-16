using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length!=2) throw new ArgumentException("original CoreModule.dll, new output directory");
var source=Path.GetFullPath(args[0]); var output=Path.GetFullPath(args[1]);
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var sourceHash=Hash(source);
if(sourceHash is not ("3C732547BE280545949EBB1B7FA4482F9490E32664649BD80E05C90ED893C839" or "26ACDBA17A3122C04E4640BD5D30833C56C8338700DBBBCD8E139FAC339568CB"))
    throw new InvalidOperationException("Unknown Unity implementation");
if(Directory.Exists(output)) throw new IOException("Preserve existing output");
using var module=ModuleDefinition.ReadModule(source);
var objectType=module.Types.Single(t=>t.FullName=="UnityEngine.Object");
var predicate=objectType.Methods.Single(m=>m.Name=="op_Implicit");
var alive=objectType.Methods.Single(m=>m.Name=="IsNativeObjectAlive");
var pointerGetter=objectType.Methods.Single(m=>m.Name=="GetCachedPtr");
var pointer=(FieldReference)pointerGetter.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldfld).Operand;
var zero=(FieldReference)alive.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldsfld).Operand;
var notEqual=(MethodReference)alive.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.Name=="op_Inequality").Operand;
if(pointer.FullName!="System.IntPtr UnityEngine.Object::m_CachedPtr" || zero.FullName!="System.IntPtr System.IntPtr::Zero")
    throw new InvalidOperationException("Unexpected pointer fields");
IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) {
    foreach(var type in types) {yield return type; foreach(var child in Types(type.NestedTypes)) yield return child;}
}
string Body(MethodDefinition m)=>m.HasBody ? string.Join("\n",m.Body.Instructions.Select(i=>i.ToString())) : "<no body>";
var originalBodies=Types(module.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.MetadataToken.ToInt32(),m=>(Signature:m.FullName+"#"+m.GenericParameters.Count,Code:Body(m)));
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output,"native-predicate.il.txt"),Body(predicate));
predicate.Body=new Mono.Cecil.Cil.MethodBody(predicate);
var il=predicate.Body.GetILProcessor(); var absent=il.Create(OpCodes.Ldc_I4_0);
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse,absent);
il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld,pointer); il.Emit(OpCodes.Ldsfld,zero);
il.Emit(OpCodes.Call,notEqual); il.Emit(OpCodes.Ret); il.Append(absent); il.Emit(OpCodes.Ret);
var candidate=Path.Combine(output,Path.GetFileName(source)); module.Write(candidate);
using var written=ModuleDefinition.ReadModule(candidate);
var writtenMethods=Types(written.Types).SelectMany(t=>t.Methods).ToArray();
if(writtenMethods.Length!=originalBodies.Count) throw new InvalidOperationException("Method count changed");
var changed=writtenMethods.Where(m=>!originalBodies.TryGetValue(m.MetadataToken.ToInt32(),out var old)||old.Signature!=m.FullName+"#"+m.GenericParameters.Count||old.Code!=Body(m)).Select(m=>m.FullName).ToArray();
if(changed.Length!=1 || changed[0]!=predicate.FullName) throw new InvalidOperationException("Unexpected method body changes");
File.WriteAllText(Path.Combine(output,"candidate-predicate.il.txt"),Body(written.Types.Single(t=>t.FullName==objectType.FullName).Methods.Single(m=>m.Name==predicate.Name)));
using var candidateStream=File.OpenRead(candidate);
using var pe=new System.Reflection.PortableExecutable.PEReader(candidateStream);
var metadata=System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
var definition=metadata.GetMethodDefinition(System.Reflection.Metadata.Ecma335.MetadataTokens.MethodDefinitionHandle((int)predicate.MetadataToken.RID));
var rawBody=System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe,definition.RelativeVirtualAddress).GetILBytes()!;
var predicateBodyHash=Convert.ToHexString(SHA256.HashData(rawBody));
if(Hash(source)!=sourceHash) throw new InvalidOperationException("Original changed");
var report=new {Original=source,OriginalHash=sourceHash,CandidateHash=Hash(candidate),PredicateBodyHash=predicateBodyHash,ChangedMethods=changed,
    Scope="Generator and method-body audit only. Object's native static initialization prevents direct CoreCLR execution; lifetime equivalence and performance require real Unity tests. No game files written."};
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(report));
