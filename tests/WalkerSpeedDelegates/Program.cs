using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using T3MP.Runtime;
using Timberborn.WalkingSystem;
using Timberborn.CharacterMovementSystem;

internal static class Program
{
    private static int _checks, _foreignCalls;
    private static bool _downstreamSawNativeConstructor;
    private static readonly Harmony Foreign = new("walker.test.foreign");
    private static MethodInfo Method(Type type, string name) => type.GetMethod(name, RuntimePatches.All)!;
    private static void Check(bool pass, string message) { _checks++; if (!pass) throw new Exception(message); }
    private static void Install() => WalkerSpeedDelegates.Install(typeof(Harmony), typeof(HarmonyMethod),
        typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5));
    private static string? Failure(Action call)
    {
        try { call(); return null; }
        catch (Exception e) { return e.GetType().FullName + ":" + e.Message; }
    }
    private static void ForeignPrefix() => _foreignCalls++;
    private static bool FixtureFingerprint(ref bool __result) { __result = true; return false; }
    private static void FailMovePatch(MethodBase original)
    {
        if (Equals(original, Method(typeof(WalkerMover), nameof(WalkerMover.Move))))
            throw new InvalidOperationException("injected final patch failure");
    }
    private static IEnumerable<CodeInstruction> ChangeMove(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        _downstreamSawNativeConstructor = list.Any(i => i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo ctor && ctor.DeclaringType == typeof(Func<float>));
        yield return new CodeInstruction(OpCodes.Nop);
        foreach (var instruction in list) yield return instruction;
    }
    private static void AddPrefix(MethodInfo method) => Foreign.Patch(method,
        prefix: new HarmonyMethod(Method(typeof(Program), nameof(ForeignPrefix))));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference TemporaryTarget()
    {
        var target = new WalkerSpeedManager();
        WalkerSpeedDelegates.Get(target);
        return new WeakReference(target);
    }

    private static void Main(string[] args)
    {
        var mode = args.FirstOrDefault() ?? "plain";
        if (mode == "native-il") { NativeIl.Verify(args[1], args[2]); return; }
        if (mode == "unknown-version")
        {
            Install();
            Check(!WalkerSpeedDelegates.Installed, "unknown fixture IL accepted");
            Check(new[] { typeof(WalkerMover), typeof(WalkerSpeedManager), typeof(PathFollower) }
                .SelectMany(t => t.GetMethods(RuntimePatches.All))
                .All(m => Harmony.GetPatchInfo(m)?.Owners.Contains("t3mp.runtime.walker-delegates") != true), "partial install retained");
            Console.WriteLine($"PASS {mode}: {_checks} checks"); return;
        }
        // Fixture methods intentionally have different IL. Only the fingerprint
        // check is substituted in this test process; real API hashes are checked
        // with scripts/IlFingerprint. All feature and Harmony helpers are actual.
        new Harmony("walker.test.fixture-version").Patch(Method(typeof(RuntimePatches), nameof(RuntimePatches.ReviewedBody)),
            prefix: new HarmonyMethod(Method(typeof(Program), nameof(FixtureFingerprint))));
        if (mode == "runtime-baseline" || mode == "feature-disabled")
        {
            RuntimePatches.Install();
            Check(!WalkerSpeedDelegates.Installed, "test switch did not disable delegate reuse");
            Console.WriteLine($"PASS {mode}: {_checks} checks"); return;
        }
        if (mode == "install-failure")
            new Harmony("walker.test.install-failure").Patch(typeof(Harmony).GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5),
                prefix: new HarmonyMethod(Method(typeof(Program), nameof(FailMovePatch))));
        if (mode == "foreign-before") AddPrefix(Method(typeof(PathFollower), nameof(PathFollower.GetMovementSpeed)));
        var originalNull = Failure(() => { WalkerSpeedManager value = null!; Func<float> f = value.GetWalkerSpeedAtCurrentPosition; GC.KeepAlive(f); });
        var original = new WalkerMover(); original._walkerSpeedManager.Value = 10; original.Move(); original.Move();
        var expected = original._walker.PathFollower.Speeds.ToArray();
        Install();
        if (mode == "install-failure")
        {
            Check(!WalkerSpeedDelegates.Installed, "failed install marked successful");
            Check(new[] { typeof(WalkerMover), typeof(WalkerSpeedManager), typeof(PathFollower) }
                .SelectMany(t => t.GetMethods(RuntimePatches.All))
                .All(m => Harmony.GetPatchInfo(m)?.Owners.Contains("t3mp.runtime.walker-delegates") != true), "partial guard installation retained");
            Console.WriteLine($"PASS {mode}: {_checks} checks"); return;
        }
        if (mode == "foreign-before")
        {
            Check(!WalkerSpeedDelegates.Installed, "foreign consumer accepted");
            Check(Harmony.GetPatchInfo(Method(typeof(PathFollower), nameof(PathFollower.GetMovementSpeed)))!.Owners.Contains(Foreign.Id), "foreign hook removed");
            Console.WriteLine($"PASS {mode}: {_checks} checks"); return;
        }
        Check(WalkerSpeedDelegates.Installed, "installation failed: " + string.Join("; ", UnityEngine.Debug.Messages));
        var mover = new WalkerMover(); mover._walkerSpeedManager.Value = 10;
        mover.Move(); var first = mover._walker.PathFollower.LastProvider; mover.Move();
        Check(ReferenceEquals(first, mover._walker.PathFollower.LastProvider), "delegate not reused");
        Check(mover._walker.PathFollower.Speeds.SequenceEqual(expected), "speed values/call order changed");
        Check(mover._walkerSpeedManager.Calls == 4, "speed provider calls skipped");
        mover._walkerSpeedManager.Value = 70; mover.Move();
        Check(mover._walker.PathFollower.Speeds[^2] == 75 && mover._walker.PathFollower.Speeds[^1] == 76, "live speed cached");
        var other = new WalkerSpeedManager();
        Check(!ReferenceEquals(first, WalkerSpeedDelegates.Get(other)), "different targets share callable");
        var weak = TemporaryTarget();
        for (var i = 0; weak.IsAlive && i < 10; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!weak.IsAlive, "weak-table value cycle retains target");
        Check(Failure(() => WalkerSpeedDelegates.Get(null!)) == originalNull, "null binding behavior changed");
        var throwing = new WalkerMover(); throwing._walkerSpeedManager.Throw = true;
        Check(Failure(throwing.Move) == "System.ApplicationException:speed failure", "provider failure altered");
        Check(throwing._walker.PathFollower.Moves == 1 && throwing._walkerSpeedManager.Calls == 1, "failed movement retried");
        var inside = new WalkerMover(); inside._enterer.IsInside = true; inside.Move();
        Check(inside._enterer.Exits == 1 && inside._walkerSpeedManager.Calls == 0 && inside._walker.PathFollower.Moves == 0, "inside branch changed");
        var previous = mover._walker.PathFollower.LastProvider;
        mover._walkerSpeedManager = other; mover.Move();
        Check(!ReferenceEquals(previous, mover._walker.PathFollower.LastProvider), "replaced manager uses stale target");
        if (mode.StartsWith("late-"))
        {
            var target = mode switch {
                "late-provider" => Method(typeof(WalkerSpeedManager), nameof(WalkerSpeedManager.GetWalkerSpeedAtCurrentPosition)),
                "late-along" => Method(typeof(PathFollower), nameof(PathFollower.MoveAlongPath)),
                "late-limit" => Method(typeof(PathFollower), nameof(PathFollower.GetSpeedLimitIfCloseToTarget)),
                "late-speed" => Method(typeof(PathFollower), nameof(PathFollower.GetMovementSpeed)),
                _ => throw new ArgumentException(mode)
            };
            AddPrefix(target);
            mover.Move(); var after = mover._walker.PathFollower.LastProvider; mover.Move();
            Check(!ReferenceEquals(after, mover._walker.PathFollower.LastProvider), "reuse survived consumer/provider patch");
            Check(_foreignCalls > 0, "foreign prefix did not execute");
            WalkerSpeedDelegates.Revalidate();
            Check(!WalkerSpeedDelegates.Installed, "guard did not remove invalidated hooks");
            Check(Harmony.GetPatchInfo(target)!.Owners.Contains(Foreign.Id), "revalidation removed foreign owner");
        }
        else if (mode == "changed-stream" || mode == "downstream-stream")
        {
            var hook = new HarmonyMethod(Method(typeof(Program), nameof(ChangeMove)));
            if (mode == "downstream-stream") hook.after = new[] { "t3mp.runtime.walker-delegates" };
            Foreign.Patch(Method(typeof(WalkerMover), nameof(WalkerMover.Move)),
                transpiler: hook);
            mover.Move(); var after = mover._walker.PathFollower.LastProvider; mover.Move();
            Check(!ReferenceEquals(after, mover._walker.PathFollower.LastProvider), "modified stream still caches");
            Check(Harmony.GetPatchInfo(Method(typeof(WalkerMover), nameof(WalkerMover.Move)))!.Owners.Contains(Foreign.Id), "foreign transpiler hidden");
            Check(_downstreamSawNativeConstructor, "foreign transpiler did not receive native allocation");
        }
        else
        {
            var beforeReload = WalkerSpeedDelegates.Get(other);
            WalkerSpeedDelegates.Revalidate();
            Check(WalkerSpeedDelegates.Installed, "reload disabled compatible feature");
            Check(!ReferenceEquals(beforeReload, WalkerSpeedDelegates.Get(other)), "reload retained delegate table");
            var cached = WalkerSpeedDelegates.Get(other);
            for (int i = 0; i < 10000; i++) GC.KeepAlive(WalkerSpeedDelegates.Get(other));
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++) GC.KeepAlive(WalkerSpeedDelegates.Get(other));
            var reusedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++) GC.KeepAlive(new Func<float>(other.GetWalkerSpeedAtCurrentPosition));
            var originalBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            Console.WriteLine($"CoreCLR allocation-only: 100000 bindings original={originalBytes}B reused={reusedBytes}B");
            Check(reusedBytes == 0 && originalBytes > 0, "steady-state allocation not removed");
        }
        Console.WriteLine($"PASS {mode}: {_checks} checks (fixture IL; no Unity/game execution)");
    }
}
