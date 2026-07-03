[CmdletBinding()]
param(
    [string] $ModsPath = (Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'),
    [string] $BackupRoot = (Join-Path $PSScriptRoot '..\backups')
)

$ErrorActionPreference = 'Stop'

$resolvedModsPath = Resolve-Path -LiteralPath $ModsPath -ErrorAction SilentlyContinue
if (-not $resolvedModsPath) {
    throw "Timberborn Mods folder was not found: $ModsPath"
}

$resolvedBackupRoot = New-Item -ItemType Directory -Force -Path $BackupRoot
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$items = Get-ChildItem -LiteralPath $resolvedModsPath.Path -Force

if ($items.Count -eq 0) {
    $markerPath = Join-Path $resolvedBackupRoot.FullName "Timberborn_Mods_$timestamp.empty.txt"
    Set-Content -LiteralPath $markerPath -Value "Mods folder was empty at $timestamp." -Encoding UTF8
    Write-Host "Mods folder is empty. Wrote marker: $markerPath"
    exit 0
}

$zipPath = Join-Path $resolvedBackupRoot.FullName "Timberborn_Mods_$timestamp.zip"
Compress-Archive -Path (Join-Path $resolvedModsPath.Path '*') -DestinationPath $zipPath -Force
Write-Host "Backup created: $zipPath"

