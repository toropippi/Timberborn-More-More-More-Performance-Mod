[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ModsPath = (Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'),
    [string] $ModFolderName = 'T3MP'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $repoRoot.Path 'src\T3MP\T3MP.csproj'
$modSourcePath = Join-Path $repoRoot.Path 'mod'
$targetPath = Join-Path $ModsPath $ModFolderName

if (-not (Test-Path -LiteralPath $ModsPath)) {
    throw "Timberborn Mods folder was not found: $ModsPath"
}

dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$builtDll = Join-Path (Split-Path -Parent $projectPath) "bin\$Configuration\netstandard2.1\Code.dll"
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "Built DLL was not found: $builtDll"
}

$resolvedModsPath = (Resolve-Path -LiteralPath $ModsPath).Path.TrimEnd('\')
if (Test-Path -LiteralPath $targetPath) {
    $resolvedTargetPath = (Resolve-Path -LiteralPath $targetPath).Path
    if (-not $resolvedTargetPath.StartsWith($resolvedModsPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean target outside Mods folder: $resolvedTargetPath"
    }
    Remove-Item -LiteralPath $resolvedTargetPath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
Copy-Item -Path (Join-Path $modSourcePath '*') -Destination $targetPath -Recurse -Force
Copy-Item -LiteralPath $builtDll -Destination (Join-Path $targetPath 'Code.dll') -Force

Write-Host "Deployed to: $targetPath"

