using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using T3MP.Loading;
using Timberborn.CharacterMovementSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Dev-only common-subexpression experiment. Movement steps, callbacks and
// float operations after normalization stay native. No values survive a call.
internal static class MovementMathExperiment
{
    private const string Owner = "t3mp.test.movement-math";
    private const BindingFlags All = LoadPatchBridge.All;
    private static bool Has(string flag) => Environment.GetCommandLineArgs().Contains(flag, StringComparer.OrdinalIgnoreCase);
    private static readonly bool Validate = Has("-t3mpTestMoveMathValidate");
    internal static bool Installed { get; private set; }
    private static long _compared;
    private static int _rewrites;
    private static readonly MethodInfo Native = typeof(PathFollower).GetMethod("MoveInDirection", All)!;
    private static readonly MethodInfo Magnitude = typeof(Vector3).GetProperty("magnitude")!.GetGetMethod()!;
    private static readonly MethodInfo Normalized = typeof(Vector3).GetProperty("normalized")!.GetGetMethod()!;
    private static readonly MethodInfo Divide = typeof(Vector3).GetMethod("op_Division", All)!;
    private static readonly MethodInfo Zero = typeof(Vector3).GetProperty("zero")!.GetGetMethod()!;

    internal static void Install()
    {
        if (!Has("-t3mpTestMoveMath") && !Validate) return;
        if (Validate && TestArguments.Speed == null) throw new InvalidOperationException("Movement validation requires a valid speed to report coverage");
        if (Validate && Has("-t3mpTestBenchmarkTicks")) throw new InvalidOperationException("Do not benchmark movement validation");
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony");
        var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, Owner)!;
        try
        {
            string Hash(Assembly assembly)
            {
                using var sha = SHA256.Create();
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(assembly.Location))).Replace("-", "");
            }
            var identity = Hash(typeof(Vector3).Assembly) + ":" + Hash(typeof(PathFollower).Assembly);
            if (identity != "26ACDBA17A3122C04E4640BD5D30833C56C8338700DBBBCD8E139FAC339568CB:F8A4C435ED94373C5079375BE1FE572782CF9DEA9C071E96370E2463D6526352" &&
                identity != "3C732547BE280545949EBB1B7FA4482F9490E32664649BD80E05C90ED893C839:4712AEC43F52BA3E4AB2ED335385D83891BC16743D29314CA289C8E3EBF01A2B")
                throw new InvalidOperationException("Unreviewed movement math modules");
            var dependencies = new MethodBase[] { Native, Magnitude, Normalized, Divide, Zero }
                .Concat(typeof(Vector3).GetMethods(All).Where(m => m.Name == "Normalize"));
            if (!LoadCompatibility.Unmodified(dependencies, Owner)) throw new InvalidOperationException("Foreign movement math patch");
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            patch.Invoke(harmony, new object?[] { Native, null, null, Activator.CreateInstance(hm, CreateBridge()), null });
            if (_rewrites != 1) throw new InvalidOperationException("Movement math rewrite did not run exactly once");
            Installed = true;
            Debug.Log($"[T3MPTEST] Movement math installed validate={Validate} modules={identity}");
        }
        catch
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
            Installed = false;
            throw;
        }
    }

    private static MethodInfo CreateBridge()
    {
        var instruction = LoadPatchBridge.Find("HarmonyLib.CodeInstruction");
        var sequence = typeof(IEnumerable<>).MakeGenericType(instruction);
        var callback = typeof(Func<,,>).MakeGenericType(sequence, typeof(ILGenerator), sequence);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Owner), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main").DefineType("MovementMathTranspiler", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var field = type.DefineField("Rewrite", callback, FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod("Transpile", MethodAttributes.Public | MethodAttributes.Static, sequence, new[] { sequence, typeof(ILGenerator) });
        method.DefineParameter(1, ParameterAttributes.None, "instructions");
        method.DefineParameter(2, ParameterAttributes.None, "generator");
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, callback.GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
        var created = type.CreateType()!;
        created.GetField("Rewrite")!.SetValue(null, Delegate.CreateDelegate(callback,
            typeof(MovementMathExperiment).GetMethod(nameof(Rewrite), All)!.MakeGenericMethod(instruction)));
        return created.GetMethod("Transpile")!;
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> source, ILGenerator generator)
    {
        var list = source.ToList();
        var opcode = typeof(T).GetField("opcode", All)!;
        var operand = typeof(T).GetField("operand", All)!;
        var labels = typeof(T).GetField("labels", All)!;
        var blocks = typeof(T).GetField("blocks", All)!;
        bool Calls(T item, MethodInfo method) => (OpCode)opcode.GetValue(item)! == OpCodes.Call && Equals(operand.GetValue(item), method);
        var magnitudeIndex = list.FindIndex(i => Calls(i, Magnitude));
        var normalizedIndex = list.FindIndex(i => Calls(i, Normalized));
        if (list.Count(i => Calls(i, Magnitude)) != 1 || list.Count(i => Calls(i, Normalized)) != 1 || magnitudeIndex < 1 || normalizedIndex <= magnitudeIndex ||
            (OpCode)opcode.GetValue(list[magnitudeIndex + 1])! != OpCodes.Stloc_1 ||
            (OpCode)opcode.GetValue(list[magnitudeIndex - 1])! != OpCodes.Ldloca_S ||
            (OpCode)opcode.GetValue(list[normalizedIndex - 1])! != OpCodes.Ldloca_S ||
            !Equals(operand.GetValue(list[magnitudeIndex - 1]), operand.GetValue(list[normalizedIndex - 1])))
            throw new InvalidOperationException("Movement normalization IL shape changed");
        if (list.Any(i => ((IList)blocks.GetValue(i)!).Count != 0)) throw new InvalidOperationException("Unexpected movement exception boundaries");
        var call = list[normalizedIndex];
        var divideLabel = generator.DefineLabel();
        var done = generator.DefineLabel();
        T Make(OpCode op, object? value = null) => (T)Activator.CreateInstance(typeof(T), new object?[] { op, value })!;
        var output = list.Take(normalizedIndex).ToList();
        opcode.SetValue(call, OpCodes.Ldloc_1); operand.SetValue(call, null); output.Add(call);
        output.Add(Make(OpCodes.Ldc_R4, 1e-5f)); output.Add(Make(OpCodes.Bgt, divideLabel));
        output.Add(Make(OpCodes.Pop)); output.Add(Make(OpCodes.Call, Zero)); output.Add(Make(OpCodes.Br, done));
        var divideStart = Make(OpCodes.Ldobj, typeof(Vector3)); ((IList)labels.GetValue(divideStart)!).Add(divideLabel); output.Add(divideStart);
        output.Add(Make(OpCodes.Ldloc_1)); output.Add(Make(OpCodes.Call, Divide));
        var end = Make(OpCodes.Nop); ((IList)labels.GetValue(end)!).Add(done); output.Add(end);
        if (Validate)
        {
            output.Add(Make(OpCodes.Ldloca_S, operand.GetValue(list[normalizedIndex - 1])));
            output.Add(Make(OpCodes.Call, typeof(MovementMathExperiment).GetMethod(nameof(Check), All)!));
        }
        output.AddRange(list.Skip(normalizedIndex + 1));
        _rewrites++;
        return output;
    }

    private static Vector3 Check(Vector3 actual, ref Vector3 input)
    {
        var expected = input.normalized;
        if (BitConverter.SingleToInt32Bits(actual.x) != BitConverter.SingleToInt32Bits(expected.x) ||
            BitConverter.SingleToInt32Bits(actual.y) != BitConverter.SingleToInt32Bits(expected.y) ||
            BitConverter.SingleToInt32Bits(actual.z) != BitConverter.SingleToInt32Bits(expected.z))
            throw new InvalidOperationException("Movement math bitwise mismatch");
        _compared++;
        return actual;
    }
    internal static void Report()
    {
        if (Validate && Installed) Debug.Log("[T3MPTEST] Movement math validation compared=" + _compared);
    }
}
