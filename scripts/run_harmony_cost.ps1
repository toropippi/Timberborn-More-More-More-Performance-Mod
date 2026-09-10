[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TimberbornExe,
    [Parameter(Mandatory)][string] $SourceSave,
    [Parameter(Mandatory)][string] $ModDll,
    [Parameter(Mandatory)][string] $DriverDll,
    [Parameter(Mandatory)][string] $SaveRoot,
    [string] $WorkshopRoot = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\1062090',
    [int] $TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
if (Get-Process Timberborn -ErrorAction SilentlyContinue) {
    throw 'A Timberborn process is already running; leave it untouched.'
}
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$runName = 't3mp-harmony-' + [guid]::NewGuid().ToString('N')
$output = Join-Path $repo ('testlogs\' + $runName)
$saves = (Resolve-Path -LiteralPath $SaveRoot).Path
$stage = Join-Path $saves $runName
$mods = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
$driver = Join-Path $mods $runName
$targetDll = Join-Path $mods 'T3MP\Code.dll'
$game = (Resolve-Path -LiteralPath $TimberbornExe).Path
$source = (Resolve-Path -LiteralPath $SourceSave).Path
$modInput = (Resolve-Path -LiteralPath $ModDll).Path
$driverInput = (Resolve-Path -LiteralPath $DriverDll).Path
if (-not (Test-Path -LiteralPath $targetDll)) { throw "Missing installed T3MP DLL: $targetDll" }
$targetDlls = @($targetDll)
# Timberborn may prefer Workshop over the local mod with the same Id. Back up
# and temporarily replace every installed T3MP DLL, then verify the loaded MVID.
# Manifests and Workshop identity files are never changed.
if (Test-Path -LiteralPath $WorkshopRoot) {
    foreach ($manifest in Get-ChildItem -LiteralPath $WorkshopRoot -Filter manifest.json -Recurse) {
        if ((Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json).Id -eq 'T3MP') {
            $installed = Join-Path $manifest.DirectoryName 'Code.dll'
            if (-not (Test-Path -LiteralPath $installed)) { throw "Missing Workshop DLL: $installed" }
            $targetDlls += $installed
        }
    }
}
$expectedMvid = [System.Reflection.Assembly]::LoadFile($modInput).ManifestModule.ModuleVersionId.ToString()
foreach ($manifest in Get-ChildItem -LiteralPath $mods -Filter manifest.json -Recurse) {
    if ((Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json).Id -eq 'T3MPTestDriver') {
        throw 'An existing T3MPTestDriver is installed; do not load two copies.'
    }
}
$log = Join-Path $output 'game.log'
$process = $null
$replaced = @()
New-Item -ItemType Directory -Path $output | Out-Null
$sourceHash = (Get-FileHash -LiteralPath $source).Hash
try {
    New-Item -ItemType Directory -Path $stage, $driver | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'test.timber')
    for ($i = 0; $i -lt $targetDlls.Count; $i++) {
        $target = $targetDlls[$i]
        $backup = Join-Path $output "original-Code-$i.dll"
        $hash = (Get-FileHash -LiteralPath $target).Hash
        Copy-Item -LiteralPath $target -Destination $backup
        $replaced += [PSCustomObject]@{ Target = $target; Backup = $backup; Hash = $hash }
        Copy-Item -LiteralPath $modInput -Destination $target -Force
    }
    $replaced | Export-Clixml -LiteralPath (Join-Path $output 'restoration.xml')
    Copy-Item -LiteralPath $driverInput -Destination (Join-Path $driver 'Code.dll')
    Copy-Item -LiteralPath (Join-Path $repo 'testmod\manifest.json') -Destination $driver
    Write-Output "Output: $output"
    Write-Output "Game: $game"
    Write-Output "SourceSHA256: $sourceHash"
    Write-Output "ModSHA256: $((Get-FileHash -LiteralPath $modInput).Hash)"
    Write-Output "ExpectedModMvid: $expectedMvid"
    $arguments = @('-skipModManager', '-settlementName', $runName, '-saveName', 'test',
        '-t3mpTestHarmonyCost', '-t3mpTestExpectedModMvid', $expectedMvid, '-logFile', ('"' + $log + '"'))
    $process = Start-Process -FilePath $game -WorkingDirectory (Split-Path -Parent $game) `
        -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Seconds 2
        if (Test-Path -LiteralPath $log) {
            $content = Get-Content -LiteralPath $log -Raw
            if ($content -match '\[T3MPHARMONY\] ERROR') { throw "Diagnostic failed: $log" }
            if ($content -match '\[T3MPHARMONY\] COMPLETE') {
                $content -split "`n" | Where-Object { $_ -match '\[T3MPHARMONY\]|Starting game version' } | Write-Output
                if ($content -match 'First uncaught exception|Failed to patch|Failed to load asset bundle|Travel distance cache disabled') {
                    throw "Scenario completed with errors; inspect $log"
                }
                return
            }
        }
        $process.Refresh()
    }
    throw "Diagnostic did not complete: $log"
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    foreach ($original in $replaced) {
        Copy-Item -LiteralPath $original.Backup -Destination $original.Target -Force
        if ((Get-FileHash -LiteralPath $original.Target).Hash -ne $original.Hash) { throw 'Installed DLL restoration failed' }
    }
    foreach ($item in @(@($stage, $saves), @($driver, $mods))) {
        if (-not (Test-Path -LiteralPath $item[0])) { continue }
        $resolved = (Resolve-Path -LiteralPath $item[0]).Path
        $parent = (Resolve-Path -LiteralPath $item[1]).Path.TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -ne $runName) { throw 'Unsafe temporary-directory cleanup path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash) { throw 'Source save changed' }
    Write-Output 'Restored all installed DLLs; removed test driver and copied settlement; source save unchanged.'
}
