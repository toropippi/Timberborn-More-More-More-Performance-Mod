using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;
using System.Security.Cryptography;

if (args.Length is < 2 or > 3) throw new ArgumentException("Managed directory, output JSON, optional experiment manifest TSV");
var results = new List<object>();
var targets = new List<string>();
foreach (var file in Directory.GetFiles(args[0], "Timberborn.*.dll").Order())
{
    using var module = ModuleDefinition.ReadModule(file);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    foreach (var type in Types(module.Types))
    foreach (var method in type.Methods.Where(m => m.HasBody))
    {
        var calls = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference m &&
            m.Name == "op_Implicit" && m.DeclaringType.FullName == "Timberborn.BaseComponentSystem.BaseComponent").ToArray();
        if (calls.Length == 0) continue;
        if (!method.HasGenericParameters && !type.HasGenericParameters &&
            !module.Name.Contains("UI") && !module.Name.Contains("Editor") && !module.Name.Contains("Debug") &&
            module.Assembly.Name.Name != "Timberborn.TickSystem" &&
            System.Text.RegularExpressions.Regex.IsMatch(method.Name, "^(Tick|Decide|Find|Closest|ProcessBehaviors|GetCoordinates|RandomDestination|GetWorkplaceBehaviorsOrdered)"))
            targets.Add($"{module.Assembly.Name.Name}|{hash}|{method.MetadataToken.ToInt32()}|{calls.Length}");
        results.Add(new { Assembly = module.Assembly.Name.Name, Hash = hash, Mvid = module.Mvid, Type = type.FullName,
            Method = method.Name, Token = method.MetadataToken.ToInt32(), Calls = calls.Length,
            Parameters = method.Parameters.Select(p => p.ParameterType.FullName).ToArray(),
            Generic = method.HasGenericParameters || type.HasGenericParameters,
            ExceptionRegions = method.Body.ExceptionHandlers.Count, Bytes = method.Body.CodeSize });
    }
}
File.WriteAllText(args[1], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count} methods contain BaseComponent bool calls: {args[1]}");
if (args.Length == 3)
{
    foreach (var name in new[] { "Timberborn.BaseComponentSystem", "UnityEngine.CoreModule" })
        targets.Add($"{name}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(args[0], name + ".dll"))))}|0|0");
    var unityHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(args[0], "UnityEngine.CoreModule.dll"))));
    File.WriteAllLines(args[2], targets.Select(row => unityHash + "|" + row));
}
static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
{
    foreach (var type in roots) { yield return type; foreach (var nested in Types(type.NestedTypes)) yield return nested; }
}
