# Offline Unity boolean experiment

Decision and evidence: [DECISIONS](../../docs/DECISIONS.md), `unity-bool`
in [evidence.json](../../docs/evidence.json).

The generator accepts only the pinned CoreModule hashes and writes a new copy.
Game assemblies and generated output must remain local.

```powershell
dotnet run --project experiments/offline-unity-bool -- '<original CoreModule.dll>' '<new output directory>'
```

`run-copy-probe.ps1` requires the owned game-copy manifest in
`testlogs/offline-bool-20260913/game-copy.json` and the exact recorded hashes.
It modifies that copy and restores it afterward. Reconstruct the diagnostic
first in an isolated checkout: add `runtime/OfflineUnityBoolValidation.cs` to
the driver, check/apply `runtime/integration.patch`, and rebuild. The normal
driver contains neither this patch nor its observation flag.
The runner's `Validate` and `Benchmark` modes are separate; the latter uses BOOB.
A copied-DLL experiment does not establish a Workshop deployment method.
