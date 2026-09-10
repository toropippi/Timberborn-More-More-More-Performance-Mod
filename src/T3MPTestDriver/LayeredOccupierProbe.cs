using System;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockObstacles;
using T3MP.Loading;
using UnityEngine;

namespace T3MPTestDriver;

internal static class LayeredOccupierProbe
{
    private static bool _reported;
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestLayeredOccupierProbe")) return;
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony"); var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        var h = Activator.CreateInstance(ht, "t3mp.test.layered-occupier-probe");
        ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5).Invoke(h,
            new object?[] { typeof(BlockOccupationLayerFactory).GetMethod("Create"), null,
                Activator.CreateInstance(hm, typeof(LayeredOccupierProbe).GetMethod(nameof(After), LoadPatchBridge.All)), null, null });
    }
    private static void After(BlockOccupationLayer __result)
    {
        if (_reported || __result._blockOccupiers.Count == 0) return;
        _reported = true;
        var root = __result._blockOccupiers[0].GameObject;
        Debug.Log("[T3MPLAYERPROBE] components=" + string.Join(",", root.GetComponent<ComponentCache>().AllComponents.Select(c => c.GetType().FullName)));
        Debug.Log("[T3MPLAYERPROBE] unity=" + string.Join(",", root.GetComponentsInChildren<Component>(true).Select(c => c.GetType().FullName)));
    }
}
