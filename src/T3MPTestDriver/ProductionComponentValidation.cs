using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Bindito.Core.Internal;
using Timberborn.BaseComponentSystem;
using Timberborn.BlueprintSystem;
using Binder = Bindito.Core.Internal.Binder;
using NativeScope = Bindito.Core.Internal.Scope;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static class ProductionComponentValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string PatchId = "t3mp.load.component-recipes";
    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null)!;
    private static Type Target => Find("T3MP.Loading.ComponentConstructionPlans");
    private static object? Get(string name) => Target.GetField(name, All)!.GetValue(null);
    private static void Set(string name, object? value) => Target.GetField(name, All)!.SetValue(null, value);
    private static object NewOwners() => Activator.CreateInstance(Target.GetField("_owners", All)!.FieldType)!;
    private static object? _owners { get => Get("_owners"); set => Set("_owners", value); }
    private static Type _harmonyType => (Type)Get("_harmonyType")!;
    private static bool _singleInitializer => (bool)Get("_singleInitializer")!;
    private static bool CheckCompatibility() => (bool)Target.GetMethod("CheckCompatibility", All)!.Invoke(null, null)!;
    private static int _depth { get => (int)Get("_depth")!; set => Set("_depth", value); }
    private static bool _compatible { get => (bool)Get("_compatible")!; set => Set("_compatible", value); }
    private static long _lists { get => (long)Get("_lists")!; set => Set("_lists", value); }
    private static long _plans { get => (long)Get("_plans")!; set => Set("_plans", value); }
    private static long _specReads { get => (long)Get("_specReads")!; set => Set("_specReads", value); }
    private static long _specHits { get => (long)Get("_specHits")!; set => Set("_specHits", value); }
    private static long _providerReads { get => (long)Get("_providerReads")!; set => Set("_providerReads", value); }
    private static long _providerHits { get => (long)Get("_providerHits")!; set => Set("_providerHits", value); }
    private static long _fallbacks { get => (long)Get("_fallbacks")!; set => Set("_fallbacks", value); }

    private static int _foreignSpecReads;
    private static void ForeignSpecRead() => _foreignSpecReads++;
    private record TestSpec : ComponentSpec { public int Value; }
    private sealed record OtherSpec : ComponentSpec { public int Value; }
    private sealed record DerivedSpec : TestSpec;
    private class Value { internal int Sequence; public override string ToString() => GetType().Name + ":" + Sequence; }
    private sealed class LateValue : Value;
    private sealed class InitialComponent : BaseComponent { public override string ToString() => "initial"; }
    private sealed class Factory : IInstanceProviderFactory
    {
        internal Func<Binding, InstanceProvider> Create = null!;
        public InstanceProvider CreateInstanceProvider(Binding binding) => Create(binding);
    }
    private sealed class InitialList : IReadOnlyList<object>
    {
        internal Action Before = null!;
        internal bool Throw;
        public int Count => 1;
        public object this[int index] => "initial";
        public IEnumerator<object> GetEnumerator()
        {
            Before(); yield return "initial";
            if (Throw) throw new InvalidOperationException("initial-list-failure");
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CustomBank : IInstanceBank
    {
        internal Func<object> Next = null!;
        public bool TryGetInstance(Type type, out object value) { value = Next(); return true; }
        public bool TryGetExportedInstance(Type type, out object value) => TryGetInstance(type, out value);
        public IEnumerable<object> GetInstances(Type type) { yield return Next(); }
        public IEnumerable<object> GetExportedInstances(Type type) => GetInstances(type);
    }

    internal static void Validate()
    {
        if (!Environment.GetCommandLineArgs().Contains("-t3mpTestProductionRecipesValidate")) return;
        if (!(bool)Target.GetProperty("Installed", All)!.GetValue(null)!) return;
        var depth = _depth; var owners = _owners; var compatible = _compatible;
        var counts = new[] { _lists, _plans, _specReads, _specHits, _providerReads, _providerHits, _fallbacks };
        var cases = 0;
        try
        {
            if (!CheckCompatibility()) throw new Exception("Component recipe validation has foreign patches");
            foreach (var mode in new[] { "normal", "parent", "private-parent", "late-positive", "register-later",
                         "initial-register", "initial-throw", "factory-throw", "null-factory", "null-initial",
                         "new-blueprint", "new-layout", "copied-layout", "mutable-spec", "missing-spec",
                         "derived-spec-only", "null-type", "default-layout", "custom-bank", "mismatched-parent", "capacity" })
            foreach (var scope in new[] { NativeScope.Transient, NativeScope.Singleton })
            {
                if (_singleInitializer && (mode == "initial-register" || mode == "initial-throw")) continue;
                string Run(bool fast)
                {
                    _depth = fast ? 1 : 0; _compatible = true;
                    _owners = NewOwners();
                    var previousHits = _providerHits;
                    var trace = new List<string>(); var sequence = 0;
                    var parentBinder = new Binder(null!);
                    var parentBank = new InstanceProviderBank(parentBinder, null!);
                    var binder = new Binder(mode == "mismatched-parent" ? null! : parentBinder);
                    var bank = new InstanceProviderBank(binder, parentBank);
                    Binding BindingFor(Type type, bool exported = true) => new Binding(ProvisionBinding.CreateToType(type), scope, exported);
                    var registered = false;
                    var factory = new Factory { Create = binding =>
                    {
                        trace.Add("provider:" + binding.ProvisionBinding.Type.Name);
                        return new InstanceProvider(new Scoper().PlaceInScope(() =>
                        {
                            trace.Add("factory:" + binding.ProvisionBinding.Type.Name + ":" + (++sequence));
                            if (mode == "factory-throw") throw new InvalidOperationException("factory-failure");
                            if (mode == "register-later" && !registered)
                            { binder.Bind(typeof(LateValue), BindingFor(typeof(LateValue))); registered = true; }
                            if (mode == "null-factory") return null!;
                            return binding.ProvisionBinding.Type == typeof(LateValue) ? new LateValue { Sequence = sequence } : new Value { Sequence = sequence };
                        }, binding.Scope), binding.Exported);
                    } };
                    bank.InstanceProviderFactory = factory; parentBank.InstanceProviderFactory = factory;
                    if (mode == "parent" || mode == "private-parent" || mode == "mismatched-parent")
                        parentBinder.Bind(typeof(Value), BindingFor(typeof(Value), mode != "private-parent"));
                    else if (mode != "late-positive" && mode != "initial-register") binder.Bind(typeof(Value), BindingFor(typeof(Value)));
                    var custom = new CustomBank { Next = () => { trace.Add("custom:" + (++sequence)); return new Value { Sequence = sequence }; } };
                    var container = new Container(mode == "custom-bank" ? (IInstanceBank)custom : new InstanceBank(bank), null!, null!, null!);
                    var service = new ComponentCacheService();
                    var instantiator = new BaseInstantiator(null!, container, service);
                    var nativeMethod = typeof(BaseInstantiator).GetMethod("InstantiateComponents", All)!;
                    var initialComponent = new InitialComponent();
                    Blueprint Make(int value) => new Blueprint("same-name", new ComponentSpec[] {
                        new TestSpec { Value = value }, new TestSpec { Value = 999 }, new OtherSpec { Value = value + 1 }
                    }, ImmutableArray<Blueprint>.Empty);
                    var blueprints = new[] { Make(1), Make(101) };
                    if (mode == "missing-spec") blueprints[0] = new Blueprint("same-name", new[] { new TestSpec { Value = 1 } }, ImmutableArray<Blueprint>.Empty);
                    if (mode == "derived-spec-only") blueprints[0] = new Blueprint("same-name", new[] { new DerivedSpec { Value = 1 } }, ImmutableArray<Blueprint>.Empty);
                    var types = ImmutableArray.Create(typeof(TestSpec), typeof(Value), typeof(OtherSpec), typeof(Value));
                    if (mode == "missing-spec") types = ImmutableArray.Create(typeof(Value), typeof(OtherSpec));
                    if (mode == "derived-spec-only") types = ImmutableArray.Create(typeof(TestSpec));
                    if (mode == "null-type") types = ImmutableArray.Create(typeof(Value), (Type)null!);
                    if (mode == "register-later") types = ImmutableArray.Create(typeof(Value), typeof(LateValue));
                    if (mode == "default-layout") types = default;
                    var reversed = types.IsDefault ? types : types.Reverse().ToImmutableArray();
                    var initial = new InitialList { Throw = mode == "initial-throw", Before = () =>
                    {
                        trace.Add("initial");
                        if (mode == "initial-register" && !registered)
                        { binder.Bind(typeof(Value), BindingFor(typeof(Value))); registered = true; }
                    } };
                    object? previous = null;
                    for (var call = 0; call < 4; call++)
                    {
                        if (mode == "late-positive" && call == 1) binder.Bind(typeof(Value), BindingFor(typeof(Value)));
                        var blueprint = blueprints[mode == "new-blueprint" ? call % 2 : 0];
                        if (mode == "mutable-spec") ((TestSpec)blueprint.GetSpec(typeof(TestSpec))).Value = 10 + call;
                        if (mode == "capacity") service.SaveComponentsCount(_singleInitializer ? "same-name" : "name", call == 0 ? 7 : 100);
                        var layout = mode == "new-layout" && call % 2 != 0 ? reversed : types;
                        if (mode == "copied-layout") layout = ImmutableArray.CreateRange(types.ToArray());
                        try
                        {
                            List<object> values;
                            try
                            {
                                values = (List<object>)nativeMethod.Invoke(instantiator, _singleInitializer
                                    ? new object?[] { blueprint, mode == "null-initial" ? null : initialComponent, layout }
                                    : new object?[] { blueprint, "name", mode == "null-initial" ? null : initial, layout })!;
                            }
                            catch (TargetInvocationException e) when (e.InnerException != null)
                            { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
                            string Describe(object value) => value is TestSpec spec ? "spec:" + spec.Value : value is OtherSpec other ? "other:" + other.Value : value?.ToString() ?? "null";
                            trace.Add("result:" + string.Join(",", values.Select(Describe)) + ":capacity=" + values.Capacity);
                            var created = values.FirstOrDefault(value => value is Value);
                            trace.Add("same:" + ReferenceEquals(previous, created)); previous = created;
                            foreach (var spec in values.OfType<TestSpec>())
                                if (!ReferenceEquals(spec, blueprint.GetSpec(typeof(TestSpec)))) throw new Exception("Spec identity changed");
                        }
                        catch (Exception error)
                        {
                            for (var e = error; e != null; e = e.InnerException)
                                trace.Add("error:" + e.GetType().FullName + ":" + e.Message);
                        }
                    }
                    if (fast && mode == "normal" && _providerHits <= previousHits) throw new Exception("Component recipe did not engage");
                    return string.Join("|", trace);
                }
                var expected = Run(false); var actual = Run(true);
                if (expected != actual) throw new Exception("Component recipe differs: " + mode + "/" + scope + " expected=" + expected + " actual=" + actual);
                cases++;
            }
            ValidateForeignGuard();
            Debug.Log($"[T3MPCOMPONENTRECIPE] VALIDATE PASS cases={cases} guardCases=1 (order, first exact spec match, spec identity/mutation, blueprint/layout identity, native scopes/providers/exports, late binding, enumeration and failure timing, capacity, custom and foreign-patch fallback)");
        }
        finally
        {
            _depth = depth; _owners = owners; _compatible = compatible;
            _lists = counts[0]; _plans = counts[1]; _specReads = counts[2]; _specHits = counts[3];
            _providerReads = counts[4]; _providerHits = counts[5]; _fallbacks = counts[6];
        }
    }

    private static void ValidateForeignGuard()
    {
        var id = PatchId + ".fixture";
        var harmony = Activator.CreateInstance(_harmonyType, id);
        var hm = Find("HarmonyLib.HarmonyMethod");
        var patch = _harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
        var target = typeof(Blueprint).GetMethod(nameof(Blueprint.GetSpec), new[] { typeof(Type) })!;
        try
        {
            patch.Invoke(harmony, new object?[] { target, Activator.CreateInstance(hm,
                typeof(ProductionComponentValidation).GetMethod(nameof(ForeignSpecRead), All)), null, null, null });
            _compatible = CheckCompatibility();
            if (_compatible) throw new Exception("Foreign spec hook did not disable recipes");
            _depth = 1; _owners = NewOwners();
            var before = _fallbacks;
            var binder = new Binder(null!); var bank = new InstanceProviderBank(binder, null!);
            var container = new Container(new InstanceBank(bank), null!, null!, null!);
            var instantiator = new BaseInstantiator(null!, container, new ComponentCacheService());
            var blueprint = new Blueprint("guard", new[] { new TestSpec { Value = 7 } }, ImmutableArray<Blueprint>.Empty);
            var types = ImmutableArray.Create(typeof(TestSpec));
            _foreignSpecReads = 0;
            var values = (List<object>)typeof(BaseInstantiator).GetMethod("InstantiateComponents", All)!.Invoke(instantiator,
                _singleInitializer ? new object?[] { blueprint, null, types } : new object?[] { blueprint, "guard", null, types })!;
            if (values.Count != 1 || !ReferenceEquals(values[0], blueprint.Specs[0]) || _foreignSpecReads != 1 || _fallbacks != before + 1)
                throw new Exception("Foreign spec callback was not preserved by fallback");
        }
        finally
        {
            _harmonyType.GetMethod("UnpatchAll", new[] { typeof(string) })!.Invoke(harmony, new object[] { id });
            _compatible = CheckCompatibility();
            _foreignSpecReads = 0;
        }
        if (!_compatible) throw new Exception("Foreign fixture patch was not removed");
    }
}
