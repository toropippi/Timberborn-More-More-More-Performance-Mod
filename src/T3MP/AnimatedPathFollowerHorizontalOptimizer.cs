using System;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Timberborn.CharacterMovementSystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class AnimatedPathFollowerHorizontalOptimizer
{
    private static readonly object LockObject = new object();
    private static Vector3Setter? _setCurrentPosition;
    private static Vector3Setter? _setCurrentDirection;
    private static FloatSetter? _setCurrentSpeed;
    private static FloatSetter? _setCurrentXRotation;
    private static FloatSetter? _setCurrentDistanceToPathCorner;
    private static IntSetter? _setCurrentGroupId;
    private static bool _initialized;

    private static long _attempts;
    private static long _handled;
    private static long _fallbacks;
    private static long _nonHorizontal;
    private static long _zeroVector;
    private static int _warningCount;

    private delegate void Vector3Setter(object instance, Vector3 value);
    private delegate void FloatSetter(object instance, float value);
    private delegate void IntSetter(object instance, int value);

    public static bool TryPlaceBetweenCorners(object follower, AnimatedPathCorner previousCorner, AnimatedPathCorner nextCorner, float timeInSeconds)
    {
        if (!BenchmarkSettings.EnableAnimatedPathFollowerHorizontalOptimizer ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return false;
        }

        Interlocked.Increment(ref _attempts);
        try
        {
            if (!EnsureInitialized(follower.GetType()) ||
                _setCurrentPosition is null ||
                _setCurrentDirection is null ||
                _setCurrentSpeed is null ||
                _setCurrentXRotation is null ||
                _setCurrentDistanceToPathCorner is null ||
                _setCurrentGroupId is null)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            var vector = nextCorner.Position - previousCorner.Position;
            if (Mathf.Abs(vector.y) > 0.0001f)
            {
                Interlocked.Increment(ref _nonHorizontal);
                return false;
            }

            var duration = nextCorner.Time - previousCorner.Time;
            if (Mathf.Abs(duration) <= 0.000001f)
            {
                Interlocked.Increment(ref _fallbacks);
                return false;
            }

            var normalizedTime = (timeInSeconds - previousCorner.Time) / duration;
            _setCurrentPosition(follower, previousCorner.Position + vector * normalizedTime);

            var sqrMagnitude = vector.sqrMagnitude;
            if (sqrMagnitude <= 0.0000001f)
            {
                _setCurrentDirection(follower, Vector3.zero);
                Interlocked.Increment(ref _zeroVector);
            }
            else
            {
                _setCurrentDirection(follower, vector / Mathf.Sqrt(sqrMagnitude));
            }

            _setCurrentXRotation(follower, 0f);
            _setCurrentDistanceToPathCorner(follower, nextCorner.DistanceToPathCorner);
            _setCurrentSpeed(follower, previousCorner.Speed);
            _setCurrentGroupId(follower, previousCorner.GroupId);
            Interlocked.Increment(ref _handled);
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Increment(ref _warningCount) <= 3)
            {
                Debug.LogWarning($"[T3MP] AnimatedPathFollower horizontal optimizer fallback: {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
    }

    public static void LogAndReset(long aggregateId)
    {
        var attempts = Interlocked.Exchange(ref _attempts, 0);
        var handled = Interlocked.Exchange(ref _handled, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        var nonHorizontal = Interlocked.Exchange(ref _nonHorizontal, 0);
        var zeroVector = Interlocked.Exchange(ref _zeroVector, 0);
        if (attempts == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] AnimatedPathFollowerHorizontal aggregate={0}, enabled={1}, attempts={2}, handled={3}, handledRate={4:F3}, nonHorizontal={5}, zeroVector={6}, fallbacks={7}",
            aggregateId,
            BenchmarkSettings.EnableAnimatedPathFollowerHorizontalOptimizer,
            attempts,
            handled,
            attempts > 0 ? (double)handled / attempts : 0.0,
            nonHorizontal,
            zeroVector,
            fallbacks));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _attempts, 0);
        Interlocked.Exchange(ref _handled, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
        Interlocked.Exchange(ref _nonHorizontal, 0);
        Interlocked.Exchange(ref _zeroVector, 0);
    }

    private static bool EnsureInitialized(Type followerType)
    {
        if (_initialized)
        {
            return _setCurrentPosition is not null;
        }

        lock (LockObject)
        {
            if (_initialized)
            {
                return _setCurrentPosition is not null;
            }

            _setCurrentPosition = CreateFieldSetter<Vector3Setter>(followerType, "<CurrentPosition>k__BackingField", typeof(Vector3));
            _setCurrentDirection = CreateFieldSetter<Vector3Setter>(followerType, "<CurrentDirection>k__BackingField", typeof(Vector3));
            _setCurrentSpeed = CreateFieldSetter<FloatSetter>(followerType, "<CurrentSpeed>k__BackingField", typeof(float));
            _setCurrentXRotation = CreateFieldSetter<FloatSetter>(followerType, "<CurrentXRotation>k__BackingField", typeof(float));
            _setCurrentDistanceToPathCorner = CreateFieldSetter<FloatSetter>(followerType, "<CurrentDistanceToPathCorner>k__BackingField", typeof(float));
            _setCurrentGroupId = CreateFieldSetter<IntSetter>(followerType, "<CurrentGroupId>k__BackingField", typeof(int));
            _initialized = true;
            return _setCurrentPosition is not null &&
                _setCurrentDirection is not null &&
                _setCurrentSpeed is not null &&
                _setCurrentXRotation is not null &&
                _setCurrentDistanceToPathCorner is not null &&
                _setCurrentGroupId is not null;
        }
    }

    private static TDelegate? CreateFieldSetter<TDelegate>(Type ownerType, string fieldName, Type valueType)
        where TDelegate : Delegate
    {
        var field = ownerType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null)
        {
            Debug.LogWarning($"[T3MP] AnimatedPathFollower field was not found: {fieldName}");
            return null;
        }

        var method = new DynamicMethod(
            string.Concat("T3MP_AnimatedPathFollower_Set_", fieldName),
            typeof(void),
            new[] { typeof(object), valueType },
            typeof(AnimatedPathFollowerHorizontalOptimizer).Module,
            true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ownerType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return (TDelegate)method.CreateDelegate(typeof(TDelegate));
    }
}
