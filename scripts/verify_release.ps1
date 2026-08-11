# Pre-upload release verification for T3MP.
#
# Verifies the mod against every available game version, without touching
# Steam branches, by launching version snapshots kept under
# Documents\TimberbornVersions (plus falling back to the live Steam install):
#
#   1. Package lint  - mod/ must contain exactly the shippable files
#                      (a stray AssetBundles folder crashed game v1.0 in v1.1.6).
#   2. Per version   - compile against that version's Managed DLLs, deploy,
#                      then run THREE scenarios and scan Player.log after each:
#                        LaunchArgs - the game's own CLI autoload (+ auto-ultra)
#                        MenuLoad   - load the save from the main menu
#                        NewGame    - start a brand-new game on a built-in map
#                      MenuLoad/NewGame are driven by the dev-only
#                      T3MPTestDriver mod (src\T3MPTestDriver), which is
#                      deployed to Mods\ just for the run and removed after.
#   3. Summary       - PASS/FAIL table; exits 1 on any failure.
#
# Capture a version snapshot with:
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
    [string] $SteamInstall = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn',
    [string] $V10Settlement = 'm7c',
    [string] $V10Save = 'm7c',
    [string] $V11Settlement = 'n10c',
    [string] $V11Save = 'n10c',
    [string] $NewGameMap = '',
    [int] $SecondsAfterLoad = 45,
    [switch] $SkipE2E
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn\Player.log'
$modsPath = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
$driverDeployPath = Join-Path $modsPath 'T3MPTestDriver'
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

function Get-UnityVersion([string] $Install) {
    (Get-Item (Join-Path $Install 'Timberborn.exe')).VersionInfo.ProductVersion
}

