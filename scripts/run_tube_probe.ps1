# Runs the T3MPTestDriver tube-light monitor (-t3mpTestTubeLights) on a staged
# settlement and extracts the "[T3MPTEST] tubelight" lines. Dev-only.
[CmdletBinding()]
param(
    [string] $Settlement = 't3mp-ab11',
    [string] $Save = 'n10c',
    [double] $Speed = 3,
    [int] $SecondsAfterLoad = 120,
    [string] $TimberbornExe = '<USERPROFILE>\Documents\TimberbornVersions\Timberborn-1.1\Timberborn.exe',
    [string] $Install = '<USERPROFILE>\Documents\TimberbornVersions\Timberborn-1.1',
    [string[]] $ExtraGameArgs = @(),
    [switch] $SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$logDir = Join-Path $repoRoot 'testlogs'
$modsPath = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
$driverDeployPath = Join-Path $modsPath 'T3MPTestDriver'
if (-not $SkipBuild) {
    dotnet build (Join-Path $repoRoot 'src\T3MPTestDriver\T3MPTestDriver.csproj') -c Release "-p:TimberbornInstall=$Install" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'driver build failed' }
}
New-Item -ItemType Directory -Force -Path $driverDeployPath | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'testmod\manifest.json') -Destination $driverDeployPath -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\T3MPTestDriver\bin\Release\netstandard2.1\T3MPTestDriver.dll') -Destination $driverDeployPath -Force

$deadline = (Get-Date).AddSeconds(90)
while ((Get-Process | Where-Object { $_.ProcessName -like '*Timberborn*' }) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
# Steam may relaunch a previous probe under a new PID; identify it by command line.
$remaining = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like '*Timberborn*' })
foreach ($gameProcess in $remaining) {
    if ($gameProcess.CommandLine -like '*-t3mpTest*') {
        Write-Host "Stopping leftover Timberborn probe pid $($gameProcess.ProcessId)"
        Stop-Process -Id $gameProcess.ProcessId -Force
    }
}
if ($remaining | Where-Object { $_.CommandLine -notlike '*-t3mpTest*' }) {
    throw 'Timberborn is running (not a probe session); close it first'
}
Start-Sleep -Seconds 3
$before = Get-ChildItem -LiteralPath $logDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' } | Select-Object -ExpandProperty FullName
$extra = @('-t3mpTestTubeLights') + $ExtraGameArgs
Write-Host "=== tube probe speed=$Speed args: $($extra -join ' ')"
& (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') -TimberbornExe $TimberbornExe -SettlementName $Settlement -SaveName $Save -Scenario LaunchArgs -TestSpeed $Speed `
    -SkipModManager -SecondsAfterLoad $SecondsAfterLoad -StopAfter -ExtraGameArgs $extra | Out-Host
$log = Get-ChildItem -LiteralPath $logDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' -and $before -notcontains $_.FullName } |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $log) { throw 'No probe log produced' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$out = Join-Path $logDir "tubelight-$stamp.log"
Get-Content -LiteralPath $log.FullName | Where-Object { $_ -match '\[T3MPTEST\] tubelight|\[T3MP\] (Loaded|Runtime patches|.*installed)|Failed to patch|First uncaught exception' } | Set-Content -LiteralPath $out -Encoding utf8
Write-Host "tube-light lines -> $out (source $($log.Name))"
Get-Content -LiteralPath $out | Select-Object -Last 40
