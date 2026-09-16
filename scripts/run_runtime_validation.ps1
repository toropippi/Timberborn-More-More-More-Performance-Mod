param(
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][ValidateSet('EventDelegates','WaterUpload','RoadCacheSynthetic','BoolSynthetic')][string]$Feature,
    [string]$CandidateCode,
    [switch]$ExpectFailure,
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $repo ('testlogs/runtime-validation-' + $Feature + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw "Output already exists: $OutputDir" }
$mods = (Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods')
$driver = Join-Path $mods 'T3MPTestDriver'
$code = Join-Path $mods 'T3MP\Code.dll'
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$exe = Join-Path $GameRoot 'Timberborn.exe'
$playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn\Player.log'
$flag = '-t3mpTest' + $Feature
$runLog = Join-Path $OutputDir 'Player.log'
$marker = switch ($Feature) { 'WaterUpload' { 'Water upload validation' }; 'RoadCacheSynthetic' { 'Road cache synthetic validation' }; 'BoolSynthetic' { 'Bool synthetic validation' }; default { 'Event delegate validation' } }
if (Get-Process Timberborn -ErrorAction SilentlyContinue) { throw 'A game is already running' }
if (Test-Path -LiteralPath $driver) { throw "Test driver already exists: $driver" }
if (-not (Test-Path -LiteralPath $exe)) { throw "Missing game: $exe" }
$originalHash = (Get-FileHash -LiteralPath $code).Hash
New-Item -ItemType Directory -Path $OutputDir | Out-Null
Copy-Item -LiteralPath $code -Destination (Join-Path $OutputDir 'original-Code.dll')
if (Test-Path -LiteralPath $playerLog) { Copy-Item -LiteralPath $playerLog -Destination (Join-Path $OutputDir 'previous.log') }
$gameProcess = $null
try {
    New-Item -ItemType Directory -Path $driver | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo 'src\T3MPTestDriver\bin\Release\netstandard2.1\T3MPTestDriver.dll') -Destination $driver
    Copy-Item -LiteralPath (Join-Path $repo 'testmod\manifest.json') -Destination $driver
    if ($CandidateCode) { Copy-Item -LiteralPath $CandidateCode -Destination $code -Force }
    $testedHash = (Get-FileHash -LiteralPath $code).Hash
    $gameProcess = Start-Process -FilePath $exe -WorkingDirectory $GameRoot -ArgumentList @('-skipModManager', $flag, '-logFile', ('"' + $runLog + '"')) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    $text = ''
    do {
        Start-Sleep -Seconds 2
        $gameProcess.Refresh()
        if (Test-Path -LiteralPath $runLog) { $text = Get-Content -LiteralPath $runLog -Raw }
        if ($text -match ([regex]::Escape($marker) + ' (PASS|FAIL)')) { break }
        if ($gameProcess.HasExited) { throw 'Game exited before the validation result' }
    } while ([DateTime]::UtcNow -lt $deadline)
    $status = if ($text -match ([regex]::Escape($marker) + ' PASS')) { 'PASS' } elseif ($text -match ([regex]::Escape($marker) + ' FAIL')) { 'FAIL' } else { 'MISSING' }
    $version = [regex]::Match($text, 'Starting game version[^\r\n]+').Value
    [ordered]@{ Game = $version; Feature = $Feature; CodeHash = $testedHash; Result = $status; ExpectedFailure = [bool]$ExpectFailure } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'result.json')
    if ($ExpectFailure) {
        if ($status -ne 'FAIL') { throw "Expected reproduced failure; got $status" }
    } elseif ($status -ne 'PASS') { throw "Validation $status" }
    Write-Output "$version $Feature $status; $OutputDir"
} finally {
    if ($gameProcess) {
        $gameProcess.Refresh()
        if (-not $gameProcess.HasExited) {
            $owned = Get-CimInstance Win32_Process -Filter "ProcessId=$($gameProcess.Id)"
            if ($owned.ExecutablePath -ne $exe -or $owned.CommandLine -notlike "*$flag*") { throw 'Refusing to stop an unowned game' }
            Stop-Process -Id $gameProcess.Id
            $gameProcess.WaitForExit()
        }
    }
    Copy-Item -LiteralPath (Join-Path $OutputDir 'original-Code.dll') -Destination $code -Force
    if ((Get-FileHash -LiteralPath $code).Hash -ne $originalHash) { throw 'Original DLL restoration failed' }
    if (Test-Path -LiteralPath $driver) {
        if ((Resolve-Path -LiteralPath $driver).Path -ne [IO.Path]::GetFullPath((Join-Path $mods 'T3MPTestDriver'))) { throw 'Unexpected driver path' }
        Move-Item -LiteralPath $driver -Destination (Join-Path $OutputDir 'driver-archive')
    }
    Write-Output "Restored original DLL: $originalHash"
}