# Checks Player.log after a probe run and appends one result row per criterion.
function Test-PlayerLog([string] $Label) {
    if (-not (Test-Path -LiteralPath $playerLog)) {
        Add-Result $Label $false 'Player.log was not produced'
        return
    }
    $log = Get-Content -LiteralPath $playerLog

    $failureDetail = $null
    $bundleFailure = @($log | Where-Object { $_ -match 'Failed to load asset bundle' })
    if ($bundleFailure.Count -gt 0) { $failureDetail = $bundleFailure[0].Trim() }

    $driverError = @($log | Where-Object { $_ -match '\[T3MPTEST\] ERROR' })
    if (-not $failureDetail -and $driverError.Count -gt 0) { $failureDetail = $driverError[0].Trim() }

    $exceptions = @($log | Where-Object { $_ -match 'First uncaught exception|^Rethrow as Exception:' })
    if (-not $failureDetail -and $exceptions.Count -gt 0) { $failureDetail = $exceptions[0].Trim() }

    $modLoaded = @($log | Where-Object { $_ -match '\[T3MP\] Loaded\.' })
    if (-not $failureDetail -and $modLoaded.Count -eq 0) { $failureDetail = 'T3MP never loaded' }

    $loadTime = @($log | Where-Object { $_ -match 'Load time:.*scene index: 2' })
    if (-not $failureDetail -and $loadTime.Count -eq 0) { $failureDetail = 'game scene never finished loading' }

    $simRates = @($log | ForEach-Object {
        if ($_ -match '\[T3MP\] Simulation rate ([0-9.]+) ticks/s') { [double] $Matches[1] } })
    $maxRate = if ($simRates.Count -gt 0) { ($simRates | Measure-Object -Maximum).Maximum } else { 0 }
    if (-not $failureDetail -and $maxRate -le 0) { $failureDetail = 'simulation never advanced (0 ticks/s)' }

    Add-Result $Label (-not $failureDetail) $(
        if ($failureDetail) { $failureDetail }
        else { ('{0}, sim {1} ticks/s, no exceptions' -f $loadTime[0].Trim(), $maxRate) })
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

# --- Phase 2: per-version compile + scenarios ------------------------------
$versions = @(
    @{ Name = 'v1.0'; Install = $V10Install; Settlement = $V10Settlement; Save = $V10Save },
    @{ Name = 'v1.1'; Install = $V11Install; Settlement = $V11Settlement; Save = $V11Save }
)

# If a snapshot is missing but the live Steam install IS that version (the
# user switches versions manually via Steam), verify against the live install.
foreach ($version in $versions) {
    if (-not (Test-Path -LiteralPath (Join-Path $version.Install 'Timberborn_Data\Managed')) -and
        (Test-Path -LiteralPath (Join-Path $SteamInstall 'Timberborn_Data\Managed'))) {
        $steamUnity = Get-UnityVersion $SteamInstall
        $wantOld = $version.Name -eq 'v1.0'
        $steamIsOld = $steamUnity -like '6000.3*'
        if ($wantOld -eq $steamIsOld) {
            Write-Host ("{0}: no snapshot, using the live Steam install (Unity {1})" -f $version.Name, $steamUnity)
            $version.Install = $SteamInstall
        }
    }
}

try {
    foreach ($version in $versions) {
        $name = $version.Name
        $install = $version.Install

        if (-not (Test-Path -LiteralPath (Join-Path $install 'Timberborn_Data\Managed'))) {
            Add-Result "$name snapshot" $false "no snapshot and Steam install is a different version: $install"
            continue
        }
        Add-Result "$name snapshot" $true ("{0} (Unity {1})" -f $install, (Get-UnityVersion $install))

        dotnet build (Join-Path $repoRoot 'src\T3MP\T3MP.csproj') -c Release "-p:TimberbornInstall=$install" | Out-Null
        Add-Result "$name compile" ($LASTEXITCODE -eq 0) 'dotnet build T3MP'
        if ($LASTEXITCODE -ne 0 -or $SkipE2E) {
            continue
        }

        dotnet build (Join-Path $repoRoot 'src\T3MPTestDriver\T3MPTestDriver.csproj') -c Release "-p:TimberbornInstall=$install" | Out-Null
        Add-Result "$name driver compile" ($LASTEXITCODE -eq 0) 'dotnet build T3MPTestDriver'
        if ($LASTEXITCODE -ne 0) {
            continue
        }

        # Deploy the release mod (deploy.ps1 rebuilds identically - cheap) and
        # the test driver for this version's DLLs.
        $env:TIMBERBORN_DIR = $install
        try {
            & (Join-Path $PSScriptRoot 'deploy.ps1') | Out-Null
        } finally {
            $env:TIMBERBORN_DIR = $null
        }
        New-Item -ItemType Directory -Force -Path $driverDeployPath | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot 'testmod\manifest.json') -Destination $driverDeployPath -Force
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src\T3MPTestDriver\bin\Release\netstandard2.1\T3MPTestDriver.dll') -Destination $driverDeployPath -Force

        $exe = Join-Path $install 'Timberborn.exe'
        $scenarios = @(
            @{ Label = "$name E2E LaunchArgs"; Args = @{ Scenario = 'LaunchArgs'; BenchAutoUltra = $true } },
            @{ Label = "$name E2E MenuLoad"; Args = @{ Scenario = 'MenuLoad' } },
            @{ Label = "$name E2E NewGame"; Args = @{ Scenario = 'NewGame'; MapName = $NewGameMap } }
        )
        foreach ($scenario in $scenarios) {
            $scenarioArgs = $scenario.Args
            & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') `
                -TimberbornExe $exe `
                -SettlementName $version.Settlement -SaveName $version.Save `
                -SkipModManager -AutoConfirmMods `
                -SecondsAfterLoad $SecondsAfterLoad -StopAfter `
                @scenarioArgs
            Test-PlayerLog $scenario.Label
        }
    }
} finally {
    # The test driver is for verification runs only - never leave it installed.
    if (Test-Path -LiteralPath $driverDeployPath) {
        Remove-Item -LiteralPath $driverDeployPath -Recurse -Force
    }
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
