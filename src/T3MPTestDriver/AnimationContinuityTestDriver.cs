using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// コピーしたセーブで位置差を観測し、測定対象の状態は補正しない。
// Test-only: observe entity/model gaps after LateUpdate without repairing them.
public sealed class AnimationContinuityTestDriver : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
    private static readonly float[] Speeds = { 1f, 3f, 50f, 50f, 3f, 0f, 3f };
    private static readonly string[] Names = { "x1", "x3", "x50", "turbo50", "afterTurbo3", "pause", "resume3" };
    private readonly Dictionary<Guid, (Vector3 Entity, Vector3 Model)> _previous = new();
    private Timberborn.EntitySystem.EntityRegistry _registry = null!;
    private bool _armed;
    private SpeedManager _speed = null!;
    private Type _controller = null!;
    private Type _movement = null!;
    private FieldInfo _model = null!;
    private PropertyInfo _position = null!;
    private PropertyInfo _blocked = null!;
    private FieldInfo _hidden = null!;
    private FieldInfo _ticks = null!;
    private int _phase = -1;
    private float _started, _nextSample;
    private long _startTicks;
    private int _samples, _moving, _stationary, _largeGap;
    private int _stationaryUnblocked, _largeGapUnblocked;
    private float _maxGap;
    private float _maxGapUnblocked;

    public void Initialize(SpeedManager speed, Timberborn.EntitySystem.EntityRegistry registry)
    {
        try
        {
            // ゲーム内部の取得失敗は診断エラーとして扱う。
            // Missing game internals fail the diagnostic rather than yielding an empty success.
            _speed = speed;
            _registry = registry;
            _controller = Find("T3MP.BenchmarkModeController");
            _movement = Find("Timberborn.CharacterMovementSystem.MovementAnimator");
            _model = _movement.GetField("_characterModel", All)
                ?? throw new MissingFieldException("MovementAnimator._characterModel");
            _position = _model.FieldType.GetProperty("Position", All)
                ?? throw new MissingMemberException("CharacterModel.Position");
            _blocked = _model.FieldType.GetProperty("ModelIsBlocked", All)
                ?? throw new MissingMemberException("CharacterModel.ModelIsBlocked");
            _hidden = _model.FieldType.GetField("_isHidden", All)
                ?? throw new MissingFieldException("CharacterModel._isHidden");
            _ticks = _controller.GetField("_overlayFullTicks", All)
                ?? throw new MissingFieldException("BenchmarkModeController._overlayFullTicks");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void LateUpdate()
    {
        if (_phase >= Speeds.Length) return;
        try
        {
            var now = Time.realtimeSinceStartup;
            // ロード時間を測定区間へ混入させない。
            // Start timing only after the first rendered frames following load.
            if (_phase < 0)
            {
                if (!_armed) { _armed = true; _started = now; }
                if (now - _started >= 3f) NextPhase();
                return;
            }
            if (now >= _nextSample)
            {
                // 診断負荷を抑えるため毎フレームの全個体探索は避ける。
                // Sample once per second to limit diagnostic overhead.
                _nextSample = now + 1f;
                Sample();
            }
            if (now - _started >= 20f)
            {
                // シミュレーション速度と表示停止の観測数を同時に残す。
                // Record simulation throughput alongside stationary-model observations.
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[T3MPANIM] phase={0} seconds={1:F2} ticksPerSecond={2:F2} samples={3} moving={4} stationaryModelWhileMoving={5} largeGap={6} maxGap={7:F3}",
                    Names[_phase], now - _started,
                    ((long)_ticks.GetValue(null)! - _startTicks) / (now - _started),
                    _samples, _moving, _stationary, _largeGap, _maxGap));
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[T3MPANIM] detailPhase={0} stationaryUnblocked={1} largeGapUnblocked={2} maxGapUnblocked={3:F3}",
                    Names[_phase], _stationaryUnblocked, _largeGapUnblocked, _maxGapUnblocked));
                NextPhase();
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void NextPhase()
    {
        _phase++;
        if (_phase == Speeds.Length)
        {
            Debug.Log("[T3MPANIM] COMPLETE (observations only; inspect phase metrics and exceptions).");
            enabled = false;
            return;
        }
        var instance = _controller.GetField("_instance", All)!.GetValue(null);
        if (instance == null) throw new InvalidOperationException("T3MP controller is not installed");
        // 同じ描画切替処理を使い、個体には直接介入しない。
        // Exercise the render policy without directly altering any character.
        _controller.GetField("_renderBlackoutRequested", All)!.SetValue(null, _phase == 3);
        _controller.GetMethod("ApplyFrameRatePolicy", All)!.Invoke(instance, null);
        _speed.ChangeSpeed(Speeds[_phase]);
        _started = Time.realtimeSinceStartup;
        _nextSample = _started + 1f;
        _startTicks = (long)_ticks.GetValue(null)!;
        _samples = _moving = _stationary = _largeGap = 0;
        _maxGap = 0;
        _stationaryUnblocked = _largeGapUnblocked = 0;
        _maxGapUnblocked = 0;
        _previous.Clear();
        Debug.Log("[T3MPANIM] BEGIN " + Names[_phase]);
    }

    private void Sample()
    {
        foreach (var component in _registry.Entities)
        {
            // 独自コンポーネントはUnityのObject探索では取得できない。
            // Game components are plain objects, so enumerate the entity registry.
            if (component.Deleted || !component.GameObject.activeInHierarchy) continue;
            var animator = component.AllComponents.FirstOrDefault(value => _movement.IsInstanceOfType(value));
            if (animator == null) continue;
            var model = _model.GetValue(animator);
            if (model == null) continue;
            var position = (Vector3)_position.GetValue(model)!;
            var entity = component.Transform.position;
            var gap = Vector3.Distance(entity, position);
            // 作業位置への固定や建物内の非表示は表示停止と区別する。
            // Separate intentionally blocked/hidden models from stalled visible movement.
            var unblocked = !(bool)_blocked.GetValue(model)! && !(bool)_hidden.GetValue(model)!;
            if (float.IsNaN(gap) || float.IsInfinity(gap))
                throw new InvalidOperationException("Non-finite character position");
            _maxGap = Mathf.Max(_maxGap, gap);
            if (gap > 2f) _largeGap++;
            if (unblocked)
            {
                _maxGapUnblocked = Mathf.Max(_maxGapUnblocked, gap);
                if (gap > 2f) _largeGapUnblocked++;
            }
            var key = component.EntityId;
            // 位置差だけでは作業中モデルも拾うため、移動履歴も記録する。
            // Track movement history because a gap alone also includes working models.
            if (_previous.TryGetValue(key, out var previous) &&
                Vector3.Distance(previous.Entity, entity) > 0.25f)
            {
                _moving++;
                if (Vector3.Distance(previous.Model, position) < 0.001f)
                {
                    _stationary++;
                    if (unblocked)
                    {
                        _stationaryUnblocked++;
                        if (_stationaryUnblocked <= 3)
                            Debug.Log($"[T3MPANIM] stationary phase={Names[_phase]} entity={key} gap={gap:F3} position={entity} model={position}");
                    }
                }
            }
            _previous[key] = (entity, position);
            _samples++;
        }
    }

    private static Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null)
        ?? throw new TypeLoadException(name);

    private void Fail(Exception exception)
    {
        Debug.LogError("[T3MPANIM] ERROR " + exception);
        enabled = false;
    }
}
