using System;
using System.Linq;
using System.Reflection;
using T3MP.Loading;
using UnityEngine;

namespace T3MPTestDriver;

internal static class VisualPreparationExperiment
{
    internal static bool Enabled => PreparedEntityVisuals.Enabled;
    internal static void Begin() => PreparedEntityVisuals.Begin();
    internal static void End() => PreparedEntityVisuals.End();
    internal static void Prepare(GameObject root, object layout) => PreparedEntityVisuals.Prepare(root, layout);
    internal static void ObserveMainModule()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Where(a => a != typeof(PreparedEntityVisuals).Assembly)
            .Select(a => a.GetType("T3MP.Loading.PreparedEntityVisuals")).FirstOrDefault(t => t != null);
        if (type != null && (bool)type.GetProperty("Installed", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            PreparedEntityVisuals.Natural = PreparedEntityVisuals.Building = false;
    }
}
