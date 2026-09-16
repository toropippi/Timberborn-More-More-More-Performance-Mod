using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.CharacterMovementSystem;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// PathFollower.MoveAlongPath with the walker position in a local (v1.2.2).
// The native loop writes Transform.position on every 0.1-unit sub-step and
// reads it back for the next one; a walker transform has about 40 model
// descendants, so each write dirties that subtree. Walker transforms are root
// objects and the set->get round trip is bit-exact, so the same float
// operations in the same order on a local yield identical corners, corner
// index, provider calls and final position. The transform is written before
// every speed-provider call (it reads the current position), once after the
// loop, and on an exception path exactly where the native loop last wrote it.
// Coarse mode (default) additionally advances corner to corner where the
// segment cannot touch the stopping proximity of the path end; it is not
// bit-exact with vanilla (low-order position bits, fewer animated corners, a
// corner arrival can shift by one tick at a tick edge). Shares the
// walker-delegate owner, fingerprints, shape guards and invalidation.
// -t3mpTestNoMovementCoarse keeps the exact loop; -t3mpTestNoMovementSubsteps
// keeps the native body. The replay audit against the native body lives in
// the test driver (MovementValidation), which calls RunSubsteps through
// reflection; the product carries no comparison code (docs/DIAGNOSTICS.md).
internal static class MovementSubsteps
{
    internal static bool Installed => WalkerSpeedDelegates.SubstepsInstalled;
    internal static bool Active => WalkerSpeedDelegates.Substepping;
    internal static string Summary() => WalkerSpeedDelegates.SubstepSummary();
}

internal static partial class WalkerSpeedDelegates
{
    private static bool _substepsEnabled, _coarse;
    // Plain call counters for the driver's state report; no timing, no audit.
    private static long _substepCalls, _substepFallbacks;
    internal static bool SubstepsInstalled => Installed && _substepsEnabled;
    internal static bool Substepping => SubstepsInstalled && Volatile.Read(ref _invalidated) == 0;
    internal static string SubstepSummary() => !_substepsEnabled ? "disabled" :
        "calls=" + _substepCalls + ",fallbacks=" + _substepFallbacks + ",coarse=" + _coarse;

