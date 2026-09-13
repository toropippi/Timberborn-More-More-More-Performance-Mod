using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using T3MP.Loading;
using Timberborn.BaseComponentSystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace T3MPTestDriver;

// Dev-only call-site experiment. Each use reads the same live GameObject and
// native pointer as BaseComponent's implicit bool. No lifetime result is cached.
internal static class BoolInliningExperiment
{
    private const string Owner = "t3mp.test.bool-inline";
    private const BindingFlags All = LoadPatchBridge.All;
    private static bool Has(string flag) => Environment.GetCommandLineArgs().Contains(flag, StringComparer.OrdinalIgnoreCase);
    internal static readonly bool Validate = Has("-t3mpTestBoolValidate");
    internal static readonly bool Synthetic = Has("-t3mpTestBoolSynthetic");
    internal static readonly bool Requested = Has("-t3mpTestBoolInline") || Validate || Synthetic;
    internal static bool Installed { get; private set; }
    internal static bool Enabled { get; private set; }
    private static readonly MethodInfo Native = typeof(BaseComponent).GetMethod("op_Implicit", All)!;
    private static readonly MethodInfo GameObjectGetter = typeof(BaseComponent).GetProperty("GameObject", All)!.GetGetMethod(true)!;
    private static readonly FieldInfo Pointer = typeof(Object).GetField("m_CachedPtr", All)!;
    private static readonly FieldInfo Zero = typeof(IntPtr).GetField("Zero", All)!;
    private static readonly MethodInfo NotEqual = typeof(IntPtr).GetMethod("op_Inequality", All)!;
    private static readonly Dictionary<MethodBase, int> Expected = new();
    private static long _compared;
    private static int _rewritten;

