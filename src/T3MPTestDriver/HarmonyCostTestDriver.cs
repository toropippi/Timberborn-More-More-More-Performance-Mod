using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.TimeSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Test-only, invoked after loading a disposable save. Production code and its
// flags stay untouched. Both timed routes use the installed T3MP prefix AND
// postfix. Reflection is used only during setup, never to invoke timed calls.
public sealed class HarmonyCostTestDriver : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
    private const int Capacity = 256;
    private const int Repetitions = 2048;
    private const int Pairs = 16;
    private delegate float Travel(object instance, Vector3 action, Vector3 position);
    private readonly struct Sample
    {
        public readonly object Instance;
        public readonly Vector3 Action, Position;
        public Sample(object instance, Vector3 action, Vector3 position)
        { Instance = instance; Action = action; Position = position; }
    }

    private static readonly Sample[] Samples = new Sample[Capacity];
    private static bool _capture;
    private static long _calls;
    private static int _samples;
    private SpeedManager _speed = null!;
    private object _harmony = null!;
    private Type _harmonyType = null!;
    private MethodInfo _target = null!, _prefix = null!, _postfix = null!;
    private FieldInfo _ticks = null!;
    private long _start, _startTicks;
    private int _phase = -1;
    private bool _samplerInstalled;

    public void Initialize(SpeedManager speed)
    {
        _speed = speed;
        try
        {
            var settings = Find("T3MP.BenchmarkSettings");
            foreach (var flag in new[] { "EnableBenchmarkMeasurement", "EnableHotOptimizerMetrics",
                         "EnableNeedTravelCacheMetrics", "EnableNeedBehaviorDecisionMetrics" })
                if ((bool)settings.GetField(flag, All)!.GetValue(null)!)
                    throw new InvalidOperationException("Contaminating profiler enabled: " + flag);
            if (!(bool)settings.GetField("EnableTravelDistanceCache", All)!.GetValue(null)!)
                throw new InvalidOperationException("Travel distance cache is disabled");
            var calculator = Find("Timberborn.NeedBehaviorSystem.ActionDurationCalculator");
            _target = calculator.GetMethod("TravelTimeBetween", All, null,
                new[] { typeof(Vector3), typeof(Vector3) }, null)!;
            var probe = Find("T3MP.BenchmarkProbe");
            var args = Environment.GetCommandLineArgs();
            var expectedIndex = Array.IndexOf(args, "-t3mpTestExpectedModMvid");
            if (expectedIndex < 0 || expectedIndex + 1 >= args.Length ||
                !Guid.TryParse(args[expectedIndex + 1], out var expectedMvid) ||
                probe.Module.ModuleVersionId != expectedMvid)
                throw new InvalidOperationException("Loaded T3MP does not match the requested build MVID");
            _prefix = probe.GetMethod("RecordNeedBehaviorTravelEstimateCall", All)!;
            _postfix = probe.GetMethod("RecordNeedBehaviorTravelEstimateReturn", All)!;
            _ticks = Find("T3MP.BenchmarkModeController").GetField("_overlayFullTicks", All)!;
            _harmonyType = Find("HarmonyLib.Harmony");
            VerifyPatches();
            Log("runtime unity=" + Application.unityVersion + " harmony=" +
                _harmonyType.Assembly.FullName + " modMvid=" + probe.Module.ModuleVersionId +
                " driverMvid=" + GetType().Module.ModuleVersionId);
            _harmony = Activator.CreateInstance(_harmonyType, "t3mp.test.harmonycost")!;
            var hm = Find("HarmonyLib.HarmonyMethod");
            var sampler = Activator.CreateInstance(hm, GetType().GetMethod(nameof(Capture), All))!;
            hm.GetField("priority", All)!.SetValue(sampler, 800);
            var patch = _harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            patch.Invoke(_harmony, new[] { _target, sampler, null, null, null });
            _samplerInstalled = true;
            _start = Stopwatch.GetTimestamp();
            _speed.ChangeSpeed(99f);
            Log("WARMUP speed=99 seconds=5 target=" + _target.DeclaringType!.FullName + "." + _target.Name);
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void Capture(object __instance, Vector3 actionPosition, Vector3 position)
    {
        if (!_capture) return;
        var index = _calls++;
        // Bounded sampling, spread through the observation window. No per-call
        // stopwatch, allocation, or reflection. Removed before timing batches.
        if ((index & 127) == 0)
            Samples[_samples++ % Capacity] = new Sample(__instance, actionPosition, position);
    }

    private void LateUpdate()
    {
        if (_phase == 3) return;
        try
        {
            // PostLoad can precede many seconds of other singleton loading.
            // Warmup starts only once the game is actually updating frames.
            if (_phase < 0)
            {
                _start = Stopwatch.GetTimestamp();
                _phase = 0;
                return;
            }
            var seconds = (Stopwatch.GetTimestamp() - _start) / (double)Stopwatch.Frequency;
            if (_phase == 0 && seconds >= 5)
            {
                _calls = 0;
                _samples = 0;
                _startTicks = (long)_ticks.GetValue(null)!;
                _capture = true;
                _phase = 1;
                _start = Stopwatch.GetTimestamp();
                Log("OBSERVE seconds=20");
            }
            else if (_phase == 1 && seconds >= 20)
            {
                _capture = false;
                var ticks = (long)_ticks.GetValue(null)! - _startTicks;
                Log(FormattableString.Invariant($"RATE calls={_calls} seconds={seconds:F6} callsPerSecond={_calls / seconds:F3} fullTicks={ticks} ticksPerSecond={ticks / seconds:F3} timeScale={Time.timeScale:F3}"));
                _speed.ChangeSpeed(0f);
                RemoveSampler();
                VerifyPatches();
                _phase = 2;
                Measure(); // One main-thread callback: same frame and world for A/B.
                _phase = 3;
                Log("COMPLETE");
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void Measure()
    {
        var count = Math.Min(_samples, Capacity);
        if (count < 16) throw new InvalidOperationException("Too few real inputs: " + count);
        if (Find("T3MP.BenchmarkModeController").GetProperty("CurrentMode", All)!
                .GetValue(null)!.ToString() != "Optimized")
            throw new InvalidOperationException("Optimized mode is not active");
        var a = BuildRoute(false);
        var b = BuildRoute(true);
        // Warm cache/JIT through both routes. B throws if the production prefix
        // requests vanilla, so an inactive optimizer cannot give a false result.
        double expected = 0;
        for (var i = 0; i < count; i++)
        {
            var s = Samples[i];
            var actual = a(s.Instance, s.Action, s.Position);
            var direct = b(s.Instance, s.Action, s.Position);
            if (float.IsNaN(actual) || float.IsInfinity(actual) || !actual.Equals(direct))
                throw new InvalidOperationException("Per-input result mismatch at " + i);
            expected += actual;
        }
        Run(a, count, 64);
        Run(b, count, 64);
        var cache = Find("T3MP.NeedBehaviorTravelOptimizer");
        long Counter(string name) => (long)cache.GetField(name, All)!.GetValue(null)!;
        var hits = Counter("_distanceCacheHits");
        var stores = Counter("_distanceCacheStores");
        var declined = Counter("_distanceCacheDeclined");
        Log(FormattableString.Invariant($"VALIDATED inputs={count} callsPerBatch={count * Repetitions} pairs={Pairs} expectedSumPerPass={expected:R}"));
        for (var pair = 0; pair < Pairs; pair++)
        {
            (double ns, double sum, int gc) ar, br;
            if ((pair & 1) == 0) { ar = Run(a, count, Repetitions); br = Run(b, count, Repetitions); }
            else { br = Run(b, count, Repetitions); ar = Run(a, count, Repetitions); }
            if (!ar.sum.Equals(br.sum)) throw new InvalidOperationException("Batch checksum mismatch");
            Log(FormattableString.Invariant($"PAIR index={pair} order={((pair & 1) == 0 ? "AB" : "BA")} harmonyNs={ar.ns:F3} directNs={br.ns:F3} deltaNs={ar.ns - br.ns:F3} gcA={ar.gc} gcB={br.gc} checksum={ar.sum:R}"));
        }
        var measured = (long)count * Repetitions * Pairs * 2;
        if (Counter("_distanceCacheHits") - hits != measured ||
            Counter("_distanceCacheStores") != stores || Counter("_distanceCacheDeclined") != declined)
            throw new InvalidOperationException("Timed routes did not both execute the cache-hit algorithm exclusively");
        Log("CACHE_VERIFIED hits=" + measured + " stores=0 declined=0");
    }

    private static (double ns, double sum, int gc) Run(Travel route, int count, int repeats)
    {
        double checksum = 0;
        var collections = Collections();
        var start = Stopwatch.GetTimestamp();
        for (var pass = 0; pass < repeats; pass++)
            for (var i = 0; i < count; i++)
            {
                var s = Samples[i];
                checksum += route(s.Instance, s.Action, s.Position);
            }
        var elapsed = Stopwatch.GetTimestamp() - start;
        return (elapsed * 1e9 / Stopwatch.Frequency / (count * repeats), checksum, Collections() - collections);
    }

    private Travel BuildRoute(bool direct)
    {
        var dm = new DynamicMethod(direct ? "TravelDirect" : "TravelHarmony", typeof(float),
            new[] { typeof(object), typeof(Vector3), typeof(Vector3) }, GetType().Module, true);
        var il = dm.GetILGenerator();
        if (!direct)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _target.DeclaringType!);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, _target);
        }
        else
        {
            var result = il.DeclareLocal(typeof(float));
            var state = il.DeclareLocal(_prefix.GetParameters()[4].ParameterType.GetElementType()!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _target.DeclaringType!);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Ldloca, state);
            il.Emit(OpCodes.Call, _prefix);
            var handled = il.DefineLabel();
            il.Emit(OpCodes.Brfalse, handled);
            il.Emit(OpCodes.Ldstr, "Direct route requested vanilla fallback");
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
            il.Emit(OpCodes.Throw);
            il.MarkLabel(handled);
            il.Emit(OpCodes.Ldloc, state);
            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Call, _postfix);
            il.Emit(OpCodes.Ldloc, result);
        }
        il.Emit(OpCodes.Ret);
        return (Travel)dm.CreateDelegate(typeof(Travel));
    }

    private void VerifyPatches()
    {
        var info = _harmonyType.GetMethod("GetPatchInfo", All)!.Invoke(null, new object[] { _target })
                   ?? throw new InvalidOperationException("Target is not patched");
        foreach (var kind in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
        {
            var entries = (IEnumerable)info.GetType().GetField(kind, All)!.GetValue(info)!;
            var methods = new List<MethodInfo>();
            foreach (var entry in entries)
                methods.Add((MethodInfo)entry!.GetType().GetProperty("PatchMethod", All)!.GetValue(entry)!);
            var expected = kind == "Prefixes" ? _prefix : kind == "Postfixes" ? _postfix : null;
            if (methods.Count != (expected == null ? 0 : 1) || (expected != null && !methods[0].Equals(expected)))
                throw new InvalidOperationException("Unexpected patch chain: " + kind);
        }
    }

    private void RemoveSampler()
    {
        if (!_samplerInstalled) return;
        _harmonyType.GetMethod("Unpatch", new[] { typeof(MethodBase), typeof(MethodInfo) })!
            .Invoke(_harmony, new object[] { _target, GetType().GetMethod(nameof(Capture), All)! });
        _samplerInstalled = false;
    }

    private static int Collections()
    {
        var n = 0;
        for (var generation = 0; generation <= GC.MaxGeneration; generation++) n += GC.CollectionCount(generation);
        return n;
    }
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(name)).FirstOrDefault(t => t != null)
        ?? throw new TypeLoadException(name);
    private static void Log(string text) => Debug.Log("[T3MPHARMONY] " + text);
    private void Fail(Exception exception)
    {
        _capture = false;
        _phase = 3;
        try { RemoveSampler(); _speed.ChangeSpeed(0f); }
        catch (Exception cleanup) { Debug.LogError("[T3MPHARMONY] ERROR cleanup: " + cleanup); }
        Debug.LogError("[T3MPHARMONY] ERROR " + exception);
    }
}
