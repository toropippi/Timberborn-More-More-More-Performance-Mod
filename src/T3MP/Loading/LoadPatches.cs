using System;
using System.Linq;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Save-load optimizations. Every feature verifies the game modules it relies on
// (LoadCompatibility) and falls back to the native code path on any mismatch or
// foreign patch. Nothing here runs outside SingletonLifecycleService.LoadAll /
// EventBus.PostLoad scopes except the deferred empty GoodStack models, which are
// created by the native factory on first access.
internal static class LoadPatches
{
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            var harmonyType = LoadPatchBridge.Find("HarmonyLib.Harmony");
            var harmonyMethodType = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            LoadEventRouter.Install(harmonyType, harmonyMethodType, patch);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Load event routing unavailable: " + exception.GetBaseException().Message);
        }
        if (ModSettings.EnablePreparedStatusIcons) PreparedStatusIcons.Install("t3mp.load.status-icons");
        PreparedEntityVisuals.Install("t3mp.load.entity-visuals");
        TransputLoadRouting.Install("t3mp.load.transput-routing", production: true);
        NavigationShapePlans.Install("t3mp.load.navigation-shapes", production: true);
        DeferredGoodStackModels.Install("t3mp.load.good-stack-models", production: true);
        LoadGcBudget.Install();
        ConstructionPlans.Install();
        ComponentConstructionPlans.Install();
        InitialNavigationLoad.Install();
        BlockLoadRouting.Install();
        EmptyFlowFieldLoad.Install();
        TerrainSurfaceLoad.Install();
        AtlasPixelsLoad.Install();
    }
}
