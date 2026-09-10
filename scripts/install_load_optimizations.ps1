[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ModDll,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string] $ExpectedSha256,
    [Parameter(Mandatory)][string[]] $TargetDlls
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name Timberborn -ErrorAction SilentlyContinue) {
    throw 'Close Timberborn before replacing the installed mod.'
}
$source = (Resolve-Path -LiteralPath $ModDll).Path
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $ExpectedSha256) {
    throw 'The supplied DLL does not match the validated build.'
}
$targets = @($TargetDlls | ForEach-Object { (Resolve-Path -LiteralPath $_).Path } | Select-Object -Unique)
if (!$targets.Count -or $targets -contains $source) { throw 'Invalid installation destinations.' }
foreach ($target in $targets) {
    if ([IO.Path]::GetFileName($target) -ne 'Code.dll' -or !(Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Not an existing mod DLL: $target"
    }
}
$backupDirectory = Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot/../testlogs")) ('install-' + [Guid]::NewGuid().ToString('N'))
New-Item -Path $backupDirectory -ItemType Directory | Out-Null
$entries = @()
for ($index = 0; $index -lt $targets.Count; $index++) {
    $target = $targets[$index]
    $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $backup = Join-Path $backupDirectory "$index-Code.dll"
    Copy-Item -LiteralPath $target -Destination $backup
    if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $hash) { throw 'Backup verification failed.' }
    $entries += [pscustomobject]@{ Target=$target; Backup=$backup; OriginalSha256=$hash }
}
$record = [ordered]@{ Source=$source; InstalledSha256=$ExpectedSha256; Status='prepared'; Entries=$entries }
$manifest = Join-Path $backupDirectory 'installation.json'
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
$attempted = @()
try {
    foreach ($entry in $entries) {
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.OriginalSha256) {
            throw 'An installed DLL changed after it was backed up.'
        }
        $attempted += $entry
        Copy-Item -LiteralPath $source -Destination $entry.Target -Force
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Installation verification failed.' }
    }
    $record.Status = 'installed'
    $record.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
    Write-Output "Installed and verified $($entries.Count) DLLs. Backup manifest: $manifest"
}
catch {
    foreach ($entry in $attempted) {
        Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.OriginalSha256) { throw "Restoration failed: $($entry.Target)" }
    }
    $record.Status = 'restored-after-failure'
    $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding utf8
    throw
}
