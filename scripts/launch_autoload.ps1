[CmdletBinding()]
param(
    [string] $TimberbornExe = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn.exe',
    [string] $SettlementName = 'n10c',
    [string] $SaveName = 'n10c',
    [switch] $LatestAutosave,
    [string] $SavesRoot = (Join-Path $env:USERPROFILE 'Documents\Timberborn\ExperimentalSaves')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $TimberbornExe)) {
    throw "Timberborn.exe was not found: $TimberbornExe"
}

if ($LatestAutosave) {
    $settlementPath = Join-Path $SavesRoot $SettlementName
    if (-not (Test-Path -LiteralPath $settlementPath)) {
        throw "Settlement save folder was not found: $settlementPath"
    }

    $latest = Get-ChildItem -LiteralPath $settlementPath -Filter '*.autosave.timber' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $latest) {
        throw "No autosave was found in: $settlementPath"
    }

    $SaveName = [System.IO.Path]::GetFileNameWithoutExtension($latest.Name)
}

function Quote-ProcessArgument([string] $Value) {
    '"' + ($Value -replace '"', '\"') + '"'
}

$arguments = @(
    '-settlementName',
    (Quote-ProcessArgument $SettlementName),
    '-saveName',
    (Quote-ProcessArgument $SaveName)
) -join ' '

Write-Host "Launching Timberborn:"
Write-Host "  exe:        $TimberbornExe"
Write-Host "  settlement: $SettlementName"
Write-Host "  save:       $SaveName"

Start-Process -FilePath $TimberbornExe -ArgumentList $arguments
