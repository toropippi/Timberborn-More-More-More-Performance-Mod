using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.TimeSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// NonlinearAnimationManager.SpeedMultiplier is Pow(timeScale, Exponent) /
// timeScale, evaluated once per animated shaft segment per tick. It is a pure
// function of the live time scale and the Exponent constant, so the result of
// the last evaluation is returned again while both inputs keep the same bits.
// The value itself is never stored beyond that one-entry memo, and a changed
// input recomputes with the native expression. No frame or tick keying.
internal static class NonlinearSpeedMemo
{
    private const string Owner = "t3mp.runtime.nonlinear-speed";
    private static Type? _harmonyType;
    private static MethodInfo? _multiplier, _nonlinear, _timeScale;
    private static RuntimePatches.Shape? _shape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _generations;
    private static bool _cached;
    private static int _scaleBits, _exponentBits;
    private static float _value;
    internal static long Hits, Misses;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed && Volatile.Read(ref _invalidated) == 0;

    private sealed class Guard
    {
        internal readonly MethodInfo Method;
        internal readonly RuntimePatches.Shape Shape;
        internal int Generations;
        internal Guard(MethodInfo method, Type harmony)
        {
            Method = method;
            Shape = RuntimePatches.OriginalShape(harmony, method);
        }
    }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (_attempted) return;
        _attempted = true;
        _harmonyType = harmonyType;
        Installed = RuntimePatches.TryInstall(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            var type = typeof(NonlinearAnimationManager);
            _multiplier = type.GetMethod("get_SpeedMultiplier", RuntimePatches.All | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException(type.FullName, "get_SpeedMultiplier");
            _nonlinear = type.GetMethod("get_NonlinearSpeed", RuntimePatches.All | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException(type.FullName, "get_NonlinearSpeed");
            // Raw-IL fingerprints, identical in the 1.1.2.4 and 1.0.13.1 APIs.
            if (!RuntimePatches.ReviewedBody(_multiplier, "256FB9F6E9F59691EB96B1160C3E8EF3F4A633B3BDBAC8BD1F8B7E0AA1DDE3D7") ||
                !RuntimePatches.ReviewedBody(_nonlinear, "4B54DBD18EC1DFB8A1B4691207930F0961DF7AEB6316A1758AB079035E4F9354"))
                throw new InvalidOperationException("nonlinear speed getters are not a reviewed build");
            if (type.GetField("Exponent", RuntimePatches.All)?.FieldType != typeof(float)) throw new MissingFieldException(type.FullName, "Exponent");
            // A memo hit bypasses Mathf.Pow and two of the three time-scale reads,
            // so patches on those are observable as well.
            var pow = typeof(Mathf).GetMethod("Pow", RuntimePatches.All | BindingFlags.DeclaredOnly, null, new[] { typeof(float), typeof(float) }, null)
                ?? throw new MissingMethodException(typeof(Mathf).FullName, "Pow");
            _timeScale = typeof(Time).GetProperty("timeScale", RuntimePatches.All)?.GetGetMethod(true) ?? throw new MissingMethodException(typeof(Time).FullName, "get_timeScale");
            foreach (var method in new[] { _multiplier, _nonlinear, pow, _timeScale })
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _shape = RuntimePatches.OriginalShape(harmonyType, _multiplier);
            _guards = new[] { new Guard(_nonlinear, harmonyType), new Guard(pow, harmonyType) };
            apply(_nonlinear, null, null, nameof(ObserveNonlinear), null);
            apply(pow, null, null, nameof(ObservePow), null);
            apply(_multiplier, null, null, nameof(RewriteMultiplier), null);
        }, typeof(NonlinearSpeedMemo));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[T3MP] Nonlinear animation speed memo installed.");
    }

    internal static void Revalidate()
    {
        _cached = false;
        if (!Installed || _harmonyType == null || _multiplier == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_multiplier).Append(_timeScale!).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[T3MP] Nonlinear speed memo revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    // Replacement body of get_SpeedMultiplier.
    private static float SpeedMultiplier(NonlinearAnimationManager self)
    {
        if (!Active)
        {
            // Native sequence through the original helper property.
            if (Time.timeScale != 0f) return self.NonlinearSpeed / Time.timeScale;
            return 0f;
        }
        var scale = Time.timeScale;
        var exponent = NonlinearAnimationManager.Exponent;
        var scaleBits = BitConverter.SingleToInt32Bits(scale);
        var exponentBits = BitConverter.SingleToInt32Bits(exponent);
        if (_cached && scaleBits == _scaleBits && exponentBits == _exponentBits) { Hits++; return _value; }
        Misses++;
        // Native expression on the values read above (native reads the live
        // time scale three times; it cannot change inside one main-thread call).
        var value = scale != 0f ? Mathf.Pow(scale, exponent) / scale : 0f;
        _scaleBits = scaleBits;
        _exponentBits = exponentBits;
        _value = value;
        _cached = true;
        return value;
    }

    private static IEnumerable<T> ObserveNonlinear<T>(IEnumerable<T> instructions) => Observe(instructions, 0);
    private static IEnumerable<T> ObservePow<T>(IEnumerable<T> instructions) => Observe(instructions, 1);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteMultiplier<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        if (Interlocked.Increment(ref _generations) != 1 || _shape == null ||
            !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(NonlinearSpeedMemo).GetMethod(nameof(SpeedMultiplier), RuntimePatches.All)!, 1);
    }
}
