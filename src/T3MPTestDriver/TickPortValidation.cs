using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace T3MPTestDriver;

internal static class TickPortValidation
{
    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestTickPortValidate");
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int _foreignCalls;
    private static int _getterCalls;
    private static void ForeignTick() => _foreignCalls++;
    private static void ForeignGetter() => _getterCalls++;
    internal static void Run()
    {
        for (var scenario = 0; scenario < 8; scenario++)
        {
            var native = RunCase(scenario, false);
            var port = RunCase(scenario, true);
            if (native != port) throw new InvalidOperationException($"Tick port scenario {scenario}: native={native}; port={port}");
        }
        Debug.Log("[T3MPTEST] TickPortValidation PASS 8 integrated bucket scenarios; native Entity.Tick reference; actual Unity objects.");
        RunCase(8, true);
        if (_foreignCalls != 1) throw new InvalidOperationException("Late entity hook was hidden by Frontier: " + _foreignCalls);
        if (_getterCalls != 3) throw new InvalidOperationException("Getter calls after fallback differ from native: " + _getterCalls);
        Debug.Log("[T3MPTEST] TickPortValidation PASS late entity hook observes the all-disabled entity; dispatch invalidated.");
    }
    private static string RunCase(int scenario, bool port)
    {
        var trace = new List<string>();
        var objects = new List<GameObject>();
        var bucket = new TickableEntityBucket();
        var cacheType = typeof(BaseComponent).Assembly.GetType("Timberborn.BaseComponentSystem.ComponentCache")!;
        var initialize = typeof(BaseComponent).GetMethod("Initialize", All)!;
        var setEnabled = typeof(BaseComponent).GetProperty("Enabled", All)!.GetSetMethod(true)!;
        var once = false;
        TickableEntity Make(int key, params PortFixtureComponent[] components)
        {
            var go = new GameObject("T3MPTEST.TickPort." + key); objects.Add(go);
            var cache = go.AddComponent(cacheType);
            cacheType.GetProperty("CachedGameObject", All)!.SetValue(cache, go);
            var owner = new EntityComponent(null!, null!);
            typeof(EntityComponent).GetProperty("EntityId", All)!.SetValue(owner, new Guid(key, 0, 0, new byte[8]));
            initialize.Invoke(owner, new object[] { cache });
            foreach (var component in components) initialize.Invoke(component, new object[] { cache });
            return new TickableEntity(owner, components.Select(c => new MeteredTickableComponent(c, null!, false)), go.name);
        }
        void Tick()
        {
            if (port) bucket.TickAll();
            else
            {
                // Statement-for-statement native TickAll control. Entity.Tick
                // itself is the actual game method, not a simulated component loop.
                bucket._isTicking = true;
                for (var i = 0; i < bucket._tickableEntities.Count; i++) bucket._tickableEntities.Values[i].Tick();
                bucket._isTicking = false;
                for (var i = 0; i < bucket._entitiesToRemove.Count; i++) bucket._tickableEntities.Remove(bucket._entitiesToRemove[i].EntityId);
                bucket._entitiesToRemove.Clear();
            }
        }
        var later = new PortFixtureComponent { Action = () => trace.Add("later") };
        var tail = Make(30, later);
        if (scenario is 2 or 8) setEnabled.Invoke(later, new object[] { false });
        object? foreign = null;
        Type? harmonyType = null;
        var next = new PortFixtureComponent { Action = () => trace.Add("next") };
        PortFixtureComponent? first = null;
        first = new PortFixtureComponent { Action = () => {
            trace.Add("first");
            if (once) return; once = true;
            switch (scenario)
            {
                case 0: next.DisableComponent(); break;
                case 1: objects[0].SetActive(false); break;
                case 2: setEnabled.Invoke(later, new object[] { true }); break;
                case 3: bucket.Add(Make(5, new PortFixtureComponent { Action = () => trace.Add("inserted") })); break;
                case 4: bucket.Remove(tail); break;
                case 5: Tick(); bucket.Remove(tail); break;
                case 6: throw new ApplicationException("fixture tick failure");
                case 7: objects[1].SetActive(false); break;
                case 8:
                    harmonyType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("HarmonyLib.Harmony")).First(t => t != null)!;
                    var harmonyMethod = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod")!;
                    foreign = Activator.CreateInstance(harmonyType, "t3mp.test.tick-port-late");
                    var prefix = Activator.CreateInstance(harmonyMethod, typeof(TickPortValidation).GetMethod(nameof(ForeignTick), All));
                    var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
                    patch.Invoke(foreign, new object?[] { typeof(TickableEntity).GetMethod("Tick"), prefix, null, null, null });
                    var getterPrefix = Activator.CreateInstance(harmonyMethod, typeof(TickPortValidation).GetMethod(nameof(ForeignGetter), All));
                    patch.Invoke(foreign, new object?[] { typeof(BaseComponent).GetProperty("Enabled")!.GetGetMethod(), getterPrefix, null, null, null });
                    first!.DisableComponent();
                    break;
            }
        } };
        try
        {
            bucket.Add(Make(10, first, next)); bucket.Add(tail);
            for (var run = 0; run < (scenario == 8 ? 1 : 2); run++)
            {
                try { Tick(); trace.Add("ok"); }
                catch (Exception e) { trace.Add(e.GetType().Name + ":" + e.Message + ":" + e.InnerException?.Message); }
            }
            return string.Join(",", trace) + "|" + bucket._isTicking + "|" + bucket._tickableEntities.Count + "|" + bucket._entitiesToRemove.Count;
        }
        finally
        {
            if (foreign != null) harmonyType!.GetMethod("UnpatchAll", All)!.Invoke(foreign, new object[] { "t3mp.test.tick-port-late" });
            foreach (var go in objects) UnityEngine.Object.DestroyImmediate(go);
        }
    }
}

internal sealed class PortFixtureComponent : TickableComponent
{
    internal Action Action = null!;
    public override void Tick() => Action();
}
