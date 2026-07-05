using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Timberborn.CharacterMovementSystem;
using Timberborn.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class PathFollowerNoAnimationFastMove
{
    private const float RemainingTimeThreshold = 0.0001f;
    private const float RemainingDistanceThreshold = 0.0001f;
    private const int SpeedModifyingThreshold = 3;

    private static readonly ConditionalWeakTable<PathFollower, FollowerState> States = new ConditionalWeakTable<PathFollower, FollowerState>();

    private static GetPathCornersDelegate? _getPathCorners;
    private static GetIntDelegate? _getNextCornerIndex;
    private static SetIntDelegate? _setNextCornerIndex;
    private static GetBoolDelegate? _getMovedAlongPath;
    private static SetBoolDelegate? _setMovedAlongPath;
    private static GetTransformDelegate? _getTransform;
    private static GetNavigationServiceDelegate? _getNavigationService;
    private static GetMovementAnimatorDelegate? _getMovementAnimator;
    private static GetMovementHandlerDelegate? _getMovedAlongPathHandler;
    private static int _initialized;
    private static int _warningCount;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _stopAnimationCalls;
    private static long _cornerSteps;
    private static long _movementEvents;
    private static long _stopwatchTicks;
    private static long _maxStopwatchTicks;

    private delegate IReadOnlyList<PathCorner>? GetPathCornersDelegate(PathFollower instance);

    private delegate int GetIntDelegate(PathFollower instance);

    private delegate void SetIntDelegate(PathFollower instance, int value);

    private delegate bool GetBoolDelegate(PathFollower instance);

    private delegate void SetBoolDelegate(PathFollower instance, bool value);

    private delegate Transform? GetTransformDelegate(PathFollower instance);

    private delegate INavigationService? GetNavigationServiceDelegate(PathFollower instance);

    private delegate MovementAnimator? GetMovementAnimatorDelegate(PathFollower instance);

    private delegate EventHandler<MovementEventArgs>? GetMovementHandlerDelegate(PathFollower instance);

    // PathFollowers whose entity we fast-moved during the current blackout.
    // On blackout exit we resync each one's animation bookkeeping (see
    // ResyncAfterBlackout) so the visual model does not lag/warp afterwards.
    private static readonly HashSet<PathFollower> FastMovedFollowers = new HashSet<PathFollower>();

    public static bool TryMove(PathFollower instance, float tickDeltaTime, string animationName, Func<float> movementSpeedProvider)
    {
        // Peek-agnostic gate: ticks carried by the one-frame render peek must
        // keep the exact fast move, or they pay the full animated MoveAlongPath
        // (measured ~half of all blackout ticks on n10c). The peek frame's
        // visuals stay correct because ResyncAfterBlackout snaps every
        // fast-moved model when the peek lifts the blackout.
        if (!BenchmarkSettings.EnablePathFollowerNoAnimationFastMove ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized ||
            !BenchmarkModeController.BlackoutTickSuppressionActive)
        {
            return false;
        }

        var recordMetrics = BenchmarkSettings.EnableHotOptimizerMetrics;
        if (recordMetrics)
        {
            Interlocked.Increment(ref _attempts);
        }
        var started = recordMetrics ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (!EnsureInitialized() ||
                _getPathCorners is null ||
                _getNextCornerIndex is null ||
                _setNextCornerIndex is null ||
                _setMovedAlongPath is null ||
                _getTransform is null ||
                _getNavigationService is null ||
                _getMovementAnimator is null ||
                _getMovedAlongPathHandler is null)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            var pathCorners = _getPathCorners(instance);
            var transform = _getTransform(instance);
            var navigationService = _getNavigationService(instance);
            if (pathCorners is null ||
                transform is null ||
                navigationService is null ||
                pathCorners.Count < 2)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            var nextCornerIndex = _getNextCornerIndex(instance);
            if (nextCornerIndex <= 0 || nextCornerIndex >= pathCorners.Count)
            {
                if (recordMetrics)
                {
                    Interlocked.Increment(ref _fallbacks);
                }
                return false;
            }

            if (BenchmarkSettings.EnablePathFollowerFastMoveStopAnimation)
            {
                StopAnimationOnce(instance);
            }

            float? speedLimitIfCloseToTarget = GetSpeedLimitIfCloseToTarget(
                pathCorners,
                transform.position,
                nextCornerIndex,
                tickDeltaTime,
                movementSpeedProvider);
            var speed = speedLimitIfCloseToTarget ?? GetMovementSpeed(pathCorners, nextCornerIndex, movementSpeedProvider);
            var remainingTime = tickDeltaTime;
            var reachedTarget = false;
            var localCornerSteps = 0L;

            while (remainingTime > RemainingTimeThreshold &&
                !ReachedLastPathCorner(navigationService, pathCorners, transform.position))
            {
                if (reachedTarget)
                {
                    nextCornerIndex = nextCornerIndex + 1 < pathCorners.Count ? nextCornerIndex + 1 : nextCornerIndex;
                    _setNextCornerIndex(instance, nextCornerIndex);
                    speed = speedLimitIfCloseToTarget ?? GetMovementSpeed(pathCorners, nextCornerIndex, movementSpeedProvider);
                }

                if (speed < float.MaxValue)
                {
                    var target = pathCorners[nextCornerIndex].Position;
                    transform.position = MoveInDirection(transform.position, target, speed, ref remainingTime, out reachedTarget);
                }
                else
                {
                    transform.position = pathCorners[nextCornerIndex].Position;
                    reachedTarget = true;
                }

                localCornerSteps++;
            }

            _setMovedAlongPath(instance, true);
            FastMovedFollowers.Add(instance);
            NotifyAfterMovement(instance, pathCorners, nextCornerIndex);
            if (recordMetrics)
            {
                Interlocked.Add(ref _cornerSteps, localCornerSteps);
                Interlocked.Increment(ref _handled);
            }
            return true;
        }
        catch (Exception exception)
        {
            if (recordMetrics)
            {
                Interlocked.Increment(ref _fallbacks);
            }
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning("[T3MP] PathFollowerNoAnimationFastMove fallback: " + exception);
            }

            return false;
        }
        finally
        {
            if (recordMetrics)
            {
                var elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref _stopwatchTicks, elapsed);
                UpdateMax(ref _maxStopwatchTicks, elapsed);
            }
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        if (!BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            return;
        }

        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var stopAnimationCalls = Interlocked.Exchange(ref _stopAnimationCalls, 0);
        var cornerSteps = Interlocked.Exchange(ref _cornerSteps, 0);
        var movementEvents = Interlocked.Exchange(ref _movementEvents, 0);
        var ticks = Interlocked.Exchange(ref _stopwatchTicks, 0);
        var maxTicks = Interlocked.Exchange(ref _maxStopwatchTicks, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] PathFollowerNoAnimationFastMove aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, fallbacks={5}, stopAnimationCalls={6}, cornerSteps={7}, avgCornerSteps={8:F2}, movementEvents={9}, ms={10:F3}, avgUs={11:F3}, maxMs={12:F3}",
            aggregateId,
            BenchmarkSettings.EnablePathFollowerNoAnimationFastMove,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            fallbacks,
            stopAnimationCalls,
            cornerSteps,
            handled > 0 ? (double)cornerSteps / handled : 0.0,
            movementEvents,
            ToMilliseconds(ticks),
            attempts > 0 ? ToMilliseconds(ticks) * 1000.0 / attempts : 0.0,
            ToMilliseconds(maxTicks)));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _stopAnimationCalls, 0);
        Interlocked.Exchange(ref _cornerSteps, 0);
        Interlocked.Exchange(ref _movementEvents, 0);
        Interlocked.Exchange(ref _stopwatchTicks, 0);
        Interlocked.Exchange(ref _maxStopwatchTicks, 0);
    }

    /// <summary>
    /// Called once when the render blackout turns off. While blacked out we move
    /// the entity transform directly (TryMove) and skip MovementAnimator.Update,
    /// which leaves PathFollower._movedAlongPath set while its animated-path-corner
    /// timestamps stay frozen at their pre-blackout values. If left untouched, the
    /// first MoveAlongPath after the blackout chains new corner times from that
    /// stale past baseline (GetTimeFromLastPathPoint), so AnimatedPathFollower
    /// keeps reporting ReachedDestination against the now-advanced Time.time and
    /// the model snaps once per tick without interpolating -- the visible
    /// "freeze then warp" -- until the beaver happens to start a brand-new path.
    /// Here we clear that state per fast-moved beaver: snap the model to the
    /// entity's current position immediately, and force the next MoveAlongPath to
    /// rebuild fresh timestamps from Time.time.
    /// </summary>
    public static void ResyncAfterBlackout()
    {
        if (FastMovedFollowers.Count == 0)
        {
            return;
        }

        EnsureInitialized();
        foreach (var follower in FastMovedFollowers)
        {
            if (follower is null)
            {
                continue;
            }

            try
            {
                // Next MoveAlongPath rebuilds animation timestamps from Time.time.
                _setMovedAlongPath?.Invoke(follower, false);

                // Snap the visual model to the entity's current (blackout-advanced)
                // position now, rather than leaving it parked until the next tick.
                _getMovementAnimator?.Invoke(follower)?.StopAnimatingMovement();

                if (States.TryGetValue(follower, out var state))
                {
                    state.AnimationStopped = false;
                }
            }
            catch (Exception)
            {
                // The beaver may have been deleted mid-blackout; ignore.
            }
        }

        FastMovedFollowers.Clear();
    }

    private static void StopAnimationOnce(PathFollower instance)
    {
        var state = States.GetValue(instance, _ => new FollowerState());
        if (state.AnimationStopped)
        {
            return;
        }

        var movementAnimator = _getMovementAnimator?.Invoke(instance);
        movementAnimator?.StopAnimatingMovement();
        state.AnimationStopped = true;
        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            Interlocked.Increment(ref _stopAnimationCalls);
        }
    }

    private static float? GetSpeedLimitIfCloseToTarget(
        IReadOnlyList<PathCorner> pathCorners,
        Vector3 position,
        int nextCornerIndex,
        float tickDeltaTime,
        Func<float> movementSpeedProvider)
    {
        if (nextCornerIndex < pathCorners.Count - SpeedModifyingThreshold)
        {
            return null;
        }

        var movementSpeed = GetMovementSpeed(pathCorners, nextCornerIndex, movementSpeedProvider);
        if (!(movementSpeed > 0f) || movementSpeed == float.MaxValue)
        {
            return null;
        }

        var remainingDistance = GetRemainingDistance(pathCorners, position, nextCornerIndex);
        var frameCount = Mathf.Max(1, Mathf.RoundToInt(remainingDistance / movementSpeed / tickDeltaTime));
        return remainingDistance / (frameCount * tickDeltaTime);
    }

    private static float GetMovementSpeed(IReadOnlyList<PathCorner> pathCorners, int nextCornerIndex, Func<float> movementSpeedProvider)
    {
        var speed = pathCorners[nextCornerIndex - 1].Speed;
        if (!(speed < float.MaxValue))
        {
            return float.MaxValue;
        }

        return speed * movementSpeedProvider();
    }

    private static float GetRemainingDistance(IReadOnlyList<PathCorner> pathCorners, Vector3 position, int nextCornerIndex)
    {
        var distance = Vector3.Distance(position, pathCorners[nextCornerIndex].Position);
        for (var index = nextCornerIndex; index < pathCorners.Count - 1; index++)
        {
            distance += Vector3.Distance(pathCorners[index].Position, pathCorners[index + 1].Position);
        }

        return distance;
    }

    private static bool ReachedLastPathCorner(INavigationService navigationService, IReadOnlyList<PathCorner> pathCorners, Vector3 position)
    {
        return navigationService.InStoppingProximity(pathCorners[pathCorners.Count - 1].Position, position);
    }

    private static Vector3 MoveInDirection(Vector3 position, Vector3 target, float speed, ref float remainingTime, out bool reachedTarget)
    {
        var vector = target - position;
        var magnitude = vector.magnitude;
        if (magnitude < RemainingDistanceThreshold)
        {
            reachedTarget = true;
            return target;
        }

        // In blackout mode we do not build animation samples, so the vanilla
        // 0.1-unit visual step cap is unnecessary. Preserve path-corner
        // traversal and the single end-of-tick movement notification.
        var distance = speed * remainingTime;
        if (magnitude > distance)
        {
            remainingTime -= distance / speed;
            reachedTarget = false;
            return position + vector.normalized * distance;
        }

        remainingTime -= magnitude / speed;
        reachedTarget = true;
        return target;
    }

    private static void NotifyAfterMovement(PathFollower instance, IReadOnlyList<PathCorner> pathCorners, int nextCornerIndex)
    {
        if (nextCornerIndex <= 0 || nextCornerIndex >= pathCorners.Count)
        {
            return;
        }

        var handler = _getMovedAlongPathHandler?.Invoke(instance);
        if (handler is null)
        {
            return;
        }

        var from = pathCorners[nextCornerIndex - 1];
        var to = pathCorners[nextCornerIndex];
        var next = nextCornerIndex + 1 < pathCorners.Count ? pathCorners[nextCornerIndex + 1] : (PathCorner?)null;
        handler.Invoke(instance, new MovementEventArgs(from, to, next));
        if (BenchmarkSettings.EnableHotOptimizerMetrics)
        {
            Interlocked.Increment(ref _movementEvents);
        }
    }

    private static bool EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return _getPathCorners is not null &&
                _getNextCornerIndex is not null &&
                _setNextCornerIndex is not null &&
                _getTransform is not null &&
                _getNavigationService is not null &&
                _getMovementAnimator is not null &&
                _getMovedAlongPathHandler is not null;
        }

        try
        {
            var type = typeof(PathFollower);
            _getPathCorners = CreateFieldGetter<GetPathCornersDelegate>(type, "_pathCorners", typeof(IReadOnlyList<PathCorner>));
            _getNextCornerIndex = CreateFieldGetter<GetIntDelegate>(type, "_nextCornerIndex", typeof(int));
            _setNextCornerIndex = CreateFieldSetter<SetIntDelegate>(type, "_nextCornerIndex", typeof(int));
            _getMovedAlongPath = CreateFieldGetter<GetBoolDelegate>(type, "_movedAlongPath", typeof(bool));
            _setMovedAlongPath = CreateFieldSetter<SetBoolDelegate>(type, "_movedAlongPath", typeof(bool));
            _getTransform = CreateFieldGetter<GetTransformDelegate>(type, "_transform", typeof(Transform));
            _getNavigationService = CreateFieldGetter<GetNavigationServiceDelegate>(type, "_navigationService", typeof(INavigationService));
            _getMovementAnimator = CreateFieldGetter<GetMovementAnimatorDelegate>(type, "_movementAnimator", typeof(MovementAnimator));
            _getMovedAlongPathHandler = CreateFieldGetter<GetMovementHandlerDelegate>(type, "MovedAlongPath", typeof(EventHandler<MovementEventArgs>));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[T3MP] Failed to initialize PathFollowerNoAnimationFastMove: " + exception);
        }

        Volatile.Write(ref _initialized, 1);
        return _getPathCorners is not null &&
            _getNextCornerIndex is not null &&
            _setNextCornerIndex is not null &&
            _getTransform is not null &&
            _getNavigationService is not null &&
            _getMovementAnimator is not null &&
            _getMovedAlongPathHandler is not null;
    }

    private static TDelegate? CreateFieldGetter<TDelegate>(Type ownerType, string fieldName, Type fieldType)
        where TDelegate : Delegate
    {
        var field = ownerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null)
        {
            Debug.LogWarning("[T3MP] PathFollower fast move field was not found: " + fieldName);
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_PathFollowerFast_Get_", fieldName),
            fieldType,
            new[] { typeof(PathFollower) },
            typeof(PathFollowerNoAnimationFastMove).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }

    private static TDelegate? CreateFieldSetter<TDelegate>(Type ownerType, string fieldName, Type fieldType)
        where TDelegate : Delegate
    {
        var field = ownerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is null)
        {
            Debug.LogWarning("[T3MP] PathFollower fast move field was not found: " + fieldName);
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_PathFollowerFast_Set_", fieldName),
            typeof(void),
            new[] { typeof(PathFollower), fieldType },
            typeof(PathFollowerNoAnimationFastMove).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static double ToMilliseconds(long stopwatchTicks)
    {
        return stopwatchTicks * 1000.0 / Stopwatch.Frequency;
    }

    private sealed class FollowerState
    {
        public bool AnimationStopped;
    }
}
