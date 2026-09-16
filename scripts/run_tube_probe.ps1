# Runs the T3MPTestDriver tube-light monitor (-t3mpTestTubeLights) on a staged
# settlement and extracts the "[T3MPTEST] tubelight" lines. Dev-only.
[CmdletBinding()]
param(
    [string] $Settlement = 't3mp-ab11',
    [string] $Save = 'n10c',
    [ValidateRange(0.01, 10000)][double] $Speed = 3,
    [ValidateRange(1, 86400)][int] $SecondsAfterLoad = 120,
    [string] $TimberbornExe = (Join-Path $env:USERPROFILE 'Documents\TimberbornVersions\Timberborn-1.1\Timberborn.exe'),
    [string] $Install = (Join-Path $env:USERPROFILE 'Documents\TimberbornVersions\Timberborn-1.1'),
    [string[]] $ExtraGameArgs = @(),
    [switch] $SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$logDir = Join-Path $repoRoot ('testlogs\tube-probe-' + [Guid]::NewGuid().ToString('N'))
$modsPath = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
$driverDeployPath = Join-Path $modsPath 'T3MPTestDriver'
function Assert-NoTubeProbeGame {
    if (Get-Process -Name Timberborn -ErrorAction SilentlyContinue) {
        throw 'Timberborn is running; preserve that session and retry after it exits.'
    }
}
# Do not overwrite an installed driver or stop a probe owned by another run.
Assert-NoTubeProbeGame
if (Test-Path -LiteralPath $driverDeployPath) { throw 'An existing test driver is installed; preserve it before running this probe.' }
if (-not (Test-Path -LiteralPath $TimberbornExe -PathType Leaf)) { throw "Missing game: $TimberbornExe" }
if (-not $SkipBuild) {
    dotnet build (Join-Path $repoRoot 'src\T3MPTestDriver\T3MPTestDriver.csproj') -c Release "-p:TimberbornInstall=$Install" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'driver build failed' }
}
$builtDriver = Join-Path $repoRoot 'src\T3MPTestDriver\bin\Release\netstandard2.1\T3MPTestDriver.dll'
if (-not (Test-Path -LiteralPath $builtDriver -PathType Leaf)) { throw "Missing driver: $builtDriver" }
# A game may have started during the build. Check again before deployment.
Assert-NoTubeProbeGame
. (Join-Path $PSScriptRoot 'ReleaseVerification.ps1')
New-Item -ItemType Directory -Path $logDir | Out-Null
$driverCreated = $false
$failure = $null
$cleanupFailure = $null
try {
    New-Item -ItemType Directory -Path $driverDeployPath -ErrorAction Stop | Out-Null
    $driverCreated = $true
    Copy-Item -LiteralPath (Join-Path $repoRoot 'testmod\manifest.json') -Destination $driverDeployPath
    Copy-Item -LiteralPath $builtDriver -Destination $driverDeployPath
    $extra = @('-t3mpTestTubeLights') + $ExtraGameArgs
    & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') -TimberbornExe $TimberbornExe -SettlementName $Settlement -SaveName $Save -Scenario LaunchArgs -TestSpeed $Speed `
        -SkipModManager -OutputDir $logDir -SecondsAfterLoad $SecondsAfterLoad -StopAfter -ExtraGameArgs $extra | Out-Host
    $summaries = @(Get-ChildItem -LiteralPath $logDir -Filter 'probe-summary-*.json')
    if ($summaries.Count -ne 1) { throw 'Expected exactly one probe summary' }
    $probe = Get-Content -LiteralPath $summaries[0].FullName -Raw | ConvertFrom-Json
    if (-not $probe.DirectLaunch -or -not $probe.SawLoadTime -or -not $probe.ObservationCompleted -or
        -not $probe.LogCaptured -or $probe.SawException -or $probe.Failure -or $probe.FinalizationFailures.Count -or
        $probe.ObservedSeconds -lt $SecondsAfterLoad) { throw 'Tube probe did not complete the requested observation' }
    $lines = @(Get-Content -LiteralPath $probe.Log)
    if (-not ($lines -match '^\[T3MPTEST\] tubelight summary ') -or
        $lines -match '^\[T3MPTEST\] (ERROR|tubelight (disabled|sampleFailed|error))') { throw 'Tube-light diagnostics did not capture valid samples' }
    $out = Join-Path $logDir 'tubelight.log'
    $lines | Where-Object { $_ -match '\[T3MPTEST\] tubelight|\[T3MP\] (Loaded|Runtime patches|.*installed)|Failed to patch|First uncaught exception' } | Set-Content -LiteralPath $out -Encoding utf8
    Write-Host "tube-light lines -> $out"
    Get-Content -LiteralPath $out | Select-Object -Last 40
} catch { $failure = $_.ToString() }
finally {
    if ($driverCreated) {
        try {
            # run_autoload_probe owns and stops only its directly launched process.
            # If Steam relaunches, retain files and report the incomplete cleanup.
            Assert-NoTubeProbeGame
            Move-ReleasePath $driverDeployPath (Join-Path $logDir 'driver-archive') $modsPath $logDir
        } catch { $cleanupFailure = $_.ToString() }
    }
    [ordered]@{ Failure=$failure; CleanupFailure=$cleanupFailure; DriverArchived=($driverCreated -and -not $cleanupFailure) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logDir 'tube-probe-summary.json') -Encoding utf8
}
if ($failure -or $cleanupFailure) { throw "Tube probe failed: $failure $cleanupFailure; records: $logDir" }
