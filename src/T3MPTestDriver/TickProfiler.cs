using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Timberborn.TickSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only attribution: wall time per tickable component type and per
// tickable singleton, plus exclusive/inclusive time of every Behavior.Decide
// override, every IExecutor.Tick implementation and a list of hot methods.
// Adds timestamps to every timed call, so the ticks/s of a profiled run is not
// a benchmark figure. Enabled by -t3mpTestTickProfile.
internal static class TickProfiler
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Bucket { internal long Ticks; internal long Calls; internal long Inclusive; }
    private struct Frame { internal MethodBase Method; internal long Start; internal long Child; }
    private static readonly Dictionary<Type, Bucket> Components = new Dictionary<Type, Bucket>();
    private static readonly Dictionary<Type, Bucket> Singletons = new Dictionary<Type, Bucket>();
    private static readonly Dictionary<MethodBase, Bucket> Methods = new Dictionary<MethodBase, Bucket>();
    private static readonly Stack<Frame> Frames = new Stack<Frame>();
    private static long _bucketServiceTicks, _bucketServiceCalls, _parallelWaitTicks;
    private static bool _installed;
    private static int _timedMethods;
    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestTickProfile", StringComparison.OrdinalIgnoreCase));

    private static readonly (string Type, string Method)[] HotMethods =
    {
        ("Timberborn.CharacterMovementSystem.PathFollower", "MoveAlongPath"),
        ("Timberborn.CharacterMovementSystem.PathFollower", "ReachedLastPathCorner"),
        ("Timberborn.CharacterMovementSystem.PathFollower", "NotifyAfterMovement"),
        ("Timberborn.CharacterMovementSystem.MovementAnimator", "AnimateMovementAlongPath"),
        ("Timberborn.WalkingSystem.WalkerSpeedManager", "GetWalkerSpeedAtCurrentPosition"),
        ("Timberborn.WalkingSystem.Walker", "CalculateTravelTimeInHours"),
        ("Timberborn.WalkingSystem.Walker", "FindPath"),
        ("Timberborn.EnterableSystem.Enterer", "Exit"),
        ("Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService", "PickBestAction"),
        ("Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService", "AppraiseNeedBehaviors"),
        ("Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService", "PickShortestAction"),
        ("Timberborn.NeedBehaviorSystem.ActionDurationCalculator", "DurationWithReturnInHours"),
        ("Timberborn.NeedBehaviorSystem.ActionDurationCalculator", "FullyEffectiveDurationInHours"),
        ("Timberborn.Hauling.HaulingCenter", "GetWorkplaceBehaviorsOrdered"),
        ("Timberborn.Hauling.DistrictHaulCandidates", "GetWorkplaceBehaviorsOrdered"),
        ("Timberborn.Navigation.NavigationService", "FindPathUnlimitedRange"),
        ("Timberborn.Navigation.NavigationService", "FindPath"),
        ("Timberborn.Navigation.NavigationService", "InStoppingProximity"),
        ("Timberborn.WaterSystem.ThreadSafeWaterMap", "CellIsUnderwater"),
        ("Timberborn.BonusSystem.BonusManager", "Multiplier"),
    };

    internal static void Install()
    {
        if (_installed || !Requested) return;
        _installed = true;
        try
        {
            Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
            Type? TryFind(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            var ht = Find("HarmonyLib.Harmony");
            var hm = Find("HarmonyLib.HarmonyMethod");
            var harmony = Activator.CreateInstance(ht, "t3mp.test.tickprofile")!;
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            object Hook(string name) => Activator.CreateInstance(hm, typeof(TickProfiler).GetMethod(name, All))!;
            var metered = typeof(MeteredTickableComponent);
            var componentTick = metered.GetMethod("StartAndTick", All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)
                                ?? metered.GetMethod("Tick", All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)!;
            patch.Invoke(harmony, new object?[] { componentTick, Hook(nameof(ComponentBefore)), Hook(nameof(ComponentAfter)), null, null });
            var meteredSingleton = typeof(TickableSingletonService).GetNestedType("MeteredSingleton", All)!;
            patch.Invoke(harmony, new object?[] { meteredSingleton.GetMethod("Tick", All | BindingFlags.DeclaredOnly)!, Hook(nameof(SingletonBefore)), Hook(nameof(SingletonAfter)), null, null });
            var bucketService = Find("Timberborn.TickSystem.TickableBucketService");
            patch.Invoke(harmony, new object?[] { bucketService.GetMethod("TickBuckets", All)!, Hook(nameof(ServiceBefore)), Hook(nameof(ServiceAfter)), null, null });
            patch.Invoke(harmony, new object?[] { typeof(TickableSingletonService).GetMethod("FinishParallelTick", All)!, Hook(nameof(ParallelBefore)), Hook(nameof(ParallelAfter)), null, null });

            // Every Behavior.Decide override and every IExecutor.Tick implementation.
            var behavior = Find("Timberborn.BehaviorSystem.Behavior");
            var executor = Find("Timberborn.BehaviorSystem.IExecutor");
            var timed = new List<MethodBase>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.GetName().Name.StartsWith("Timberborn", StringComparison.Ordinal)) continue;
                Type[] types;
                try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;
                    if (behavior.IsAssignableFrom(type))
                    {
                        foreach (var decide in type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == "Decide" && !m.IsAbstract && m.GetParameters().Length == 1))
                            timed.Add(decide);
                    }
                    if (executor.IsAssignableFrom(type))
                    {
                        foreach (var tick in type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == "Tick" && !m.IsAbstract && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float)))
                            timed.Add(tick);
                    }
                }
            }
            foreach (var (typeName, methodName) in HotMethods)
            {
                var type = TryFind(typeName);
                if (type == null) continue;
                foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name == methodName && !m.IsAbstract && !m.ContainsGenericParameters))
                    timed.Add(method);
            }
            var before = Hook(nameof(MethodBefore));
            var after = Hook(nameof(MethodAfter));
            foreach (var method in timed.Distinct())
            {
                try { patch.Invoke(harmony, new object?[] { method, before, after, null, null }); _timedMethods++; }
                catch (Exception e) { Debug.Log("[T3MPTEST] profiler could not time " + method.DeclaringType?.Name + "." + method.Name + ": " + e.GetBaseException().Message); }
            }
            Debug.Log("[T3MPTEST] Tick profiler installed (component call=" + componentTick.Name + ", timed methods=" + _timedMethods + ").");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPTEST] Tick profiler unavailable: " + exception.GetBaseException().Message);
        }
    }

    private static void ComponentBefore(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void ComponentAfter(MeteredTickableComponent __instance, long __state)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;
        var type = __instance._tickableComponent?.GetType() ?? typeof(MeteredTickableComponent);
        if (!Components.TryGetValue(type, out var bucket)) Components[type] = bucket = new Bucket();
        bucket.Ticks += elapsed;
        bucket.Calls++;
    }

    private static void SingletonBefore(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void SingletonAfter(object __instance, long __state)
    {
        var elapsed = Stopwatch.GetTimestamp() - __state;
        var inner = __instance.GetType().GetField("_tickableSingleton", All)?.GetValue(__instance);
        var type = inner?.GetType() ?? __instance.GetType();
        if (!Singletons.TryGetValue(type, out var bucket)) Singletons[type] = bucket = new Bucket();
        bucket.Ticks += elapsed;
        bucket.Calls++;
    }

    private static void ServiceBefore(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void ServiceAfter(long __state) { _bucketServiceTicks += Stopwatch.GetTimestamp() - __state; _bucketServiceCalls++; }
    private static void ParallelBefore(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void ParallelAfter(long __state) => _parallelWaitTicks += Stopwatch.GetTimestamp() - __state;

    // Exclusive/inclusive timing with a frame stack (main thread only).
    private static void MethodBefore(MethodBase __originalMethod)
    {
        Frames.Push(new Frame { Method = __originalMethod, Start = Stopwatch.GetTimestamp() });
    }

    private static void MethodAfter(MethodBase __originalMethod)
    {
        if (Frames.Count == 0) return;
        var frame = Frames.Pop();
        if (!ReferenceEquals(frame.Method, __originalMethod)) { Frames.Clear(); return; } // unbalanced (exception path); drop
        var elapsed = Stopwatch.GetTimestamp() - frame.Start;
        if (!Methods.TryGetValue(frame.Method, out var bucket)) Methods[frame.Method] = bucket = new Bucket();
        bucket.Ticks += elapsed - frame.Child;
        bucket.Inclusive += elapsed;
        bucket.Calls++;
        if (Frames.Count > 0)
        {
            var parent = Frames.Pop();
            parent.Child += elapsed;
            Frames.Push(parent);
        }
    }

    // Called by the rate logger each window; resets the counters.
    internal static void Report(float windowSeconds)
    {
        if (!_installed) return;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var componentTotal = Components.Values.Sum(b => b.Ticks);
        var singletonTotal = Singletons.Values.Sum(b => b.Ticks);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Debug.Log(string.Format(culture,
            "[T3MPTEST] profile window={0:F1}s bucketService={1:F0}ms ({2} calls) components={3:F0}ms singletons={4:F0}ms parallelWait={5:F0}ms",
            windowSeconds, Ms(_bucketServiceTicks), _bucketServiceCalls, Ms(componentTotal), Ms(singletonTotal), Ms(_parallelWaitTicks)));
        foreach (var pair in Components.OrderByDescending(p => p.Value.Ticks).Take(20))
            Debug.Log(string.Format(culture, "[T3MPTEST] profile component={0} ms={1:F1} calls={2} avgUs={3:F2} share={4:F1}%",
                pair.Key.FullName, Ms(pair.Value.Ticks), pair.Value.Calls, Ms(pair.Value.Ticks) * 1000.0 / Math.Max(1, pair.Value.Calls), 100.0 * pair.Value.Ticks / Math.Max(1, componentTotal)));
        foreach (var pair in Singletons.OrderByDescending(p => p.Value.Ticks).Take(10))
            Debug.Log(string.Format(culture, "[T3MPTEST] profile singleton={0} ms={1:F1} calls={2} avgUs={3:F2}",
                pair.Key.FullName, Ms(pair.Value.Ticks), pair.Value.Calls, Ms(pair.Value.Ticks) * 1000.0 / Math.Max(1, pair.Value.Calls)));
        foreach (var pair in Methods.OrderByDescending(p => p.Value.Ticks).Take(45))
            Debug.Log(string.Format(culture, "[T3MPTEST] profile method={0}.{1} exclusiveMs={2:F1} inclusiveMs={3:F1} calls={4} avgExclUs={5:F2}",
                pair.Key.DeclaringType?.Name, pair.Key.Name, Ms(pair.Value.Ticks), Ms(pair.Value.Inclusive), pair.Value.Calls, Ms(pair.Value.Ticks) * 1000.0 / Math.Max(1, pair.Value.Calls)));
        Components.Clear();
        Singletons.Clear();
        Methods.Clear();
        Frames.Clear();
        _bucketServiceTicks = 0; _bucketServiceCalls = 0; _parallelWaitTicks = 0;
    }
}
