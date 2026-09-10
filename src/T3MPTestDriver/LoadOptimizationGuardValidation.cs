using System;
using System.Linq;
using System.Reflection;
using T3MP.Loading;
using UnityEngine;

namespace T3MPTestDriver;

internal static class LoadOptimizationGuardValidation
{
    private const BindingFlags All = LoadPatchBridge.All;
    private static void Observer() { }
    internal static void Run()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestInitGuardsValidate")) return;
        var ht = LoadPatchBridge.Find("HarmonyLib.Harmony"); var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
        const string owner = "t3mp.test.foreign-init-observer";
        var harmony = Activator.CreateInstance(ht, owner);
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var cases = 0;
        foreach (var row in new[] {
            "TransputLoadRouting|Timberborn.MechanicalSystem.TransputMap|SetTransput|Begin|End",
            "NavigationShapePlans|Timberborn.BlockSystemNavigation.NavMeshObjectUpdater|Update|Begin|End",
            "NavigationShapePlans|Timberborn.BlockSystemNavigation.BlockObjectNavMeshSettingsSpec|get_NoAutoWalls|Begin|End",
            "DeferredGoodStackModels|Timberborn.GoodStackSystem.GoodStackModel|UpdateModel|BeginLoad|EndLoad" })
        {
            var fields = row.Split('|');
            var implementation = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Code").GetType("T3MP.Loading." + fields[0])!;
            if (!(bool)implementation.GetProperty("Installed", All)!.GetValue(null)!) continue;
            var target = LoadPatchBridge.Find(fields[1]).GetMethod(fields[2], All)!;
            var begin = implementation.GetMethod(fields[3], All)!; var end = implementation.GetMethod(fields[4], All)!;
            var compatible = implementation.GetField("_compatible", All)!;
            try
            {
                patch.Invoke(harmony, new object?[] { target,
                    Activator.CreateInstance(hm, typeof(LoadOptimizationGuardValidation).GetMethod(nameof(Observer), All)), null, null, null });
                begin.Invoke(null, null);
                try { if ((bool)compatible.GetValue(null)!) throw new InvalidOperationException("Foreign hook was accepted: " + fields[0]); }
                finally { end.Invoke(null, null); }
            }
            finally { ht.GetMethod("UnpatchAll", All)!.Invoke(harmony, new object[] { owner }); }
            begin.Invoke(null, null);
            try { if (!(bool)compatible.GetValue(null)!) throw new InvalidOperationException("Native path did not recover: " + fields[0]); }
            finally { end.Invoke(null, null); }
            cases++;
        }
        Debug.Log("[T3MPINITGUARD] PASS foreign-hook rejection and recovery cases=" + cases);
    }
}
