using System;
using System.Linq;
using System.Reflection;
using Bindito.Core;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.TimeSystem;
using UnityEngine;

namespace T3MPTestDriver;

// Opt-in, development-only three-world lifecycle probe. Uses the game's normal
// scene loader and staged saves; it does not patch simulation or lifetime code.
public sealed class WorldReloadProbe : MonoBehaviour
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _worlds;
    private static WeakReference? _previousMeter;
    private static UnityEngine.Object? _previousView;
    private object _meter = null!;
    private Type _viewType = null!;
    private PropertyInfo _ticksProperty = null!;
    private FieldInfo _textField = null!;
    private GameSceneLoader _loader = null!;
    private GameSaveRepository _saves = null!;
    private SpeedManager _speed = null!;
    private UnityEngine.Object _view = null!;
    private int _world, _state, _frames;
    private long _startTicks, _startFullTicks, _pauseTicks, _pauseFullTicks;
    private double _deadline;
    private bool _initialized;

    internal static bool Requested => Environment.GetCommandLineArgs().Contains("-t3mpTestWorldReload");
    private long Ticks => (long)_ticksProperty.GetValue(_meter)!;

    internal void Initialize(IContainer container, SpeedManager speed)
    {
        try
        {
            _world = ++_worlds;
            Require(_world <= 3, "Unexpected fourth world");
            var meterType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("T3MP.UI.SimulationRateMeter")).Single(t => t != null)!;
            _viewType = meterType.Assembly.GetType("T3MP.UI.SimulationRateMeterView", true)!;
            _ticksProperty = meterType.GetProperty("Ticks", All)!;
            _textField = _viewType.GetField("_text", All)!;
            _meter = container.GetInstance(meterType);
            _loader = container.GetInstance<GameSceneLoader>();
            _saves = container.GetInstance<GameSaveRepository>();
            _speed = speed;
            Require(Ticks == 0, "Meter did not start at zero");
            Require(_previousMeter == null || !ReferenceEquals(_previousMeter.Target, _meter), "Meter reused across worlds");
            // Resolve both staged references before any transition is attempted.
            Resolve("-t3mpTestReloadSettlement", "-t3mpTestReloadSave");
            Resolve("-t3mpTestReturnSettlement", "-t3mpTestReturnSave");
            _initialized = true;
            Debug.Log($"[T3MPRELOAD] begin world={_world} initialMeterTicks={Ticks}");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private void Update()
    {
        if (!_initialized) return;
        try
        {
            var now = Time.realtimeSinceStartupAsDouble;
            if (_state == 0)
            {
                if (++_frames < 3) return; // Allow deferred scene-object destruction.
                Require(!_previousView, "Previous world's meter view survived unloading");
                var views = Resources.FindObjectsOfTypeAll(_viewType).Where(v => v).ToArray();
                Require(views.Length == 1, "Expected one live meter view");
                _view = views[0];
                _startTicks = Ticks;
                _startFullTicks = FullTickCounter.FullTicks;
                _deadline = now + 45;
                _state = 1;
                Debug.Log($"[T3MPRELOAD] ready world={_world} views=1 previousViewDestroyed=true");
                return;
            }
            if (now < _deadline) return;
            if (_state == 1)
            {
                var ticks = Ticks - _startTicks;
                var fullTicks = FullTickCounter.FullTicks - _startFullTicks;
                Require(ticks > 0 && fullTicks > 0 && Math.Abs(ticks - fullTicks) <= 1, "Meter/native tick progress mismatch");
                Debug.Log($"[T3MPRELOAD] progress world={_world} meterTicks={ticks} nativeTicks={fullTicks}");
                _speed.ChangeSpeed(0);
                _deadline = now + 1;
                _state = 2;
            }
            else if (_state == 2)
            {
                Require(Time.timeScale == 0, "Pause was not applied");
                _pauseTicks = Ticks;
                _pauseFullTicks = FullTickCounter.FullTicks;
                _deadline = now + 2;
                _state = 3;
            }
            else if (_state == 3)
            {
                Require(Time.timeScale == 0 && Ticks == _pauseTicks && FullTickCounter.FullTicks == _pauseFullTicks, "Simulation advanced while paused");
                Require((string)_textField.GetValue(_view)! == "rSPD/iSPD x0.0 / x0.0\nUPS 0.0 ticks/s", "Paused meter text was not zero");
                Debug.Log($"[T3MPRELOAD] pause world={_world} meterDelta=0 nativeDelta=0 textZero=true");
                _speed.ChangeSpeed(3);
                _deadline = now + 5;
                _state = 4;
            }
            else if (_state == 4)
            {
                Require(Ticks > _pauseTicks && FullTickCounter.FullTicks > _pauseFullTicks, "Simulation did not resume");
                Require(Resources.FindObjectsOfTypeAll(_viewType).Count(v => v) == 1, "Meter view count changed");
                Debug.Log($"[T3MPRELOAD] resume world={_world} meterDelta={Ticks - _pauseTicks} nativeDelta={FullTickCounter.FullTicks - _pauseFullTicks}");
                _initialized = false;
                if (_world == 3)
                {
                    Debug.Log("[T3MPRELOAD] PASS worlds=3 (new meters, old view destruction, native progress, pause and resume)");
                    return;
                }
                _previousMeter = new WeakReference(_meter);
                _previousView = _view;
                var next = _world == 1 ? Resolve("-t3mpTestReloadSettlement", "-t3mpTestReloadSave") : Resolve("-t3mpTestReturnSettlement", "-t3mpTestReturnSave");
                Debug.Log($"[T3MPRELOAD] transition world={_world} target={next}");
                _loader.StartSaveGame(next);
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    private SaveReference Resolve(string settlementFlag, string saveFlag)
    {
        var args = Environment.GetCommandLineArgs();
        string Value(string flag)
        {
            var index = Array.IndexOf(args, flag);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + flag);
            return args[index + 1];
        }
        var name = Value(settlementFlag);
        Require(name.StartsWith("T3MP-Reload-", StringComparison.Ordinal), "Reload targets must be staged test settlements");
        var settlement = _saves.GetAllSettlements().Single(s => s.SettlementName == name);
        var reference = new SaveReference(Value(saveFlag), settlement);
        Require(_saves.SaveExists(reference), "Reload save does not exist");
        return reference;
    }

    private void OnDestroy() => Debug.Log($"[T3MPRELOAD] observerDestroyed world={_world}");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Fail(Exception exception)
    {
        _initialized = false;
        enabled = false;
        Debug.LogError($"[T3MPRELOAD] FAIL world={_world}: {exception}");
    }
}
