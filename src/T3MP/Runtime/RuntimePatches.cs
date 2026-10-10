using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using T3MP.Loading;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Runtime patches: the game-speed core of T3MP. They are always applied; there
// is no build check and no fallback. A patch that cannot be applied throws out
// of StartMod, which the game reports on its crash screen with this message.
internal static class RuntimePatches
{
    internal const BindingFlags All = LoadPatchBridge.All;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        var harmonyType = LoadPatchBridge.Find("HarmonyLib.Harmony");
        var harmonyMethodType = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        RequestedSpeedPolicy.Install(harmonyType, harmonyMethodType, patch);
        if (!ModSettings.EnableRuntimePatches)
        {
            Debug.Log("[T3MP] Runtime optimizations skipped (-t3mpTestRuntimeBaseline); requested speed policy retained.");
            return;
        }
        if (ModSettings.EnableEventBusFastDelegates) EventBusFastDelegates.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableWaterTextureUpload) WaterTextureUpload.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableTickFrontier) TickFrontier.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableWalkerSpeedDelegates) WalkerSpeedDelegates.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableTerrainNeighborVisits) TerrainNeighborVisits.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableTubeVisitFix) TubeVisitFix.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableAllowedGoodRows) AllowedGoodRows.Install(harmonyType, harmonyMethodType, patch);
        if (ModSettings.EnableYielderReachabilitySkip) YielderReachabilitySkip.Install(harmonyType, harmonyMethodType, patch);
    }

    // Applies one feature's hooks under its own Harmony id. Any failure is
    // rethrown with the feature name so the crash screen names T3MP.
    internal static void Apply(string owner, Type harmonyType, Type harmonyMethodType, MethodInfo patch,
        Action<Func<MethodBase, string?, string?, string?, string?, object?>> body, Type hooks)
    {
        try
        {
            var harmony = Activator.CreateInstance(harmonyType, owner)!;
            object? Hook(string? name, bool transpiler)
            {
                if (name == null) return null;
                var method = hooks.GetMethod(name, All) ?? throw new MissingMethodException(hooks.FullName, name);
                if (transpiler) method = LoadPatchBridge.Create(owner + "." + name, method);
                return Activator.CreateInstance(harmonyMethodType, method)!;
            }
            body((target, prefix, postfix, transpiler, finalizer) =>
                patch.Invoke(harmony, new[] { (object)target, Hook(prefix, false), Hook(postfix, false), Hook(transpiler, true), Hook(finalizer, false) }));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("T3MP: " + owner + " could not be applied: " + exception.GetBaseException().Message, exception);
        }
    }

    // Without parameter types the name must be unique; with them (including
    // Type.EmptyTypes) the exact overload is taken.
    internal static MethodInfo Find(Type type, string name, Type[]? parameters = null) =>
        (parameters == null
            ? type.GetMethod(name, All | BindingFlags.DeclaredOnly)
            : type.GetMethod(name, All | BindingFlags.DeclaredOnly, null, parameters, null))
        ?? throw new MissingMethodException(type.FullName, name);

    // Transpiler body: replace the whole original body with a call to a static
    // method that takes the original arguments in order, then return.
    internal static IEnumerable<T> CallAndReturn<T>(MethodInfo target, int argumentCount)
    {
        var loads = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };
        // Arguments beyond the four short forms use ldarg.s with a byte index.
        for (var i = 0; i < argumentCount; i++) yield return i < loads.Length ? Instruction<T>(loads[i], null) : Instruction<T>(OpCodes.Ldarg_S, (byte)i);
        yield return Instruction<T>(OpCodes.Call, target);
        yield return Instruction<T>(OpCodes.Ret, null);
    }

    private static T Instruction<T>(OpCode opcode, object? operand) =>
        (T)Activator.CreateInstance(typeof(T), opcode, operand)!;
}
