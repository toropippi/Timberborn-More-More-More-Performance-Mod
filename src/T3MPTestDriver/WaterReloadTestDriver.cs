using System;
using System.Linq;
using Bindito.Core;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace T3MPTestDriver;

[Context("Game")]
public sealed class WaterReloadTestConfigurator : IConfigurator
{
    public void Configure(IContainerDefinition containerDefinition)
    {
        containerDefinition.Bind<WaterReloadTestDriver>().AsSingleton();
    }
}

public sealed class WaterReloadTestDriver : IPostLoadableSingleton, IUpdatableSingleton
{
    private const float ReloadDelaySeconds = 10f;
    private const float AuditRetrySeconds = 0.5f;
    private const float AuditTimeoutSeconds = 15f;
    private static bool _reloadStarted;
    private static int _loadedSceneCount;

    private readonly GameSceneLoader _gameSceneLoader;
    private readonly GameSaveRepository _gameSaveRepository;
    private bool _armed;
    private bool _auditPending;
    private float _reloadAt;
    private float _reloadDeadline;
    private float _auditAt;
    private float _auditDeadline;

    public WaterReloadTestDriver(GameSceneLoader gameSceneLoader,
                                 GameSaveRepository gameSaveRepository)
    {
        _gameSceneLoader = gameSceneLoader;
        _gameSaveRepository = gameSaveRepository;
    }

    // 最初のworldを記録してからsaveを切り替え、両worldの同一性を比較する。
    // Snapshot the first world before switching saves, then compare both identities.
    public void PostLoad()
    {
        if (!WaterReloadRequested())
        {
            return;
        }

        _loadedSceneCount++;
        if (_reloadStarted)
        {
            _auditPending = true;
            _auditAt = Time.realtimeSinceStartup + AuditRetrySeconds;
            _auditDeadline =
                Time.realtimeSinceStartup + AuditTimeoutSeconds;
            Debug.Log($"[T3MPTEST] WaterReload complete scenes={_loadedSceneCount}.");
            return;
        }

        _armed = true;
        _reloadAt = Time.realtimeSinceStartup + ReloadDelaySeconds;
        _reloadDeadline = _reloadAt + AuditTimeoutSeconds;
        Debug.Log(
            $"[T3MPTEST] WaterReload first scene ready; target=" +
            $"{TestArguments.Settlement}/{TestArguments.Save} delay={ReloadDelaySeconds:F0}s.");
    }

    public void UpdateSingleton()
    {
        UpdateFinalAudit();

        if (!_armed || _reloadStarted || Time.realtimeSinceStartup < _reloadAt)
        {
            return;
        }

        var targetSave = FindTargetSave();
        if (targetSave is null)
        {
            _armed = false;
            return;
        }

        // fast pathが最初のworldへbind済みになるまで待ってから遷移する。
        // Wait for the fast path to bind to the first world before transitioning.
        var auditStatus = WaterReloadFastPathAudit.TryCaptureInitial();
        if (auditStatus == WaterReloadAuditStatus.Pending)
        {
            if (Time.realtimeSinceStartup >= _reloadDeadline)
            {
                _armed = false;
                WaterReloadFastPathAudit.LogTimeout("initial");
            }
            else
            {
                _reloadAt = Time.realtimeSinceStartup + AuditRetrySeconds;
            }

            return;
        }

        _armed = false;
        if (auditStatus == WaterReloadAuditStatus.Failed)
        {
            return;
        }

        _reloadStarted = true;
        Debug.Log("[T3MPTEST] WaterReload: starting target " + targetSave);
        _gameSceneLoader.StartSaveGameInstantly(targetSave);
    }

    private void UpdateFinalAudit()
    {
        if (!_auditPending || Time.realtimeSinceStartup < _auditAt)
        {
            return;
        }

        var status = WaterReloadFastPathAudit.TryAuditFinal();
        if (status != WaterReloadAuditStatus.Pending)
        {
            _auditPending = false;
            return;
        }

        if (Time.realtimeSinceStartup >= _auditDeadline)
        {
            _auditPending = false;
            WaterReloadFastPathAudit.LogTimeout("final");
            return;
        }

        _auditAt = Time.realtimeSinceStartup + AuditRetrySeconds;
    }

    private SaveReference? FindTargetSave()
    {
        var settlementName = TestArguments.Settlement;
        var saveName = TestArguments.Save;
        if (string.IsNullOrEmpty(settlementName) || string.IsNullOrEmpty(saveName))
        {
            Debug.LogError(
                "[T3MPTEST] ERROR: WaterReload needs settlement and save arguments.");
            return null;
        }

        var settlement = _gameSaveRepository.GetAllSettlements()
            .FirstOrDefault(reference => reference.SettlementName == settlementName);
        if (settlement is null)
        {
            Debug.LogError("[T3MPTEST] ERROR: settlement not found: " + settlementName);
            return null;
        }

        var saveReference = new SaveReference(saveName, settlement);
        if (!_gameSaveRepository.SaveExists(saveReference))
        {
            Debug.LogError("[T3MPTEST] ERROR: save not found: " + saveReference);
            return null;
        }

        return saveReference;
    }

    private static bool WaterReloadRequested()
    {
        return Environment.GetCommandLineArgs().Any(argument =>
            string.Equals(
                argument, "-t3mpTestWaterReload", StringComparison.OrdinalIgnoreCase));
    }
}
