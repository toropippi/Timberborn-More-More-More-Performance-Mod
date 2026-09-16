using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only call counting for candidate hot spots that the tick profiler
// does not time: how often a few inner methods run per window and how large the
// collections they scan are. Postfix counters only; never a benchmark figure.
// Enabled by -t3mpTestHotCounts.
internal static class HotCountProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Target
    {
        internal string Type = "", Method = "", SizeField = "";
        internal int Parameters = -1;
        internal long Calls, SizeSum;
        internal FieldInfo? Size;
    }
    private static readonly Target[] Targets =
    {
        new Target { Type = "Timberborn.RangedEffectSystem.RangedEffect", Method = "GetEfficiency", SizeField = "_appliers" },
        new Target { Type = "Timberborn.RangedEffectSystem.RangedEffect", Method = "ToContinuousEffect" },
        new Target { Type = "Timberborn.RangedEffectSystem.RangedEffectSubject", Method = "ApplyEffects" },
        new Target { Type = "Timberborn.Goods.StorableGoodRegistry", Method = "GetAmount", SizeField = "_storableGoods" },
        new Target { Type = "Timberborn.InventorySystem.InventoryFillCalculator", Method = "GetInventoryFillPercentage" },
        new Target { Type = "Timberborn.InventorySystem.Inventory", Method = "GetCapacity", Parameters = 1 },
        new Target { Type = "Timberborn.ModularShafts.ModularShaftAnimator", Method = "UpdateAnimation", SizeField = "_animators" },
        new Target { Type = "Timberborn.MechanicalSystem.MechanicalNode", Method = "get_PowerEfficiency" },
        new Target { Type = "Timberborn.MechanicalSystem.MechanicalGraph", Method = "get_PowerEfficiency" },
        new Target { Type = "Timberborn.NeedSystem.NeedManager", Method = "GetNeed" },
        new Target { Type = "Timberborn.NeedBehaviorSystem.Appraiser", Method = "AppraiseEffects" },
    };
    private static readonly Dictionary<MethodBase, Target> ByMethod = new Dictionary<MethodBase, Target>();
    private static bool _installed;
    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestHotCounts", StringComparison.OrdinalIgnoreCase));

    internal static void Install()
    {
        if (_installed || !Requested) return;
        _installed = true;
        try
        {
            Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            var harmonyType = Find("HarmonyLib.Harmony")!;
            var methodType = Find("HarmonyLib.HarmonyMethod")!;
            var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.hotcounts")!;
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            var postfix = Activator.CreateInstance(methodType, typeof(HotCountProbe).GetMethod(nameof(After), All))!;
            foreach (var target in Targets)
            {
                var type = Find(target.Type);
                if (type == null) { Debug.Log("[T3MPHOT] missing type " + target.Type); continue; }
                var methods = type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == target.Method && (target.Parameters < 0 || m.GetParameters().Length == target.Parameters)).ToArray();
                if (methods.Length == 0) { Debug.Log("[T3MPHOT] missing method " + target.Type + "." + target.Method); continue; }
                if (target.SizeField.Length > 0) target.Size = type.GetField(target.SizeField, All);
                foreach (var method in methods)
                {
                    patch.Invoke(harmony, new object?[] { method, null, postfix, null, null });
                    ByMethod[method] = target;
                }
            }
            Debug.Log("[T3MPHOT] installed targets=" + ByMethod.Count);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPHOT] unavailable: " + exception.GetBaseException().Message);
        }
    }

    private static void After(MethodBase __originalMethod, object? __instance)
    {
        if (!ByMethod.TryGetValue(__originalMethod, out var target)) return;
        target.Calls++;
        if (target.Size != null && __instance != null && target.Size.GetValue(__instance) is ICollection collection) target.SizeSum += collection.Count;
    }

    internal static void Report(double windowSeconds)
    {
        if (!_installed) return;
        var culture = CultureInfo.InvariantCulture;
        Debug.Log(string.Format(culture, "[T3MPHOT] window={0:F1}s", windowSeconds));
        foreach (var target in Targets)
        {
            Debug.Log(string.Format(culture, "[T3MPHOT] {0}.{1} calls={2} avgSize={3:F2}", target.Type, target.Method, target.Calls,
                target.SizeField.Length > 0 ? target.SizeSum / (double)Math.Max(1, target.Calls) : 0.0));
            target.Calls = 0; target.SizeSum = 0;
        }
    }
}
