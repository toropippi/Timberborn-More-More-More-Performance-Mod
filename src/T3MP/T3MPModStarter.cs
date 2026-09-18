using Timberborn.ModManagerScene;
using UnityEngine;

namespace T3MP;

public sealed class T3MPModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        Debug.Log($"[T3MP] Loaded. Version={ModSettings.Version} ModPath={modEnvironment.ModPath}");
        Runtime.RuntimePatches.Install();
        Loading.LoadPatches.Install();
        Loading.LoadProgress.Install();
        // Last: Harmony compiles methods while patching; a callee inlined into such a caller before its own
        // patch would escape it, so the larger limit starts only after every T3MP patch is in place.
        if (ModSettings.EnableMonoInlineLimit) Runtime.MonoInlineLimit.Install();
    }
}
