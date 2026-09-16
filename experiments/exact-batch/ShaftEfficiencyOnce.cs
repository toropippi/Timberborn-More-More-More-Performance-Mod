using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.MechanicalSystem;
using Timberborn.ModularShafts;
using Timberborn.TimbermeshAnimations;
using Timberborn.TimeSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// ModularShaftAnimator.UpdateAnimation reads the node's power efficiency once
// for the active test and again for every animator, and the nonlinear speed
// multiplier for every animator. Both are pure reads of state that the
// animator setters (TimbermeshAnimator: managed field writes only) cannot
// change, so each is read once per call and the same products are assigned
// to the same animators in the same order. Any other IAnimator implementation
// keeps the native sequence. Nothing is cached across calls.
internal static class ShaftEfficiencyOnce
{
    private const string Owner = "t3mp.runtime.shaft-efficiency";
    private static Type? _harmonyType;
    private static MethodInfo? _update;
    private static RuntimePatches.Shape? _shape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _generations;
    internal static long OnceCalls, NativeCalls;
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
            MethodInfo Find(Type type, string name) => type.GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(type.FullName, name);
            _update = Find(typeof(ModularShaftAnimator), "UpdateAnimation");
            var checks = new (MethodInfo Method, string[] Hashes)[]
            {
                (_update, new[] { "B72E2D6BD258D589436703F3371ADF36E3888657745B34053A0C36F1026BEB7C" }),
                (Find(typeof(MechanicalNode), "get_PowerEfficiency"), new[] { "FB452B58DABD224B5F09C84B2EC1DB0261D93FFDC462CD4131F8062E9FEA616B" }),
                (Find(typeof(MechanicalNode), "get_ActiveAndPowered"), new[] { "BAE0D900ADD2EA90421247CD76993C0B929A1DD38D7CFD29F03959ECD3AE11F6" }),
                (Find(typeof(MechanicalGraph), "get_PowerEfficiency"), new[] { "5AB4F03956805159B4DC9CA2CFABED7B29F7F9F1D5F7DB47D0BE0B69B5378759", "34AA3E2DC5AB5F5C5D7B7B5758F477ADC7AB4835098730F1177B7E9F68AE6B2F" }),
                (Find(typeof(TimbermeshAnimator), "set_Enabled"), new[] { "D5C6F9971FE68227DDA0466AC8BDBE0E886C9D47A4C506650A8D7429429C194B" }),
                (Find(typeof(TimbermeshAnimator), "set_Speed"), new[] { "3CA06DF55034F7786DA4A7CBE04759D4FEE1C220ED462A4A422D0FD48072BE10" }),
                (Find(typeof(NonlinearAnimationManager), "get_SpeedMultiplier"), new[] { "256FB9F6E9F59691EB96B1160C3E8EF3F4A633B3BDBAC8BD1F8B7E0AA1DDE3D7" }),
                (Find(typeof(ModularShaftAnimator), "get_IsAnimated"), new[] { "15886FAD36F216950BBED589B5A7318715F19341F4AE6BCE0B7167BF3D5B951D" }),
                (Find(typeof(ModularShaftAnimator), "set_IsAnimated"), new[] { "05BA624E0612FFFF2FF0D0335B0DE3CB06412EA338318E709C2D3608F9C5B08A" }),
            };
            // Raw-IL fingerprints from the reviewed 1.1.2.4 and 1.0.13.1 APIs.
            foreach (var (method, hashes) in checks)
                if (!RuntimePatches.ReviewedBody(method, hashes)) throw new InvalidOperationException(method.Name + " is not a reviewed build");
            foreach (var field in new[] { "_mechanicalNode", "_animators", "_nonlinearAnimationManager" })
                if (typeof(ModularShaftAnimator).GetField(field, RuntimePatches.All) == null) throw new MissingFieldException(typeof(ModularShaftAnimator).FullName, field);
            var methods = checks.Skip(1).Select(c => c.Method).ToArray();
            foreach (var method in methods.Append(_update))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _shape = RuntimePatches.OriginalShape(harmonyType, _update);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            var hooks = new[] { nameof(Observe0), nameof(Observe1), nameof(Observe2), nameof(Observe3), nameof(Observe4), nameof(Observe5), nameof(Observe6), nameof(Observe7) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
            apply(_update, null, null, nameof(RewriteUpdate), null);
        }, typeof(ShaftEfficiencyOnce));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[T3MP] Shaft efficiency single read installed.");
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _update == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_update).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[T3MP] Shaft efficiency revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static bool AllTimbermesh(List<IAnimator> animators)
    {
        for (var i = 0; i < animators.Count; i++)
            if (animators[i]?.GetType() != typeof(TimbermeshAnimator)) return false;
        return true;
    }

    private static void UpdateAnimation(ModularShaftAnimator self)
    {
        var animators = self._animators;
        if (Active && !ReferenceEquals(self._mechanicalNode, null) && AllTimbermesh(animators))
        {
            OnceCalls++;
            var node = self._mechanicalNode;
            float efficiency = 0f;
            bool animated = false;
            if (node.ActiveAndPowered)
            {
                efficiency = node.PowerEfficiency;
                animated = efficiency > 0f;
            }
            self.IsAnimated = animated;
            float speed = 0f;
            bool speedRead = false;
            foreach (IAnimator animator in animators)
            {
                // The flag is re-read through its getter like native.
                bool isAnimated = self.IsAnimated;
                animator.Enabled = isAnimated;
                if (isAnimated)
                {
                    // Native: _mechanicalNode.PowerEfficiency * SpeedMultiplier per
                    // animator; both are pure reads of state the Timbermesh setters
                    // cannot change, so the first product is reused.
                    if (!speedRead) { speed = self._mechanicalNode.PowerEfficiency * self._nonlinearAnimationManager.SpeedMultiplier; speedRead = true; }
                    animator.Speed = speed;
                }
            }
            return;
        }
        NativeCalls++;
        self.IsAnimated = self._mechanicalNode!.ActiveAndPowered && self._mechanicalNode.PowerEfficiency > 0f;
        foreach (IAnimator animator in animators)
        {
            animator.Enabled = self.IsAnimated;
            if (self.IsAnimated) animator.Speed = self._mechanicalNode.PowerEfficiency * self._nonlinearAnimationManager.SpeedMultiplier;
        }
    }

    private static IEnumerable<T> Observe0<T>(IEnumerable<T> i) => Observe(i, 0);
    private static IEnumerable<T> Observe1<T>(IEnumerable<T> i) => Observe(i, 1);
    private static IEnumerable<T> Observe2<T>(IEnumerable<T> i) => Observe(i, 2);
    private static IEnumerable<T> Observe3<T>(IEnumerable<T> i) => Observe(i, 3);
    private static IEnumerable<T> Observe4<T>(IEnumerable<T> i) => Observe(i, 4);
    private static IEnumerable<T> Observe5<T>(IEnumerable<T> i) => Observe(i, 5);
    private static IEnumerable<T> Observe6<T>(IEnumerable<T> i) => Observe(i, 6);
    private static IEnumerable<T> Observe7<T>(IEnumerable<T> i) => Observe(i, 7);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteUpdate<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        if (Interlocked.Increment(ref _generations) != 1 || _shape == null ||
            !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(ShaftEfficiencyOnce).GetMethod(nameof(UpdateAnimation), RuntimePatches.All)!, 1);
    }
}
