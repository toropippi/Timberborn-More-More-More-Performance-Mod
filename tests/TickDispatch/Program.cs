using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using HarmonyLib;

// Execute the real game loop and wrappers under CoreCLR with generated
// components/timers. Only Unity logging is suppressed; no game body or
// production fingerprint is replaced in the equivalence runs.
internal static class Program
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int foreignCalls;
    private static MethodBase? failTarget;
    private static bool SuppressLog() => false;
    private static void ForeignPrefix() => foreignCalls++;
    private static bool ForceDisabled(ref bool __result) { foreignCalls++; __result = false; return false; }
    private static bool RejectBody(ref bool __result) { __result = false; return false; }
    private static void FailPatch(MethodBase original)
    {
        if (Equals(original, failTarget)) throw new InvalidOperationException("injected patch failure");
    }
    private static IEnumerable<CodeInstruction> ChangedBody(IEnumerable<CodeInstruction> input)
    { yield return new CodeInstruction(OpCodes.Nop); foreach (var i in input) yield return i; }
    private static IEnumerable<CodeInstruction> ObserveBody(IEnumerable<CodeInstruction> input) => input;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static void Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("TickDispatch <Code.dll> <Managed directory> <equivalence|frontier|install-failure|before|after-enabled|after-tick|after-loop|during-enabled|during-tick|getter-hook|unknown>");
        var native = new Harness(args[1]);
        var patched = new Harness(args[1]);
        var feature = patched.LoadFeature(args[0]);
        var mode = args[2];
        if (mode == "excluded")
        {
            Check(feature == null, "normal build contains the trial");
            Console.WriteLine("PASS trial excluded from normal build"); return;
        }
        var foreign = new Harmony("tick-dispatch.tests.foreign");
        var prefix = new HarmonyMethod(typeof(Program).GetMethod(nameof(ForeignPrefix), All));
        var enabled = patched.Metered.GetMethod("get_Enabled", All)!;
        var tick = patched.Metered.GetMethod("StartAndTick", All) ?? patched.Metered.GetMethod("Tick", All)!;
        if (mode == "before") foreign.Patch(tick, prefix: prefix);
        if (mode == "before-base") foreign.Patch(patched.Base.GetMethod("get_Enabled", All), prefix: prefix);
        if (mode == "unknown") foreign.Patch(feature.Assembly.GetType("T3MP.Runtime.RuntimePatches")!.GetMethod("ReviewedBody", All),
            prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(RejectBody), All)));
        if (mode == "install-failure")
        {
            failTarget = patched.Loop;
            foreign.Patch(typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5),
                prefix: new HarmonyMethod(typeof(Program).GetMethod(nameof(FailPatch), All)));
        }
        var frontier = feature.Assembly.GetType("T3MP.Runtime.TickFrontier")!;
        if (mode == "frontier") frontier.GetMethod("Install", All)!.Invoke(null, new object[] { typeof(Harmony), typeof(HarmonyMethod),
            typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5) });
        feature.GetMethod("Install", All)!.Invoke(null, new object[] { typeof(Harmony), typeof(HarmonyMethod),
            typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5) });
        bool Active() => Equals(feature.GetProperty("Active", All)!.GetValue(null), true);
        if (mode is "before" or "before-base" or "unknown" or "install-failure")
        {
            Check(!Active(), "unsafe installation accepted");
            foreach (var m in new[] { enabled, tick, patched.Loop, patched.Base.GetMethod("get_Enabled", All)! })
                Check(Harmony.GetPatchInfo(m)?.Owners.Contains("t3mp.runtime.tick-dispatch") != true, "partial install retained");
            Console.WriteLine("PASS " + mode); return;
        }
        Check(Active(), "actual production installer rejected reviewed game IL");
        if (mode == "frontier")
        {
            frontier.GetMethod("Revalidate", All)!.Invoke(null, null);
            Check(Equals(frontier.GetProperty("Installed", All)!.GetValue(null), true) && Active(), "Frontier coexistence failed");
            Check(native.Run(17, 3) == patched.Run(17, 3), "coexisting dispatch changed loop behavior");
            Console.WriteLine("PASS Frontier installation/revalidation coexistence; native Unity object lifecycle is not exercised offline"); return;
        }
        if (mode == "equivalence")
        {
            var count = 0;
            for (var seed = 0; seed < 500; seed++)
                for (var scenario = 0; scenario < 18; scenario++)
                {
                    var a = native.Run(seed, scenario); var b = patched.Run(seed, scenario);
                    Check(a == b, $"mismatch seed={seed} scenario={scenario}\nnative:{a}\npatch:{b}"); count++;
                }
            feature.GetMethod("Revalidate", All)!.Invoke(null, null);
            Check(Active(), "unchanged load disabled feature");
            // Only the inner loop is rewritten; entity activeInHierarchy and
            // exception wrapper must retain their original instruction stream.
            Check(Harmony.GetPatchInfo(patched.Entity.GetMethod("Tick", All)) == null, "outer entity tick was patched");
            Console.WriteLine($"PASS {count} native ordered trace/exception/state comparisons; game TickSystem MVID={patched.Metered.Assembly.ManifestModule.ModuleVersionId}; CoreCLR only");
            return;
        }
        var target = mode is "getter-hook" or "getter-skip" || mode.EndsWith("base-enabled")
            ? patched.Base.GetMethod("get_Enabled", All)!
            : mode.EndsWith("enabled") ? enabled : mode.EndsWith("loop") ? patched.Loop : tick;
        if (mode is "getter-hook" or "getter-skip")
        {
            var hook = mode == "getter-skip" ? new HarmonyMethod(typeof(Program).GetMethod(nameof(ForceDisabled), All)) : prefix;
            foreign.Patch(native.Base.GetMethod("get_Enabled", All), prefix: hook);
            var expected = native.Run(17, 0); var expectedCalls = foreignCalls; foreignCalls = 0;
            foreign.Patch(target, prefix: hook);
            var actual = patched.Run(17, 0);
            // Include both loop reads and end-state reads. Merely checking >0
            // would allow an inlined loop to bypass all hooks undetected.
            Check(!Active() && expectedCalls > 0 && foreignCalls == expectedCalls && actual == expected,
                $"base getter hook changed order/count/result: native={expectedCalls}, patch={foreignCalls}");
        }
        else if (mode.StartsWith("during"))
        {
            var nativeTarget = mode.EndsWith("base-enabled") ? native.Base.GetMethod("get_Enabled", All)!
                : mode.EndsWith("enabled") ? native.Metered.GetMethod("get_Enabled", All)!
                : native.Metered.GetMethod("StartAndTick", All) ?? native.Metered.GetMethod("Tick", All)!;
            // Give the reference wrapper an unchanged Harmony body before JIT,
            // just like the production observer. Otherwise CoreCLR inlines it
            // into the native loop and ignores late patches entirely (0 calls),
            // which cannot serve as a reference for the late-hook contract.
            new Harmony("tick-dispatch.tests.native-observer").Patch(nativeTarget,
                transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(ObserveBody), All)));
            var expected = native.Run(17, 0, () => foreign.Patch(nativeTarget, prefix: prefix));
            var expectedCalls = foreignCalls;
            foreignCalls = 0;
            var result = patched.Run(17, 0, () => foreign.Patch(target, prefix: prefix));
            Check(!Active() && expectedCalls > 0 && foreignCalls == expectedCalls && result == expected,
                $"mid-loop helper patch order/count changed: native={expectedCalls}, patched={foreignCalls}; " + result);
        }
        else if (mode == "after-loop")
        {
            foreign.Patch(target, transpiler: new HarmonyMethod(typeof(Program).GetMethod(nameof(ChangedBody), All)));
            Check(!Active(), "caller repatch did not disable expansion");
            Check(native.Run(17, 0) == patched.Run(17, 0), "caller fallback changed results");
        }
        else
        {
            foreign.Patch(target, prefix: prefix);
            Check(!Active(), "helper repatch did not disable expansion");
            Check(native.Run(17, 0) == patched.Run(17, 0), "helper fallback changed results");
            Check(foreignCalls > 0, "foreign hook did not run");
        }
        feature.GetMethod("Revalidate", All)!.Invoke(null, null);
        Check(!Equals(feature.GetProperty("Installed", All)!.GetValue(null), true), "load did not remove inactive owner");
        Check(Harmony.GetPatchInfo(target)?.Owners.Contains(foreign.Id) == true, "foreign patch removed");
        Console.WriteLine($"PASS {mode}, foreign calls={foreignCalls}");
    }

    private sealed class Harness
    {
        private readonly AssemblyLoadContext context;
        internal readonly Type Metered, Entity, Base;
        internal readonly MethodInfo Loop;
        private readonly Type component, timer;
        private readonly MethodInfo setEnabled;
        private readonly string managed;
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
            return context.LoadFromAssemblyPath(Path.GetFullPath(path)).GetType("T3MP.Runtime.TickDispatch")!;
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
                        if (scenario == 5 && !reentered) { reentered = true; Loop.Invoke(entity, null); }
                        if (scenario is 6 or 11) throw new ApplicationException("component failure");
                        if (scenario == 10)
                        {
                            var empty = Activator.CreateInstance(Entity, new object?[] { null, Array.CreateInstance(Metered, 0), "empty" })!;
                            Entity.GetField("_tickableComponents", All)!.SetValue(entity,
                                Entity.GetField("_tickableComponents", All)!.GetValue(empty));
                        }
                        // Native code reads metricsEnabled again after Tick.
                        if (scenario == 17) Metered.GetField("_metricsEnabled", All)!.SetValue(wrappers.GetValue(0), false);
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
            for (var turn = 0; turn < 2; turn++)
            {
                try { Loop.Invoke(entity, null); trace.Add("ok"); }
                catch (Exception e)
                {
                    while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                    trace.Add(e.GetType().FullName + ":" + e.Message);
                }
            }
            return string.Join(",", trace) + "|" + string.Join(",", components.Select(c => Base.GetProperty("Enabled")!.GetValue(c)));
        }
    }
}
