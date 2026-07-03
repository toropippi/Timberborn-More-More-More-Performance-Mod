using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP;

internal static class SoundListenerStaticCameraOptimizer
{
    private static readonly ConditionalWeakTable<object, InstanceState> States = new();

    private static FieldInfo? _cameraServiceField;
    private static PropertyInfo? _cameraTransformProperty;
    private static PropertyInfo? _cameraZoomProperty;
    private static int _reflectionInitialized;
    private static int _reflectionFailed;

    private static long _calls;
    private static long _originalRuns;
    private static long _skips;
    private static long _cameraChanges;
    private static long _fallbacks;

    public static bool ShouldRunOriginal(object instance)
    {
        if (!BenchmarkSettings.EnableSoundListenerStaticCameraOptimizer ||
            BenchmarkSettings.SoundListenerStaticCameraIntervalFrames <= 1 ||
            BenchmarkModeController.CurrentMode != BenchmarkMode.Optimized)
        {
            return true;
        }

        Interlocked.Increment(ref _calls);

        if (!TryCaptureCameraSignature(instance, out var signature))
        {
            Interlocked.Increment(ref _fallbacks);
            return true;
        }

        var frame = Time.frameCount;
        var state = States.GetOrCreateValue(instance);
        if (!state.HasSignature)
        {
            state.LastSignature = signature;
            state.HasSignature = true;
            state.LastOriginalFrame = frame;
            Interlocked.Increment(ref _originalRuns);
            return true;
        }

        if (!signature.NearlyEquals(state.LastSignature))
        {
            state.LastSignature = signature;
            state.LastOriginalFrame = frame;
            Interlocked.Increment(ref _cameraChanges);
            Interlocked.Increment(ref _originalRuns);
            return true;
        }

        if (frame - state.LastOriginalFrame >= BenchmarkSettings.SoundListenerStaticCameraIntervalFrames)
        {
            state.LastOriginalFrame = frame;
            Interlocked.Increment(ref _originalRuns);
            return true;
        }

        Interlocked.Increment(ref _skips);
        return false;
    }

    public static void LogAndReset(long aggregateId)
    {
        var calls = Interlocked.Exchange(ref _calls, 0);
        var originalRuns = Interlocked.Exchange(ref _originalRuns, 0);
        var skips = Interlocked.Exchange(ref _skips, 0);
        var cameraChanges = Interlocked.Exchange(ref _cameraChanges, 0);
        var fallbacks = Interlocked.Exchange(ref _fallbacks, 0);
        if (calls == 0 && originalRuns == 0 && skips == 0 && cameraChanges == 0 && fallbacks == 0)
        {
            return;
        }

        Debug.Log(string.Format(
            CultureInfo.InvariantCulture,
            "[T3MP] SoundListenerStaticCamera aggregate={0}, calls={1}, originalRuns={2}, skips={3}, skipRate={4:F2}, cameraChanges={5}, fallbacks={6}, intervalFrames={7}",
            aggregateId,
            calls,
            originalRuns,
            skips,
            calls > 0 ? (double)skips / calls : 0.0,
            cameraChanges,
            fallbacks,
            BenchmarkSettings.SoundListenerStaticCameraIntervalFrames));
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _originalRuns, 0);
        Interlocked.Exchange(ref _skips, 0);
        Interlocked.Exchange(ref _cameraChanges, 0);
        Interlocked.Exchange(ref _fallbacks, 0);
    }

    private static bool TryCaptureCameraSignature(object soundListener, out CameraSignature signature)
    {
        signature = default;
        if (!TryInitializeReflection(soundListener.GetType()))
        {
            return false;
        }

        try
        {
            var cameraService = _cameraServiceField?.GetValue(soundListener);
            if (cameraService is null)
            {
                return false;
            }

            if (_cameraTransformProperty?.GetValue(cameraService) is not Transform transform)
            {
                return false;
            }

            var zoom = 0f;
            if (_cameraZoomProperty?.GetValue(cameraService) is float zoomValue)
            {
                zoom = zoomValue;
            }

            signature = new CameraSignature(
                transform.position,
                transform.rotation,
                zoom,
                Screen.width,
                Screen.height);
            return true;
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref _reflectionFailed) <= 3)
            {
                Debug.LogWarning("[T3MP] SoundListener static-camera optimizer reflection failed: " + exception.Message);
            }

            return false;
        }
    }

    private static bool TryInitializeReflection(Type soundListenerType)
    {
        if (Volatile.Read(ref _reflectionInitialized) == 1)
        {
            return _cameraServiceField is not null && _cameraTransformProperty is not null;
        }

        _cameraServiceField = soundListenerType.GetField("_cameraService", BindingFlags.Instance | BindingFlags.NonPublic);
        var cameraServiceType = _cameraServiceField?.FieldType;
        if (cameraServiceType is not null)
        {
            _cameraTransformProperty = cameraServiceType.GetProperty("Transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _cameraZoomProperty = cameraServiceType.GetProperty("NormalizedDefaultZoomLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        Volatile.Write(ref _reflectionInitialized, 1);
        if (_cameraServiceField is null || _cameraTransformProperty is null)
        {
            Debug.LogWarning("[T3MP] SoundListener static-camera optimizer could not find CameraService fields.");
            return false;
        }

        return true;
    }

    private sealed class InstanceState
    {
        public bool HasSignature;
        public CameraSignature LastSignature;
        public int LastOriginalFrame;
    }

    private readonly struct CameraSignature
    {
        private readonly Vector3 _position;
        private readonly Quaternion _rotation;
        private readonly float _zoom;
        private readonly int _screenWidth;
        private readonly int _screenHeight;

        public CameraSignature(Vector3 position, Quaternion rotation, float zoom, int screenWidth, int screenHeight)
        {
            _position = position;
            _rotation = rotation;
            _zoom = zoom;
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
        }

        public bool NearlyEquals(CameraSignature other)
        {
            return (_position - other._position).sqrMagnitude <= 0.0001f &&
                Mathf.Abs(Quaternion.Dot(_rotation, other._rotation)) >= 0.99999f &&
                Mathf.Abs(_zoom - other._zoom) <= 0.0001f &&
                _screenWidth == other._screenWidth &&
                _screenHeight == other._screenHeight;
        }
    }
}
