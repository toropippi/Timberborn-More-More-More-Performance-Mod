# V = no T3MP, B = currently deployed T3MP. Both retain the same test driver
# and Harmony for measurement. Saves/settings are restored and stages archived.
# C/R = installed T3MP plus identical experimental road observers, cache off/on.
# H/U = installed T3MP plus bool call-site wrappers, native/expanded IL.
# D = same candidate with only walker speed delegate reuse disabled; compare D/B.
# S = same candidate with only terrain single-lookup visits disabled; compare S/B.
# O = the actual Workshop v1.1.7 package, with local T3MP disabled.
# P = alternate trial DLL; B/P compares full current installation against that trial.
# LegacyRuntime P loads an audited v1.1.7 byte variant under the local manifest.
# Use its DLL hash and live settings as identity; the original MVID is retained.
# The actual Workshop file is read-only throughout this comparison.
# With RuntimeAttribution: E/W/F disable only events/water/frontier; D/S disable
# walking delegates/terrain visits; M keeps the exact 0.1-unit movement loop
# (coarse corner stepping off). Prepared visuals stay disabled in every arm
# because removing Frontier otherwise changes their load compatibility outcome.
# MovementValidation audits the movement loop against the native body (single
# B or P arm, not a performance run); MovementExact audits or measures the exact
# loop instead of the default coarse stepping.
[CmdletBinding()]
param(
    [ValidatePattern('^[VBCRHUDSPOEWFM]+$')][string] $Order = 'VBBV',
    [ValidateRange(1, 1000000)][int] $WarmupTicks = 256,
    [ValidateRange(1, 1000000)][int] $MeasuredTicks = 2048,
    [ValidateRange(1, 10000)][double] $Speed = 99,
    [ValidateRange(1, 86400)][int] $TimeoutSeconds = 900,
    [string] $SourceSave = (Join-Path $env:USERPROFILE 'Documents\Timberborn\Saves\n10c\n10c.timber'),
    [switch] $MatchedConditions,
    [switch] $RuntimeAttribution,
    [string] $LegacyModDir = '',
    [string] $ComparisonDll = '',
    # ProductVariant: P is a build of the shipped feature set (no experiment patches), compared against the installed DLL.
    [ValidateSet('', 'ProductVariant', 'ResourceCounterLookups', 'NodeCoordinateLookup', 'HarvestCandidateTree', 'HarvestIndex', 'LegacyRuntime', 'TickDispatch', 'TickComponentDispatch', 'NonlinearSpeedMemo', 'AllowedGoodRows', 'ExactBatch', 'YielderReachabilitySkip')][string] $ExpectedExperiment = '',
    [switch] $TickPortValidation,
    [string] $LegacyVariantRecord = '',
    [switch] $LegacyVisualPreparationBaseline,
    [switch] $HarvestTreeValidation,
    [switch] $MovementValidation,
    [switch] $MovementExact,
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseVerification.ps1')
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$Order = $Order.ToUpperInvariant()
if($RuntimeAttribution -and (-not $MatchedConditions -or $Order -notmatch '^[BEWFDSM]+$')) {throw 'Runtime attribution requires matched B/E/W/F/D/S/M arms'}
if(-not $RuntimeAttribution -and $Order -match '[EWF]') {throw 'E/W/F require RuntimeAttribution'}
if ($Order.Contains('P') -and ([string]::IsNullOrWhiteSpace($ComparisonDll) -or [string]::IsNullOrWhiteSpace($ExpectedExperiment))) { throw 'P requires ComparisonDll and ExpectedExperiment' }
if ($ComparisonDll) { $ComparisonDll = (Resolve-Path -LiteralPath $ComparisonDll).Path }
if ($TickPortValidation -and ($Order -ne 'P' -or $ExpectedExperiment -ne 'TickComponentDispatch')) { throw 'Tick port validation requires isolated P arm' }
$legacyVariant = $null
if ($ExpectedExperiment -eq 'LegacyRuntime') {
    if (-not $MatchedConditions -or $Order -notmatch '^[BPO]+$' -or -not $LegacyVariantRecord) { throw 'LegacyRuntime requires matched B/P/O and an audited variant record' }
    $legacyVariant = Get-Content -LiteralPath $LegacyVariantRecord -Raw | ConvertFrom-Json
    if ($legacyVariant.OriginalHash -ne '825B95FA2DAEAFE82B9B54B4F94F4B3EFB02BCB98025EEF4AF1C3191320B16FD') { throw 'Unexpected original legacy binary' }
    $matches = @($legacyVariant.Variants | Where-Object { $_.Path -eq $ComparisonDll })
    if ($matches.Count -ne 1 -or $matches[0].Hash -ne (Get-FileHash -LiteralPath $ComparisonDll).Hash) { throw 'Legacy variant identity mismatch' }
    $legacyVariant = $matches[0]
} elseif ($LegacyVariantRecord) { throw 'LegacyVariantRecord requires LegacyRuntime' }
if ($LegacyVisualPreparationBaseline -and ($ExpectedExperiment -ne 'LegacyRuntime' -or $Order -ne 'P')) { throw 'Legacy visual baseline requires isolated legacy P arm' }
if($HarvestTreeValidation -and ($ExpectedExperiment -notin @('HarvestCandidateTree','HarvestIndex') -or $Order -ne 'P')) {throw 'Harvest validation requires isolated P arm'}
if($MovementValidation -and ($Order -notin @('B','P') -or $ExpectedExperiment -eq 'LegacyRuntime' -or $LegacyModDir)) {throw 'Movement validation requires a single B or P arm with the movement hooks installed'}
if ($Order.Contains('O') -and (-not $MatchedConditions -or -not $LegacyModDir)) { throw 'O requires MatchedConditions and LegacyModDir' }
$legacyManifest = $null; $legacyHash = $null
if ($LegacyModDir) {
    $LegacyModDir = (Resolve-Path -LiteralPath $LegacyModDir).Path
    $steamApps = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $TimberbornExe))
    $actualWorkshop = (Resolve-Path -LiteralPath (Join-Path $steamApps 'workshop\content\1062090\3756656421')).Path
    if ($LegacyModDir -ne $actualWorkshop) { throw 'LegacyModDir must be the actual enabled Workshop package' }
    $legacyManifest = Get-Content -LiteralPath (Join-Path $LegacyModDir 'manifest.json') -Raw | ConvertFrom-Json
    if ($legacyManifest.Id -ne 'T3MP' -or $legacyManifest.Version -ne '1.1.7') { throw 'Expected actual legacy 1.1.7 package' }
    $legacyHash = (Get-FileHash -LiteralPath (Join-Path $LegacyModDir 'Code.dll')).Hash
}
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
$comparisonHash = if ($ComparisonDll) { (Get-FileHash -LiteralPath $ComparisonDll).Hash } else { $null }
$driverHash = (Get-FileHash -LiteralPath $builtDriver).Hash
$manifest = Get-Content -LiteralPath (Join-Path $mods 'T3MP\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.Id -ne 'T3MP') { throw 'Unexpected candidate manifest' }
$results = @()
$failure = $null
$stageCreated = $false
$driverCreated = $false
New-Item -ItemType Directory -Path $output | Out-Null
$originalDll = Join-Path $output 'original-Code.dll'
if ($ComparisonDll) { Copy-Item -LiteralPath $candidate -Destination $originalDll }
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
        $legacyArm = $arm -eq 'O' -or ($arm -eq 'P' -and $ExpectedExperiment -eq 'LegacyRuntime')
        if ($ComparisonDll) {
            $sourceDll = if ($arm -eq 'P') { $ComparisonDll } else { $originalDll }
            Copy-Item -LiteralPath $sourceDll -Destination $candidate -Force
        }
        $runDllHash = (Get-FileHash -LiteralPath $candidate).Hash
        $expectedDllHash = if ($arm -eq 'P') { $comparisonHash } else { $candidateHash }
        if ($runDllHash -ne $expectedDllHash) { throw 'Unexpected run DLL' }
        Set-ItemProperty -LiteralPath $prefs -Name $keys.Local -Value ([int]($arm -ne 'V' -and $arm -ne 'O'))
        Set-ItemProperty -LiteralPath $prefs -Name $keys.Workshop -Value ([int]($arm -eq 'O'))
        Set-ItemProperty -LiteralPath $prefs -Name $keys.Harmony -Value 1
        # Reset the input file even if an earlier session saved over it.
        Copy-Item -LiteralPath $SourceSave -Destination (Join-Path $stage 'n10c.timber') -Force
        $runDir = Join-Path $output ("{0:D2}-{1}" -f ($results.Count + 1), $arm)
        New-Item -ItemType Directory -Path $runDir | Out-Null
        $gameArgs = @('-t3mpTestBenchmarkTicks', [string]$MeasuredTicks, '-t3mpTestBenchmarkWarmup', [string]$WarmupTicks)
        if ($MatchedConditions) { $gameArgs += '-t3mpTestMatchedConditions' }
        if ($RuntimeAttribution) { $gameArgs += '-t3mpTestVisualPreparationBaseline' }
        if ($LegacyVisualPreparationBaseline) { $gameArgs += '-t3mpTestVisualPreparationBaseline' }
        if ($arm -eq 'E') { $gameArgs += '-t3mpTestNoEvents' }
        if ($arm -eq 'W') { $gameArgs += '-t3mpTestNoWater' }
        if ($arm -eq 'F') { $gameArgs += '-t3mpTestNoFrontier' }
        if ($ExpectedExperiment -eq 'HarvestCandidateTree') { $gameArgs += '-t3mpTestHarvestTree' }
        if ($ExpectedExperiment -eq 'HarvestIndex') { $gameArgs += '-t3mpTestHarvestIndex' }
        if ($arm -eq 'P' -and $ExpectedExperiment -eq 'TickDispatch') { $gameArgs += '-t3mpTestNoComponentCalls' }
        if ($TickPortValidation) { $gameArgs += '-t3mpTestTickPortValidate' }
        if ($HarvestTreeValidation) { $gameArgs += $(if($ExpectedExperiment -eq 'HarvestIndex'){'-t3mpTestHarvestIndexValidate'}else{'-t3mpTestHarvestTreeValidate'}) }
        if ($MovementValidation) { $gameArgs += '-t3mpTestMovementValidate' }
        if ($MovementExact -or $arm -eq 'M') { $gameArgs += '-t3mpTestNoMovementCoarse' }
        if ($arm -eq 'D') { $gameArgs += '-t3mpTestNoWalkerDelegates' }
        if ($arm -eq 'S') { $gameArgs += '-t3mpTestNoTerrainVisits' }
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
        if($HarvestTreeValidation) {
            if($result.performanceValid -or $result.validationMode -ne 'harvest-order') {throw 'Validation was classified as performance'}
        } elseif($TickPortValidation) {
            if($result.performanceValid -or $result.validationMode -ne 'tick-port') {throw 'Tick port validation was classified as performance'}
        } elseif($MovementValidation) {
            if($result.performanceValid -or $result.validationMode -ne 'movement') {throw 'Movement validation was classified as performance'}
        } elseif(-not $result.performanceValid -or $result.validationMode -ne 'none') {throw 'Diagnostics contaminated performance'}
        # AllowedGoodRows ships since the 2026-09-15 evening build, YielderReachabilitySkip since v1.2.3 (product features, absent without T3MP).
        foreach ($name in @('EventBusFastDelegates','TickEntityFast','WaterTextureUpload','TickFrontier','TubeVisitFix','AllowedGoodRows','YielderReachabilitySkip')) {
            $expected = if ($arm -eq 'V' -or $legacyArm -or $name -eq 'TickEntityFast') { 'absent' } else { 'True' }
            if (($arm -eq 'E' -and $name -eq 'EventBusFastDelegates') -or ($arm -eq 'W' -and $name -eq 'WaterTextureUpload') -or ($arm -eq 'F' -and $name -eq 'TickFrontier')) { $expected='False' }
            if (($result.patches -split ',') -cnotcontains "$name=$expected") { throw "Unexpected patch state for $arm : $($result.patches)" }
        }
        $rowsExpected = if ($arm -eq 'V' -or $legacyArm) { 'absent' } else { 'True' }
        foreach ($name in @('AllowedGoodRowsActive', 'YielderReachabilitySkipActive')) {
            if (($result.patches -split ',') -cnotcontains "$name=$rowsExpected") { throw "Unexpected product feature state for $arm : $($result.patches)" }
        }
        $walkerExpected = if ($arm -eq 'V' -or $legacyArm) { 'absent' } elseif ($arm -eq 'D') { 'False' } else { 'True' }
        # The driver's validation hooks on MoveAlongPath are a later patch generation: delegate reuse is off for that run.
        $reusingExpected = if ($MovementValidation -and $walkerExpected -eq 'True') { 'False' } else { $walkerExpected }
        if (($result.patches -split ',') -cnotcontains "WalkerSpeedDelegates=$walkerExpected" -or ($result.patches -split ',') -cnotcontains "WalkerDelegatesReusing=$reusingExpected") { throw "Unexpected walker state for $arm : $($result.patches)" }
        $terrainExpected = if ($arm -eq 'V' -or $legacyArm) { 'absent' } elseif ($arm -eq 'S') { 'False' } else { 'True' }
        foreach ($name in @('TerrainNeighborVisits', 'TerrainVisitsActive')) {
            if (($result.patches -split ',') -cnotcontains "$name=$terrainExpected") { throw "Unexpected terrain state for $arm : $($result.patches)" }
        }
        # Movement sub-steps ship with walker delegates: absent without T3MP, False with delegates off (D), inactive during validation.
        $movementExpected = if ($arm -eq 'V' -or $legacyArm) { 'absent' } elseif ($arm -eq 'D') { 'False' } else { 'True' }
        $movementActive = if ($MovementValidation -and $movementExpected -eq 'True') { 'False' } else { $movementExpected }
        if (($result.patches -split ',') -cnotcontains "MovementSubsteps=$movementExpected" -or ($result.patches -split ',') -cnotcontains "MovementSubstepsActive=$movementActive") { throw "Unexpected movement state for $arm : $($result.patches)" }
        # ExactBatch: the four sub-1% exact rewrites of 2026-09-15 measured together in one P build.
        $exactBatch = @('NeedUpdateInline', 'ShaftEfficiencyOnce', 'PathEdgeSingleScan', 'NeedAppraisalLookup')
        foreach ($experiment in @('ResourceCounterLookups', 'NodeCoordinateLookup', 'HarvestCandidateTree', 'HarvestIndex', 'NonlinearSpeedMemo') + $exactBatch) {
            foreach ($suffix in @('', 'Active')) {
                $expected = if ($arm -eq 'P' -and ($experiment -eq $ExpectedExperiment -or ($ExpectedExperiment -eq 'ExactBatch' -and $experiment -in $exactBatch))) { 'True' } else { 'absent' }
                if (($result.patches -split ',') -cnotcontains "$experiment$suffix=$expected") { throw "Unexpected trial state: $($result.patches)" }
            }
        }
        $roadInstalled = [string]([bool]($arm -eq 'C' -or $arm -eq 'R'))
        $dispatchStates = $result.patches -split ','
        $dispatchExpected = if ($arm -eq 'P' -and $ExpectedExperiment -eq 'TickDispatch') { 'True' }
            elseif ($arm -eq 'V' -or $legacyArm -or $dispatchStates -contains 'TickDispatch=absent') { 'absent' }
            else { 'True' }
        foreach ($name in @('TickDispatch', 'TickDispatchActive')) {
            if ($dispatchStates -cnotcontains "$name=$dispatchExpected") { throw "Unexpected tick dispatch state: $($result.patches)" }
        }
        $componentExpected = if ($arm -eq 'V' -or $legacyArm -or $dispatchStates -contains 'TickComponentDispatch=absent') { 'absent' }
            elseif ($arm -eq 'F' -or ($arm -eq 'P' -and $ExpectedExperiment -eq 'TickDispatch')) { 'False' }
            else { 'True' }
        if ($arm -eq 'P' -and $ExpectedExperiment -eq 'TickComponentDispatch') { $componentExpected = 'True' }
        foreach ($suffix in @('', 'Active')) {
            $expected = if ($TickPortValidation -and $suffix -eq 'Active') { 'False' } else { $componentExpected }
            if ($dispatchStates -cnotcontains "TickComponentDispatch$suffix=$expected") { throw "Unexpected component dispatch state: $($result.patches)" }
        }
        $roadEnabled = [string]([bool]($arm -eq 'R'))
        if (($result.patches -split ',') -cnotcontains "RoadCacheInstalled=$roadInstalled" -or
            ($result.patches -split ',') -cnotcontains "RoadCacheEnabled=$roadEnabled") { throw 'Unexpected road experiment state' }
        $boolInstalled = [string]([bool]($arm -eq 'H' -or $arm -eq 'U'))
        $boolEnabled = [string]([bool]($arm -eq 'U'))
        if (($result.patches -split ',') -cnotcontains "BoolInlineInstalled=$boolInstalled" -or
            ($result.patches -split ',') -cnotcontains "BoolInlineEnabled=$boolEnabled") { throw 'Unexpected bool experiment state' }
        if ($MatchedConditions -and (-not $result.matchedConditions -or $result.timeScale -ne $Speed)) { throw 'Matched effective speed failed' }
        $expectedLegacy = if ($legacyArm) { 'mode=Optimized,blackout=False,smooth=False,tickInitialized=True,tickDisabled=False,flatHooks=True,fastTicks=True,tickWarnings=0' } else { 'absent' }
        if ($arm -eq 'P' -and $ExpectedExperiment -eq 'LegacyRuntime') {
            if ($result.legacySettings -cne $legacyVariant.ExpectedSettings) { throw 'Live legacy settings differ from audited variant' }
            if ($legacyVariant.Disabled -contains 'EnableTickDispatchOptimizer') {
                $expectedLegacy = 'mode=Optimized,blackout=False,smooth=False,tickInitialized=False,tickDisabled=False,flatHooks=False,fastTicks=False,tickWarnings=0'
            } elseif ($legacyVariant.Disabled -contains 'EnableFlatTickDispatch') {
                $expectedLegacy = $expectedLegacy.Replace('flatHooks=True', 'flatHooks=False')
            }
        }
        if ($MatchedConditions -and $result.legacy -cne $expectedLegacy) { throw "Unexpected legacy mode: $($result.legacy)" }
        $loadedMods = @($lines | Where-Object { $_ -match '^- .+ \(v[^)]+\)$' })
        $allowed = @('- Harmony (v2.4.1)', '- T3MP Test Driver (dev only) (v1.0.0)')
        if ($arm -eq 'O') { $allowed += "- $($legacyManifest.Name) (v$($legacyManifest.Version))" }
        elseif ($arm -ne 'V') { $allowed += "- $($manifest.Name) (v$($manifest.Version))" }
        if (($loadedMods.Count -ne $allowed.Count) -or ($loadedMods | Where-Object { $_ -cnotin $allowed })) { throw "Unexpected loaded mods: $($loadedMods -join ', ')" }
        if ($lines -match 'First uncaught exception|^Rethrow as Exception:') { throw "Game exception in $arm" }
        if ($arm -eq 'P' -and $ExpectedExperiment -in @('HarvestCandidateTree','HarvestIndex')) {
            if ($result.harvestTree -notmatch '^calls=([1-9]\d*),validated=(\d+),') {throw 'Harvest tree was not exercised'}
            if ($HarvestTreeValidation -and $Matches[1] -ne $Matches[2]) {throw 'Incomplete harvest validation'}
            if (-not $HarvestTreeValidation -and $Matches[2] -ne '0') {throw 'Validation contaminated timing'}
        }
        if ($movementExpected -eq 'True') {
            if ($result.movementSubsteps -notmatch '^calls=(\d+),fallbacks=(\d+),coarse=(True|False)$') {throw 'Movement summary missing'}
            $coarseExpected = [string](-not ($MovementExact -or $arm -eq 'M'))
            if ($Matches[3] -ne $coarseExpected) {throw "Movement coarse state differs: $($result.movementSubsteps)"}
            if ($Matches[2] -ne '0') {throw "Movement fallbacks: $($result.movementSubsteps)"}
            # Validation keeps the native body (the driver's hooks are the later generation), so the replacement never runs.
            if ($MovementValidation) { if ($Matches[1] -ne '0') {throw "Movement replacement ran during validation: $($result.movementSubsteps)"} }
            elseif ($Matches[1] -eq '0') {throw "Movement sub-steps not exercised: $($result.movementSubsteps)"}
        } elseif ($movementExpected -eq 'False') {
            if ($result.movementSubsteps -ne 'disabled') { throw "Unexpected movement summary: $($result.movementSubsteps)" }
        } elseif ($result.movementSubsteps -ne 'absent') { throw "Unexpected movement summary: $($result.movementSubsteps)" }
        # Driver-side replay audit (T3MPTestDriver.MovementValidation, docs/DIAGNOSTICS.md): present only in validation runs.
        if ($MovementValidation) {
            if ($result.movementValidation -notmatch '^validated=(\d+),mismatches=(\d+),skipped=(\d+),unsupported=(\d+),coarse=(True|False),cornerCountDiffs=(\d+),positionDiffs=(\d+),maxPositionDelta=([^,]+),indexDiffs=(\d+),arrivalDiffs=(\d+),integrityFailures=(\d+)$') {throw "Movement validation summary missing: $($result.movementValidation)"}
            $coarseExpected = [string](-not $MovementExact)
            if ($Matches[5] -ne $coarseExpected) {throw "Movement validation coarse state differs: $($result.movementValidation)"}
            # skipped = replay errors (never tolerated); unsupported = calls with a speed provider other than the reviewed walker one (reported only).
            if ($Matches[1] -eq '0' -or $Matches[3] -ne '0') {throw "Movement validation incomplete: $($result.movementValidation)"}
            # Coarse stepping is not bit-exact by design; its audit records decision differences instead of failing.
            if ($Matches[2] -ne '0' -and $coarseExpected -ne 'True') {throw "Movement validation mismatches: $($result.movementValidation)"}
            # Transform restoration failures are never tolerated.
            if ($Matches[11] -ne '0') {throw "Movement transform integrity failures: $($result.movementValidation)"}
        } elseif ($result.movementValidation -ne 'disabled') { throw "Movement validation ran in a performance arm: $($result.movementValidation)" }
        if($RuntimeAttribution -and ($result.patches -split ',') -cnotcontains 'VisualPreparationInstalled=False') {throw 'Visual preparation changed attribution load state'}
        if($LegacyVisualPreparationBaseline -and ($result.patches -split ',') -cnotcontains 'VisualPreparationInstalled=False') {throw 'Legacy visual preparation remained enabled'}
        if ((Get-FileHash -LiteralPath $candidate).Hash -ne $runDllHash) { throw 'DLL changed during run' }
        $loadedHash = if ($arm -eq 'O') { (Get-FileHash -LiteralPath (Join-Path $LegacyModDir 'Code.dll')).Hash } elseif ($arm -eq 'V') { 'none' } else { $runDllHash }
        if ($arm -eq 'O' -and $loadedHash -ne $legacyHash) { throw 'Legacy DLL changed' }
        $expectedMvid = if ($arm -eq 'O') { Get-ReleaseMvid (Join-Path $LegacyModDir 'Code.dll') } elseif ($arm -eq 'V') { 'none' } else { Get-ReleaseMvid $candidate }
        if ($result.productMvid -ne $expectedMvid -or $result.driverMvid -ne (Get-ReleaseMvid $builtDriver)) { throw 'Loaded module identity does not match hashed DLL' }
        # Per-entity native fallbacks are an active optimizer's normal safety path;
        # keep their counters in the log, but reject disabled/failed optimizers.
        if ($legacyArm -and ($lines -match '\[T3MP\].*(disabled:|failed(?![=: ]+False\b)|fallback(?!Entities=\d+\b))')) { throw 'Legacy fallback or initialization failure; inspect log before accepting comparison' }
        $result | Add-Member -NotePropertyName DllHash -NotePropertyValue $loadedHash
        $result | Add-Member -NotePropertyName Arm -NotePropertyValue ([string]$arm)
        $result | Add-Member -NotePropertyName Log -NotePropertyValue $logs[0].FullName
        $result | Add-Member -NotePropertyName GameVersion -NotePropertyValue (@($lines | Where-Object { $_ -match '^Starting game version ' }) -join '')
        if ($results.Count -and ($result.display -cne $results[0].display -or $result.timeScale -ne $results[0].timeScale -or $result.GameVersion -cne $results[0].GameVersion -or $result.originTick -ne $results[0].originTick -or $result.focused -ne $results[0].focused)) { throw 'Display, speed, game version, focus or tick origin differed between arms' }
        $results += $result
        $results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'runs.json') -Encoding utf8
        if($HarvestTreeValidation) {Write-Host ("Validation only: "+$result.harvestTree)}
        else {Write-Host ("arm {0}: {1:F3} ticks/s, {2:F2} FPS, p99 {3:F1} ms" -f $arm, $result.ticksPerSecond, $result.averageFps, $result.p99FrameMs)}
    }
} catch {
    $failure = $_.ToString()
    throw
} finally {
    # Archives stay under this run's workspace directory; no recursive deletion.
    Stop-OwnedProbe
    if ($ComparisonDll) { Copy-Item -LiteralPath $originalDll -Destination $candidate -Force }
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
    [PSCustomObject]@{ Order=$Order; MatchedConditions=[bool]$MatchedConditions; RuntimeAttribution=[bool]$RuntimeAttribution; TickPortValidation=[bool]$TickPortValidation; HarvestTreeValidation=[bool]$HarvestTreeValidation; MovementValidation=[bool]$MovementValidation; MovementExact=[bool]$MovementExact; LegacyHash=$legacyHash; LegacyVariant=$legacyVariant; LegacyVisualPreparationBaseline=[bool]$LegacyVisualPreparationBaseline; SourceSave=$SourceSave; SourceHash=$sourceHash; CandidateHash=$candidateHash; ComparisonHash=$comparisonHash; ExpectedExperiment=$ExpectedExperiment; DriverHash=$driverHash; WarmupTicks=$WarmupTicks; MeasuredTicks=$MeasuredTicks; Failure=$failure; Runs=$results } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    if ((Get-FileHash -LiteralPath $SourceSave).Hash -ne $sourceHash -or (Get-FileHash -LiteralPath $candidate).Hash -ne $candidateHash) { throw 'Original save or candidate changed during measurement' }
}
Write-Host "Results and restoration archive: $output"
