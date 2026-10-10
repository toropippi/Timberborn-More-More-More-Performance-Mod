using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Timberborn.CharacterMovementSystem;
using Timberborn.WalkingSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Reuse the callable, never its result. The native movement body and every
// speed-provider invocation remain intact. Weak keys do not retain characters.
internal static partial class WalkerSpeedDelegates
{
    private const string Owner = "t3mp.runtime.walker-delegates";
    private static readonly ConditionalWeakTable<WalkerSpeedManager, Func<float>> Delegates = new();
    private static readonly ConditionalWeakTable<WalkerSpeedManager, Func<float>>.CreateValueCallback Factory = Create;
    private static MethodInfo? _provider;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            _provider = RuntimePatches.Find(typeof(WalkerSpeedManager), "GetWalkerSpeedAtCurrentPosition");
            apply(RuntimePatches.Find(typeof(WalkerMover), "Move"), null, null, nameof(RewriteMove), null);
#if MOVEMENT_SUBSTEPS
            InstallSubsteps(apply);
#endif
        }, typeof(WalkerSpeedDelegates));
        Installed = true;
        Debug.Log("[T3MP] Walker speed delegate reuse installed.");
    }

    private static Func<float> Create(WalkerSpeedManager target) => target.GetWalkerSpeedAtCurrentPosition;

    internal static Func<float> Get(WalkerSpeedManager target) => Delegates.GetValue(target, Factory);

    private static IEnumerable<T> RewriteMove<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        var type = typeof(T);
        var opcode = type.GetField("opcode", RuntimePatches.All)!;
        var operand = type.GetField("operand", RuntimePatches.All)!;
        var constructor = typeof(Func<float>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })!;
        var sites = new List<int>();
        for (var i = 0; i + 1 < list.Count; i++)
            if ((OpCode)opcode.GetValue(list[i])! == OpCodes.Ldftn && Equals(operand.GetValue(list[i]), _provider) &&
                (OpCode)opcode.GetValue(list[i + 1])! == OpCodes.Newobj && Equals(operand.GetValue(list[i + 1]), constructor)) sites.Add(i);
        if (sites.Count != 1) throw new InvalidOperationException("WalkerMover.Move: expected one walker speed delegate constructor, found " + sites.Count);
        var at = sites[0];
        opcode.SetValue(list[at], OpCodes.Call);
        operand.SetValue(list[at], typeof(WalkerSpeedDelegates).GetMethod(nameof(Get), RuntimePatches.All));
        opcode.SetValue(list[at + 1], OpCodes.Nop);
        operand.SetValue(list[at + 1], null);
        return list;
    }
}
