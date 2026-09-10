using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Loading;

// Coarse observers only: no per-entity hooks, yields or changes to load order.
// Unity cannot paint during synchronous LoadAll. The native window has its own
// message loop and consumes plain managed snapshots, never Unity objects.
internal static class LoadProgress
{
    private const string Owner = "t3mp.load.progress";
    private static readonly object Gate = new object();
    private static readonly HashSet<string> Fallbacks = new HashSet<string>();
    private static readonly string[] Targets = {
        "Timberborn.SingletonSystem.SingletonLifecycleService.LoadSingletons",
        "Timberborn.WorldPersistence.WorldEntitiesLoader.InstantiateEntities",
        "Timberborn.WorldPersistence.EntitiesLoader.PreInitialize",
        "Timberborn.WorldPersistence.EntitiesLoader.Initialize",
        "Timberborn.WorldPersistence.EntitiesLoader.PostInitialize",
        "Timberborn.SingletonSystem.EventBus.PostLoad",
        "Timberborn.Navigation.NavigationSynchronizer.PostLoad"
    };
    private static readonly string[] Labels = {
        "サービス Load", "インスタンス生成", "PreInitialize", "Initialize",
        "PostInitialize", "EventBus.PostLoad", "NavigationSynchronizer.PostLoad"
    };
    private static Session? _session;
    private static bool _observing, _installed;
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
        internal string Heading = "", Status = "", Detail = "", Footer = "";
        internal string[] Rows = Array.Empty<string>();
        internal int Completed;
    }

    internal static void ObserveCompatibility()
    {
        if (_observing) return;
        _observing = true;
        Application.logMessageReceived += ObserveLog;
    }
    private static void ObserveLog(string message, string stack, LogType type)
    {
        if (!message.StartsWith("[T3MP", StringComparison.Ordinal) || message.StartsWith("[T3MPPROGRESS]", StringComparison.Ordinal)) return;
        // Do not mistake successful statistics such as "fallback=0" for a
        // disabled feature. Only explicit fallback/disable notices count.
        if (message.IndexOf("native fallback:", StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf("] fallback ", StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf(" disabled", StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf("using original", StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) < 0) return;
        var close = message.IndexOf(']');
        // Distinct feature reports, not the number of affected patches.
        var key = message.Substring(0, close + 1);
        if (key == "[T3MP]") key = message.Split(':')[0];
        lock (Gate) { if (Fallbacks.Count < 128) Fallbacks.Add(key); }
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
            Debug.Log("[T3MPPROGRESS] installed: 7 phase observers; wall time; build=load-progress-v3");
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
                    log = $"[T3MPPROGRESS] LoadAll wallSeconds={Seconds(session.Ended - session.Started):F3} completed={session.Done.Count(x => x)}/7 failed={session.Failed} fallbackReports={Fallbacks.Count}";
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

    internal static View? Snapshot()
    {
        lock (Gate)
        {
            var session = _session;
            var now = Stopwatch.GetTimestamp();
            if (session == null || (session.Ended != 0 && Seconds(now - session.Ended) > 8)) return null;
            double Time(int i) => session.Seconds[i] + (session.Running[i] == 0 ? 0 : Seconds(now - session.Running[i]));
            string Row(string label, params int[] indices)
            {
                var running = indices.Any(i => session.Depth[i] > 0);
                var done = indices.All(i => session.Done[i]);
                var time = indices.Sum(i => Time(i));
                return $"{(running ? "処理中" : done ? "完了" : "待機")}   {label}    {(time > 0 ? time.ToString("F2") + " 秒" : "—")}";
            }
            var count = session.Done.Count(x => x);
            return new View {
                GameWindow = session.GameWindow,
                Heading = $"T3MP 読み込み状況   {Seconds((session.Ended == 0 ? now : session.Ended) - session.Started):F1} 秒",
                Status = Fallbacks.Count == 0 ? "MOD 読み込み済み  |  高速化の無効化報告: なし" : $"MOD 読み込み済み  |  高速化の無効化報告: {Fallbacks.Count} 件",
                Detail = Fallbacks.Count == 0 ? "表示の有無と、個別の高速化の適用状況は別です。" : "互換性などにより一部の高速化が停止中。詳細は Player.log。",
                Completed = count,
                Rows = new[] { Row(Labels[0], 0), Row(Labels[1], 1), Row("PreInitialize + Initialize + PostInitialize", 2, 3, 4), Row(Labels[5], 5), Row(Labels[6], 6) },
                Footer = session.Ended != 0 ? (session.Failed ? "ロード処理でエラーが発生しました。" : $"LoadAll 終了  |  {count}/7 区間を計測（8 秒後に非表示）") :
                    $"{count}/7 区間完了  |  現在: {(session.Current < 0 ? "その他のロード処理" : Labels[session.Current])}"
            };
        }
    }
}
