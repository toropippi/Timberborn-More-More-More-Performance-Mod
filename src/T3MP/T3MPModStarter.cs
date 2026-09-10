using Timberborn.ModManagerScene;
using UnityEngine;

namespace T3MP;

public sealed class T3MPModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        Loading.LoadProgress.ObserveCompatibility();
        Debug.Log($"[T3MP] Loaded. Version={ModSettings.Version} ModPath={modEnvironment.ModPath}");
        Runtime.RuntimePatches.Install();
        Loading.LoadPatches.Install();
        Loading.LoadProgress.Install();
    }
}
