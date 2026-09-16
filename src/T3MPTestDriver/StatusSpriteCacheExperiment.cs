using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Per-loader, per-load result cache. The validation mode independently invokes
// the original lookup at every hit and checks exact Unity object identity.
internal static class StatusSpriteCacheExperiment
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [ThreadStatic] private static int _depth, _generation;
    [ThreadStatic] private static bool _checking;
    [ThreadStatic] private static Dictionary<object, Dictionary<string, Sprite>>? _cache;
    [ThreadStatic] private static long _hits, _misses, _checks;
    private static bool _validate;
    private static MethodInfo _load = null!;
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    internal static void Install()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestStatusSpriteCache")) return;
        _validate = Environment.GetCommandLineArgs().Contains("-t3mpTestStatusSpriteCacheValidate");
        var ht = Find("HarmonyLib.Harmony"); var hm = Find("HarmonyLib.HarmonyMethod");
        var harmony = Activator.CreateInstance(ht, "t3mp.test.status-sprite-cache");
        var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        void Patch(Type type, string name, string? prefix, string? postfix = null, string? finalizer = null)
        {
            object? Hook(string? hook) => hook == null ? null : Activator.CreateInstance(hm, typeof(StatusSpriteCacheExperiment).GetMethod(hook, All));
            patch.Invoke(harmony, new object?[] { type.GetMethod(name, All), Hook(prefix), Hook(postfix), null, Hook(finalizer) });
        }
        _load = Find("Timberborn.StatusSystem.StatusSpriteLoader").GetMethod("LoadSprite", All)!;
        Patch(_load.DeclaringType!, _load.Name, nameof(Before), nameof(After));
        Patch(Find("Timberborn.SingletonSystem.SingletonLifecycleService"), "LoadAll", nameof(Begin), finalizer: nameof(End));
        Patch(Find("Timberborn.AssetSystem.AssetLoader"), "Reset", nameof(Reset));
        Debug.Log("[T3MPSTATUSCACHE] installed validate=" + _validate);
    }
    private static void Begin()
    {
        if (_depth++ == 0) { _cache = new Dictionary<object, Dictionary<string, Sprite>>(); _hits = _misses = _checks = 0; }
    }
    private static void End()
    {
        if (--_depth != 0) return;
        Debug.Log($"[T3MPSTATUSCACHE] hits={_hits} misses={_misses} identityChecks={_checks} entries={_cache!.Values.Sum(c => c.Count)}");
        _cache = null;
    }
    private static void Reset() { _cache?.Clear(); _generation++; }
    private static bool Before(object __instance, string spriteName, ref Sprite __result, out int __state)
    {
        __state = -1;
        if (_depth == 0 || _checking || spriteName == null) return true;
        if (!_cache!.TryGetValue(__instance, out var entries))
        {
            entries = new Dictionary<string, Sprite>(StringComparer.Ordinal); _cache.Add(__instance, entries);
            var loader = __instance.GetType().GetField("_assetLoader", All)!.GetValue(__instance)!;
            var providers = loader.GetType().GetField("_assetProviders", All)?.GetValue(loader) as IEnumerable;
            Debug.Log("[T3MPSTATUSCACHE] loader=" + loader.GetType().FullName + " providers=" +
                (providers == null ? "custom" : string.Join(",", providers.Cast<object>().Select(p => p.GetType().FullName))));
        }
        if (entries.TryGetValue(spriteName, out var sprite) && sprite)
        {
            if (_validate)
            {
                _checking = true;
                try
                {
                    var original = _load.Invoke(__instance, new object[] { spriteName });
                    if (!ReferenceEquals(original, sprite)) throw new Exception("Status sprite cache differs: " + spriteName);
                    _checks++;
                }
                finally { _checking = false; }
            }
            _hits++; __result = sprite; return false;
        }
        _misses++; __state = _generation; return true;
    }
    private static void After(object __instance, string spriteName, Sprite __result, int __state)
    {
        if (__state >= 0 && __state == _generation && _depth > 0 && __result && _cache!.TryGetValue(__instance, out var entries))
            entries[spriteName] = __result;
    }
}
