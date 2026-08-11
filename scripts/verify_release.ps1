# Pre-upload release verification for T3MP.
#
# Verifies the mod against BOTH game versions without touching Steam branches,
# by launching version snapshots kept under Documents\TimberbornVersions:
#
#   1. Package lint  - mod/ must contain exactly the shippable files
#                      (a stray AssetBundles folder crashed game v1.0 in v1.1.6).
#   2. Per version   - compile against that version's Managed DLLs,
#                      deploy, launch, autoload a save, run the sim at
#                      auto-ultra, then scan Player.log for failures.
#   3. Summary       - PASS/FAIL table; exits 1 on any failure.
#
# A missing snapshot folder is reported and skipped, so this runs today with
# only v1.0 captured and picks v1.1 up automatically once its snapshot exists.
# Capture a snapshot with:
#   robocopy "C:\Program Files (x86)\Steam\steamapps\common\Timberborn" `
#            "$env:USERPROFILE\Documents\TimberbornVersions\<name>" /E
#
# Save-compatibility note: saves are NOT forward-compatible. v1.0 (stable)
# reads Documents\Timberborn\Saves, v1.1 (experimental) reads
# ExperimentalSaves, and a v1.1 save fails on v1.0 inside vanilla loading
# code. Each version therefore verifies with its own save below.

[CmdletBinding()]
param(
    [string] $V10Install = (Join-Path $env:USERPROFILE 'Documents\TimberbornVersions\Timberborn-1.0-build23107127'),
    [string] $V11Install = (Join-Path $env:USERPROFILE 'Documents\TimberbornVersions\Timberborn-1.1'),
    [string] $V10Settlement = 'm7c',
    [string] $V10Save = 'm7c',
    [string] $V11Settlement = 'n10c',
    [string] $V11Save = 'n10c',
    [int] $SecondsAfterLoad = 45,
    [switch] $SkipE2E
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn\Player.log'
$results = @()

function Add-Result([string] $Check, [bool] $Passed, [string] $Detail) {
    $script:results += [PSCustomObject]@{
        Check  = $Check
        Result = if ($Passed) { 'PASS' } else { 'FAIL' }
        Detail = $Detail
    }
    $color = if ($Passed) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Check, $Detail) -ForegroundColor $color
}

# --- Phase 1: package lint -------------------------------------------------
$allowedShipFiles = @('manifest.json', 'README.md', 'thumbnail.jpg')
$modDir = Join-Path $repoRoot 'mod'
$unexpected = Get-ChildItem -LiteralPath $modDir -Recurse -Force |
    Where-Object { $_.PSIsContainer -or $allowedShipFiles -notcontains $_.Name }
Add-Result 'package lint' (-not $unexpected) $(
    if ($unexpected) { 'unexpected in mod/: ' + (($unexpected | Select-Object -ExpandProperty Name) -join ', ') }
    else { 'mod/ contains only: ' + ($allowedShipFiles -join ', ') })

$manifest = Get-Content -LiteralPath (Join-Path $modDir 'manifest.json') -Raw | ConvertFrom-Json
Write-Host ("Verifying T3MP v{0}" -f $manifest.Version)

# --- Phase 2: per-version compile + E2E ------------------------------------
$versions = @(
    @{ Name = 'v1.0'; Install = $V10Install; Settlement = $V10Settlement; Save = $V10Save },
    @{ Name = 'v1.1'; Install = $V11Install; Settlement = $V11Settlement; Save = $V11Save }
)

foreach ($version in $versions) {
    $name = $version.Name
    $install = $version.Install

    if (-not (Test-Path -LiteralPath (Join-Path $install 'Timberborn_Data\Managed'))) {
        Add-Result "$name snapshot" $false "snapshot not found: $install (capture it, see header)"
        continue
    }
    $unityVersion = (Get-Item (Join-Path $install 'Timberborn.exe')).VersionInfo.ProductVersion
    Add-Result "$name snapshot" $true "$install (Unity $unityVersion)"

    dotnet build (Join-Path $repoRoot 'src\T3MP\T3MP.csproj') -c Release "-p:TimberbornInstall=$install" | Out-Null
    Add-Result "$name compile" ($LASTEXITCODE -eq 0) "dotnet build against $name Managed DLLs"
    if ($LASTEXITCODE -ne 0 -or $SkipE2E) {
        continue
    }

    # Deploy the just-built DLL with the standard script (skips its own build
    # by rebuilding identically - cheap - and preserves workshop_data.json).
    $env:TIMBERBORN_DIR = $install
    try {
        & (Join-Path $PSScriptRoot 'deploy.ps1') | Out-Null
    } finally {
        $env:TIMBERBORN_DIR = $null
    }

    & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') `
        -TimberbornExe (Join-Path $install 'Timberborn.exe') `
        -SettlementName $version.Settlement -SaveName $version.Save `
        -SkipModManager -AutoConfirmMods -BenchAutoUltra `
        -SecondsAfterLoad $SecondsAfterLoad -StopAfter

    if (-not (Test-Path -LiteralPath $playerLog)) {
        Add-Result "$name E2E" $false 'Player.log was not produced'
        continue
    }
    $log = Get-Content -LiteralPath $playerLog

    $bundleFailure = @($log | Where-Object { $_ -match 'Failed to load asset bundle' })
    Add-Result "$name bundle load" ($bundleFailure.Count -eq 0) $(
        if ($bundleFailure) { $bundleFailure[0] } else { 'no asset bundle failures' })

    $modLoaded = @($log | Where-Object { $_ -match '\[T3MP\] Loaded\.' })
    Add-Result "$name mod loaded" ($modLoaded.Count -gt 0) 'marker: [T3MP] Loaded'

    $loadTime = @($log | Where-Object { $_ -match 'Load time:' })
    Add-Result "$name save load" ($loadTime.Count -gt 0) $(
        if ($loadTime) { $loadTime[-1].Trim() } else { 'save never finished loading' })

    $exceptions = @($log | Where-Object { $_ -match 'First uncaught exception|^Rethrow as Exception:' })
    Add-Result "$name exceptions" ($exceptions.Count -eq 0) $(
        if ($exceptions) { $exceptions[0] } else { 'no uncaught exceptions' })

    # "[T3MP] Simulation rate 26.5 ticks/s (...)" - require the sim to have
    # actually advanced, not just loaded and sat paused.
    $simRates = @($log | ForEach-Object {
        if ($_ -match '\[T3MP\] Simulation rate ([0-9.]+) ticks/s') { [double] $Matches[1] } })
    $maxRate = if ($simRates.Count -gt 0) { ($simRates | Measure-Object -Maximum).Maximum } else { 0 }
    Add-Result "$name sim ran" ($maxRate -gt 0) ("max simulation rate: {0} ticks/s" -f $maxRate)
}

# --- Phase 3: summary -------------------------------------------------------
Write-Host ''
Write-Host ("=== T3MP v{0} release verification ===" -f $manifest.Version)
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' })
if ($failed.Count -gt 0) {
    Write-Host ("NOT READY: {0} check(s) failed." -f $failed.Count) -ForegroundColor Red
    exit 1
}
Write-Host 'READY: all checks passed. Upload via the in-game mod uploader.' -ForegroundColor Green
exit 0
