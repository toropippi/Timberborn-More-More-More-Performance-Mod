using Timberborn.CharacterMovementSystem;
using Timberborn.WalkingSystem;

namespace T3MP;

/// <summary>
/// Fixes the "beaver stuck in a tube" visual at high game speeds.
///
/// Vanilla moves the simulation entity per tick but replays the visual model
/// through MovementAnimator/AnimatedPathFollower animation samples, whose
/// playback cannot keep up once the mod's uncapped speeds (far beyond
/// vanilla's x3) multiply ticks per frame. The model then lags seconds behind
/// the entity, and everything keyed off the model position lags with it -
/// most visibly TubeVisitor, which keeps the tube travel glow (and the
/// character inside the tube) parked long after the simulation finished the
/// transit.
///
/// The render blackout already solves this by snapping fast-moved models on
/// blackout exit (PathFollowerNoAnimationFastMove.ResyncAfterBlackout). This
/// applies the same recipe continuously for rendered high-speed play: when a
/// model's animated position falls more than a couple of units behind its
/// entity, snap it forward (StopAnimatingMovement) and clear
/// PathFollower._movedAlongPath so the next MoveAlongPath rebuilds animation
/// timestamps from the present instead of chaining from the stale past.
///
/// The distance gate keeps this inert at vanilla speeds: normal playback lag
/// stays well under the vanilla 0.1-unit visual step, so snapping only ever
/// triggers in the high-speed regime only this mod makes reachable.
/// </summary>
internal static class HighSpeedModelLagSnap
{
    private const float SnapDistance = 2f;
    private const float SnapDistanceSqr = SnapDistance * SnapDistance;

    internal static void AfterMovementAnimatorUpdate(MovementAnimator __instance)
    {
        if (!BenchmarkSettings.EnableHighSpeedModelLagSnap)
        {
            return;
        }

        var animatedPathFollower = __instance._animatedPathFollower;
        if (animatedPathFollower is null || animatedPathFollower.Stopped)
        {
            return;
        }

        var lag = animatedPathFollower.CurrentPosition - __instance.Transform.position;
        if (lag.sqrMagnitude <= SnapDistanceSqr)
        {
            return;
        }

        // Same recipe as ResyncAfterBlackout, applied per character on demand.
        __instance.StopAnimatingMovement();
        var pathFollower = __instance.GetComponent<Walker>()?.PathFollower;
        if (pathFollower is not null)
        {
            pathFollower._movedAlongPath = false;
        }
    }
}
