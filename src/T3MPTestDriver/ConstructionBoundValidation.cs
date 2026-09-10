using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bindito.Core.Internal;
using Binder = Bindito.Core.Internal.Binder;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

internal static partial class ConstructionPlanExperiment
{
    private static void ValidateBoundArguments()
    {
        var counts = new[] { _boundPlans, _boundSources, _multiSources, _unboundPlans };
        var cases = 0;
        try
        {
            var methods = new MethodBase[] { typeof(Sample).GetConstructors(All).Single(),
                typeof(Sample).GetMethod("Set", All)!, typeof(Sample).GetMethod("Multi", All)!,
                typeof(Sample).GetMethod("Throw", All)!, typeof(Sample).GetMethod("Nullable", All)!,
                typeof(Sample).GetMethod("Static", All)!, typeof(Sample).GetMethod("Enum", All)!,
                typeof(Sample).GetMethod("Interface", All)!, typeof(Throwing).GetConstructors(All).Single() };
            foreach (var method in methods)
            foreach (var mode in new[] { "normal", "null", "numeric", "wrong", "missing", "receiver", "virtual", "boxed", "nullReceiver" })
            {
                string Run(bool fast)
                {
                    var trace = new List<string>(); var requests = 0;
                    var bank = new InstanceProviderBank(new Binder(null!), null!);
                    object? Value(Type type)
                    {
                        trace.Add(type.Name + ":" + (++requests));
                        if (mode == "null") return null;
                        if (mode == "wrong") return new object();
                        if (mode == "numeric" && type == typeof(int)) return (short)5;
                        if (mode == "boxed" && (type == typeof(int) || type == typeof(int?))) return 7;
                        if (mode == "boxed" && type == typeof(DayOfWeek)) return DayOfWeek.Wednesday;
                        if (type == typeof(int)) return 7;
                        return type == typeof(string) || type == typeof(IComparable) ? "value" + requests : null;
                    }
                    if (mode != "missing")
                    {
                        foreach (var type in method.GetParameters().Select(p => p.ParameterType).Distinct())
                            bank._singleInstanceProviders.Add(type, new InstanceProvider(() => Value(type)!, true));
                        bank._multiInstanceProviders.Add(typeof(string), new List<InstanceProvider> {
                            new InstanceProvider(() => Value(typeof(string))!, true), new InstanceProvider(() => Value(typeof(string))!, true)
                        });
                    }
                    var provider = new ParameterProvider(new InstanceBank(bank), new MultiBindingService());
                    var plan = Build(method, provider)!;
                    if (plan.Sources == null) throw new Exception("Native bound plan did not engage");
                    object? receiver = mode == "nullReceiver" ? null : mode == "receiver" ? new object() : mode == "virtual" ? new Derived() : new Sample("initial", 0);
                    for (var call = 0; call < 2; call++)
                    {
                        try
                        {
                            var value = fast ? plan.Invoke(plan, provider, receiver) : Reflect(plan, receiver, provider.GetParameters(method));
                            trace.Add("result:" + (value is Sample created ? created.State : value?.ToString() ?? "void"));
                        }
                        catch (Exception error)
                        {
                            for (var e = error; e != null; e = e.InnerException)
                                trace.Add(e.GetType().FullName + ":" + e.Message);
                        }
                        trace.Add("receiver:" + (receiver as Sample)?.State);
                    }
                    return string.Join("|", trace);
                }
                var expected = Run(false); var actual = Run(true);
                if (expected != actual) throw new Exception("Bound argument plan differs: " + method + "/" + mode + " expected=" + expected + " actual=" + actual);
                cases++;
            }
            var custom = new Bank();
            var customProvider = new ParameterProvider(custom, custom);
            var fallback = Build(typeof(Sample).GetConstructors(All).Single(), customProvider)!;
            if (fallback.Sources != null) throw new Exception("Custom parameter services were bound");
            if (Build(typeof(Sample).GetMethod("Ref", All)!, customProvider) != null) throw new Exception("Bound by-ref fallback failed");
            ValidateLinks(true);
            if (_boundSources <= counts[1] || _multiSources <= counts[2]) throw new Exception("Bound source fixtures did not engage");
            Debug.Log($"[T3MPDIBOUND] VALIDATE PASS argumentCases={cases} fallbackCases=2 linkCases=8 (first and repeated requests, native reflection values/errors, fresh dependencies, multi order, contexts and late bindings)");
        }
        finally
        {
            _boundPlans = counts[0]; _boundSources = counts[1]; _multiSources = counts[2]; _unboundPlans = counts[3];
        }
    }
}
