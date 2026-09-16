using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.CharacterMovementSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only movement attribution: inclusive time of
// PathFollower.MoveAlongPath, its sub-step count, speed-provider calls, and the
// in-place cost of Transform.position on real walkers. Hooks every movement
// call, so a probed run is never a benchmark figure. -t3mpTestMovementProbe.
internal static class MovementProbe
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const int SampleEvery = 64, MaxSamples = 3000, InSituOps = 256;
    internal static bool Requested => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-t3mpTestMovementProbe", StringComparison.OrdinalIgnoreCase));
    private static bool _installed, _benchDone;
    private static long _moveTicks, _moveCalls, _substeps, _reachedCalls, _providerCalls, _animatorTicks, _animatorCalls;
    private static long _samples, _rootCount, _parentedCount, _identityParentCount, _descendantSum, _maxDescendants;
    private static long _roundTripChecks, _roundTripMismatches, _restoreMismatches, _inSituGetTicks, _inSituSetTicks, _inSituOps;
    private static int _callIndex;
    private static FieldInfo? _transformField;

    internal static void Install()
    {
        if (_installed || !Requested) return;
        _installed = true;
        try
        {
            Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
            var harmonyType = Find("HarmonyLib.Harmony");
            var methodType = Find("HarmonyLib.HarmonyMethod");
            var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.movementprobe")!;
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            object Hook(string name) => Activator.CreateInstance(methodType, typeof(MovementProbe).GetMethod(name, All))!;
            MethodInfo Method(Type type, string name) => type.GetMethod(name, All | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException(type.FullName, name);
            var follower = typeof(PathFollower);
            _transformField = follower.GetField("_transform", All) ?? throw new MissingFieldException("PathFollower._transform");
            patch.Invoke(harmony, new object?[] { Method(follower, "MoveAlongPath"), Hook(nameof(MoveBefore)), Hook(nameof(MoveAfter)), null, null });
            patch.Invoke(harmony, new object?[] { Method(follower, "ReachedLastPathCorner"), null, Hook(nameof(ReachedAfter)), null, null });
            patch.Invoke(harmony, new object?[] { Method(follower, "MoveInDirection"), null, Hook(nameof(SubstepAfter)), null, null });
            patch.Invoke(harmony, new object?[] { Method(typeof(MovementAnimator), "AnimateMovementAlongPath"), Hook(nameof(AnimatorBefore)), Hook(nameof(AnimatorAfter)), null, null });
            patch.Invoke(harmony, new object?[] { Method(Find("Timberborn.WalkingSystem.WalkerSpeedManager"), "GetWalkerSpeedAtCurrentPosition"), null, Hook(nameof(ProviderAfter)), null, null });
            Debug.Log("[T3MPMOVE] installed");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPMOVE] unavailable: " + exception.GetBaseException().Message);
        }
    }

    private static void MoveBefore(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void MoveAfter(PathFollower __instance, long __state)
    {
        _moveTicks += Stopwatch.GetTimestamp() - __state;
        _moveCalls++;
        if ((++_callIndex % SampleEvery) == 0 && _samples < MaxSamples) Sample(__instance);
    }
    private static void ReachedAfter() => _reachedCalls++;
    private static void SubstepAfter() => _substeps++;
    private static void ProviderAfter() => _providerCalls++;
    private static void AnimatorBefore(out long __state) => __state = Stopwatch.GetTimestamp();
    private static void AnimatorAfter(long __state) { _animatorTicks += Stopwatch.GetTimestamp() - __state; _animatorCalls++; }

    private static bool SameBits(Vector3 a, Vector3 b) =>
        BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x) &&
        BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y) &&
        BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);

    private static int Descendants(Transform t)
    {
        var count = 0;
        for (var i = 0; i < t.childCount; i++) count += 1 + Descendants(t.GetChild(i));
        return count;
    }

    // Runs after the native call finished; restores the exact position bits.
    private static void Sample(PathFollower instance)
    {
        try
        {
            if (_transformField?.GetValue(instance) is not Transform t) return;
            _samples++;
            var parent = t.parent;
            if (parent == null) _rootCount++;
            else { _parentedCount++; if (parent.localToWorldMatrix.isIdentity) _identityParentCount++; }
            var descendants = Descendants(t);
            _descendantSum += descendants;
            if (descendants > _maxDescendants) _maxDescendants = descendants;
            var p = t.position;
            foreach (var delta in new[] { new Vector3(0.1f, 0f, 0f), new Vector3(-0.037f, 0.011f, 0.052f), new Vector3(0.0001f, 0f, -0.0001f) })
            {
                var q = p + delta;
                t.position = q;
                _roundTripChecks++;
                if (!SameBits(q, t.position)) _roundTripMismatches++;
            }
            var alternate = p + new Vector3(0.05f, 0f, 0f);
            var start = Stopwatch.GetTimestamp();
            var acc = 0f;
            for (var i = 0; i < InSituOps; i++) acc += t.position.x;
            _inSituGetTicks += Stopwatch.GetTimestamp() - start;
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < InSituOps; i++) t.position = (i & 1) == 0 ? alternate : p;
            _inSituSetTicks += Stopwatch.GetTimestamp() - start;
            _inSituOps += InSituOps;
            t.position = p;
            if (!SameBits(p, t.position)) _restoreMismatches++;
            if (float.IsNaN(acc)) Debug.Log("[T3MPMOVE] nan");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPMOVE] sample failed: " + exception.GetBaseException().Message);
        }
    }

    private static void Bench()
    {
        if (_benchDone) return;
        _benchDone = true;
        const int n = 1_000_000;
        double Ns(long ticks, int ops) => ticks * 1e9 / Stopwatch.Frequency / ops;
        var go = new GameObject("T3MPTEST.MovementProbe");
        try
        {
            var t = go.transform;
            var a = new Vector3(10f, 5f, 10f); var b = new Vector3(10.05f, 5f, 10f);
            t.position = a;
            var acc = 0f;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < n; i++) acc += t.position.x;
            var get = Stopwatch.GetTimestamp() - start;
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < n; i++) t.position = (i & 1) == 0 ? b : a;
            var set = Stopwatch.GetTimestamp() - start;
            for (var i = 0; i < 40; i++) new GameObject("child" + i).transform.SetParent(t, false);
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < n; i++) t.position = (i & 1) == 0 ? b : a;
            var setWithChildren = Stopwatch.GetTimestamp() - start;
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < n; i++) acc += t.position.x;
            var getWithChildren = Stopwatch.GetTimestamp() - start;
            var list = new List<AnimatedPathCorner>(128);
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < n; i++)
            {
                if ((i & 63) == 0) list.Clear();
                var d = Vector3.Distance(a, b);
                list.Add(new AnimatedPathCorner(a, i, d, d, 1));
            }
            var managed = Stopwatch.GetTimestamp() - start;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[T3MPMOVE] bench rootGetNs={0:F1} rootSetNs={1:F1} get40ChildrenNs={2:F1} set40ChildrenNs={3:F1} distancePlusListAddNs={4:F1} acc={5}",
                Ns(get, n), Ns(set, n), Ns(getWithChildren, n), Ns(setWithChildren, n), Ns(managed, n), acc));
        }
        finally { UnityEngine.Object.Destroy(go); }
    }

    internal static void Report(double windowSeconds)
    {
        if (!_installed) return;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var culture = CultureInfo.InvariantCulture;
        Debug.Log(string.Format(culture,
            "[T3MPMOVE] window={0:F1}s moveCalls={1} moveMs={2:F1} avgInclUs={3:F2} substepsPerCall={4:F2} reachedPerCall={5:F2} providerPerCall={6:F2} animatorCalls={7} animatorMs={8:F1}",
            windowSeconds, _moveCalls, Ms(_moveTicks), Ms(_moveTicks) * 1000.0 / Math.Max(1, _moveCalls),
            _substeps / (double)Math.Max(1, _moveCalls), _reachedCalls / (double)Math.Max(1, _moveCalls),
            _providerCalls / (double)Math.Max(1, _moveCalls), _animatorCalls, Ms(_animatorTicks)));
        Debug.Log(string.Format(culture,
            "[T3MPMOVE] samples={0} root={1} parented={2} identityParent={3} avgDescendants={4:F1} maxDescendants={5} roundTripChecks={6} roundTripMismatches={7} restoreMismatches={8} inSituGetNs={9:F1} inSituSetNs={10:F1}",
            _samples, _rootCount, _parentedCount, _identityParentCount, _descendantSum / (double)Math.Max(1, _samples), _maxDescendants,
            _roundTripChecks, _roundTripMismatches, _restoreMismatches,
            _inSituGetTicks * 1e9 / Stopwatch.Frequency / Math.Max(1, _inSituOps), _inSituSetTicks * 1e9 / Stopwatch.Frequency / Math.Max(1, _inSituOps)));
        _moveTicks = _moveCalls = _substeps = _reachedCalls = _providerCalls = _animatorTicks = _animatorCalls = 0;
        _samples = _rootCount = _parentedCount = _identityParentCount = _descendantSum = _maxDescendants = 0;
        _roundTripChecks = _roundTripMismatches = _restoreMismatches = _inSituGetTicks = _inSituSetTicks = _inSituOps = 0;
        try { Bench(); } catch (Exception exception) { Debug.LogWarning("[T3MPMOVE] bench failed: " + exception.GetBaseException().Message); }
    }
}
