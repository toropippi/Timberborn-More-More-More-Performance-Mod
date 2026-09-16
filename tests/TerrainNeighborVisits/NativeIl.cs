using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

internal static class NativeIl
{
    internal static void Verify(string codePath, string managed)
    {
        var context = new AssemblyLoadContext("TerrainNativeIl", isCollectible: true);
        context.Resolving += (_, name) => {
            var p = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(p) ? context.LoadFromAssemblyPath(Path.GetFullPath(p)) : null;
        };
        var code = context.LoadFromAssemblyPath(Path.GetFullPath(codePath));
        var nav = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(managed, "Timberborn.Navigation.dll")));
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var service = nav.GetType("Timberborn.Navigation.TerrainReachabilityService", true)!;
        var neighbors = service.GetMethod("VisitNeighbors", flags)!;
        var feature = code.GetType("T3MP.Runtime.TerrainNeighborVisits", true)!;
        var helper = code.GetType("T3MP.Runtime.RuntimePatches", true)!;
        var shape = helper.GetMethod("OriginalShape", flags)!.Invoke(null, new object[] { typeof(Harmony), neighbors });
        feature.GetField("_shape", flags)!.SetValue(null, shape);
        var original = PatchProcessor.GetOriginalInstructions(neighbors, (ILGenerator)null!);
        var rewrite = feature.GetMethod("RewriteNeighbors", flags)!.MakeGenericMethod(typeof(CodeInstruction));
        var changed = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { original })!).ToArray();
        var target = feature.GetMethod("VisitNeighbors", flags)!;
        if (!changed.Select(i => i.opcode).SequenceEqual(new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Call, OpCodes.Ret }) ||
            !Equals(changed[2].operand, target) || target.GetParameters()[0].ParameterType != service ||
            target.GetParameters()[1].ParameterType != neighbors.GetParameters()[0].ParameterType)
            throw new Exception("Invalid native call/argument mapping");
        var second = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { original })!).ToArray();
        if (!second.SequenceEqual(original)) throw new Exception("Second generation did not retain native input");
        Console.WriteLine($"PASS native IL: production call mapping and later generation passthrough; Navigation MVID={nav.ManifestModule.ModuleVersionId}");
        context.Unload();
    }
}
