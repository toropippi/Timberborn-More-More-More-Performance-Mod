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

    // Runtime guards are re-checked at the start of every world load, after
    // every other mod has had the chance to install its hooks.
    private static void InstallRevalidation(Type harmonyType, Type harmonyMethodType, System.Reflection.MethodInfo patch)
    {
        var target = LoadPatchBridge.Find("Timberborn.SingletonSystem.SingletonLifecycleService").GetMethod("LoadAll", LoadPatchBridge.All);
        var prefix = Activator.CreateInstance(harmonyMethodType, typeof(LoadPatches).GetMethod(nameof(BeforeLoadAll), LoadPatchBridge.All));
        var harmony = Activator.CreateInstance(harmonyType, "t3mp.runtime.guard")!;
        patch.Invoke(harmony, new object?[] { target, prefix, null, null, null });
    }

    private static void BeforeLoadAll()
    {
        Runtime.WaterTextureUpload.Revalidate();
        Runtime.TickFrontier.Revalidate();
        Runtime.TubeVisitFix.Revalidate();
    }

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
            InstallRevalidation(harmonyType, harmonyMethodType, patch);
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
