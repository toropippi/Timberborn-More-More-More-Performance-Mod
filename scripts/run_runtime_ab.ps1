# A/B throughput comparison of the v1.2 runtime patches on the installed game.
#
# A = same DLL with -t3mpTestRuntimeBaseline (runtime patches skipped),
# B = runtime optimizations. Both disable the tube fix and prepared visuals
# to isolate runtime cost. This is not a pure vanilla/product comparison.
# Runs in the given order (default ABBA),
# each through scripts/run_autoload_probe.ps1 with the test driver's
# -t3mpTestSpeed rate logger, then divides total ticks by elapsed time after the
# warm-up window. Never touches the source settlement: stage the save into a
# disposable settlement first (see -Settlement).

[CmdletBinding()]
param(
    [string] $Settlement = 't3mp-ab',
    [string] $Save = 'n10c',
    [double] $Speed = 50,
    [int] $SecondsAfterLoad = 150,
    [ValidatePattern('^[ABWENF]+$')]
    [string] $Order = 'ABBA',
    [ValidateRange(0, 100000)]
    [int] $WarmupWindows = 1,
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$probe = Join-Path $PSScriptRoot 'run_autoload_probe.ps1'
$logDir = Join-Path $repoRoot 'testlogs'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = @()
$Order = $Order.ToUpperInvariant()

foreach ($arm in $Order.ToCharArray()) {
    # A = runtime baseline, B = all runtime patches,
    # W = water upload only, E = event delegates only, N = all but frontier, F = frontier only.
    $extra = switch ($arm) {
        'A' { @('-t3mpTestRuntimeBaseline') }
        'B' { @() }
        'W' { @('-t3mpTestNoEvents', '-t3mpTestNoFrontier') }
        'E' { @('-t3mpTestNoWater', '-t3mpTestNoFrontier') }
        'N' { @('-t3mpTestNoFrontier') }
        'F' { @('-t3mpTestNoEvents', '-t3mpTestNoWater') }
        default { throw "Unknown arm: $arm" }
    }
    $extra = @($extra) + @('-t3mpTestNoTubeFix', '-t3mpTestVisualPreparationBaseline')
    # A previous run's game process may still be shutting down.
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
    Start-Sleep -Seconds 5
    $before = Get-ChildItem -LiteralPath $logDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' } | Select-Object -ExpandProperty FullName
    Write-Host "=== arm $arm args: $($extra -join ' ')"
    & $probe -TimberbornExe $TimberbornExe -SettlementName $Settlement -SaveName $Save -Scenario LaunchArgs -TestSpeed $Speed `
        -SkipModManager -SecondsAfterLoad $SecondsAfterLoad -StopAfter -ExtraGameArgs $extra | Out-Host
    $log = Get-ChildItem -LiteralPath $logDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' -and $before -notcontains $_.FullName } |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { throw "No probe log produced for arm $arm" }
    $lines = Get-Content -LiteralPath $log.FullName
    $windows = @($lines | ForEach-Object {
        if ($_ -match '\[T3MPTEST\] Simulation rate ([0-9.]+) ticks/s window=(\d+) ticks=(\d+).* elapsed=([0-9.]+)') {
            [PSCustomObject]@{ Rate = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture); Ticks = [long]$Matches[3]; Seconds = [double]::Parse($Matches[4], [Globalization.CultureInfo]::InvariantCulture) }
        }
    })
    $used = @($windows | Select-Object -Skip $WarmupWindows)
    if (-not $used.Count -or ($used | Where-Object { $_.Seconds -le 0 })) { throw "Insufficient valid windows for $arm; deploy the current test driver" }
    $ticks = ($used | Measure-Object Ticks -Sum).Sum
    $elapsed = ($used | Measure-Object Seconds -Sum).Sum
    $mean = $ticks / $elapsed
    $expectedFeatures = @{
        A=@(); B=@('events','water','frontier'); W=@('water'); E=@('events'); N=@('events','water'); F=@('frontier')
    }
    $markers = @{
        events='[T3MP] EventBus fast delegates installed.'; tick='[T3MP] Tick traversal installed';
        water='[T3MP] Water upload de-duplication installed'; frontier='[T3MP] Tick frontier installed.'; tube='[T3MP] Tube visit fix installed.'
    }
    foreach ($feature in $markers.Keys) {
        $present = @($lines | Where-Object { $_.Contains($markers[$feature]) }).Count -gt 0
        if ($present -ne ($expectedFeatures[[string]$arm] -contains $feature)) { throw "Unexpected installed feature $feature for arm $arm" }
    }
    if ($lines -match '\[T3MP\].*(removed:|First uncaught exception)' -or $lines -match 'First uncaught exception|^Rethrow as Exception:') { throw "Patch removal or game failure for arm $arm" }
    $installed = @($lines | Where-Object { $_ -match '\[T3MP\] (Tick traversal installed|Water upload de-duplication installed|EventBus fast delegates installed|Runtime patches skipped|Tick frontier installed)' }) -join ' | '
    $failures = @($lines | Where-Object { $_ -match 'Failed to patch|First uncaught exception|\[T3MP\] .*(disabled|unavailable|vanilla retained)' })
    $results += [PSCustomObject]@{
        Arm = [string] $arm; Log = $log.Name; Windows = $used.Count; MeanTicksPerSecond = [math]::Round($mean, 3)
        Ticks = $ticks; ElapsedSeconds = $elapsed
        Rates = ($windows | ForEach-Object { '{0:F2}' -f $_.Rate }) -join ','; Installed = $installed; Failures = ($failures -join ' | ')
    }
    Start-Sleep -Seconds 5
}

$results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
$a = @($results | Where-Object { $_.Arm -eq 'A' } | Select-Object -ExpandProperty MeanTicksPerSecond)
$b = @($results | Where-Object { $_.Arm -eq 'B' } | Select-Object -ExpandProperty MeanTicksPerSecond)
$summary = [PSCustomObject]@{
    Stamp = $stamp; Settlement = $Settlement; Save = $Save; Speed = $Speed; SecondsAfterLoad = $SecondsAfterLoad; Order = $Order
    Comparison = 'Runtime attribution only; loading remains enabled, tube fix and prepared entity visuals disabled in every arm. Fixed wall-time, not matched simulation phases.'
    MeanA = if ($a.Count) { ($a | Measure-Object -Average).Average } else { $null }
    MeanB = if ($b.Count) { ($b | Measure-Object -Average).Average } else { $null }
    Runs = $results
}
$summary | Add-Member -NotePropertyName Ratio -NotePropertyValue $(if ($summary.MeanA -gt 0 -and $b.Count) { [math]::Round($summary.MeanB / $summary.MeanA, 4) } else { $null })
$out = Join-Path $logDir "runtime-ab-$stamp.json"
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $out -Encoding UTF8
if ($null -ne $summary.Ratio) {
    Write-Host ("A mean {0:F3} ticks/s, B mean {1:F3} ticks/s, B/A = {2:F4}  -> {3}" -f $summary.MeanA, $summary.MeanB, $summary.Ratio, $out)
} else { Write-Host "Feature runs saved without A/B ratio: $out" }
