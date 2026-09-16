using System.Reflection;
using HarmonyLib;

internal static class NativeExecution
{
    private static bool SuppressUnityLog() => false;

    internal static void Verify(string codePath, string managed)
    {
        var path = Path.GetFullPath(Path.Combine(managed, "Timberborn.Navigation.dll"));
        var a = new Harness(managed, path);
        var b = new Harness(managed, path);
        var code = b.LoadCode(codePath);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        // Suppress only Unity logging in this offline process. All production
        // fingerprints, installation, Harmony guards and terrain calls are real.
        var core = b.LoadCode(Path.Combine(managed, "UnityEngine.CoreModule.dll"));
        var logging = new Harmony("terrain.test.native-log");
        foreach (var name in new[] { "Log", "LogWarning", "LogError" })
            logging.Patch(core.GetType("UnityEngine.Debug", true)!.GetMethod(name, new[] { typeof(object) }),
                prefix: new HarmonyMethod(typeof(NativeExecution).GetMethod(nameof(SuppressUnityLog), flags)));
        var feature = code.GetType("T3MP.Runtime.TerrainNeighborVisits", true)!;
        feature.GetMethod("Install", flags)!.Invoke(null, new object[] {
            typeof(Harmony), typeof(HarmonyMethod), typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5)
        });
        if (!Equals(feature.GetProperty("Active", flags)!.GetValue(null), true)) throw new Exception("Native fingerprint/install rejected");
        var random = new Random(613561);
        var costs = new[] { -1f, 0f, 0.5f, 1f, 2f, 5f, float.NaN, float.PositiveInfinity };
        var cases = 0;
        void Compare(int start, int range) {
            if (a.Run(start, range) != b.Run(start, range)) throw new Exception("Native execution mismatch: " + cases);
            cases++;
        }
        for (var graph = 0; graph < 500; graph++)
        {
            var count = random.Next(1, 80);
            var edges = Enumerable.Range(0, count).Select(_ => Enumerable.Range(0, random.Next(8))
                .Select(_ => new Edge(random.Next(count), costs[random.Next(costs.Length)])).ToArray()).ToArray();
            var restricted = Enumerable.Range(0, count).Select(_ => random.Next(4) == 0).ToArray();
            a.SetGraph(edges, restricted); b.SetGraph(edges, restricted);
            for (var j = 0; j < 10; j++) Compare(random.Next(count), random.Next(-1, 8));
        }
        feature.GetMethod("Revalidate", flags)!.Invoke(null, null);
        if (!Equals(feature.GetProperty("Active", flags)!.GetValue(null), true)) throw new Exception("Unchanged native load invalidated feature");
        Console.WriteLine($"PASS native execution: {cases} ordered result/exception/remaining-state comparisons using real game assemblies, actual production installer/helper and Harmony; CoreCLR only.");
    }
}
