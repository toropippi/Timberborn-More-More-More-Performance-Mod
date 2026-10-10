using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace T3MP.Loading;

internal static class LoadCompatibility
{
    private static readonly Dictionary<LoadBodies.Set, bool> Results = new Dictionary<LoadBodies.Set, bool>();

    // True when the game bodies a feature relies on are a reviewed version
    // (LoadBodies). Evaluated once per process; a mismatch is logged once.
    internal static bool Reviewed(LoadBodies.Set set)
    {
        lock (Results) return Results.TryGetValue(set, out var cached) ? cached : Results[set] = Evaluate(set);
    }

    private static bool Evaluate(LoadBodies.Set set)
    {
        var missing = new List<string>();
        string print;
        try { print = LoadBodies.Fingerprint(set, FindType, missing); }
        catch (Exception exception) { print = "error " + exception.GetBaseException().Message; }
        var reviewed = Array.IndexOf(set.Accepted, print) >= 0;
        if (!reviewed)
            UnityEngine.Debug.Log("[T3MPLOADCOMPAT] " + set.Name + " native fallback: game bodies " + print +
                (missing.Count == 0 ? "" : " missing " + string.Join(", ", missing)));
        return reviewed;
    }

    private static Type? FindType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(name, false);
            if (type != null) return type;
        }
        return null;
    }

    // Frontier's reviewed postfix only invalidates its own tracked tick groups
    // when a cache is initialized again. It does not change components, models,
    // activation or the native Initialize call. Keep this exemption method-local.
    internal static bool ReviewedCacheObserver(MethodBase method, string owner) =>
        owner == "t3mp.runtime.frontier" &&
        method.DeclaringType == typeof(Timberborn.BaseComponentSystem.ComponentCache) &&
        method.Name == "Initialize";

    internal static bool Unmodified(IEnumerable<MethodBase> methods, string owner, Func<MethodBase, string, bool>? reviewed = null)
    {
        var getInfo = LoadPatchBridge.Find("HarmonyLib.Harmony").GetMethod("GetPatchInfo", LoadPatchBridge.All)!;
        foreach (var method in methods)
        {
            var info = getInfo.Invoke(null, new object[] { method });
            if (info == null) continue;
            var owners = (IEnumerable<string>)info.GetType().GetProperty("Owners", LoadPatchBridge.All)!.GetValue(info)!;
            var foreign = owners.FirstOrDefault(o => o != owner && !(reviewed?.Invoke(method, o) ?? false));
            if (foreign != null)
            {
                UnityEngine.Debug.Log("[T3MPLOADCOMPAT] fallback " + method.DeclaringType?.FullName + "." + method.Name + " owner=" + foreign);
                return false;
            }
        }
        return true;
    }

    internal static bool MainInstalled(Type localType)
    {
        var main = AppDomain.CurrentDomain.GetAssemblies().Where(a => a != localType.Assembly)
            .Select(a => a.GetType(localType.FullName!)).FirstOrDefault(t => t != null);
        return main != null && (bool)(main.GetProperty("Installed", LoadPatchBridge.All)?.GetValue(null) ?? false);
    }
}
