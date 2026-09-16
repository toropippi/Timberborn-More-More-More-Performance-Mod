using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Coarse observers only: no per-entity hooks, yields or changes to load order.
// Unity cannot paint during synchronous LoadAll. The native bar has its own
// message loop and consumes plain managed snapshots, never Unity objects.
// Product feature (a load progress bar for the player), not a diagnostic: it
// shows only the phase count, logs one wall-time line per load, and hides as
// soon as LoadAll returns. Menu loads (no world phases) never show it.
internal static class LoadProgress
{
    private const string Owner = "t3mp.load.progress";
    internal const int PhaseCount = 7;
    private static readonly object Gate = new object();
    private static readonly string[] Targets = {
        "Timberborn.SingletonSystem.SingletonLifecycleService.LoadSingletons",
        "Timberborn.WorldPersistence.WorldEntitiesLoader.InstantiateEntities",
        "Timberborn.WorldPersistence.EntitiesLoader.PreInitialize",
        "Timberborn.WorldPersistence.EntitiesLoader.Initialize",
        "Timberborn.WorldPersistence.EntitiesLoader.PostInitialize",
        "Timberborn.SingletonSystem.EventBus.PostLoad",
        "Timberborn.Navigation.NavigationSynchronizer.PostLoad"
    };
    private static Session? _session;
    private static bool _installed;
    internal sealed class Session
    {
        internal long Started = Stopwatch.GetTimestamp(), Ended;
        internal readonly long[] Running = new long[7];
        internal readonly double[] Seconds = new double[7];
        internal readonly int[] Depth = new int[7];
        internal readonly bool[] Done = new bool[7];
        internal int Current = -1;
        internal bool Failed;
        internal IntPtr GameWindow;
    }
    internal sealed class Scope
    {
        internal Session Session = null!;
        internal int Index, Previous;
        internal bool Root;
    }
    internal sealed class View
    {
        internal IntPtr GameWindow;
        internal int Completed;
    }

    internal static void Install()
    {
        if (_installed) return;
        object? harmony = null;
        Type? ht = null;
        try
        {
            ht = LoadPatchBridge.Find("HarmonyLib.Harmony");
            var hm = LoadPatchBridge.Find("HarmonyLib.HarmonyMethod");
            harmony = Activator.CreateInstance(ht, Owner)!;
            var patch = ht.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            object Hook(string name, int priority)
            {
                var hook = Activator.CreateInstance(hm, typeof(LoadProgress).GetMethod(name, LoadPatchBridge.All))!;
                hm.GetField("priority")!.SetValue(hook, priority);
                return hook;
            }
            foreach (var target in Targets.Concat(new[] { "Timberborn.SingletonSystem.SingletonLifecycleService.LoadAll" }))
            {
                var split = target.LastIndexOf('.');
                var method = LoadPatchBridge.Find(target.Substring(0, split)).GetMethods(LoadPatchBridge.All)
                    .Single(m => m.Name == target.Substring(split + 1) && !m.ContainsGenericParameters);
                patch.Invoke(harmony, new object?[] { method, Hook(nameof(Begin), 800), null, null, Hook(nameof(End), -800) });
            }
            _installed = true;
            NativeLoadProgressWindow.Start();
            Debug.Log("[T3MPPROGRESS] installed: 7 phase observers; progress bar only");
        }
        catch (Exception e)
        {
            if (harmony != null) ht!.GetMethod("UnpatchAll", LoadPatchBridge.All)?.Invoke(harmony, new object[] { Owner });
            Debug.LogWarning("[T3MPPROGRESS] unavailable: " + e.GetBaseException().Message);
        }
    }

    private static void Begin(MethodBase __originalMethod, out Scope? __state)
    {
        __state = null;
        try
        {
            var name = __originalMethod.DeclaringType!.FullName + "." + __originalMethod.Name;
            lock (Gate)
            {
                if (__originalMethod.Name == "LoadAll")
                {
                    if (_session != null && _session.Ended == 0) return;
                    _session = new Session { GameWindow = NativeLoadProgressWindow.FindGameWindow() };
                    __state = new Scope { Session = _session, Root = true };
                }
                else
                {
                    var session = _session;
                    if (session == null || session.Ended != 0) return;
                    var index = Array.IndexOf(Targets, name);
                    if (index < 0) return;
                    __state = new Scope { Session = session, Index = index, Previous = session.Current };
                    session.Current = index;
                    if (session.Depth[index]++ == 0) session.Running[index] = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception e) { Debug.LogWarning("[T3MPPROGRESS] observer failed: " + e.Message); }
    }

    // A void finalizer observes failures without replacing/suppressing them.
    private static void End(Scope? __state, Exception? __exception)
    {
        if (__state == null) return;
        try
        {
            string? log = null;
            lock (Gate)
            {
                var session = __state.Session;
                session.Failed |= __exception != null;
                if (__state.Root)
                {
                    session.Ended = Stopwatch.GetTimestamp();
                    session.Current = -1;
                    log = $"[T3MPPROGRESS] LoadAll wallSeconds={Seconds(session.Ended - session.Started):F3} completed={session.Done.Count(x => x)}/7 failed={session.Failed}";
                }
                else
                {
                    var index = __state.Index;
                    if (--session.Depth[index] == 0)
                    {
                        session.Seconds[index] += Seconds(Stopwatch.GetTimestamp() - session.Running[index]);
                        session.Running[index] = 0;
                        session.Done[index] = __exception == null;
                        log = $"[T3MPPROGRESS] phase={Targets[index]} wallSeconds={session.Seconds[index]:F3} failed={__exception != null}";
                    }
                    session.Current = __state.Previous;
                }
            }
            if (log != null) Debug.Log(log);
        }
        catch (Exception e) { Debug.LogWarning("[T3MPPROGRESS] observer failed: " + e.Message); }
    }
    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

    // Null (bar hidden) unless a world load is in progress: the session is
    // open and at least one entity phase (index 1 onward) has started, which a
    // main-menu LoadAll never does.
    internal static View? Snapshot()
    {
        lock (Gate)
        {
            var session = _session;
            if (session == null || session.Ended != 0) return null;
            var worldLoad = false;
            for (var i = 1; i < PhaseCount && !worldLoad; i++) worldLoad = session.Depth[i] > 0 || session.Done[i] || session.Seconds[i] > 0;
            // The service phase precedes the entity phases by several seconds on
            // a large save; a menu load finishes it well under a second.
            if (!worldLoad && session.Depth[0] > 0 && session.Running[0] != 0 && Seconds(Stopwatch.GetTimestamp() - session.Running[0]) > 2) worldLoad = true;
            if (!worldLoad) return null;
            return new View { GameWindow = session.GameWindow, Completed = session.Done.Count(x => x) };
        }
    }
}
