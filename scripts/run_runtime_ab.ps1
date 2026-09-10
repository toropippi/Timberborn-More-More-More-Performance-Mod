# A/B throughput comparison of the v1.2 runtime patches on the installed game.
#
# A = same DLL with -t3mpTestRuntimeBaseline (runtime patches skipped, load
#     patches unchanged), B = normal. Runs in the given order (default ABBA),
# each through scripts/run_autoload_probe.ps1 with the test driver's
# -t3mpTestSpeed rate logger, then averages the per-window ticks/s after the
# warm-up window. Never touches the source settlement: stage the save into a
# disposable settlement first (see -Settlement).

[CmdletBinding()]
param(
    [string] $Settlement = 't3mp-ab',
    [string] $Save = 'n10c',
    [double] $Speed = 50,
    [int] $SecondsAfterLoad = 150,
    [string] $Order = 'ABBA',
    [int] $WarmupWindows = 1,
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$probe = Join-Path $PSScriptRoot 'run_autoload_probe.ps1'
$logDir = Join-Path $repoRoot 'testlogs'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = @()

foreach ($arm in $Order.ToCharArray()) {
    # A = runtime baseline, B = all runtime patches, T = tick traversal only,
    # W = water upload only, E = event delegates only.
    $extra = switch ($arm) {
        'A' { @('-t3mpTestRuntimeBaseline') }
        'T' { @('-t3mpTestNoEvents', '-t3mpTestNoWater') }
        'W' { @('-t3mpTestNoEvents', '-t3mpTestNoTick') }
        'E' { @('-t3mpTestNoTick', '-t3mpTestNoWater') }
        default { @() }
    }
    $before = Get-ChildItem -LiteralPath $logDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' } | Select-Object -ExpandProperty FullName
    Write-Host "=== arm $arm args: $($extra -join ' ')"
    & $probe -TimberbornExe $TimberbornExe -SettlementName $Settlement -SaveName $Save -Scenario LaunchArgs -TestSpeed $Speed `
        -SkipModManager -SecondsAfterLoad $SecondsAfterLoad -StopAfter -ExtraGameArgs $extra | Out-Host
    $log = Get-ChildItem -LiteralPath $logDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' -and $before -notcontains $_.FullName } |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { throw "No probe log produced for arm $arm" }
    $lines = Get-Content -LiteralPath $log.FullName
    $rates = @($lines | ForEach-Object { if ($_ -match '\[T3MPTEST\] Simulation rate ([0-9.]+) ticks/s window=(\d+)') { [double] $Matches[1] } })
    $used = @($rates | Select-Object -Skip $WarmupWindows)
    $mean = if ($used.Count -gt 0) { ($used | Measure-Object -Average).Average } else { 0 }
    $installed = @($lines | Where-Object { $_ -match '\[T3MP\] (Tick traversal installed|Water upload de-duplication installed|EventBus fast delegates installed|Runtime patches skipped)|\[T3MPTEST\] water ' } | Select-Object -Last 4) -join ' | '
    $failures = @($lines | Where-Object { $_ -match 'Failed to patch|First uncaught exception|\[T3MP\] .*(disabled|unavailable|vanilla retained)' })
    $results += [PSCustomObject]@{
        Arm = [string] $arm; Log = $log.Name; Windows = $used.Count; MeanTicksPerSecond = [math]::Round($mean, 3)
        Rates = ($rates | ForEach-Object { '{0:F2}' -f $_ }) -join ','; Installed = $installed; Failures = ($failures -join ' | ')
    }
    Start-Sleep -Seconds 5
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
$a = @($results | Where-Object { $_.Arm -eq 'A' } | Select-Object -ExpandProperty MeanTicksPerSecond)
$b = @($results | Where-Object { $_.Arm -eq 'B' } | Select-Object -ExpandProperty MeanTicksPerSecond)
$summary = [PSCustomObject]@{
    Stamp = $stamp; Settlement = $Settlement; Save = $Save; Speed = $Speed; SecondsAfterLoad = $SecondsAfterLoad; Order = $Order
    MeanA = if ($a.Count) { ($a | Measure-Object -Average).Average } else { 0 }
    MeanB = if ($b.Count) { ($b | Measure-Object -Average).Average } else { 0 }
    Runs = $results
}
$summary | Add-Member -NotePropertyName Ratio -NotePropertyValue $(if ($summary.MeanA -gt 0) { [math]::Round($summary.MeanB / $summary.MeanA, 4) } else { 0 })
$out = Join-Path $logDir "runtime-ab-$stamp.json"
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host ("A mean {0:F3} ticks/s, B mean {1:F3} ticks/s, B/A = {2:F4}  -> {3}" -f $summary.MeanA, $summary.MeanB, $summary.Ratio, $out)
