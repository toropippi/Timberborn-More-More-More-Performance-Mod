using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace T3MPTestDriver;

// Used only by the release scenario gate, before the game scene starts.
internal static class ReleaseIdentity
{
    internal static void ReportIfRequested()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestReleaseRun")) return;
        try
        {
            var products = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetType("T3MP.T3MPModStarter", false) != null).ToArray();
            if (products.Length != 1) throw new InvalidOperationException("Expected one loaded T3MP assembly");
            Report("product", products[0]);
            Report("driver", typeof(TestDriverModStarter).Assembly);
        }
        catch (Exception exception)
        {
            Debug.LogError("[T3MPTEST] ERROR: release assembly identity: " + exception);
        }
    }

    private static void Report(string kind, Assembly assembly)
    {
        // Timberborn loads mod byte arrays, so Location can be empty. Match
        // the loaded module's MVID to the frozen, SHA256-verified PE file.
        Debug.Log("[T3MPTEST] Release identity " + JsonUtility.ToJson(new Identity
        { kind = kind, location = assembly.Location, mvid = assembly.ManifestModule.ModuleVersionId.ToString("D") }));
    }

    [Serializable]
    private sealed class Identity
    {
        public string kind = "";
        public string location = "";
        public string mvid = "";
    }
}
