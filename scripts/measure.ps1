# One switch for every measurement (docs/DIAGNOSTICS.md). Relaunches itself
# under PowerShell 7, runs the product purity gate, then the recorded procedure
# for the requested kind on the requested save. Never deploys, never publishes.
#
#   measure.ps1 -Kind ab -Candidate build\Code.dll -Experiment YielderReachabilitySkip [-Save n10c2]
#       installed DLL (B) against a candidate (P), BPPB, matched conditions
#   measure.ps1 -Kind nomod                     no mod (V) against installed (B), VBBV
#   measure.ps1 -Kind off -Arm D                installed against one feature off (D/S/M/E/W/F)
#   measure.ps1 -Kind validate [-Exact] [-Candidate dll]
#       movement replay audit (driver MovementValidation); not a performance run
#   measure.ps1 -Kind tickprofile|hotcounts|subsystem|movement [-Save n10c2]
#       driver profilers on the installed DLL (run_movement_probe.ps1)
#
# Every kind requires an idle machine: focus changes invalidate arms. Results
# land under testlogs/ and are recorded by hand in docs/evidence.json.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('ab', 'nomod', 'off', 'validate', 'tickprofile', 'hotcounts', 'subsystem', 'movement')][string]$Kind,
    [string]$Candidate = '',
    [string]$Experiment = 'ProductVariant',
    [ValidatePattern('^[DSMEWF]$')][string]$Arm = 'D',
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Save = 'n10c',
    [string]$Order = '',
    [ValidateRange(1, 10000)][double]$Speed = 50,
    [ValidateRange(1, 1000000)][int]$WarmupTicks = 256,
    [ValidateRange(1, 1000000)][int]$MeasuredTicks = 2048,
    [ValidateRange(65, 600)][int]$SecondsAfterLoad = 130,
    [switch]$Exact
)
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    # run_fixed_tick_ab.ps1 needs PEReader and UTF-8 defaults; Windows PowerShell 5.1 has neither.
    $pwsh = $null
    $command = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($command) { $pwsh = $command.Source }
    if (-not $pwsh) { $pwsh = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe' }
    if (-not (Test-Path -LiteralPath $pwsh -PathType Leaf)) { throw 'PowerShell 7 (pwsh) is required' }
    $forward = @()
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Value -is [switch]) { if ($entry.Value.IsPresent) { $forward += ('-' + $entry.Key) } }
        else { $forward += ('-' + $entry.Key); $forward += [string]$entry.Value }
    }
    & $pwsh -NoProfile -File $PSCommandPath @forward
    exit $LASTEXITCODE
}

# The gate covers the sources, the installed DLL and the candidate that will run
# (identity checks in the runners prove which bytes ran, not that they are clean).
# A historical experimental binary that fails here is measured through
# run_fixed_tick_ab.ps1 directly, with the reason recorded in docs/evidence.json.
$gate = Join-Path $PSScriptRoot 'check_product_purity.ps1'
$installed = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods\T3MP\Code.dll'
if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Installed product not found: $installed" }
& $gate -Dll $installed | Out-Host
if ($Candidate) { $Candidate = (Resolve-Path -LiteralPath $Candidate).Path; & $gate -Dll $Candidate | Out-Host }
if (Get-Process Timberborn -ErrorAction SilentlyContinue) { throw 'A game is running' }
$sourceSave = Join-Path $env:USERPROFILE ('Documents\Timberborn\Saves\n10c\' + $Save + '.timber')
if (-not (Test-Path -LiteralPath $sourceSave -PathType Leaf)) { throw "Save not found: $sourceSave" }
$ab = Join-Path $PSScriptRoot 'run_fixed_tick_ab.ps1'
$probe = Join-Path $PSScriptRoot 'run_movement_probe.ps1'
$common = @{ MatchedConditions = $true; Speed = $Speed; WarmupTicks = $WarmupTicks; MeasuredTicks = $MeasuredTicks; SourceSave = $sourceSave }

Write-Output ("measure: kind=$Kind save=$Save speed=$Speed ticks=$WarmupTicks+$MeasuredTicks" + $(if ($Candidate) { " candidate=$Candidate experiment=$Experiment" } else { '' }))
switch ($Kind) {
    'ab' {
        if (-not $Candidate) { throw 'ab requires -Candidate <Code.dll>' }
        $order = if ($Order) { $Order } else { 'BPPB' }
        & $ab -Order $order -ComparisonDll $Candidate -ExpectedExperiment $Experiment @common
    }
    'nomod' {
        $order = if ($Order) { $Order } else { 'VBBV' }
        & $ab -Order $order @common
    }
    'off' {
        $order = if ($Order) { $Order } else { 'B' + $Arm + $Arm + 'B' }
        if ($order -notmatch '^[BDSMEWF]+$') { throw "off accepts B/D/S/M/E/W/F arms only: $order" }
        # E/W/F need the attribution mode (visual preparation off in every arm); D/S/M do not.
        if ($order -match '[EWF]') { & $ab -Order $order -RuntimeAttribution @common } else { & $ab -Order $order @common }
    }
    'validate' {
        $order = if ($Candidate) { 'P' } else { 'B' }
        $extra = @{ MovementValidation = $true }
        if ($Exact) { $extra.MovementExact = $true }
        if ($Candidate) { $extra.ComparisonDll = $Candidate; $extra.ExpectedExperiment = $Experiment }
        & $ab -Order $order @common @extra
    }
    default {
        $flag = @{ tickprofile = '-t3mpTestTickProfile'; hotcounts = '-t3mpTestHotCounts'; subsystem = '-t3mpTestSubsystemProfile'; movement = '-t3mpTestMovementProbe' }[$Kind]
        $tag = @{ tickprofile = 'tick-profile'; hotcounts = 'hot-counts'; subsystem = 'subsystem-profile'; movement = 'movement-probe' }[$Kind] + '-' + $Save
        $extraArgs = @($flag)
        if ($Exact) { $extraArgs += '-t3mpTestNoMovementCoarse' }
        & $probe -SourceSave $sourceSave -Speed $Speed -SecondsAfterLoad $SecondsAfterLoad -Tag $tag -ExtraGameArgs $extraArgs
    }
}
