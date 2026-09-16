using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

// Real game assemblies, generated component/timer fixtures. This suite checks
// ordered component execution; native Unity activation is tested separately.
internal static class Program
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int foreignCalls;
    private static bool SuppressLog() => false;
    private static void ForeignPrefix() => foreignCalls++;
    private static bool ForceDisabled(ref bool __result) { foreignCalls++; __result = false; return false; }
    private static bool SkipTick() { foreignCalls++; return false; }
    private static IEnumerable<CodeInstruction> Observe(IEnumerable<CodeInstruction> instructions) => instructions;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Main(string[] args)
    {
        var native = new Harness(args[1]); var patched = new Harness(args[1]);
        var feature = patched.LoadFeature(args[0]);
        var foreign = new Harmony("tick-port.tests.foreign");
        var mode = args.Length > 2 ? args[2] : "equivalence";
        if (mode == "warm-loop") { native.Run(17, 2); patched.Run(17, 2); }
        var frontier = feature.Assembly.GetType("T3MP.Runtime.TickFrontier")!;
        if (mode == "frontier") frontier.GetMethod("Install", All)!.Invoke(null, new object[] { typeof(Harmony), typeof(HarmonyMethod),
            typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5) });
        var target = patched.Base.GetMethod("get_Enabled", All)!;
        if (mode == "before") foreign.Patch(target, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ForeignPrefix), All)));
        feature.GetMethod("Install", All)!.Invoke(null, new object[] { typeof(Harmony), typeof(HarmonyMethod),
            typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5) });
        bool Active() => Equals(feature.GetProperty("Active", All)!.GetValue(null), true);
        if (mode == "before") { Check(!Active(), "foreign getter accepted"); Console.WriteLine("PASS before"); return; }
        Check(Active(), "installer rejected reviewed IL");
        if (mode == "frontier")
        {
            frontier.GetMethod("Revalidate", All)!.Invoke(null, null);
            feature.GetMethod("Revalidate", All)!.Invoke(null, null);
            Check(Active() && Equals(frontier.GetProperty("Installed", All)!.GetValue(null), true), "Frontier coexistence failed");
        }
        if (mode == "after")
        {
            foreign.Patch(target, prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ForceDisabled), All)));
            Check(!Active(), "late getter hook did not invalidate");
            foreign.Patch(native.Base.GetMethod("get_Enabled", All), prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(ForceDisabled), All)));
            var expected = native.Run(17, 0); var calls = foreignCalls; foreignCalls = 0;
            Check(expected == patched.Run(17, 0) && calls == foreignCalls, "fallback getter result/count mismatch");
            Console.WriteLine("PASS after"); return;
        }
        if (mode is "during" or "during-tick" or "during-tick-skip")
        {
            var nativeTarget = mode == "during" ? native.Base.GetMethod("get_Enabled", All)!
                : native.Metered.GetMethod("StartAndTick", All) ?? native.Metered.GetMethod("Tick", All)!;
            if (mode.StartsWith("during-tick")) target = patched.Metered.GetMethod("StartAndTick", All) ?? patched.Metered.GetMethod("Tick", All)!;
            new Harmony("tick-port.reference-observer").Patch(nativeTarget,
                transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(Observe), All)));
            var hook = new HarmonyMethod(typeof(Program).GetMethod(mode == "during-tick-skip" ? nameof(SkipTick) : nameof(ForeignPrefix), All));
            var expected = native.Run(17, 2, () => foreign.Patch(nativeTarget, prefix: hook));
            var countExpected = foreignCalls; foreignCalls = 0;
            var actual = patched.Run(17, 2, () => foreign.Patch(target, prefix: hook));
            Check(!Active() && Equals(feature.GetProperty("NeedsNativeTraversal", All)!.GetValue(null), true), "late hook did not disable skipping");
            Check(expected == actual && foreignCalls == countExpected, $"mid-sweep calls: native={countExpected}, port={foreignCalls}\nnative:{expected}\nport:{actual}");
            Console.WriteLine("PASS " + mode + "; exact native trace and hook count after invalidation"); return;
        }
        var count = 0;
        for (var seed = 0; seed < 500; seed++) for (var scenario = 0; scenario < 20; scenario++)
        {
            var a = native.Run(seed, scenario); var b = patched.Run(seed, scenario);
            Check(a == b, $"mismatch seed={seed}, scenario={scenario}\nnative:{a}\nport:{b}"); count++;
        }
        Console.WriteLine($"PASS {count} actual-game component traces, exceptions, Enabled states; CoreCLR; {patched.Entity.Assembly.ManifestModule.ModuleVersionId}");
    }

    private sealed class Harness
    {
        private readonly AssemblyLoadContext context;
        internal readonly Type Metered, Entity, Base;
        internal readonly MethodInfo Loop;
        private readonly Type component, timer;
        private readonly MethodInfo setEnabled;
        private readonly string managed;
        private Type? feature;
        internal Harness(string directory)
        {
            managed = Path.GetFullPath(directory);
            context = new AssemblyLoadContext(Guid.NewGuid().ToString(), true);
            context.Resolving += (_, name) => {
                var path = Path.Combine(managed, name.Name + ".dll");
                return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
            };
            var tick = context.LoadFromAssemblyPath(Path.Combine(managed, "Timberborn.TickSystem.dll"));
            Metered = tick.GetType("Timberborn.TickSystem.MeteredTickableComponent")!;
            Entity = tick.GetType("Timberborn.TickSystem.TickableEntity")!;
            Loop = Entity.GetMethod("TickTickableComponents", All)!;
            var tickable = tick.GetType("Timberborn.TickSystem.TickableComponent")!;
            Base = tickable.BaseType!;
            setEnabled = Base.GetMethod("set_Enabled", All)!;
            using var scope = context.EnterContextualReflection();
            var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TickFixtures" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run).DefineDynamicModule("Main");
            var builder = module.DefineType("Component", TypeAttributes.Public, tickable);
            ImplementAction(builder, tickable.GetMethod("Tick", All)!);
            var start = tickable.GetMethod("StartTickable", All);
            if (start != null) ImplementAction(builder, start);
            component = builder.CreateType()!;
            var metric = Metered.GetField("_timerMetric", All)!.FieldType;
            builder = module.DefineType("Timer", TypeAttributes.Public, typeof(object), new[] { metric });
            foreach (var m in metric.GetMethods()) ImplementAction(builder, m);
            timer = builder.CreateType()!;
        }
        private static void ImplementAction(TypeBuilder builder, MethodInfo original)
        {
            var field = builder.DefineField("On" + original.Name, typeof(Action), FieldAttributes.Public);
            var method = builder.DefineMethod(original.Name, MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), Type.EmptyTypes);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke")!); il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(method, original);
        }
        internal Type LoadFeature(string path)
        {
            var core = context.LoadFromAssemblyPath(Path.Combine(managed, "UnityEngine.CoreModule.dll"));
            var logging = new Harmony("tick-dispatch.tests.logging." + Guid.NewGuid().ToString("N"));
            foreach (var name in new[] { "Log", "LogWarning", "LogError" })
                logging.Patch(core.GetType("UnityEngine.Debug")!.GetMethod(name, new[] { typeof(object) }),
                    prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(SuppressLog), All)));
            return feature = context.LoadFromAssemblyPath(Path.GetFullPath(path)).GetType("T3MP.Runtime.TickComponentDispatch")!;
        }
        internal string Run(int seed, int scenario, Action? during = null)
        {
            var rng = new Random(seed);
            var size = rng.Next(3, 16);
            var components = new object[size];
            var wrappers = Array.CreateInstance(Metered, size);
            var trace = new List<string>();
            var metrics = scenario % 2 != 0 || scenario == 14;
            object? entity = null;
            var reentered = false;
            Action? execute = null;
            for (var i = 0; i < size; i++)
            {
                int index = i;
                var c = components[i] = Activator.CreateInstance(component)!;
                setEnabled.Invoke(c, new object[] { i == 0 || i == 1 || rng.Next(3) != 0 });
                component.GetField("OnStartTickable")?.SetValue(c, (Action)(() => {
                    trace.Add("start:" + index);
                    if (scenario == 15 && index == 0) throw new ApplicationException("start failure");
                }));
                component.GetField("OnTick")!.SetValue(c, (Action)(() => {
                    trace.Add("tick:" + index);
                    if (index == 0)
                    {
                        var hook = during; during = null; hook?.Invoke();
                        if (scenario == 2) setEnabled.Invoke(components[1], new object[] { false });
                        if (scenario == 3) setEnabled.Invoke(components[size - 1], new object[] { true });
                        if (scenario == 4) setEnabled.Invoke(c, new object[] { false });
                        if (scenario == 5 && !reentered) { reentered = true; execute!(); }
                        if (scenario is 6 or 11) throw new ApplicationException("component failure");
                        if (scenario == 10)
                        {
                            var empty = Activator.CreateInstance(Entity, new object?[] { null, Array.CreateInstance(Metered, 0), "empty" })!;
                            Entity.GetField("_tickableComponents", All)!.SetValue(entity,
                                Entity.GetField("_tickableComponents", All)!.GetValue(empty));
                        }
                        // Native code reads metricsEnabled again after Tick.
                        if (scenario == 17) Metered.GetField("_metricsEnabled", All)!.SetValue(wrappers.GetValue(0), false);
                        if (scenario is 18 or 19)
                        {
                            var immutable = Entity.GetField("_tickableComponents", All)!.GetValue(entity)!;
                            var backingField = immutable.GetType().GetFields(All).Single(f => f.FieldType.IsArray);
                            var backing = (Array)backingField.GetValue(immutable)!;
                            backing.SetValue(scenario == 18 ? null : wrappers.GetValue(size - 1), 1);
                        }
                    }
                }));
                var t = Activator.CreateInstance(timer)!;
                timer.GetField("OnResume")!.SetValue(t, (Action)(() => {
                    trace.Add("resume:" + index);
                    if (scenario == 7 && index == 1) throw new InvalidOperationException("resume failure");
                    // Timer.Resume can replace the receiver's component before
                    // the native wrapper reads it; do not hoist that read.
                    if (scenario == 13 && index == 0)
                        Metered.GetField("_tickableComponent", All)!.SetValue(wrappers.GetValue(0), components[1]);
                }));
                timer.GetField("OnPause")!.SetValue(t, (Action)(() => {
                    trace.Add("pause:" + index);
                    if (scenario == 9 && index == 1) throw new InvalidOperationException("pause failure");
                }));
                wrappers.SetValue(Activator.CreateInstance(Metered, c, t, metrics), i);
            }
            if (scenario == 8) wrappers.SetValue(null, 1);
            if (scenario == 12) Metered.GetField("_tickableComponent", All)!.SetValue(wrappers.GetValue(1), null);
            if (scenario is 14 or 16) Metered.GetField("_timerMetric", All)!.SetValue(wrappers.GetValue(0), null);
            entity = Activator.CreateInstance(Entity, new object?[] { null, wrappers, "fixture" })!;
            var bucketType = Entity.Assembly.GetType("Timberborn.TickSystem.TickableEntityBucket")!;
            var bucket = Activator.CreateInstance(bucketType, true)!;
            var list = (System.Collections.IDictionary)bucketType.GetField("_tickableEntities", All)!.GetValue(bucket)!;
            list.Add(Guid.Empty, entity);
            void Execute()
            {
                if (feature == null) { Loop.Invoke(entity, null); return; }
                var snapshot = feature.GetMethod("Prepare", All)!.Invoke(null, new[] { bucket });
                if (snapshot == null) { Loop.Invoke(entity, null); return; }
                var lists = (Array)snapshot.GetType().GetField("Lists", All)!.GetValue(snapshot)!;
                if (!Equals(lists.GetValue(0), Entity.GetField("_tickableComponents", All)!.GetValue(entity)))
                { Loop.Invoke(entity, null); return; }
                var wrappersFlat = (Array)snapshot.GetType().GetField("Wrappers", All)!.GetValue(snapshot)!;
                feature.GetMethod("TickComponents", All)!.Invoke(null, new[] { snapshot, lists.GetValue(0), (object)0, wrappersFlat.Length });
            }
            execute = Execute;
            for (var turn = 0; turn < 2; turn++)
            {
                try { Execute(); trace.Add("ok"); }
                catch (Exception e)
                {
                    while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                    if (e is not ApplicationException && e is not InvalidOperationException && e is not NullReferenceException)
                        throw new Exception("Unexpected offline fixture failure", e);
                    trace.Add(e.GetType().FullName + ":" + e.Message);
                }
            }
            return string.Join(",", trace) + "|" + string.Join(",", components.Select(c => Base.GetProperty("Enabled")!.GetValue(c)));
        }
    }
}
