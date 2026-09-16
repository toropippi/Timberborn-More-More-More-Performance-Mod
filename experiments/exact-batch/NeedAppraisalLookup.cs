using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.Effects;
using Timberborn.NeedBehaviorSystem;
using Timberborn.NeedSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Appraiser.AppraiseEffects resolves each effect's need by string id inside
// NeedManager.TryAppraise and then again, once or twice, inside
// NeedFilter.Filter (NeedIsCritical / NeedIsInCriticalState /
// NeedIsBelowWarningThreshold). When the filter uses the appraiser's own
// NeedManager, the need is resolved once and the same predicates are read
// from it in the native order. A filter with another manager (or none) keeps
// the native calls. Sums, early returns and float order are unchanged.
internal static class NeedAppraisalLookup
{
    private const string Owner = "t3mp.runtime.need-appraisal";
    private static Type? _harmonyType;
    private static MethodInfo? _appraise;
    private static RuntimePatches.Shape? _shape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static bool _attempted;
    private static int _invalidated, _generations;
    internal static long SharedLookups, NativeLookups;
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
            MethodInfo Find(Type type, string name, int parameters = -1) =>
                type.GetMethods(RuntimePatches.All | BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == name && (parameters < 0 || m.GetParameters().Length == parameters))
                ?? throw new MissingMethodException(type.FullName, name);
            var manager = typeof(NeedManager);
            _appraise = Find(typeof(Appraiser), "AppraiseEffects", 2);
            var filter = Find(typeof(NeedFilter), "Filter");
            var tryAppraise = Find(manager, "TryAppraise", 3);
            var checks = new (MethodInfo Method, string[] Hashes)[]
            {
                (_appraise, new[] { "FCAA9064D9C43C4544D7592A36321B13960594D8CA35A07D33E6DC06B6DECD56", "8CAF1BFC53D5C809D697D1A08119EB398D1CF7B3BEAFB7755131C0E750A43F6D" }),
                (filter, new[] { "D2D349E12D019EAB5B70AC545E49D2D058834947EAF7717599EEF89D74484D0A", "0B50B2C0DCF69666E79E81180A9E71C9D56BA283BAC0365210FEE20747273346" }),
                (tryAppraise, new[] { "99438B7BDD61958552668044CD9B85C3EDAD27E15B7A4CF6CF2FFF72107BF4A2" }),
                (Find(manager, "NeedIsCritical"), new[] { "733524BD2DC1152AB95745ED0BC4525B5A36BE121F517D403563119610AA724F" }),
                (Find(manager, "NeedIsInCriticalState"), new[] { "F28870E50BC718A992EA75071F5C71A249956562407FD117E7CEE822DE2A9A8F" }),
                (Find(manager, "NeedIsBelowWarningThreshold"), new[] { "F45B925AB800BFCCB913DDBF0FAC3FA3B0D7A17A7F859466CE5103D99FE31992" }),
                (Find(manager, "GetNeed"), new[] { "427098C6BF3EB30FC18400D88DB98277725C2E4562C5D96BDEE04380DCA551C2", "8E3025E374DF2B34346BBEDD95BBB491D8A5698C1CAAD221B29C641473C4A24A" }),
                (Find(typeof(Need), "TryAppraise", 2), new[] { "B62B74A0549C22D3E79F6888E042998BA575023395BBBADB4104B3A571452BFB" }),
            };
            // Raw-IL fingerprints from the reviewed 1.1.2.4 and 1.0.13.1 APIs.
            foreach (var (method, hashes) in checks)
                if (!RuntimePatches.ReviewedBody(method, hashes)) throw new InvalidOperationException(method.Name + " is not a reviewed build");
            if (typeof(Appraiser).GetField("_needManager", RuntimePatches.All)?.FieldType != manager) throw new MissingFieldException(typeof(Appraiser).FullName, "_needManager");
            foreach (var (field, type) in new[] { ("_needManager", manager), ("_onlyCritical", typeof(bool)), ("_onlyCriticalState", typeof(bool)), ("_belowWarningThreshold", typeof(bool)) })
                if (typeof(NeedFilter).GetField(field, RuntimePatches.All)?.FieldType != type) throw new MissingFieldException(typeof(NeedFilter).FullName, field);
            // Bypassed or reduced on the shared path: the filter, the manager's
            // id lookups and GetNeed itself (called once instead of up to three times).
            var methods = checks.Skip(1).Take(6).Select(c => c.Method).ToArray();
            foreach (var method in methods.Append(_appraise))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _shape = RuntimePatches.OriginalShape(harmonyType, _appraise);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            var hooks = new[] { nameof(Observe0), nameof(Observe1), nameof(Observe2), nameof(Observe3), nameof(Observe4), nameof(Observe5) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
            apply(_appraise, null, null, nameof(RewriteAppraise), null);
        }, typeof(NeedAppraisalLookup));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[T3MP] Need appraisal single lookup installed.");
    }

    internal static void Revalidate()
    {
        if (!Installed || _harmonyType == null || _appraise == null) return;
        try
        {
            if (Volatile.Read(ref _invalidated) == 0 &&
                !_guards.Select(g => g.Method).Append(_appraise).Any(m => RuntimePatches.ForeignPatched(_harmonyType, m, Owner, false))) return;
            Interlocked.Exchange(ref _invalidated, 1);
            RuntimePatches.Unpatch(_harmonyType, Owner);
            Installed = false;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _invalidated, 1);
            Debug.LogWarning("[T3MP] Need appraisal revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    private static float AppraiseEffects(Appraiser self, ImmutableArray<InstantEffect> effects, NeedFilter needFilter)
    {
        float num = 0f;
        bool flag = false;
        for (int i = 0; i < effects.Length; i++)
        {
            InstantEffect instantEffect = effects[i];
            string needId = instantEffect.NeedId;
            // Native order: the effect is converted, then the manager (re-read
            // per effect) resolves the need inside TryAppraise.
            Effect effect = Effect.From(instantEffect);
            var manager = self._needManager;
            float points;
            bool passes;
            // The filter's manager must be this manager for one lookup to serve both.
            if (Active && !ReferenceEquals(manager, null) && !ReferenceEquals(needFilter, null) && ReferenceEquals(needFilter._needManager, manager))
            {
                SharedLookups++;
                Need need = manager.GetNeed(needId);
                if (!need.TryAppraise(effect, out points)) return 0f;
                if (Active)
                {
                    // NeedFilter.Filter on the resolved need, same predicate order.
                    if (needFilter._onlyCritical && !need.IsCritical) passes = false;
                    else if (needFilter._onlyCriticalState && !need.IsInCriticalState) passes = false;
                    else if (needFilter._belowWarningThreshold) passes = need.IsBelowWarningThreshold;
                    else passes = true;
                }
                else passes = needFilter.Filter(needId);
            }
            else
            {
                NativeLookups++;
                if (!manager!.TryAppraise(needId, effect, out points)) return 0f;
                passes = needFilter!.Filter(needId);
            }
            if (passes && points > 0f) flag = true;
            num += points;
        }
        if (!flag) return 0f;
        return num;
    }

    private static IEnumerable<T> Observe0<T>(IEnumerable<T> i) => Observe(i, 0);
    private static IEnumerable<T> Observe1<T>(IEnumerable<T> i) => Observe(i, 1);
    private static IEnumerable<T> Observe2<T>(IEnumerable<T> i) => Observe(i, 2);
    private static IEnumerable<T> Observe3<T>(IEnumerable<T> i) => Observe(i, 3);
    private static IEnumerable<T> Observe4<T>(IEnumerable<T> i) => Observe(i, 4);
    private static IEnumerable<T> Observe5<T>(IEnumerable<T> i) => Observe(i, 5);
    private static IEnumerable<T> Observe<T>(IEnumerable<T> instructions, int index)
    {
        var list = new List<T>(instructions);
        var guard = _guards[index];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
            Interlocked.Exchange(ref _invalidated, 1);
        return list;
    }

    private static IEnumerable<T> RewriteAppraise<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        if (Interlocked.Increment(ref _generations) != 1 || _shape == null ||
            !RuntimePatches.SameShape(_shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        return RuntimePatches.CallAndReturn<T>(typeof(NeedAppraisalLookup).GetMethod(nameof(AppraiseEffects), RuntimePatches.All)!, 3);
    }
}
