using System;

namespace T3MP;

// Shipped configuration. Every value is a constant or a test-only command line
// flag; nothing here reads game state.
internal static class ModSettings
{
    public const string Version = "1.2.1";

    // Runtime patches: typed EventBus delegates, sparse bucket traversal,
    // de-duplicated water uploads and visual tube-visit repair. See Runtime/.
    public static readonly bool EnableRuntimePatches = !HasCommandLineFlag("-t3mpTestRuntimeBaseline");
    // Per-feature test switches (attribution runs only; omit in normal play).
    public static readonly bool EnableEventBusFastDelegates = !HasCommandLineFlag("-t3mpTestNoEvents");
    public static readonly bool EnableWaterTextureUpload = !HasCommandLineFlag("-t3mpTestNoWater");
    public static readonly bool EnableTickFrontier = !HasCommandLineFlag("-t3mpTestNoFrontier");
    public static readonly bool EnableTubeVisitFix = !HasCommandLineFlag("-t3mpTestNoTubeFix");

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
