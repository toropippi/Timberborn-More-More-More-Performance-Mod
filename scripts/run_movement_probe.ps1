# Development-only movement attribution on the currently installed T3MP DLL.
# No DLL swap: the installed candidate stays as is; the built test driver is
# deployed for the run and archived afterwards. Not a benchmark.
[CmdletBinding()]
param(
    [string]$SourceSave=(Join-Path $env:USERPROFILE 'Documents\Timberborn\Saves\n10c\n10c.timber'),
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\Timberborn',
    [ValidateRange(65,600)][int]$SecondsAfterLoad=130,
    [double]$Speed=50,
    [string[]]$ExtraGameArgs=@('-t3mpTestMovementProbe'),
    [string]$Tag='movement-probe'
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$mods=(Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods')
$code=Join-Path $mods 'T3MP/Code.dll'
$driver=Join-Path $mods 'T3MPTestDriver'
$builtDriver=Join-Path $repo 'src/T3MPTestDriver/bin/Release/netstandard2.1/T3MPTestDriver.dll'
$exe=Join-Path $GameRoot 'Timberborn.exe'
$stamp=Get-Date -Format yyyyMMdd-HHmmss
$settlement='t3mp-probe-'+$stamp
$stage=Join-Path (Split-Path -Parent (Split-Path -Parent $SourceSave)) $settlement
$output=Join-Path $repo ('testlogs/'+$Tag+'-'+$stamp)
$prefs='HKCU:\Software\Mechanistry\Timberborn'
$keyPatterns=@{
    Local='^ModEnabled\.Local\.T3MP\.T3MP_h\d+$'
    Workshop='^ModEnabled\.Steam Workshop\.3756656421\.T3MP_h\d+$'
    Harmony='^ModEnabled\.Steam Workshop\.3284904751\.Harmony_h\d+$'
}
if(Get-Process Timberborn -ErrorAction SilentlyContinue) {throw 'Existing game'}
foreach($path in @($driver,$stage,$output)) {if(Test-Path -LiteralPath $path) {throw "Stage exists: $path"}}
foreach($path in @($SourceSave,$code,$builtDriver,$exe)) {if(-not (Test-Path -LiteralPath $path -PathType Leaf)) {throw "Missing: $path"}}
$registry=Get-Item -LiteralPath $prefs
$keys=@{}
foreach($name in $keyPatterns.Keys) {
    $matchesForKey=@($registry.GetValueNames() | Where-Object {$_ -match $keyPatterns[$name]})
    if($matchesForKey.Count -ne 1) {throw "Expected one existing PlayerPrefs key for $name"}
    $keys[$name]=$matchesForKey[0]
}
$original=@{}
foreach($key in $keys.Values) {
    if($registry.GetValueKind($key) -ne [Microsoft.Win32.RegistryValueKind]::DWord) {throw "Unexpected preference type: $key"}
    $original[$key]=$registry.GetValue($key)
}
$sourceHash=(Get-FileHash -LiteralPath $SourceSave).Hash
$codeHash=(Get-FileHash -LiteralPath $code).Hash
$driverHash=(Get-FileHash -LiteralPath $builtDriver).Hash
New-Item -ItemType Directory -Path $output | Out-Null
$original | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'original-mod-prefs.json')
$failure=$null
try {
    New-Item -ItemType Directory -Path $driver,$stage | Out-Null
    Copy-Item -LiteralPath $builtDriver -Destination $driver
    Copy-Item -LiteralPath (Join-Path $repo 'testmod/manifest.json') -Destination $driver
    Copy-Item -LiteralPath $SourceSave -Destination (Join-Path $stage 'n10c.timber')
    Set-ItemProperty -LiteralPath $prefs -Name $keys.Local -Value 1
    Set-ItemProperty -LiteralPath $prefs -Name $keys.Workshop -Value 0
    Set-ItemProperty -LiteralPath $prefs -Name $keys.Harmony -Value 1
    & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') -TimberbornExe $exe -SettlementName $settlement -SaveName n10c -TestSpeed $Speed -SkipModManager -SecondsAfterLoad $SecondsAfterLoad -ExtraGameArgs $ExtraGameArgs -OutputDir $output | Out-Host
    $logs=@(Get-ChildItem -LiteralPath $output -Filter 'autoload-*.log' | Where-Object Name -NotLike 'autoload-previous-*')
    if($logs.Count -ne 1) {throw 'Ambiguous probe log'}
    $lines=Get-Content -LiteralPath $logs[0].FullName
    if($lines -match 'First uncaught exception|^Rethrow as Exception:') {throw 'Exception in probe run'}
    $manifest=Get-Content -LiteralPath (Join-Path $mods 'T3MP/manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $allowed=@('- Harmony (v2.4.1)','- T3MP Test Driver (dev only) (v1.0.0)',"- $($manifest.Name) (v$($manifest.Version))")
    $loaded=@($lines | Where-Object {$_ -match '^- .+ \(v[^)]+\)$'})
    if($loaded.Count -ne $allowed.Count -or @($loaded | Where-Object {$_ -cnotin $allowed}).Count) {throw 'Unexpected loaded mods'}
    $lines | Where-Object {$_ -like '[[]T3MPMOVE]*' -or $_ -like '[[]T3MPTEST] Simulation rate*' -or $_ -like '[[]T3MPTEST] profile*' -or $_ -like '[[]T3MP]*'} | Set-Content -LiteralPath (Join-Path $output 'probe.log')
} catch {$failure=$_.ToString(); throw}
finally {
    $cleanup=[Collections.Generic.List[string]]::new()
    $remaining=@()
    try {$remaining=@(Get-CimInstance Win32_Process -Filter "Name='Timberborn.exe'")} catch {$cleanup.Add($_.ToString())}
    foreach($process in $remaining) {
        if($process.ExecutablePath -ne $exe -or $process.CommandLine -notlike "*$settlement*") {$cleanup.Add('Unrelated game; preserve recovery files'); continue}
        try {
            Stop-Process -Id $process.ProcessId -Force
            Wait-Process -Id $process.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
        } catch {if(Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) {$cleanup.Add($_.ToString())}}
    }
    $teardownDeadline=[DateTime]::UtcNow.AddSeconds(5)
    while((Get-Process Timberborn -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $teardownDeadline) {Start-Sleep -Milliseconds 250}
    if(Get-Process Timberborn -ErrorAction SilentlyContinue) {$cleanup.Add('Game remains; do not overwrite live settings')}
    else {
        foreach($key in $original.Keys) {
            try {Set-ItemProperty -LiteralPath $prefs -Name $key -Value $original[$key]} catch {$cleanup.Add($_.ToString())}
        }
        foreach($entry in @(@($driver,'driver-archive'),@($stage,'save-archive'))) {
            try {
                if(Test-Path -LiteralPath $entry[0]) {
                    if((Resolve-Path -LiteralPath $entry[0]).Path -ne [IO.Path]::GetFullPath($entry[0])) {throw 'Unexpected archive path'}
                    Move-Item -LiteralPath $entry[0] -Destination (Join-Path $output $entry[1])
                }
            } catch {$cleanup.Add($_.ToString())}
        }
    }
    try {if((Get-FileHash -LiteralPath $code).Hash -ne $codeHash) {throw 'Installed DLL changed during probe'}} catch {$cleanup.Add($_.ToString())}
    try {if((Get-FileHash -LiteralPath $SourceSave).Hash -ne $sourceHash) {throw 'Source changed'}} catch {$cleanup.Add($_.ToString())}
    [ordered]@{Failure=$failure;CleanupFailures=@($cleanup);CodeHash=$codeHash;DriverHash=$driverHash;SourceHash=$sourceHash;SourceSave=$SourceSave;GameRoot=$GameRoot;Speed=$Speed;SecondsAfterLoad=$SecondsAfterLoad;ExtraGameArgs=@($ExtraGameArgs);Restored=($cleanup.Count -eq 0)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json')
    if($cleanup.Count) {throw ($cleanup -join '; ')}
    Write-Output "Probe archive: $output"
}
