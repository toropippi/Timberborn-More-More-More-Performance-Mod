using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.NeedSpecs;
using Timberborn.NeedSystem;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// NeedManager.UpdateNeed evaluates four state predicates before Need.Update
// and again after it (CheckNewState) through small property getters; every
// character does this for every need every tick. The same predicates are
// computed here from the same fields (Points, Enabled, _isNeverPositive,
// IsCritical, NeedSpec.MinimumValue) in the same order, with the live state
// re-read at the same points between event invocations, and the same events
// raised with the same arguments. Nothing is cached across calls.
internal static class NeedUpdateInline
{
    private const string Owner = "t3mp.runtime.need-update";
    private static Type? _harmonyType;
    private static MethodInfo? _update;
    private static RuntimePatches.Shape? _shape;
    private static Guard[] _guards = Array.Empty<Guard>();
    private static FieldInfo? _atMinimumEvent, _favorableEvent, _criticalEvent, _activeEvent;
    private static bool _attempted;
    private static int _invalidated, _generations;
    internal static long InlineCalls, NativeCalls;
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
            var manager = typeof(NeedManager);
            var need = typeof(Need);
            _update = Find(manager, "UpdateNeed");
            var check = Find(manager, "CheckNewState");
            var checks = new (MethodInfo Method, string Hash)[]
            {
                (_update, "738B29AE9ABFB114936AB2282BF552C9A4FDF2384DED373389F3EFF1E9580F37"),
                (check, "42A83932199F0D589E4068B5B17F85D7EDCF82F404AAB0BDC8FB70A7675A5167"),
                (Find(need, "get_IsAtMinimumPoints"), "5AE1E6F7C87742D9F8437CC14F6D7A86DC474E0E333E6854FDDDB782A62252B9"),
                (Find(need, "get_IsFavorable"), "C2E38E782ADA3FCE421CA4F640BF3185D730A4AF99C58177F52EDBA742F8ECBA"),
                (Find(need, "get_IsInCriticalState"), "36A94566906D09C4E55A390BD8963A4423A17D248F342047F24A8C4DA1FE0D84"),
                (Find(need, "get_IsActive"), "7A6E97CD5810B9ED52F97B251202082CF566A3FE68B0BD7B01D9C1B6DADBEA61"),
                (Find(need, "get_Points"), "6C5013842B38AF6F2076BD9C00B63F60402B7A93E5DDD9F9D082FB6BAF04550A"),
                (Find(need, "get_Enabled"), "1F7CFD5FEAA5D7F15A0F16D56C1BC463C129C2BECD6D139680D10134BBEBF743"),
                (Find(need, "get_IsCritical"), "55A3235CD35155C73695CBB4414689120DD9100730595500FD62FF8496A09D16"),
                (Find(need, "Update"), "E566A3ECB7B62EEF2291422A11119BAD2301507D78B83D470129C00E616272A1"),
                (Find(need, "get_NeedSpec"), "A42B33234BAD279EC5D573C04257AB8869486DCF80EFE867031389346376EBBE"),
                (Find(typeof(NeedSpec), "get_MinimumValue"), "B7EBE7AEDC77B4801DED13C1ACB15CA51D05D845E6B59318342E586F83D2A417"),
            };
            // Raw-IL fingerprints, identical in the 1.1.2.4 and 1.0.13.1 APIs.
            foreach (var (method, hash) in checks)
                if (!RuntimePatches.ReviewedBody(method, hash)) throw new InvalidOperationException(method.Name + " is not a reviewed build");
            if (need.GetField("_isNeverPositive", RuntimePatches.All)?.FieldType != typeof(bool)) throw new MissingFieldException(need.FullName, "_isNeverPositive");
            if (typeof(NeedSpec).GetProperty("MinimumValue", RuntimePatches.All)?.PropertyType != typeof(float)) throw new MissingFieldException(typeof(NeedSpec).FullName, "MinimumValue");
            FieldInfo Event(string name) => manager.GetField(name, RuntimePatches.All) is { } field && typeof(Delegate).IsAssignableFrom(field.FieldType) ? field
                : throw new MissingFieldException(manager.FullName, name);
            _atMinimumEvent = Event("NeedChangedIsAtMinimumState");
            _favorableEvent = Event("NeedChangedIsFavorable");
            _criticalEvent = Event("NeedChangedCriticalState");
            _activeEvent = Event("NeedChangedActiveState");
            // Bypassed: CheckNewState and the getters. Need.Update is still called.
            var methods = checks.Skip(1).Select(c => c.Method).ToArray();
            foreach (var method in methods.Append(_update))
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches " + method.DeclaringType?.Name + "." + method.Name);
            _shape = RuntimePatches.OriginalShape(harmonyType, _update);
            _guards = methods.Select(m => new Guard(m, harmonyType)).ToArray();
            var hooks = new[] { nameof(Observe0), nameof(Observe1), nameof(Observe2), nameof(Observe3), nameof(Observe4), nameof(Observe5), nameof(Observe6), nameof(Observe7), nameof(Observe8), nameof(Observe9), nameof(Observe10) };
            for (var i = 0; i < methods.Length; i++) apply(methods[i], null, null, hooks[i], null);
            apply(_update, null, null, nameof(RewriteUpdate), null);
        }, typeof(NeedUpdateInline));
        if (!Installed) Interlocked.Exchange(ref _invalidated, 1);
        if (Installed) Debug.Log("[T3MP] Need update inlining installed.");
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
            Debug.LogWarning("[T3MP] Need update revalidation failed: " + exception.GetBaseException().Message);
        }
    }

    // Same read as `this.Event` before `?.Invoke`: the delegate is captured
    // first, and the arguments are constructed only when it is non-null. Read
    // only when a state actually changed.
    private static EventHandler<T>? Handler<T>(FieldInfo? field, NeedManager self) => field?.GetValue(self) as EventHandler<T>;

    // Native predicates on the live state. Each re-reads Points like the getters.
    private static bool AtMinimum(Need need) { float p = need.Points; return p <= need.NeedSpec.MinimumValue; }
    private static bool Favorable(Need need) { float p = need.Points; return !need._isNeverPositive ? p > 0f : p == 0f; }

    private static void UpdateNeed(NeedManager self, Need need)
    {
        if (!Active)
        {
            NativeCalls++;
            bool a = need.IsAtMinimumPoints, b = need.IsFavorable, c = need.IsInCriticalState, d = need.IsActive;
            need.Update();
            self.CheckNewState(need, a, b, c, d);
            return;
        }
        InlineCalls++;
        // Before Need.Update: the same reads in the native order (Points, then
        // NeedSpec.MinimumValue; IsFavorable; IsCritical; Enabled).
        bool wasAtMinimumPoints = AtMinimum(need);
        bool wasFavorable = Favorable(need);
        bool wasInCriticalState = need.IsCritical && !Favorable(need);
        bool wasActive = need.Enabled && need.Points != 0f;
        need.Update();
        // CheckNewState: each predicate is re-read live after the events raised
        // by the previous one. A guard invalidated by a handler switches the
        // remaining predicates back to the native getters.
        bool isAtMinimumPoints = Active ? AtMinimum(need) : need.IsAtMinimumPoints;
        if (wasAtMinimumPoints != isAtMinimumPoints)
        {
            var handler = Handler<NeedChangedIsAtMinimumStateEventArgs>(_atMinimumEvent, self);
            if (handler != null) handler(self, new NeedChangedIsAtMinimumStateEventArgs(need.NeedSpec, isAtMinimumPoints));
        }
        bool isFavorable = Active ? Favorable(need) : need.IsFavorable;
        if (wasFavorable != isFavorable)
        {
            var handler = Handler<NeedChangedIsFavorableEventArgs>(_favorableEvent, self);
            if (handler != null) handler(self, new NeedChangedIsFavorableEventArgs(need.NeedSpec));
        }
        if (need.IsCritical)
        {
            bool isInCriticalState = Active ? !Favorable(need) : need.IsInCriticalState;
            if (wasInCriticalState != isInCriticalState)
            {
                var handler = Handler<NeedChangedCriticalStateEventArgs>(_criticalEvent, self);
                if (handler != null) handler(self, new NeedChangedCriticalStateEventArgs(need.NeedSpec, isInCriticalState));
            }
        }
        bool isActive = Active ? need.Enabled && need.Points != 0f : need.IsActive;
        if (wasActive != isActive)
        {
            var handler = Handler<NeedChangedActiveStateEventArgs>(_activeEvent, self);
            if (handler != null) handler(self, new NeedChangedActiveStateEventArgs(need.NeedSpec, isActive));
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
    private static IEnumerable<T> Observe8<T>(IEnumerable<T> i) => Observe(i, 8);
    private static IEnumerable<T> Observe9<T>(IEnumerable<T> i) => Observe(i, 9);
    private static IEnumerable<T> Observe10<T>(IEnumerable<T> i) => Observe(i, 10);
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
        return RuntimePatches.CallAndReturn<T>(typeof(NeedUpdateInline).GetMethod(nameof(UpdateNeed), RuntimePatches.All)!, 2);
    }
}
