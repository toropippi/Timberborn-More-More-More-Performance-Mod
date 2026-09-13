# V = no T3MP, B = currently deployed T3MP. Both retain the same test driver
# and Harmony for measurement. Saves/settings are restored and stages archived.
# C/R = installed T3MP plus identical experimental road observers, cache off/on.
# H/U = installed T3MP plus bool call-site wrappers, native/expanded IL.
# K = installed T3MP with only TickEntityFast disabled; Frontier remains enabled.
[CmdletBinding()]
param(
    [ValidatePattern('^[VBCRHUK]+$')][string] $Order = 'VBBV',
    [ValidateRange(1, 1000000)][int] $WarmupTicks = 256,
    [ValidateRange(1, 1000000)][int] $MeasuredTicks = 2048,
    [ValidateRange(1, 10000)][double] $Speed = 99,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 900,
    [string] $SourceSave = (Join-Path $env:USERPROFILE 'Documents\Timberborn\Saves\n10c\n10c.timber'),
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$Order = $Order.ToUpperInvariant()
$gameRoot = Split-Path -Parent (Split-Path -Parent $SourceSave)
$documents = Split-Path -Parent $gameRoot
$mods = Join-Path $documents 'Mods'
$driver = Join-Path $mods 'T3MPTestDriver'
$candidate = Join-Path $mods 'T3MP\Code.dll'
$builtDriver = Join-Path $repo 'src\T3MPTestDriver\bin\Release\netstandard2.1\T3MPTestDriver.dll'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$settlement = "t3mp-perf-$stamp"
$stage = Join-Path $gameRoot $settlement
$output = Join-Path $repo "testlogs\fixed-ab-$stamp"
$prefs = 'HKCU:\Software\Mechanistry\Timberborn'
# Resolve existing keys from this installation; never invent a PlayerPrefs hash.
$keyPatterns = @{
    Local = '^ModEnabled\.Local\.T3MP\.T3MP_h\d+$'
    Workshop = '^ModEnabled\.Steam Workshop\.3756656421\.T3MP_h\d+$'
    Harmony = '^ModEnabled\.Steam Workshop\.3284904751\.Harmony_h\d+$'
}
$keys = @{}
$registry = Get-Item -LiteralPath $prefs
foreach ($name in $keyPatterns.Keys) {
    $matchesForKey = @($registry.GetValueNames() | Where-Object { $_ -match $keyPatterns[$name] })
    if ($matchesForKey.Count -ne 1) { throw "Expected one existing PlayerPrefs key for $name" }
    $keys[$name] = $matchesForKey[0]
}
$driverKeys = @($registry.GetValueNames() | Where-Object { $_ -match '^ModEnabled\.Local\.T3MPTestDriver\.T3MPTestDriver_h\d+$' })
foreach ($key in $driverKeys) { if ($registry.GetValue($key) -ne 1) { throw 'Test driver is disabled in Mod Manager' } }
$original = @{}
foreach ($key in $keys.Values) {
    if ($registry.GetValueKind($key) -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw "Unexpected preference type: $key" }
    $original[$key] = $registry.GetValue($key)
}
if (Get-Process -Name Timberborn -ErrorAction SilentlyContinue) { throw 'Close the existing game before benchmarking' }
foreach ($path in @($SourceSave, $TimberbornExe, $candidate, $builtDriver)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing file: $path" }
}
if ((Test-Path -LiteralPath $driver) -or (Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $output)) { throw 'Benchmark stage already exists; preserve it and use a new run' }
$sourceHash = (Get-FileHash -LiteralPath $SourceSave).Hash
$candidateHash = (Get-FileHash -LiteralPath $candidate).Hash
$driverHash = (Get-FileHash -LiteralPath $builtDriver).Hash
$manifest = Get-Content -LiteralPath (Join-Path $mods 'T3MP\manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Id -ne 'T3MP') { throw 'Unexpected candidate manifest' }
$results = @()
$failure = $null
$stageCreated = $false
$driverCreated = $false
New-Item -ItemType Directory -Path $output | Out-Null
$original | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'original-mod-prefs.json') -Encoding utf8
function Stop-OwnedProbe {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='Timberborn.exe'")
    foreach ($item in $running) {
        if ($item.CommandLine -like "*$settlement*" -and $item.CommandLine -like '*-t3mpTestBenchmarkTicks*') {
            Stop-Process -Id $item.ProcessId -Force
            Wait-Process -Id $item.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
        } else { throw "Unrelated game process $($item.ProcessId); do not overwrite its settings" }
    }
}
try {
    New-Item -ItemType Directory -Path $driver | Out-Null
    $driverCreated = $true
    Copy-Item -LiteralPath $builtDriver -Destination $driver
    Copy-Item -LiteralPath (Join-Path $repo 'testmod\manifest.json') -Destination $driver
    New-Item -ItemType Directory -Path $stage | Out-Null
    $stageCreated = $true
    Copy-Item -LiteralPath $SourceSave -Destination (Join-Path $stage 'n10c.timber')
    foreach ($arm in $Order.ToCharArray()) {
        Stop-OwnedProbe
        Set-ItemProperty -LiteralPath $prefs -Name $keys.Local -Value ([int]($arm -ne 'V'))
        Set-ItemProperty -LiteralPath $prefs -Name $keys.Workshop -Value 0
        Set-ItemProperty -LiteralPath $prefs -Name $keys.Harmony -Value 1
        # Reset the input file even if an earlier session saved over it.
        Copy-Item -LiteralPath $SourceSave -Destination (Join-Path $stage 'n10c.timber') -Force
        $runDir = Join-Path $output ("{0:D2}-{1}" -f ($results.Count + 1), $arm)
        New-Item -ItemType Directory -Path $runDir | Out-Null
        $gameArgs = @('-t3mpTestBenchmarkTicks', [string]$MeasuredTicks, '-t3mpTestBenchmarkWarmup', [string]$WarmupTicks)
        if ($arm -eq 'K') { $gameArgs += '-t3mpTestNoTick' }
        if ($arm -eq 'C' -or $arm -eq 'R') { $gameArgs += '-t3mpTestRoadCache' }
        if ($arm -eq 'C') { $gameArgs += '-t3mpTestRoadCacheBaseline' }
        if ($arm -eq 'H' -or $arm -eq 'U') { $gameArgs += '-t3mpTestBoolInline' }
        if ($arm -eq 'H') { $gameArgs += '-t3mpTestBoolBaseline' }
        & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') -TimberbornExe $TimberbornExe -SettlementName $settlement -SaveName n10c `
            -Scenario LaunchArgs -TestSpeed $Speed -SkipModManager -OutputDir $runDir -SecondsAfterLoad $TimeoutSeconds `
            -CompletionPattern '\[T3MPTEST\] Fixed benchmark result ' -StopAfter `
            -ExtraGameArgs $gameArgs | Out-Host
        Start-Sleep -Seconds 5
        Stop-OwnedProbe
        $logs = @(Get-ChildItem -LiteralPath $runDir -Filter 'autoload-*.log' | Where-Object { $_.Name -notlike 'autoload-previous-*' })
        if ($logs.Count -ne 1) { throw "Expected one log for $arm" }
        $lines = Get-Content -LiteralPath $logs[0].FullName
        $resultLines = @($lines | Where-Object { $_ -match '^\[T3MPTEST\] Fixed benchmark result ' })
        if ($resultLines.Count -ne 1) { throw "Missing or duplicated completion for $arm" }
        $result = ($resultLines[0] -replace '^\[T3MPTEST\] Fixed benchmark result ', '') | ConvertFrom-Json
        if (-not $result.valid -or $result.endTick - $result.startTick -ne $MeasuredTicks) { throw "Invalid measurement for $arm" }
        foreach ($name in @('EventBusFastDelegates','TickEntityFast','WaterTextureUpload','TickFrontier','TubeVisitFix')) {
            $expected = if ($arm -eq 'V') { 'absent' } elseif ($arm -eq 'K' -and $name -eq 'TickEntityFast') { 'False' } else { 'True' }
            if (($result.patches -split ',') -cnotcontains "$name=$expected") { throw "Unexpected patch state for $arm : $($result.patches)" }
        }
        $roadInstalled = [string]([bool]($arm -eq 'C' -or $arm -eq 'R'))
        $roadEnabled = [string]([bool]($arm -eq 'R'))
        if (($result.patches -split ',') -cnotcontains "RoadCacheInstalled=$roadInstalled" -or
            ($result.patches -split ',') -cnotcontains "RoadCacheEnabled=$roadEnabled") { throw 'Unexpected road experiment state' }
        $boolInstalled = [string]([bool]($arm -eq 'H' -or $arm -eq 'U'))
        $boolEnabled = [string]([bool]($arm -eq 'U'))
        if (($result.patches -split ',') -cnotcontains "BoolInlineInstalled=$boolInstalled" -or
            ($result.patches -split ',') -cnotcontains "BoolInlineEnabled=$boolEnabled") { throw 'Unexpected bool experiment state' }
        $loadedMods = @($lines | Where-Object { $_ -match '^- .+ \(v[^)]+\)$' })
        $allowed = @('- Harmony (v2.4.1)', '- T3MP Test Driver (dev only) (v1.0.0)')
        if ($arm -ne 'V') { $allowed += "- $($manifest.Name) (v$($manifest.Version))" }
        if (($loadedMods.Count -ne $allowed.Count) -or ($loadedMods | Where-Object { $_ -cnotin $allowed })) { throw "Unexpected loaded mods: $($loadedMods -join ', ')" }
        if ($lines -match 'First uncaught exception|^Rethrow as Exception:') { throw "Game exception in $arm" }
        $result | Add-Member -NotePropertyName Arm -NotePropertyValue ([string]$arm)
        $result | Add-Member -NotePropertyName Log -NotePropertyValue $logs[0].FullName
        $result | Add-Member -NotePropertyName GameVersion -NotePropertyValue (@($lines | Where-Object { $_ -match '^Starting game version ' }) -join '')
        if ($results.Count -and ($result.display -cne $results[0].display -or $result.timeScale -ne $results[0].timeScale -or $result.GameVersion -cne $results[0].GameVersion -or $result.originTick -ne $results[0].originTick -or $result.focused -ne $results[0].focused)) { throw 'Display, speed, game version, focus or tick origin differed between arms' }
        $results += $result
        $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'runs.json') -Encoding utf8
        Write-Host ("arm {0}: {1:F3} ticks/s, {2:F2} FPS, p99 {3:F1} ms" -f $arm, $result.ticksPerSecond, $result.averageFps, $result.p99FrameMs)
    }
} catch {
    $failure = $_.ToString()
    throw
} finally {
    # Archives stay under this run's workspace directory; no recursive deletion.
    Stop-OwnedProbe
    foreach ($key in $original.Keys) { Set-ItemProperty -LiteralPath $prefs -Name $key -Value $original[$key] }
    if ($driverCreated) {
        $resolvedDriver = (Resolve-Path -LiteralPath $driver).Path
        if ($resolvedDriver -ne [IO.Path]::GetFullPath((Join-Path $mods 'T3MPTestDriver'))) { throw 'Unexpected driver archive source' }
        Move-Item -LiteralPath $resolvedDriver -Destination (Join-Path $output 'driver-archive')
    }
    if ($stageCreated) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ($resolvedStage -ne [IO.Path]::GetFullPath((Join-Path $gameRoot $settlement))) { throw 'Unexpected save archive source' }
        Move-Item -LiteralPath $resolvedStage -Destination (Join-Path $output 'save-archive')
    }
    [PSCustomObject]@{ Order=$Order; SourceSave=$SourceSave; SourceHash=$sourceHash; CandidateHash=$candidateHash; DriverHash=$driverHash; WarmupTicks=$WarmupTicks; MeasuredTicks=$MeasuredTicks; Failure=$failure; Runs=$results } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    if ((Get-FileHash -LiteralPath $SourceSave).Hash -ne $sourceHash -or (Get-FileHash -LiteralPath $candidate).Hash -ne $candidateHash) { throw 'Original save or candidate changed during measurement' }
}
Write-Host "Results and restoration archive: $output"
