using Timberborn.ModManagerScene;
using UnityEngine;

namespace T3MP;

public sealed class T3MPModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        Debug.Log($"[T3MP] Loaded. ModPath={modEnvironment.ModPath}");
        if (BenchmarkSettings.EnableBenchmark)
        {
            BenchmarkProbe.TryInstall();
            if (BenchmarkSettings.EnableBenchmarkController)
            {
                BenchmarkModeController.Install();
            }
        }
    }
}