    internal static void Install()
    {
        if (!Requested || Installed) return;
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony");
        var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, Owner)!;
        try
        {
            Enabled = !Has("-t3mpTestBoolBaseline");
            if ((Validate || Synthetic) && !Enabled) throw new InvalidOperationException("Bool validation requires inlining");
            using var reader = new StreamReader(typeof(BoolInliningExperiment).Assembly.GetManifestResourceStream("T3MPTestDriver.BoolTargets.tsv")!);
            string Hash(Assembly assembly)
            {
                using var sha = SHA256.Create();
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(assembly.Location))).Replace("-", "");
            }
            var unityHash = Hash(typeof(Object).Assembly);
            var rows = reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.TrimStart('\uFEFF').Split('|')).Where(r => r[0] == unityHash)
                .Select(r => r.Skip(1).ToArray()).ToArray();
            if (rows.Length == 0) throw new InvalidOperationException("Unreviewed Unity bool version");
            foreach (var group in rows.GroupBy(r => r[0]))
            {
                var assembly = Assembly.Load(group.Key);
                var hash = Hash(assembly);
                var accepted = group.Where(r => r[1] == hash).ToArray();
                if (accepted.Length == 0) throw new InvalidOperationException("Unreviewed bool module: " + group.Key);
                foreach (var row in accepted.Where(r => r[2] != "0"))
                    Expected.Add(assembly.ManifestModule.ResolveMethod(int.Parse(row[2]))!, int.Parse(row[3]));
            }
            var dependencies = new MethodBase[] { Native, GameObjectGetter,
                typeof(ComponentCache).GetProperty("CachedGameObject", All)!.GetGetMethod(true)!,
                typeof(Object).GetMethod("op_Implicit", All)!, typeof(Object).GetMethod("CompareBaseObjects", All)!,
                typeof(Object).GetMethod("IsNativeObjectAlive", All)!, typeof(Object).GetMethod("GetCachedPtr", All)! };
            if (!LoadCompatibility.Unmodified(Expected.Keys.Concat(dependencies), Owner))
                throw new InvalidOperationException("Bool experiment cannot run with foreign target/predicate patches");
            var transpiler = CreateBridge();
            var hook = Activator.CreateInstance(hm, transpiler)!;
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            foreach (var method in Expected.Keys.ToArray())
                patch.Invoke(harmony, new object?[] { method, null, null, hook, null });
            if (_rewritten != Expected.Count) throw new InvalidOperationException("Bool patch count differs");
            if (Synthetic)
            {
                var probe = typeof(BoolInliningExperiment).GetMethod(nameof(Probe), All)!;
                Expected.Add(probe, 1);
                patch.Invoke(harmony, new object?[] { probe, null, null, hook, null });
                SyntheticValidation();
            }
            Installed = true;
            Debug.Log($"[T3MPTEST] Bool inline installed enabled={Enabled} methods={_rewritten} validate={Validate}");
        }
        catch (Exception exception)
        {
            ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { Owner });
            Installed = Enabled = false;
            Debug.LogError("[T3MPTEST] Bool inline FAIL: " + exception);
            if (Synthetic) Debug.LogError("[T3MPTEST] Bool synthetic validation FAIL: " + exception);
            throw;
        }
    }

    // Keep this specialized bridge in the driver; product transpilers do not
    // acquire new ILGenerator or original-method requirements.
    private static MethodInfo CreateBridge()
    {
        var instruction = LoadPatchBridge.Find("HarmonyLib.CodeInstruction");
        var sequence = typeof(IEnumerable<>).MakeGenericType(instruction);
        var callback = typeof(Func<,,,>).MakeGenericType(sequence, typeof(ILGenerator), typeof(MethodBase), sequence);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Owner), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main").DefineType("BoolTranspiler", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var field = type.DefineField("Rewrite", callback, FieldAttributes.Public | FieldAttributes.Static);
        var method = type.DefineMethod("Transpile", MethodAttributes.Public | MethodAttributes.Static, sequence,
            new[] { sequence, typeof(ILGenerator), typeof(MethodBase) });
        method.DefineParameter(1, ParameterAttributes.None, "instructions");
        method.DefineParameter(2, ParameterAttributes.None, "generator");
        method.DefineParameter(3, ParameterAttributes.None, "__originalMethod");
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, callback.GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
        var created = type.CreateType()!;
        created.GetField("Rewrite")!.SetValue(null, Delegate.CreateDelegate(callback,
            typeof(BoolInliningExperiment).GetMethod(nameof(Rewrite), All)!.MakeGenericMethod(instruction)));
        return created.GetMethod("Transpile")!;
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> source, ILGenerator generator, MethodBase original)
    {
        var list = source.ToList();
        var opcode = typeof(T).GetField("opcode", All)!;
        var operand = typeof(T).GetField("operand", All)!;
        var labels = typeof(T).GetField("labels", All)!;
        var blocks = typeof(T).GetField("blocks", All)!;
        bool IsCall(T i) => (OpCode)opcode.GetValue(i)! == OpCodes.Call && Equals(operand.GetValue(i), Native);
        if (list.Count(IsCall) != Expected[original]) throw new InvalidOperationException("Bool call count changed: " + original);
        _rewritten++;
        if (!Enabled) return list;
        var output = new List<T>();
        T Make(OpCode code, object? value = null) => (T)Activator.CreateInstance(typeof(T), new object?[] { code, value })!;
        foreach (var instruction in list)
        {
            if (!IsCall(instruction)) { output.Add(instruction); continue; }
            // Do not relocate an exception boundary without explicitly modelling it.
            if (((IList)blocks.GetValue(instruction)!).Count != 0) throw new InvalidOperationException("Bool call at exception boundary");
            var componentPresent = generator.DefineLabel();
            var objectPresent = generator.DefineLabel();
            var done = generator.DefineLabel();
            // Reuse the first instruction so incoming native labels remain valid.
            opcode.SetValue(instruction, OpCodes.Dup); operand.SetValue(instruction, null);
            output.Add(instruction);
            LocalBuilder? input = null;
            if (Validate && original.DeclaringType != typeof(BoolInliningExperiment))
            {
                input = generator.DeclareLocal(typeof(BaseComponent));
                output.Add(Make(OpCodes.Stloc, input)); output.Add(Make(OpCodes.Dup));
            }
            output.Add(Make(OpCodes.Brtrue, componentPresent));
            output.Add(Make(OpCodes.Pop)); output.Add(Make(OpCodes.Ldc_I4_0)); output.Add(Make(OpCodes.Br, done));
            var getObject = Make(OpCodes.Call, GameObjectGetter);
            ((IList)labels.GetValue(getObject)!).Add(componentPresent); output.Add(getObject);
            output.Add(Make(OpCodes.Dup)); output.Add(Make(OpCodes.Brtrue, objectPresent));
            output.Add(Make(OpCodes.Pop)); output.Add(Make(OpCodes.Ldc_I4_0)); output.Add(Make(OpCodes.Br, done));
            var getPointer = Make(OpCodes.Ldfld, Pointer);
            ((IList)labels.GetValue(getPointer)!).Add(objectPresent); output.Add(getPointer);
            output.Add(Make(OpCodes.Ldsfld, Zero)); output.Add(Make(OpCodes.Call, NotEqual));
            var end = Make(OpCodes.Nop); ((IList)labels.GetValue(end)!).Add(done); output.Add(end);
            if (input != null)
            {
                output.Add(Make(OpCodes.Ldloc, input));
                output.Add(Make(OpCodes.Call, typeof(BoolInliningExperiment).GetMethod(nameof(Check), All)!));
            }
        }
        return output;
    }

    private static bool Check(bool actual, BaseComponent value)
    {
        bool expected = value;
        if (actual != expected) throw new InvalidOperationException("Bool inline/native mismatch");
        if (FullTickCounter.FullTicks > 0) _compared++;
        return actual;
    }
    internal static void Report()
    {
        if (Validate && Installed && FullTickCounter.FullTicks > 0 && _compared > 0)
            Debug.Log("[T3MPTEST] Bool runtime validation PASS: compared=" + _compared);
    }

    private sealed class TestComponent : BaseComponent { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Probe(BaseComponent value) => value;
    private static void SyntheticValidation()
    {
        var checks = 0;
        void Compare(BaseComponent value)
        {
            bool expected = false, actual = false;
            Type? nativeError = null, inlineError = null;
            try { expected = value; } catch (Exception e) { nativeError = e.GetType(); }
            try { actual = Probe(value); } catch (Exception e) { inlineError = e.GetType(); }
            if (nativeError != inlineError || actual != expected) throw new InvalidOperationException("Bool fixture result/exception differs");
            checks++;
        }
        Compare(null!);
        Compare(new TestComponent()); // Uninitialized cache must retain native NullReferenceException.
        var cacheObject = new GameObject("T3MP bool fixture cache");
        var liveObject = new GameObject("T3MP bool fixture target");
        try
        {
            var cache = cacheObject.AddComponent<ComponentCache>();
            var value = new TestComponent();
            typeof(BaseComponent).GetMethod("Initialize", All)!.Invoke(value, new object[] { cache });
            var cachedObject = typeof(ComponentCache).GetProperty("CachedGameObject", All)!;
            cachedObject.SetValue(cache, liveObject); Compare(value);
            cachedObject.SetValue(cache, null); Compare(value);
            cachedObject.SetValue(cache, liveObject); Compare(value);
            Object.DestroyImmediate(liveObject); Compare(value); // Still a managed reference to a destroyed object.
            liveObject = new GameObject("T3MP bool fixture live at cache destruction");
            cachedObject.SetValue(cache, liveObject); Compare(value);
            Object.DestroyImmediate(cacheObject); Compare(value); // Native OnDestroy clears CachedGameObject.
        }
        finally
        {
            if (liveObject) Object.DestroyImmediate(liveObject);
            if (cacheObject) Object.DestroyImmediate(cacheObject);
        }
        Debug.Log("[T3MPTEST] Bool synthetic validation PASS: checks=" + checks);
    }
}
