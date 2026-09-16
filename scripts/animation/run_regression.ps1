[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TimberbornExe,
    [Parameter(Mandatory)][string] $SourceSave,
    [Parameter(Mandatory)][string] $ModDll,
    [Parameter(Mandatory)][string] $DriverDll,
    [string] $Label = 'candidate',
    [int] $TimeoutSeconds = 360
)
$ErrorActionPreference = 'Stop'
if (Get-Process Timberborn -ErrorAction SilentlyContinue) {
    throw 'A Timberborn process is already running; leave it untouched.'
}
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$runName = 't3mp-animation-' + [guid]::NewGuid().ToString('N')
$output = Join-Path $repo ('testlogs\' + $runName)
$saves = Join-Path $env:USERPROFILE 'Documents\Timberborn\Saves'
$staged = Join-Path $saves $runName
$mods = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
$targets = @((Join-Path $mods 'T3MP\Code.dll'), (Join-Path $mods 'T3MPTestDriver\Code.dll'))
$inputs = @((Resolve-Path -LiteralPath $ModDll).Path, (Resolve-Path -LiteralPath $DriverDll).Path)
$game = (Resolve-Path -LiteralPath $TimberbornExe).Path
$source = (Resolve-Path -LiteralPath $SourceSave).Path
$log = Join-Path $output ($Label + '.log')
$process = $null
$replaced = @()
New-Item -ItemType Directory -Path $output | Out-Null
New-Item -ItemType Directory -Path $staged | Out-Null
try {
    Copy-Item -LiteralPath $source -Destination (Join-Path $staged 'test.timber')
    for ($i = 0; $i -lt $targets.Count; $i++) {
        # Existing installations are required, including their manifests.
        if (-not (Test-Path -LiteralPath $targets[$i])) { throw "Missing installed DLL: $($targets[$i])" }
        Copy-Item -LiteralPath $targets[$i] -Destination (Join-Path $output "original-$i.dll")
        $replaced += $i
        Copy-Item -LiteralPath $inputs[$i] -Destination $targets[$i] -Force
    }
    $arguments = @('-skipModManager', '-settlementName', $runName, '-saveName', 'test',
        '-t3mpTestAnimationContinuity', '-logFile', ('"' + $log + '"'))
    $process = Start-Process -FilePath $game -WorkingDirectory (Split-Path -Parent $game) `
        -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Seconds 2
        if (Test-Path -LiteralPath $log) {
            $content = Get-Content -LiteralPath $log -Raw
            if ($content -match '\[T3MPANIM\] ERROR') { throw "Diagnostic failed: $log" }
            if ($content -match '\[T3MPANIM\] COMPLETE') {
                $content -split "`n" | Where-Object { $_ -match '\[T3MPANIM\]' } | Write-Output
                Write-Output "Log: $log"
                if ($content -match '\b[A-Za-z0-9_.]*Exception\b|Failed to patch|Failed to load asset bundle') {
                    throw "Scenario completed with errors; inspect $log"
                }
                return
            }
        }
        $process.Refresh()
    }
    throw "Animation scenario did not complete: $log"
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    foreach ($i in $replaced) {
        Copy-Item -LiteralPath (Join-Path $output "original-$i.dll") -Destination $targets[$i] -Force
    }
    $resolvedStage = (Resolve-Path -LiteralPath $staged).Path
    $resolvedSaves = (Resolve-Path -LiteralPath $saves).Path.TrimEnd('\') + '\'
    if (-not $resolvedStage.StartsWith($resolvedSaves, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedStage) -ne $runName) { throw 'Unsafe staging cleanup path' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
