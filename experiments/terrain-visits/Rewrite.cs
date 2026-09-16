using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline prototype on a copy of Navigation.dll. Not a deployable MOD patch:
// helper-call interception / Harmony coexistence must be resolved before adoption.
internal static class Rewrite
{
    internal const string ServiceName = "Timberborn.Navigation.TerrainReachabilityService";

    internal static void Apply(ModuleDefinition module)
    {
        var type = module.Types.Single(t => t.FullName == ServiceName);
        var neighbors = type.Methods.Single(m => m.Name == "VisitNeighbors");
        var visit = type.Methods.Single(m => m.Name == "VisitNode");
        var visited = type.Fields.Single(f => f.Name == "_visitedNodes");
        Require(visited.FieldType.FullName == "System.Collections.Generic.HashSet`1<System.Int32>");
        Require(visit.Body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(new[] {
            Code.Ldarg_0, Code.Ldfld, Code.Ldarg_1, Code.Callvirt, Code.Pop,
            Code.Ldarg_0, Code.Ldfld, Code.Ldarg_1, Code.Ldarg_2, Code.Newobj, Code.Callvirt, Code.Ret
        }));
        Require(Equals(visit.Body.Instructions[1].Operand, visited));
        var add = (MethodReference)visit.Body.Instructions[3].Operand;
        Require(add.Name == "Add" && add.DeclaringType.FullName == visited.FieldType.FullName);

        // The moved Add crosses only these native, side-effect-free value reads.
        foreach (var getter in neighbors.Body.Instructions.Where(i => i.Operand is MethodReference m &&
                     (m.Name == "get_Distance" || m.Name == "get_Cost")))
        {
            var body = ((MethodReference)getter.Operand).Resolve().Body;
            Require(body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(new[] { Code.Ldarg_0, Code.Ldfld, Code.Ret }));
        }
        var native = Clone(neighbors, "T3mpNativeVisitNeighbors");
        var fast = neighbors;
        var enqueue = Clone(visit, "T3mpEnqueueVisitedNode");
        for (var i = 0; i < 5; i++) enqueue.Body.Instructions.RemoveAt(0);
        var contains = fast.Body.Instructions.Single(i => i.Operand is MethodReference m &&
            m.Name == "Contains" && m.DeclaringType.FullName == visited.FieldType.FullName);
        Require(contains.OpCode == OpCodes.Callvirt && contains.Next.OpCode == OpCodes.Brtrue_S);
        contains.Operand = add;
        contains.Next.OpCode = OpCodes.Brfalse_S;
        var callVisit = fast.Body.Instructions.Single(i => Equals(i.Operand, visit));
        Require(callVisit.OpCode == OpCodes.Call);
        callVisit.Operand = enqueue;

        // Custom comparer calls can have observable side effects or throw.
        // Retain their original Contains/Add sequence. Null also takes native.
        var hashset = (GenericInstanceType)visited.FieldType;
        TypeReference Generic(string name) {
            var t = new TypeReference("System.Collections.Generic", name, module, hashset.ElementType.Scope);
            t.GenericParameters.Add(new GenericParameter("T", t));
            return t;
        }
        GenericInstanceType Of(TypeReference t, TypeReference arg) {
            var result = new GenericInstanceType(t); result.GenericArguments.Add(arg); return result;
        }
        var equality = Generic("EqualityComparer`1");
        var equalityInt = Of(equality, module.TypeSystem.Int32);
        var getDefault = new MethodReference("get_Default", Of(equality, equality.GenericParameters[0]), equalityInt) { HasThis = false };
        var comparer = Generic("IEqualityComparer`1");
        // Member references use !0 from the declaring generic type, not Int32.
        var getComparer = new MethodReference("get_Comparer", Of(comparer, add.Parameters[0].ParameterType), hashset) { HasThis = true };
        var il = neighbors.Body.GetILProcessor();
        var fallback = Instruction.Create(OpCodes.Ldarg_0);
        var first = neighbors.Body.Instructions[0];
        foreach (var instruction in new[] {
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, visited), Instruction.Create(OpCodes.Brfalse, fallback),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, visited), Instruction.Create(OpCodes.Callvirt, getComparer),
            Instruction.Create(OpCodes.Call, getDefault), Instruction.Create(OpCodes.Bne_Un, fallback)
        }) il.InsertBefore(first, instruction);
        il.Append(fallback); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, native); il.Emit(OpCodes.Ret);
    }

    private static MethodDefinition Clone(MethodDefinition original, string name)
    {
        Require(original.Body.ExceptionHandlers.Count == 0);
        var copy = new MethodDefinition(name, original.Attributes, original.ReturnType);
        original.DeclaringType.Methods.Add(copy);
        foreach (var p in original.Parameters) copy.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, p.ParameterType));
        copy.Body.InitLocals = original.Body.InitLocals;
        foreach (var v in original.Body.Variables) copy.Body.Variables.Add(new VariableDefinition(v.VariableType));
        var map = original.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
        foreach (var i in original.Body.Instructions)
        {
            var c = map[i]; c.OpCode = i.OpCode;
            c.Operand = i.Operand switch {
                Instruction target => map[target],
                Instruction[] targets => targets.Select(t => map[t]).ToArray(),
                VariableDefinition v => copy.Body.Variables[v.Index],
                ParameterDefinition p => copy.Parameters[p.Index],
                _ => i.Operand
            };
            copy.Body.Instructions.Add(c);
        }
        return copy;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unreviewed terrain visit shape");
    }
}
