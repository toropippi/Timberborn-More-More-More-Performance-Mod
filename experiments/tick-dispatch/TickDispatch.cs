using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Timberborn.BaseComponentSystem;
using Timberborn.TickSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Trial: expand the two metering wrappers at their original call sites. Keep the
// native enumerator, live BaseComponent.Enabled field, both metrics reads,
// component call (including 1.0 StartAndTick), and Entity.Tick exception scope.
// No component list, active state or simulation result is cached here.
internal static class TickDispatch
{
    internal const string Owner = "t3mp.runtime.tick-dispatch";
    private static Type? _harmony;
    private static MethodInfo? _loop, _enabled, _tick, _baseEnabled;
    private static RuntimePatches.Shape? _shape;
    private static int _invalidated, _loopGenerations, _enabledGenerations, _tickGenerations, _baseEnabledGenerations;
    private static bool _attempted;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;

    // Frontier may coexist only on this loop and its Enabled getter paths:
    // their observable semantics
    // are unchanged even when this feature falls back after a later patch.
    internal static bool CompatibleFrontierPath(MethodBase method) =>
        Equals(method, _loop) || Equals(method, _enabled) || Equals(method, _baseEnabled);

    internal static void Install(Type harmony, Type harmonyMethod, MethodInfo patch)
    {
        if (_attempted) return;
        _attempted = true;
        _harmony = harmony;
        Installed = RuntimePatches.TryInstall(Owner, harmony, harmonyMethod, patch, apply =>
        {
            var flags = RuntimePatches.All | BindingFlags.DeclaredOnly;
            _loop = typeof(TickableEntity).GetMethod("TickTickableComponents", flags)!;
            _enabled = typeof(MeteredTickableComponent).GetMethod("get_Enabled", flags)!;
            _baseEnabled = typeof(BaseComponent).GetMethod("get_Enabled", flags)!;
            _tick = typeof(MeteredTickableComponent).GetMethod("StartAndTick", flags)
                ?? typeof(MeteredTickableComponent).GetMethod("Tick", flags)!;
            if (_loop == null || _enabled == null || _tick == null || _baseEnabled == null ||
                !RuntimePatches.ReviewedBody(_baseEnabled,
                    "A42B33234BAD279EC5D573C04257AB8869486DCF80EFE867031389346376EBBE") ||
                !RuntimePatches.ReviewedBody(_loop,
                    "F7D063689233726D8BB0CE335466978A40CC9B86E088D99721D5A18216119180",
                    "ADF101E3EEB721D1535EC28BAC0105EB26853DB0617C8998EA65F4808BC6F5CB") ||
                !RuntimePatches.ReviewedBody(_enabled,
                    "130955918CD7930B535E1996D9CDFB35A82E4E9078B14566E5B9F6F944244CB9") ||
                !RuntimePatches.ReviewedBody(_tick,
                    "452693D36F7D5C7384B73CF01C66C6FF2F7B0CC5DF641C09B6A7BCB7EB4A0FD3",
                    "1E7F912A17BE37FDC74F6B4EF96D61341000557C001ACA2E919EA80BFCF684B5"))
                throw new InvalidOperationException("tick dispatch is not a reviewed build");
            foreach (var method in new[] { _loop, _enabled, _tick, _baseEnabled })
                if (method.IsStatic || method.IsVirtual || method.GetParameters().Length != 0 ||
                    method.GetMethodBody()!.ExceptionHandlingClauses.Count != 0 ||
                    RuntimePatches.ForeignPatched(harmony, method, Owner, false))
                    throw new InvalidOperationException("unexpected or patched tick dispatch: " + method.Name);
            foreach (var method in new[] { _enabled, _tick, _baseEnabled })
                if (method.GetMethodBody()!.LocalVariables.Count != 0)
                    throw new InvalidOperationException("wrapper has unexpected locals");
            _shape = RuntimePatches.OriginalShape(harmony, _loop);
            // A helper repatch invalidates already-generated callers immediately.
            // Every expanded call site checks the flag, including after a prior
            // component installs another mod's hook during this very tick.
            // Install this guard first so fallback wrappers cannot JIT-inline
            // the base getter before a later mod patches it. The fast path
            // expands its single field read under the same invalidation flag.
            apply(_baseEnabled, null, null, nameof(ObserveBaseEnabled), null);
            apply(_enabled, null, null, nameof(ObserveEnabled), null);
            apply(_tick, null, null, nameof(ObserveTick), null);
            apply(_loop, null, null, nameof(Rewrite), null);
        }, typeof(TickDispatch));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Active) Debug.Log("[T3MP] Tick dispatch installed (live state, native metering).");
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmony == null) return;
        try
        {
            if (Active && !new[] { _loop!, _enabled!, _tick!, _baseEnabled! }.Any(m =>
                    RuntimePatches.ForeignPatched(_harmony, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmony, Owner);
            Installed = false;
            Debug.Log("[T3MP] Tick dispatch removed after call-chain changes; native calls restored.");
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[T3MP] Tick dispatch revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static IEnumerable<T> ObserveEnabled<T>(IEnumerable<T> instructions)
    {
        if (Interlocked.Increment(ref _enabledGenerations) != 1) Interlocked.Exchange(ref _invalidated, 1);
        return instructions;
    }

    private static IEnumerable<T> ObserveBaseEnabled<T>(IEnumerable<T> instructions)
    {
        if (Interlocked.Increment(ref _baseEnabledGenerations) != 1) Interlocked.Exchange(ref _invalidated, 1);
        return instructions;
    }

    private static IEnumerable<T> ObserveTick<T>(IEnumerable<T> instructions)
    {
        if (Interlocked.Increment(ref _tickGenerations) != 1) Interlocked.Exchange(ref _invalidated, 1);
        return instructions;
    }

    private static IEnumerable<T> Original<T>(MethodInfo method, ILGenerator generator)
    {
        var processor = _harmony!.Assembly.GetType("HarmonyLib.PatchProcessor")!;
        var get = processor.GetMethods(RuntimePatches.All).Single(m => m.Name == "GetOriginalInstructions" &&
            m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(ILGenerator));
        return (IEnumerable<T>)get.Invoke(null, new object[] { method, generator })!;
    }

    private static IEnumerable<T> Rewrite<T>(IEnumerable<T> instructions, ILGenerator generator)
    {
        var original = new List<T>(instructions);
        if (Interlocked.Increment(ref _loopGenerations) != 1 || _shape == null ||
            Volatile.Read(ref _invalidated) != 0 ||
            !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(original)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return original;
        }
        var opcode = typeof(T).GetField("opcode", RuntimePatches.All)!;
        var operand = typeof(T).GetField("operand", RuntimePatches.All)!;
        var labels = typeof(T).GetField("labels", RuntimePatches.All)!;
        var blocks = typeof(T).GetField("blocks", RuntimePatches.All)!;
        OpCode Op(T i) => (OpCode)opcode.GetValue(i)!;
        T New(OpCode op, object? value = null) => (T)Activator.CreateInstance(typeof(T), op, value)!;
        void LabelAt(T i, Label label) => ((System.Collections.IList)labels.GetValue(i)!).Add(label);
        var output = new List<T>();
        var getter = Original<T>(_baseEnabled!, generator).ToList();
        if (getter.Count != 3 || Op(getter[0]) != OpCodes.Ldarg_0 || Op(getter[1]) != OpCodes.Ldfld ||
            Op(getter[2]) != OpCodes.Ret || operand.GetValue(getter[1]) is not FieldInfo enabledField ||
            enabledField.FieldType != typeof(bool) || enabledField.DeclaringType != typeof(BaseComponent))
            throw new InvalidOperationException("Enabled getter is not a single live field read");
        int enabledSites = 0, tickSites = 0;
        foreach (var instruction in original)
        {
            var method = operand.GetValue(instruction) as MethodInfo;
            if (Op(instruction) != OpCodes.Callvirt || (!Equals(method, _enabled) && !Equals(method, _tick)))
            {
                output.Add(instruction);
                continue;
            }
            if (Equals(method, _enabled)) enabledSites++; else tickSites++;
            if (((System.Collections.ICollection)blocks.GetValue(instruction)!).Count != 0)
                throw new InvalidOperationException("wrapper call has an exception boundary");
            var receiver = generator.DeclareLocal(typeof(MeteredTickableComponent));
            var native = generator.DefineLabel();
            var end = generator.DefineLabel();
            var first = New(OpCodes.Stloc, receiver);
            foreach (Label label in (System.Collections.IEnumerable)labels.GetValue(instruction)!) LabelAt(first, label);
            output.Add(first);
            output.Add(New(OpCodes.Volatile));
            output.Add(New(OpCodes.Ldsfld, typeof(TickDispatch).GetField(nameof(_invalidated), RuntimePatches.All)));
            output.Add(New(OpCodes.Brtrue, native));
            var body = Original<T>(method!, generator).ToList();
            // The original callvirt checks null before any user code. Each
            // reviewed wrapper starts with a field read, with the same effect.
            if (body.Count < 2 || Op(body[0]) != OpCodes.Ldarg_0 || Op(body[1]) != OpCodes.Ldfld)
                throw new InvalidOperationException("wrapper no longer starts with a null-checking field read");
            foreach (var item in body)
            {
                if (Op(item) == OpCodes.Ldarg_0) { opcode.SetValue(item, OpCodes.Ldloc); operand.SetValue(item, receiver); }
                else if (Op(item) == OpCodes.Ret) { opcode.SetValue(item, OpCodes.Br); operand.SetValue(item, end); }
                else if (Op(item) == OpCodes.Callvirt && Equals(operand.GetValue(item), _baseEnabled))
                { opcode.SetValue(item, OpCodes.Ldfld); operand.SetValue(item, enabledField); }
                output.Add(item);
            }
            var fallback = New(OpCodes.Ldloc, receiver);
            LabelAt(fallback, native);
            output.Add(fallback);
            output.Add(New(OpCodes.Callvirt, method));
            var finish = New(OpCodes.Nop);
            LabelAt(finish, end);
            output.Add(finish);
        }
        if (enabledSites != 1 || tickSites != 1) throw new InvalidOperationException("unexpected wrapper call sites");
        // Expanding calls can move a branch beyond its signed-byte reach.
        var longBranches = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!).Where(o => o.OperandType == OperandType.InlineBrTarget)
            .ToDictionary(o => o.Name!, o => o);
        foreach (var item in output)
            if (Op(item).OperandType == OperandType.ShortInlineBrTarget)
                opcode.SetValue(item, longBranches[Op(item).Name!.Replace(".s", "")]);
        return output;
    }
}
