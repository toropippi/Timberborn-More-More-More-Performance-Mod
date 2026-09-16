using System;

namespace T3MP;

// Shipped configuration. Every value is a constant or a feature-off command
// line switch (-t3mpTestNoXxx / -t3mpTestXxxBaseline); nothing here reads game
// state. Diagnostic switches (validation, probes, profilers) belong to the
// test driver, never here: see docs/DIAGNOSTICS.md.
internal static class ModSettings
{
    public const string Version = "1.2.3";

    // Runtime patches: typed EventBus delegates, sparse bucket traversal,
    // reusable walking delegates, de-duplicated water uploads and tube-visit repair.
    public static readonly bool EnableRuntimePatches = !HasCommandLineFlag("-t3mpTestRuntimeBaseline");
    // Per-feature test switches (attribution runs only; omit in normal play).
    public static readonly bool EnableEventBusFastDelegates = !HasCommandLineFlag("-t3mpTestNoEvents");
    public static readonly bool EnableWaterTextureUpload = !HasCommandLineFlag("-t3mpTestNoWater");
    public static readonly bool EnableTickFrontier = !HasCommandLineFlag("-t3mpTestNoFrontier");
    public static readonly bool EnableWalkerSpeedDelegates = !HasCommandLineFlag("-t3mpTestNoWalkerDelegates");
    public static readonly bool EnableTerrainNeighborVisits = !HasCommandLineFlag("-t3mpTestNoTerrainVisits");
#if MOVEMENT_SUBSTEPS
    // Movement sub-steps (v1.2.2): walker position kept in a local across the
    // native 0.1-unit sub-steps; installed together with WalkerSpeedDelegates.
    public static readonly bool EnableMovementSubsteps = !HasCommandLineFlag("-t3mpTestNoMovementSubsteps");
    // Coarse stepping (default): advance corner to corner; native 0.1-unit steps
    // only on a segment that touches the stopping proximity of the path end.
    // Not bit-exact with vanilla; -t3mpTestNoMovementCoarse keeps the exact loop.
    public static readonly bool CoarseMovementSubsteps = !HasCommandLineFlag("-t3mpTestNoMovementCoarse");
#endif
    public static readonly bool EnableTubeVisitFix = !HasCommandLineFlag("-t3mpTestNoTubeFix");
    // Trial (2026-09-15): allowed-good row amounts instead of a repeated string search.
    public static readonly bool EnableAllowedGoodRows = !HasCommandLineFlag("-t3mpTestNoAllowedGoodRows");
    // Trial (2026-09-16): yielder searches skip the path query for candidates that cannot contribute.
    public static readonly bool EnableYielderReachabilitySkip = !HasCommandLineFlag("-t3mpTestNoYielderReachabilitySkip");

    // Load patches (Loading/, Shared/).
    public static readonly bool EnableLoadEventRouting = true;
    public static readonly bool EnablePreparedStatusIcons = !HasCommandLineFlag("-t3mpTestStatusIconBaseline");
    public const float LoadSlowCallThresholdMilliseconds = 250f;

    private static bool HasCommandLineFlag(string flag)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
