using System;
using System.Collections.Generic;
using System.Reflection;
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
// every speed-provider call (it reads the current position) and once after the
// loop.
// Coarse mode (default) additionally advances corner to corner where the
// segment cannot touch the stopping proximity of the path end; it is not
// bit-exact with vanilla (low-order position bits, fewer animated corners, a
// corner arrival can shift by one tick at a tick edge). Shares the
// walker-delegate owner.
// -t3mpTestNoMovementCoarse keeps the exact loop; -t3mpTestNoMovementSubsteps
// keeps the native body. The replay audit against the native body lives in
// the test driver (MovementValidation), which calls RunSubsteps through
// reflection; the product carries no comparison code (docs/DIAGNOSTICS.md).
internal static class MovementSubsteps
{
    internal static bool Installed => WalkerSpeedDelegates.SubstepsInstalled;
    internal static string Summary() => WalkerSpeedDelegates.SubstepSummary();
}

internal static partial class WalkerSpeedDelegates
{
    private static bool _substepsInstalled, _coarse;
    // Plain call counter for the driver's state report; no timing, no audit.
    private static long _substepCalls;
    internal static bool SubstepsInstalled => _substepsInstalled;
    internal static string SubstepSummary() => !_substepsInstalled ? "disabled" : "calls=" + _substepCalls + ",coarse=" + _coarse;

    private static void InstallSubsteps(Func<MethodBase, string?, string?, string?, string?, object?> apply)
    {
        if (!ModSettings.EnableMovementSubsteps) return;
        _coarse = ModSettings.CoarseMovementSubsteps;
        apply(RuntimePatches.Find(typeof(PathFollower), "MoveAlongPath"), null, null, nameof(RewriteAlong), null);
        _substepsInstalled = true;
        Debug.Log("[T3MP] Movement sub-steps installed.");
    }

    private static IEnumerable<T> RewriteAlong<T>(IEnumerable<T> instructions) =>
        RuntimePatches.CallAndReturn<T>(typeof(WalkerSpeedDelegates).GetMethod(nameof(MoveAlongPath), RuntimePatches.All)!, 4);

    // Replacement body. The live state is read on entry; nothing is cached.
    private static void MoveAlongPath(PathFollower self, float tickDeltaTime, string animationName, Func<float> movementSpeedProvider)
    {
        _substepCalls++;
        MoveLocally(self, tickDeltaTime, animationName, movementSpeedProvider);
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
        RunSubsteps(self, transform, corners, ref position, tickDeltaTime, movementSpeedProvider, speedLimitIfCloseToTarget,
            ref num, timeFromLastPathPoint, ref remainingTime, ref reachedTarget, groupId, _coarse);
        transform.position = position;
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
                    // The speed provider reads the walker's position from the transform.
                    transform.position = position;
                    num = self.GetMovementSpeed(movementSpeedProvider);
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
