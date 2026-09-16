using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

internal static class NativeIl
{
    // Inspect real game IL and the built production transpiler in an isolated
    // load context. No native movement, Unity call, or game startup executes.
    internal static void Verify(string codePath, string managed)
    {
        var context = new AssemblyLoadContext("WalkerNativeIl", isCollectible: true);
        context.Resolving += (_, name) => {
            var path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
        };
        var code = context.LoadFromAssemblyPath(Path.GetFullPath(codePath));
        var walking = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(managed, "Timberborn.WalkingSystem.dll")));
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var move = walking.GetType("Timberborn.WalkingSystem.WalkerMover")!.GetMethod("Move", flags)!;
        var provider = walking.GetType("Timberborn.WalkingSystem.WalkerSpeedManager")!.GetMethod("GetWalkerSpeedAtCurrentPosition", flags)!;
        var helper = code.GetType("T3MP.Runtime.RuntimePatches")!;
        var feature = code.GetType("T3MP.Runtime.WalkerSpeedDelegates")!;
        var shape = helper.GetMethod("OriginalShape", flags)!.Invoke(null, new object[] { typeof(Harmony), move });
        feature.GetField("_move", flags)!.SetValue(null, move);
        feature.GetField("_provider", flags)!.SetValue(null, provider);
        feature.GetField("_moveShape", flags)!.SetValue(null, shape);
        var original = PatchProcessor.GetOriginalInstructions(move, (ILGenerator)null!);
        var before = original.Select(i => (i.opcode, i.operand, labels: i.labels.ToArray(), blocks: i.blocks.ToArray())).ToArray();
        var rewritten = ((IEnumerable<CodeInstruction>)feature.GetMethod("RewriteMove", flags)!
            .MakeGenericMethod(typeof(CodeInstruction)).Invoke(null, new object[] { original })!).ToArray();
        if (rewritten.Length != before.Length) throw new Exception("Instruction count changed");
        var changes = new List<int>();
        for (var i = 0; i < before.Length; i++)
        {
            if (!before[i].labels.SequenceEqual(rewritten[i].labels) || !before[i].blocks.SequenceEqual(rewritten[i].blocks))
                throw new Exception("Control-flow metadata changed");
            if (before[i].opcode != rewritten[i].opcode || !Equals(before[i].operand, rewritten[i].operand)) changes.Add(i);
        }
        if (changes.Count != 2 || changes[1] != changes[0] + 1 ||
            before[changes[0]].opcode != OpCodes.Ldftn || !Equals(before[changes[0]].operand, provider) ||
            before[changes[1]].opcode != OpCodes.Newobj || rewritten[changes[0]].opcode != OpCodes.Call ||
            !Equals(rewritten[changes[0]].operand, feature.GetMethod("Get", flags)) || rewritten[changes[1]].opcode != OpCodes.Nop)
            throw new Exception("Unexpected delegate rewrite");
        Console.WriteLine($"PASS native IL: {before.Length} instructions, only delegate construction changed; WalkingSystem MVID={walking.ManifestModule.ModuleVersionId}");
        context.Unload();
    }
}