    // Reviewed raw-IL fingerprints, identical in the 1.1.2.4 and 1.0.13.1 APIs.
    // Returns the helper methods that the local loop no longer calls; their
    // observe transpilers join the shared guard chain. Any failure keeps the
    // delegate feature and only disables the sub-step replacement.
    private static MethodInfo[] PrepareSubsteps(Type harmonyType, MethodInfo along)
    {
        _substepsEnabled = false;
        if (!ModSettings.EnableMovementSubsteps) return Array.Empty<MethodInfo>();
        try
        {
            MethodInfo Find(string name) => typeof(PathFollower).GetMethod(name, RuntimePatches.All | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(typeof(PathFollower).FullName, name);
            var reached = Find("ReachedLastPathCorner");
            var add = Find("AddAnimatedPathCorner");
            var direction = Find("MoveInDirection");
            var proximity = typeof(NavigationService).GetMethod("InStoppingProximity", RuntimePatches.All | BindingFlags.DeclaredOnly)
                ?? throw new MissingMethodException(typeof(NavigationService).FullName, "InStoppingProximity");
            var checks = new (MethodInfo Method, string[] Hashes)[]
            {
                (reached, new[] { "998A113F56CA8629F9529B6CD279715F9D13D5D0D694FB61818530A099319B4B" }),
                (add, new[] { "061B1D84724C171ABBEAF9BDA1CA67E1F4A591E1FCD2794194F69FFF95E3F1B8" }),
                (direction, new[] { "13B40AE1B731C22F8F2248ADB766718D03E89FBCEEAF5475C036EBD8C582A077" }),
                (Find("GetRemainingDistance"), new[] { "AC7CC077007B61EF4693FA834DE2769C68F04EB7A27E7AFF7CD3C45D58CC8ACB" }),
                (Find("GetTimeFromLastPathPoint"), new[] { "F4B417B338A6D290ACE03AECA7CD031E68DF21BD3EBD80C8C9C03975B7E4B9D6" }),
                (Find("AddSmoothingAnimatedPathCorner"), new[] { "BDF04CBC81D7AB2BD7793CDAFEFAD26A27FBF131C7E644FBB59E0C492489E8FF" }),
                (Find("NotifyAfterMovement"), new[] { "BB54FA46D093E1FF52731432C908F20F8278B80CFC9241E6416EC9411CD30DC1" }),
                // 1.1.2.4 and 1.0.13.1 bodies.
                (proximity, new[] { "7FC025D3E2F263BAF78B3C6BAC2A8E6BD190940506F46A4848A72FBC4EA6F91E", "B615D3A9F70691E3B118D102AEFCDD5DFD887F14585125BC823E6AD320DA9945" }),
            };
            foreach (var (method, hashes) in checks)
                if (!RuntimePatches.ReviewedBody(method, hashes)) throw new InvalidOperationException(method.Name + " is not a reviewed build");
            if (along.GetMethodBody()!.ExceptionHandlingClauses.Count != 0) throw new InvalidOperationException("unexpected movement body shape");
            foreach (var field in new[] { "_navigationService", "_movementAnimator", "_transform", "_animatedPathCorners", "_pathCorners", "_nextCornerIndex", "_movedAlongPath", "RemainingTimeThreshold" })
                if (typeof(PathFollower).GetField(field, RuntimePatches.All) == null) throw new MissingFieldException(typeof(PathFollower).FullName, field);
            foreach (var (method, _) in checks)
                if (RuntimePatches.ForeignPatched(harmonyType, method, Owner, false))
                    throw new InvalidOperationException("another mod patches PathFollower." + method.Name);
            _coarse = ModSettings.CoarseMovementSubsteps;
            _substepsEnabled = true;
            // Bypassed helpers, plus the two callees that observe sub-step
            // positions while the transform is not yet written.
            return new[] { reached, add, direction, proximity };
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Movement sub-steps disabled: " + exception.GetBaseException().Message);
            return Array.Empty<MethodInfo>();
        }
    }

    private static IEnumerable<T> ObserveReached<T>(IEnumerable<T> instructions) => Observe(instructions, 4);
    private static IEnumerable<T> ObserveAdd<T>(IEnumerable<T> instructions) => Observe(instructions, 5);
    private static IEnumerable<T> ObserveDirection<T>(IEnumerable<T> instructions) => Observe(instructions, 6);
    private static IEnumerable<T> ObserveProximity<T>(IEnumerable<T> instructions) => Observe(instructions, 7);

    // Transpiler body for MoveAlongPath: observe the shape like every other
    // guarded method, then replace the body.
    private static IEnumerable<T> ObserveAlongSubsteps<T>(IEnumerable<T> instructions)
    {
        var list = new List<T>(instructions);
        var guard = _guards[0];
        if (Interlocked.Increment(ref guard.Generations) != 1 ||
            !RuntimePatches.SameShape(guard.Shape, RuntimePatches.DescribeShape(list)))
        {
            Interlocked.Exchange(ref _invalidated, 1);
            return list;
        }
        if (!_substepsEnabled) return list;
        return RuntimePatches.CallAndReturn<T>(typeof(WalkerSpeedDelegates).GetMethod(nameof(MoveAlongPath), RuntimePatches.All)!, 4);
    }

    // Replacement body. The live state is read on entry; nothing is cached.
    // Only the reviewed, guarded NavigationService may observe sub-step
    // positions; any other injected INavigationService keeps the native sequence.
    private static void MoveAlongPath(PathFollower self, float tickDeltaTime, string animationName, Func<float> movementSpeedProvider)
    {
        if (Substepping && self._navigationService?.GetType() == typeof(NavigationService)) { _substepCalls++; MoveLocally(self, tickDeltaTime, animationName, movementSpeedProvider); }
        else { _substepFallbacks++; MoveNatively(self, tickDeltaTime, animationName, movementSpeedProvider); }
    }

    // Native sequence through the original helpers, used once a guarded helper
    // gained another patch. Differs from the local loop only in reading and
    // writing the transform through PathFollower's own methods.
    private static void MoveNatively(PathFollower self, float tickDeltaTime, string animationName, Func<float> movementSpeedProvider)
    {
        float? speedLimitIfCloseToTarget = self.GetSpeedLimitIfCloseToTarget(tickDeltaTime, movementSpeedProvider);
        float num = speedLimitIfCloseToTarget ?? self.GetMovementSpeed(movementSpeedProvider);
        int groupId = self._pathCorners[self._nextCornerIndex - 1].GroupId;
        float remainingTime = tickDeltaTime;
        bool reachedTarget = false;
        float timeFromLastPathPoint = self.GetTimeFromLastPathPoint();
        self._animatedPathCorners.Clear();
        self.AddAnimatedPathCorner(self._transform.position, timeFromLastPathPoint, num, groupId);
        while (remainingTime > PathFollower.RemainingTimeThreshold && !self.ReachedLastPathCorner())
        {
            if (reachedTarget)
            {
                self._nextCornerIndex = ((self._nextCornerIndex + 1 < self._pathCorners.Count) ? (self._nextCornerIndex + 1) : self._nextCornerIndex);
                num = speedLimitIfCloseToTarget ?? self.GetMovementSpeed(movementSpeedProvider);
            }
            int groupId2 = self._pathCorners[self._nextCornerIndex - 1].GroupId;
            if (num < float.MaxValue)
            {
                Vector3 position = self._pathCorners[self._nextCornerIndex].Position;
                Vector3 position2 = PathFollower.MoveInDirection(self._transform.position, position, num, ref remainingTime, out reachedTarget);
                float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                self.AddAnimatedPathCorner(position2, time, num, groupId2);
            }
            else
            {
                Vector3 position3 = self._pathCorners[self._nextCornerIndex].Position;
                float time2 = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                self.AddAnimatedPathCorner(position3, time2, num, groupId2);
                reachedTarget = true;
            }
        }
        self.AddSmoothingAnimatedPathCorner(tickDeltaTime, reachedTarget, timeFromLastPathPoint, num);
        self._movementAnimator.AnimateMovementAlongPath(self._animatedPathCorners, animationName);
        self._movedAlongPath = true;
        self.NotifyAfterMovement();
    }

    private static void MoveLocally(PathFollower self, float tickDeltaTime, string animationName, Func<float> movementSpeedProvider)
    {
        float? speedLimitIfCloseToTarget = self.GetSpeedLimitIfCloseToTarget(tickDeltaTime, movementSpeedProvider);
        float num = speedLimitIfCloseToTarget ?? self.GetMovementSpeed(movementSpeedProvider);
        int groupId = self._pathCorners[self._nextCornerIndex - 1].GroupId;
        float remainingTime = tickDeltaTime;
        bool reachedTarget = false;
        float timeFromLastPathPoint = self.GetTimeFromLastPathPoint();
        var corners = self._animatedPathCorners;
        corners.Clear();
        var transform = self._transform;
        var position = transform.position;
        try
        {
            RunSubsteps(self, transform, corners, ref position, tickDeltaTime, movementSpeedProvider, speedLimitIfCloseToTarget,
                ref num, timeFromLastPathPoint, ref remainingTime, ref reachedTarget, groupId, _coarse);
        }
        finally
        {
            // Normal exit and the exception path both leave the transform where
            // the native loop last wrote it.
            transform.position = position;
        }
        self.AddSmoothingAnimatedPathCorner(tickDeltaTime, reachedTarget, timeFromLastPathPoint, num);
        self._movementAnimator.AnimateMovementAlongPath(corners, animationName);
        self._movedAlongPath = true;
        self.NotifyAfterMovement();
    }

    // The native loop with `position` standing in for _transform.position.
    // `list` receives the animated corners; `coarse` selects corner-to-corner
    // stepping. The signature is part of the test driver's replay audit contract.
    private static void RunSubsteps(PathFollower self, Transform transform, List<AnimatedPathCorner> list, ref Vector3 position,
        float tickDeltaTime, Func<float> movementSpeedProvider, float? speedLimitIfCloseToTarget, ref float num,
        float timeFromLastPathPoint, ref float remainingTime, ref bool reachedTarget, int groupId, bool coarse)
    {
        AddCorner(self, list, ref position, position, timeFromLastPathPoint, num, groupId);
        while (remainingTime > PathFollower.RemainingTimeThreshold && !ReachedLast(self, position))
        {
            if (reachedTarget)
            {
                self._nextCornerIndex = ((self._nextCornerIndex + 1 < self._pathCorners.Count) ? (self._nextCornerIndex + 1) : self._nextCornerIndex);
                if (speedLimitIfCloseToTarget == null)
                {
                    // GetMovementSpeed may invoke the provider, which reads the
                    // transform and, for an arbitrary callback, may write it. The
                    // native loop continues from the transform, so read it back.
                    transform.position = position;
                    try { num = self.GetMovementSpeed(movementSpeedProvider); }
                    finally { position = transform.position; }
                }
                else
                {
                    num = speedLimitIfCloseToTarget.GetValueOrDefault();
                }
            }
            int groupId2 = self._pathCorners[self._nextCornerIndex - 1].GroupId;
            if (num < float.MaxValue)
            {
                Vector3 target = self._pathCorners[self._nextCornerIndex].Position;
                // Coarse: one step to the corner unless this segment passes through
                // the stopping proximity of the path end, where the native 0.1-unit
                // steps decide the stop point.
                Vector3 position2 = coarse && !SegmentTouchesEnd(self, position, target)
                    ? MoveToward(position, target, num, ref remainingTime, out reachedTarget)
                    : PathFollower.MoveInDirection(position, target, num, ref remainingTime, out reachedTarget);
                float time = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                AddCorner(self, list, ref position, position2, time, num, groupId2);
            }
            else
            {
                Vector3 position3 = self._pathCorners[self._nextCornerIndex].Position;
                float time2 = timeFromLastPathPoint + tickDeltaTime - remainingTime;
                AddCorner(self, list, ref position, position3, time2, num, groupId2);
                reachedTarget = true;
            }
        }
    }

    // PathFollower.AddAnimatedPathCorner without the transform write.
    private static void AddCorner(PathFollower self, List<AnimatedPathCorner> list, ref Vector3 position, Vector3 next, float time, float speed, int groupId)
    {
        float distanceToPathCorner = Vector3.Distance(next, self._pathCorners[self._nextCornerIndex].Position);
        position = next;
        list.Add(new AnimatedPathCorner(next, time, speed, distanceToPathCorner, groupId));
    }

    // Native sub-step positions drift off the exact segment by rounding; the
    // closest point is pulled this much toward the path end before the
    // proximity test, so grazing cases fall back to native stepping.
    private const float TouchMargin = 0.01f;

    // True when the closest point of the segment position->target to the path
    // end lies inside the stopping proximity, with TouchMargin of allowance.
    // Whenever a native 0.1-unit sub-step on this segment would stop, this is
    // true; the converse is not required (native stepping is then used).
    private static bool SegmentTouchesEnd(PathFollower self, Vector3 position, Vector3 target)
    {
        var pathCorners = self._pathCorners;
        var end = pathCorners[pathCorners.Count - 1].Position;
        var segment = target - position;
        var length2 = segment.sqrMagnitude;
        var t = length2 > 0f ? Mathf.Clamp01(Vector3.Dot(end - position, segment) / length2) : 0f;
        var closest = position + segment * t;
        var toEnd = end - closest;
        var distance = toEnd.magnitude;
        if (distance <= TouchMargin) return true;
        return self._navigationService.InStoppingProximity(end, closest + toEnd * (TouchMargin / distance));
    }

    // PathFollower.MoveInDirection without MaxMovementStep.
    private static Vector3 MoveToward(Vector3 position, Vector3 target, float speed, ref float remainingTime, out bool reachedTarget)
    {
        Vector3 vector = target - position;
        float magnitude = vector.magnitude;
        if (magnitude < PathFollower.RemainingDistanceThreshold)
        {
            reachedTarget = true;
            return target;
        }
        float num = speed * remainingTime;
        if (magnitude > num)
        {
            remainingTime -= num / speed;
            reachedTarget = false;
            return position + vector.normalized * num;
        }
        remainingTime -= magnitude / speed;
        reachedTarget = true;
        return target;
    }

    // PathFollower.ReachedLastPathCorner without the transform read.
    private static bool ReachedLast(PathFollower self, Vector3 position)
    {
        var pathCorners = self._pathCorners;
        return self._navigationService.InStoppingProximity(pathCorners[pathCorners.Count - 1].Position, position);
    }

}
