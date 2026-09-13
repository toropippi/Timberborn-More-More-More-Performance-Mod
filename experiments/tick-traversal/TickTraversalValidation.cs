using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace T3MPTestDriver;

internal static class TickTraversalValidation
{
    private const string Owner = "t3mp.test.tick-traversal";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly List<string> Trace = new();
    private static readonly Dictionary<MeteredTickableComponent, string> Names = new();
    private static int _reads;
    private static int _fastCalls;
    private static string _case = "";
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestTickTraversal");
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;

    internal static void Run()
    {
        var harmonyType = Find("HarmonyLib.Harmony");
        var hm = Find("HarmonyLib.HarmonyMethod");
        var getterOwner = Owner + ".getter";
        var nativeOwner = Owner + ".native";
        var getterHarmony = Activator.CreateInstance(harmonyType, getterOwner)!;
        var nativeHarmony = Activator.CreateInstance(harmonyType, nativeOwner)!;
        var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var unpatch = harmonyType.GetMethod("UnpatchAll", All)!;
        object Hook(string name) => Activator.CreateInstance(hm, typeof(TickTraversalValidation).GetMethod(name, All))!;
        var failures = new List<Exception>();
        try
        {
            if (!(bool)Find("T3MP.Runtime.TickEntityFast").GetProperty("Installed", All)!.GetValue(null)!)
                throw new InvalidOperationException("Tick traversal must be installed");
            patch.Invoke(getterHarmony, new object?[] { typeof(MeteredTickableComponent).GetProperty("Enabled")!.GetGetMethod()!, Hook(nameof(Getter)), null, null, null });
            patch.Invoke(getterHarmony, new object?[] { Find("T3MP.Runtime.TickEntityFast").GetMethod("TickFast", All)!, Hook(nameof(FastEntered)), null, null, null });
            var bridge = T3MP.Loading.LoadPatchBridge.Create(nativeOwner, typeof(TickTraversalValidation).GetMethod(nameof(NativeBody), All)!);
            patch.Invoke(nativeHarmony, new object?[] { typeof(TickableEntity).GetMethod("Tick")!, null, null, Activator.CreateInstance(hm, bridge), null });
            // Getter coexistence must not rely on Frontier suppressing calls.
            var frontier = Find("T3MP.Runtime.TickFrontier");
            frontier.GetMethod("Revalidate", All)!.Invoke(null, null);
            if ((bool)frontier.GetProperty("Installed", All)!.GetValue(null)!) throw new InvalidOperationException("Frontier did not yield to foreign getter");
            _fastCalls = 0;
            var native = Cases();
            if (_fastCalls != 0) throw new InvalidOperationException("Native fixture entered TickFast");
            var expected = new[] {
                "mixed:get:A,get:B,tick:B", "inactive:", "changing:get:A,tick:A",
                "throwOnce:get:A,error:System.Exception:Exception thrown while ticking entity 00000000-0000-0000-0000-000000000000 'T3MP tick traversal fixture'->System.InvalidOperationException:tick-getter-sentinel",
                "disableNext:get:A,tick:A,get:B"
            };
            if (!native.SequenceEqual(expected)) throw new InvalidOperationException("Native fixture or getter interception failed: " + string.Join("|", native));
            Debug.Log("[T3MPTEST] Tick traversal native baseline verified cases=5 fastCalls=0");
            unpatch.Invoke(nativeHarmony, new object[] { nativeOwner });
            _fastCalls = 0;
            var candidate = Cases();
            if (_fastCalls != 5) throw new InvalidOperationException("Candidate fixture did not enter TickFast five times");
            for (var i = 0; i < native.Length; i++) Debug.Log("[T3MPTEST] Tick traversal case native=" + native[i] + " candidate=" + candidate[i]);
            if (!candidate.SequenceEqual(native)) throw new InvalidOperationException("Candidate getter order/tick/exception differs from native");
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            foreach (var item in new[] { (nativeHarmony, nativeOwner), (getterHarmony, getterOwner) })
                try { unpatch.Invoke(item.Item1, new object[] { item.Item2 }); }
                catch (Exception exception)
                {
                    Debug.LogError("[T3MPTEST] Tick traversal cleanup FAIL owner=" + item.Item2);
                    failures.Add(exception);
                }
            Names.Clear();
        }
        if (failures.Count != 0)
        {
            var failure = new AggregateException("Tick traversal diagnostic failed", failures);
            Debug.LogError("[T3MPTEST] Tick traversal validation FAIL: " + failure);
            throw failure;
        }
        Debug.Log("[T3MPTEST] Tick traversal validation PASS: cases=5 native getter interception, order, inactive entity, changing return, exception chain, live enable change; cleanup completed");
    }
    private static IEnumerable<T> NativeBody<T>(IEnumerable<T> instructions)
    {
        yield return (T)Activator.CreateInstance(typeof(T), new object?[] { OpCodes.Nop, null })!;
        foreach (var instruction in instructions) yield return instruction;
    }
    private static void FastEntered() => _fastCalls++;
    private static bool Getter(MeteredTickableComponent __instance, ref bool __result)
    {
        if (!Names.TryGetValue(__instance, out var name)) return true;
        Trace.Add("get:" + name);
        _reads++;
        if (_case == "throwOnce" && _reads == 1) throw new InvalidOperationException("tick-getter-sentinel");
        if (_case != "changing") return true;
        __result = _reads == 1;
        return false;
    }
    private static string[] Cases()
    {
        var results = new List<string>();
        foreach (var name in new[] { "mixed", "inactive", "changing", "throwOnce", "disableNext" })
        {
            var go = new GameObject("T3MP tick traversal fixture");
            try
            {
                var cache = go.AddComponent<ComponentCache>();
                typeof(ComponentCache).GetProperty("CachedGameObject", All)!.SetValue(cache, go);
                var entity = new EntityComponent(null!, null!);
                typeof(BaseComponent).GetMethod("Initialize", All)!.Invoke(entity, new object[] { cache });
                var a = new Fixture("A");
                var b = new Fixture("B");
                var enabled = typeof(BaseComponent).GetProperty("Enabled", All)!;
                // These are private diagnostic objects, never world components.
                if (name == "mixed") enabled.SetValue(a, false);
                if (name == "disableNext") a.Action = () => enabled.SetValue(b, false);
                MeteredTickableComponent Wrap(Fixture component)
                {
                    var metered = (MeteredTickableComponent)Activator.CreateInstance(typeof(MeteredTickableComponent), new object?[] { component, null, false })!;
                    Names.Add(metered, component.Id);
                    return metered;
                }
                var components = name == "mixed" || name == "disableNext" ? new[] { Wrap(a), Wrap(b) } : new[] { Wrap(a) };
                var tickable = new TickableEntity(entity, components, go.name);
                if (name == "inactive") go.SetActive(false);
                Trace.Clear(); _reads = 0; _case = name;
                try { tickable.Tick(); }
                catch (Exception exception) { Trace.Add("error:" + ErrorChain(exception)); }
                results.Add(name + ":" + string.Join(",", Trace));
            }
            finally { Names.Clear(); UnityEngine.Object.DestroyImmediate(go); }
        }
        return results.ToArray();
    }
    private static string ErrorChain(Exception exception) => exception.GetType().FullName + ":" + exception.Message +
        (exception.InnerException == null ? "" : "->" + ErrorChain(exception.InnerException));
    private sealed class Fixture : TickableComponent
    {
        internal readonly string Id;
        internal Action? Action;
        internal Fixture(string id) { Id = id; }
        public override void Tick() { Trace.Add("tick:" + Id); Action?.Invoke(); }
    }
}
