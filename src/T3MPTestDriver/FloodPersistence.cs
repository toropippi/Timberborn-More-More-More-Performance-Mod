using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace T3MPTestDriver;

public sealed partial class FloodRegression
{
    private string _mode = "Normal";
    private bool _capturePending;
    private bool _deferInitialStatus;
    private readonly System.Collections.Generic.Dictionary<Guid, string> _expectedInitialStatus = new();
    private bool Reload => _mode.StartsWith("Reload", StringComparison.Ordinal);

    private void ConfigurePersistence()
    {
        _mode = Argument("-t3mpTestFloodMode") ?? "Normal";
        if (!new[] { "Normal", "CaptureWet", "CaptureDry", "ReloadWet", "ReloadDry" }.Contains(_mode))
            throw new ArgumentException("Unknown flood mode");
        if (Reload && (Argument("-t3mpTestFloodExpected") == null || Argument("-t3mpTestFloodPlan") == null))
            throw new ArgumentException("Reload requires expected snapshot and fixture plan");
    }
    private void Drive(bool wet, bool drain)
    {
        foreach (var entry in _entries)
            if (wet) foreach (var cell in entry.WetCells) _add.Invoke(_waterService, new object[] { cell, 0.2f });
            else if (drain) foreach (var cell in entry.DrainCells) _remove.Invoke(_waterService, new object[] { cell, 10f });
    }
    private void Complete(string detail)
    {
        _done = true;
        _speed.ChangeSpeed(0f);
        SaveRecords();
        var result = "PASS targets=6 " + detail + " frames=" + _frames.Count;
        File.WriteAllText(Path.Combine(_output, "result.txt"), result);
        Debug.Log("[T3MPTEST] Flood regression " + result);
    }
    private string[] Snapshot() => _entries.Select(e => e.Id + "\t" + State(e)).ToArray();
    private void CheckReload()
    {
        var expected = File.ReadAllLines(Argument("-t3mpTestFloodExpected")!);
        var actual = Snapshot();
        // 1.0 registers status callbacks in Unity Start; 1.1 does so during
        // entity initialization. Keep simulation-state checks before the clock,
        // but require 1.0 status agreement at the first observed water tick.
        _deferInitialStatus = Find("Timberborn.NaturalResourcesMoistureUI.LivingWaterNaturalResourceStatus")
            .GetInterfaces().Any(t => t.FullName == "Timberborn.BaseComponentSystem.IStartableComponent");
        File.WriteAllLines(Path.Combine(_output, "reloaded.tsv"), actual);
        if (expected.Length != TargetCount) throw new InvalidOperationException("Wrong expected snapshot count");
        for (var i = 0; i < TargetCount; i++)
        {
            var a = actual[i].Split('\t');
            var b = expected[i].Split('\t');
            if (a.Length != 8 || b.Length != 8) throw new InvalidOperationException("Unexpected snapshot shape");
            _expectedInitialStatus.Add(_entries[i].Id, b[6]);
            for (var j = 0; j < a.Length; j++)
            {
                if (j == 6 && _deferInitialStatus) continue;
                // Native load reconstructs remaining time by multiplication;
                // its 1 - remaining/full float progress can round by one ULP.
                var same = j == 4 ? Math.Abs(float.Parse(a[j], CultureInfo.InvariantCulture) - float.Parse(b[j], CultureInfo.InvariantCulture)) <= 0.00000012f : a[j] == b[j];
                if (!same) throw new InvalidOperationException("Reload snapshot differs id=" + a[0] + " column=" + j + " expected=" + b[j] + " actual=" + a[j]);
            }
            if ((bool)Get(_entries[i].Needs, "WaterNeedsAreMet") != (_mode == "ReloadDry"))
                throw new InvalidOperationException("Reload fixture is not the requested water condition");
        }
        Debug.Log("[T3MPTEST] Flood reload initial state checked targets=6 mode=" + _mode);
    }
    private void CheckDeferredStatus()
    {
        if (!Reload || !_deferInitialStatus || _ticks != 1) return;
        var rows = _entries.Select(e => e.Id + "\t" + Get(e.Status, "IsActive")).ToArray();
        File.WriteAllLines(Path.Combine(_output, "deferred-status.tsv"), rows);
        foreach (var entry in _entries)
            if (Get(entry.Status, "IsActive").ToString() != _expectedInitialStatus[entry.Id])
                throw new InvalidOperationException("Legacy status differs at first water tick id=" + entry.Id);
        Debug.Log("[T3MPTEST] Flood reload deferred status checked targets=6 at first water tick");
    }
    private void LateUpdate()
    {
        if (!_capturePending || _done) return;
        try
        {
            // Never save reentrantly from WaterObjectService.Tick. Finish the
            // native partial tick from the frame boundary, as GameSaver.Save does.
            // Until this point SampleAndDrive maintains the requested water state.
            var tickerType = Find("Timberborn.TickSystem.Ticker");
            tickerType.GetMethod("FinishFullTick")!.Invoke(_container.GetInstance(tickerType), null);
            var wet = _mode == "CaptureWet";
            if (!wet && _entries.Any(e => !e.Wet[0] || !e.Recovered[0]))
                throw new InvalidOperationException("Dry save requires prior flooding and recovery for every tree");
            foreach (var entry in _entries)
                if ((bool)Get(entry.Needs, "WaterNeedsAreMet") == wet || (bool)Get(entry.Status, "IsActive") != wet ||
                    (int)Get(entry.Water, "WaterAboveBase") != Math.Max(0, Height(entry.Position) - entry.Position.z) ||
                    (bool)Get(Get(entry.Dying, "DyingProgress"), "IsDying") != wet ||
                    (bool)Get(entry.Living, "IsDead") || (!wet && (float)Get(Get(entry.Dying, "DyingProgress"), "Progress") != 0f))
                    throw new InvalidOperationException("Save fixture missed requested wet/dry condition");
            var name = wet ? "wet" : "dry";
            var before = Snapshot();
            var saverType = Find("Timberborn.GameSaveRuntimeSystem.GameSaver");
            var savedRandom = UnityEngine.Random.state;
            try
            {
                using var stream = new MemoryStream();
                saverType.GetMethod("SaveWithoutFinishingTick")!.Invoke(_container.GetInstance(saverType), new object[] { stream });
                File.WriteAllBytes(Path.Combine(_output, name + ".timber"), stream.ToArray());
            }
            finally { UnityEngine.Random.state = savedRandom; }
            if (!before.SequenceEqual(Snapshot())) throw new InvalidOperationException("Save changed observed tree state");
            File.WriteAllLines(Path.Combine(_output, name + "-expected.tsv"), before);
            File.WriteAllLines(Path.Combine(_output, "timer-periods.tsv"), _entries.Select(e => e.Id + "\t" +
                ((float)Field(Field(e.Dying, "_timeTrigger"), "_fullDelayInDays")).ToString("R", CultureInfo.InvariantCulture)));
            Complete("mode=" + _mode + " saved=" + name + ".timber nativeFinishFullTick=True");
        }
        catch (Exception exception)
        {
            _done = true;
            _speed.ChangeSpeed(0f);
            SaveRecords();
            Debug.LogError("[T3MPTEST] Flood regression FAIL: " + exception);
            throw;
        }
    }
}
