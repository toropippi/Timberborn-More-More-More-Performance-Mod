using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.CharacterMovementSystem;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Development-only replay audit of the product's movement loop. Enabled by
// -t3mpTestMovementValidate (add -t3mpTestNoMovementCoarse for the exact
// loop); never a benchmark figure.
//
// The product's loop (T3MP.Runtime.WalkerSpeedDelegates.RunSubsteps, reached
// through reflection; its signature is the contract) replays every native
// PathFollower.MoveAlongPath call on a scratch corner list before the
// untouched native body runs. The prefix restores the transform bits and the
// corner index, the postfix compares corners, corner index, arrival state and
// position with the native outcome. Installing this prefix/postfix re-runs the
// product's observe transpiler on MoveAlongPath, which then keeps the native
// body (MovementSubstepsActive=False) and disables walker delegate reuse for
// the run; the product itself carries no comparison code.
//
// Exact mode expects bit-identical results. Coarse mode expects fewer corners
// and low-order position differences and counts only decision differences
// (corner index, arrival) as mismatches. Transform restoration failures are
// never tolerated in any mode.
internal static class MovementValidation
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private delegate void RunSubsteps(PathFollower self, Transform transform, List<AnimatedPathCorner> list, ref Vector3 position,
        float tickDeltaTime, Func<float> movementSpeedProvider, float? speedLimitIfCloseToTarget, ref float num,
        float timeFromLastPathPoint, ref float remainingTime, ref bool reachedTarget, int groupId, bool coarse);

    private static RunSubsteps? _run;
    private static bool _installed, _coarse;
    private static long _calls, _mismatches, _skipped, _unsupported, _cornerCountDiffs, _positionDiffs, _indexDiffs, _arrivalDiffs, _integrityFailures;
    private static float _maxPositionDelta;

    internal static bool Requested => HasFlag("-t3mpTestMovementValidate");
    private static bool HasFlag(string flag) => Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    internal static string Summary() => !_installed ? "disabled" :
        "validated=" + _calls + ",mismatches=" + _mismatches + ",skipped=" + _skipped + ",unsupported=" + _unsupported + ",coarse=" + _coarse +
        ",cornerCountDiffs=" + _cornerCountDiffs + ",positionDiffs=" + _positionDiffs +
        ",maxPositionDelta=" + _maxPositionDelta.ToString("R", CultureInfo.InvariantCulture) +
        ",indexDiffs=" + _indexDiffs + ",arrivalDiffs=" + _arrivalDiffs + ",integrityFailures=" + _integrityFailures;

    internal static void Install()
    {
        if (_installed || !Requested) return;
        try
        {
            Type? Find(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
            var product = Find("T3MP.Runtime.WalkerSpeedDelegates") ?? throw new InvalidOperationException("T3MP is not loaded");
            var run = product.GetMethod("RunSubsteps", All | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException(product.FullName, "RunSubsteps");
            _run = (RunSubsteps)Delegate.CreateDelegate(typeof(RunSubsteps), run);
            _coarse = !HasFlag("-t3mpTestNoMovementCoarse");
            var along = typeof(PathFollower).GetMethod("MoveAlongPath", All | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(typeof(PathFollower).FullName, "MoveAlongPath");
            var harmonyType = Find("HarmonyLib.Harmony") ?? throw new InvalidOperationException("Harmony is not loaded");
            var methodType = Find("HarmonyLib.HarmonyMethod")!;
            var harmony = Activator.CreateInstance(harmonyType, "t3mp.test.movement-validation")!;
            var patch = harmonyType.GetMethods().Single(m => m.Name == "Patch" && m.GetParameters().Length == 5);
            var prefix = Activator.CreateInstance(methodType, typeof(MovementValidation).GetMethod(nameof(Before), All))!;
            var postfix = Activator.CreateInstance(methodType, typeof(MovementValidation).GetMethod(nameof(After), All))!;
            patch.Invoke(harmony, new object?[] { along, prefix, postfix, null, null });
            _installed = true;
            Debug.Log("[T3MPTEST] Movement validation installed (coarse=" + _coarse + ").");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MPTEST] Movement validation unavailable: " + exception.GetBaseException().Message);
        }
    }

    private sealed class Replay
    {
        internal bool Valid;
        internal readonly List<AnimatedPathCorner> Corners = new List<AnimatedPathCorner>(128);
        internal int NextCornerIndex;
        internal Vector3 Position;
    }

    private static bool SameBits(Vector3 a, Vector3 b) =>
        BitConverter.SingleToInt32Bits(a.x) == BitConverter.SingleToInt32Bits(b.x) &&
        BitConverter.SingleToInt32Bits(a.y) == BitConverter.SingleToInt32Bits(b.y) &&
        BitConverter.SingleToInt32Bits(a.z) == BitConverter.SingleToInt32Bits(b.z);

    // PathFollower.ReachedLastPathCorner on a given position.
    private static bool ReachedLast(PathFollower self, Vector3 position)
    {
        var pathCorners = self._pathCorners;
        return self._navigationService.InStoppingProximity(pathCorners[pathCorners.Count - 1].Position, position);
    }

    private static void Before(PathFollower __instance, float tickDeltaTime, Func<float> movementSpeedProvider, out Replay __state)
    {
        var replay = new Replay();
        __state = replay;
        var self = __instance;
        // The product replaces the body only for the reviewed NavigationService;
        // other injected services keep native movement and are not compared.
        if (_run == null || self._navigationService?.GetType() != typeof(NavigationService)) { _skipped++; return; }
        // The replay invokes the live speed provider once more than native does.
        // Only the reviewed walker provider (pure read of the current position)
        // is replay-safe; any other callback is counted, not replayed.
        var providerMethod = movementSpeedProvider?.Method;
        if (providerMethod == null || providerMethod.DeclaringType?.FullName != "Timberborn.WalkingSystem.WalkerSpeedManager" ||
            providerMethod.Name != "GetWalkerSpeedAtCurrentPosition") { _unsupported++; return; }
        Transform? transform = null;
        var captured = false;
        var origin = Vector3.zero;
        var originIndex = 0;
        try
        {
            transform = self._transform;
            origin = transform.position;
            originIndex = self._nextCornerIndex;
            captured = true;
            float? speedLimitIfCloseToTarget = self.GetSpeedLimitIfCloseToTarget(tickDeltaTime, movementSpeedProvider);
            float num = speedLimitIfCloseToTarget ?? self.GetMovementSpeed(movementSpeedProvider);
            int groupId = self._pathCorners[self._nextCornerIndex - 1].GroupId;
            float remainingTime = tickDeltaTime;
            bool reachedTarget = false;
            float timeFromLastPathPoint = self.GetTimeFromLastPathPoint();
            // Like the product's MoveLocally: the loop starts from the position
            // after the initial speed calls; `origin` stays the restoration snapshot.
            var position = transform.position;
            _run(self, transform, replay.Corners, ref position, tickDeltaTime, movementSpeedProvider, speedLimitIfCloseToTarget,
                ref num, timeFromLastPathPoint, ref remainingTime, ref reachedTarget, groupId, _coarse);
            // PathFollower.AddSmoothingAnimatedPathCorner on the local position.
            if (!ReachedLast(self, position))
            {
                int index = Math.Min(reachedTarget ? (self._nextCornerIndex + 1) : self._nextCornerIndex, self._pathCorners.Count - 1);
                var pathCorner = self._pathCorners[index];
                float distance = Vector3.Distance(position, pathCorner.Position);
                float time = timeFromLastPathPoint + tickDeltaTime + distance / num;
                replay.Corners.Add(new AnimatedPathCorner(pathCorner.Position, time, num, distance, pathCorner.GroupId));
            }
            replay.NextCornerIndex = self._nextCornerIndex;
            replay.Position = position;
            replay.Valid = true;
        }
        catch (Exception exception)
        {
            _skipped++;
            if (_skipped <= 3) Debug.LogWarning("[T3MPTEST] Movement replay skipped: " + exception.GetBaseException().Message);
        }
        finally
        {
            if (captured)
            {
                self._nextCornerIndex = originIndex;
                transform!.position = origin;
                if (!SameBits(transform.position, origin)) _integrityFailures++;
            }
        }
    }

    private static void After(PathFollower __instance, Replay __state)
    {
        _calls++;
        if (!__state.Valid) return;
        var self = __instance;
        var native = self._animatedPathCorners;
        var replay = __state.Corners;
        if (_coarse)
        {
            var nativePosition = self._transform.position;
            if (native.Count != replay.Count) _cornerCountDiffs++;
            if (!SameBits(nativePosition, __state.Position))
            {
                _positionDiffs++;
                var delta = Vector3.Distance(nativePosition, __state.Position);
                if (delta > _maxPositionDelta) _maxPositionDelta = delta;
            }
            var indexSame = self._nextCornerIndex == __state.NextCornerIndex;
            var arrivalSame = ReachedLast(self, nativePosition) == ReachedLast(self, __state.Position);
            if (!indexSame) _indexDiffs++;
            if (!arrivalSame) _arrivalDiffs++;
            if (indexSame && arrivalSame) return;
            _mismatches++;
            if (_mismatches <= 5)
                Debug.LogWarning("[T3MPTEST] Movement coarse decision mismatch: nativeIndex=" + self._nextCornerIndex + " replayIndex=" + __state.NextCornerIndex +
                                 " nativePosition=" + nativePosition.ToString("R") + " replayPosition=" + __state.Position.ToString("R"));
            return;
        }
        var same = native.Count == replay.Count && self._nextCornerIndex == __state.NextCornerIndex && SameBits(self._transform.position, __state.Position);
        for (var i = 0; same && i < native.Count; i++)
        {
            var a = native[i];
            var b = replay[i];
            same = SameBits(a.Position, b.Position) && BitConverter.SingleToInt32Bits(a.Time) == BitConverter.SingleToInt32Bits(b.Time) &&
                   BitConverter.SingleToInt32Bits(a.Speed) == BitConverter.SingleToInt32Bits(b.Speed) &&
                   BitConverter.SingleToInt32Bits(a.DistanceToPathCorner) == BitConverter.SingleToInt32Bits(b.DistanceToPathCorner) &&
                   a.GroupId == b.GroupId;
        }
        if (same) return;
        _mismatches++;
        if (_mismatches <= 5)
            Debug.LogWarning("[T3MPTEST] Movement sub-step mismatch: nativeCorners=" + native.Count + " replayCorners=" + replay.Count +
                             " nativeIndex=" + self._nextCornerIndex + " replayIndex=" + __state.NextCornerIndex +
                             " nativePosition=" + self._transform.position.ToString("R") + " replayPosition=" + __state.Position.ToString("R"));
    }
}
