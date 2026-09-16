using System;
using System.Linq;
using System.Reflection;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Measurement-only common control. Population speed policy must not be counted
// as CPU throughput. Keep native ScaleSpeed execution, then normalize its result
// for positive requested speeds in all compared builds, including MOD-less runs.
internal static class MatchedBenchmarkConditions
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestMatchedConditions", StringComparer.OrdinalIgnoreCase);
    internal static bool Installed { get; private set; }
    private static float _speed;

    internal static void Install()
    {
        if (!Requested) return;
        if (!FixedTickBenchmark.Requested || TestArguments.Speed is not float speed || float.IsInfinity(speed))
            throw new ArgumentException("Matched conditions require fixed tick benchmark and finite positive test speed");
        _speed = speed;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
        var harmony = Find("HarmonyLib.Harmony");
        var harmonyMethod = Find("HarmonyLib.HarmonyMethod");
        var owner = Activator.CreateInstance(harmony, "t3mp.test.matched-conditions");
        var target = typeof(SpeedManager).GetMethod("ScaleSpeed", flags, null, new[] { typeof(float) }, null)
            ?? throw new MissingMethodException("SpeedManager.ScaleSpeed");
        var postfix = Activator.CreateInstance(harmonyMethod, typeof(MatchedBenchmarkConditions).GetMethod(nameof(NormalizeSpeed), flags));
        harmony.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5)
            .Invoke(owner, new object?[] { target, null, postfix, null, null });
        Installed = true;
        Debug.Log("[T3MPTEST] Matched conditions: positive simulation speed normalized to " + speed + "; VSync=0/cap=-1 set once before timed ticks.");
    }

    private static void NormalizeSpeed(float __0, ref float __result)
    {
        if (__0 > 0f && !float.IsInfinity(__0)) __result = _speed;
    }

    // Called before the timing boundary, after the warmup has allowed legacy
    // mode initialization to finish. No frame setting writes during measurement.
    internal static void PrepareTiming()
    {
        if (!Requested) return;
        if (!Installed || Time.timeScale != _speed) throw new InvalidOperationException("Matched timeScale was not applied");
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
    }

    internal static string LegacyState()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        object? Read(string type, string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(type))
            .FirstOrDefault(t => t != null)?.GetProperty(name, flags)?.GetValue(null);
        var mode = Read("T3MP.BenchmarkModeController", "CurrentMode");
        if (mode == null) return "absent";
        var dispatch = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.TickDispatchOptimizer")).Single(t => t != null)!;
        object? Field(string name) => dispatch.GetField(name, flags)?.GetValue(null);
        return "mode=" + mode + ",blackout=" + Read("T3MP.BenchmarkModeController", "RenderBlackoutActive") +
            ",smooth=" + Read("T3MP.SmoothTimeScaleGovernor", "Enabled") +
            ",tickInitialized=" + Field("_initialized") + ",tickDisabled=" + Field("_disabled") +
            ",flatHooks=" + Field("FlatHooksInstalled") + ",fastTicks=" + (Field("_fastBucketTicks") is long ticks && ticks > 0) +
            ",tickWarnings=" + Field("_warnCount");
    }

    internal static string ProductMvid()
    {
        var products = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetType("T3MP.T3MPModStarter", false) != null).ToArray();
        if (products.Length == 0) return "none";
        if (products.Length != 1) throw new InvalidOperationException("Expected at most one loaded T3MP assembly");
        return products[0].ManifestModule.ModuleVersionId.ToString("D");
    }

    internal static string LegacySettings()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.BenchmarkSettings"))
            .FirstOrDefault(t => t != null);
        if (type == null) return "absent";
        return string.Join(";", type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(bool) && f.Name.StartsWith("Enable", StringComparison.Ordinal) && f.Name != "EnablePreparedStatusIcons")
            .OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + "=" + f.GetValue(null)));
    }
}
