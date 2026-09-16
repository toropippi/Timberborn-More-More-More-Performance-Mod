[CmdletBinding()]
param(
    [ValidateSet('Harvest','Decisions','WaterObjects')][string]$SearchKind='Harvest',
    [string]$SourceSave=(Join-Path $env:USERPROFILE 'Documents\Timberborn\Saves\n10c\n10c.timber'),
    [string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\Timberborn',
    [ValidateRange(65,600)][int]$SecondsAfterLoad=100,
    [double]$Speed=99
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$mods=(Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods')
$code=Join-Path $mods 'T3MP/Code.dll'
$driver=Join-Path $mods 'T3MPTestDriver'
$candidate=Join-Path $repo 'src/T3MP/bin/Release/netstandard2.1/Code.dll'
$builtDriver=Join-Path $repo 'src/T3MPTestDriver/bin/Release/netstandard2.1/T3MPTestDriver.dll'
$exe=Join-Path $GameRoot 'Timberborn.exe'
$stamp=Get-Date -Format yyyyMMdd-HHmmss
$settlement='t3mp-search-'+$stamp
$profileFlag=switch($SearchKind) {'WaterObjects' {'-t3mpTestWaterObjectProfile'}; 'Decisions' {'-t3mpTestDecisionProfile'}; default {'-t3mpTestHarvestProfile'}}
$stage=Join-Path (Split-Path -Parent (Split-Path -Parent $SourceSave)) $settlement
$output=Join-Path $repo ('testlogs/search-profile-'+$stamp)
$prefs='HKCU:\Software\Mechanistry\Timberborn'
$keys=@('ModEnabled.Local.T3MP.T3MP_h1796111333','ModEnabled.Steam Workshop.3756656421.T3MP_h539883676','ModEnabled.Steam Workshop.3284904751.Harmony_h3757451374')
if(Get-Process Timberborn -ErrorAction SilentlyContinue) {throw 'Existing game'}
foreach($path in @($driver,$stage,$output)) {if(Test-Path -LiteralPath $path) {throw "Stage exists: $path"}}
foreach($path in @($SourceSave,$candidate,$code,$builtDriver,$exe)) {if(-not (Test-Path -LiteralPath $path -PathType Leaf)) {throw "Missing: $path"}}
foreach($path in @($driver,$stage)) {if([IO.Path]::GetPathRoot($path) -ne [IO.Path]::GetPathRoot($output)) {throw 'Archive must stay on the same drive'}}
$registry=Get-Item -LiteralPath $prefs
$original=@{}
foreach($key in $keys) {
    if($registry.GetValueNames() -notcontains $key -or $registry.GetValueKind($key) -ne [Microsoft.Win32.RegistryValueKind]::DWord) {throw 'Missing expected preference'}
    $original[$key]=$registry.GetValue($key)
}
$sourceHash=(Get-FileHash -LiteralPath $SourceSave).Hash
$originalHash=(Get-FileHash -LiteralPath $code).Hash
$candidateHash=(Get-FileHash -LiteralPath $candidate).Hash
$driverHash=(Get-FileHash -LiteralPath $builtDriver).Hash
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $code -Destination (Join-Path $output 'original-Code.dll')
$original | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'original-mod-prefs.json')
$failure=$null
try {
    New-Item -ItemType Directory -Path $driver,$stage | Out-Null
    Copy-Item -LiteralPath $builtDriver -Destination $driver
    Copy-Item -LiteralPath (Join-Path $repo 'testmod/manifest.json') -Destination $driver
    Copy-Item -LiteralPath $SourceSave -Destination (Join-Path $stage 'n10c.timber')
    Copy-Item -LiteralPath $candidate -Destination $code -Force
    Set-ItemProperty -LiteralPath $prefs -Name $keys[0] -Value 1
    Set-ItemProperty -LiteralPath $prefs -Name $keys[1] -Value 0
    Set-ItemProperty -LiteralPath $prefs -Name $keys[2] -Value 1
    & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') -TimberbornExe $exe -SettlementName $settlement -SaveName n10c -TestSpeed $Speed -SkipModManager -SecondsAfterLoad $SecondsAfterLoad -ExtraGameArgs @($profileFlag) -OutputDir $output | Out-Host
    $summaries=@(Get-ChildItem -LiteralPath $output -Filter 'probe-summary-*.json')
    if($summaries.Count -ne 1) {throw 'Missing observation result'}
    $observation=Get-Content -LiteralPath $summaries[0].FullName -Raw | ConvertFrom-Json
    if(-not $observation.DirectLaunch -or -not $observation.ObservationCompleted -or $observation.RequestedSeconds -ne $SecondsAfterLoad -or
       $observation.SawException -or $observation.Failure -or $observation.FinalizationFailures.Count -or -not $observation.LogCaptured) {throw 'Observation failed or ended prematurely'}
    $logs=@(Get-ChildItem -LiteralPath $output -Filter 'autoload-*.log' | Where-Object Name -NotLike 'autoload-previous-*')
    if($logs.Count -ne 1) {throw 'Ambiguous profile log'}
    $lines=Get-Content -LiteralPath $logs[0].FullName
    if($lines -match 'First uncaught exception|^Rethrow as Exception:' -or $lines -match '\[T3MPSEARCH\].*exceptions=[1-9]') {throw 'Exception in profile'}
    $expectedMethods=if($SearchKind -eq 'WaterObjects') {
        @('Timberborn.WaterObjects.WaterObjectService.Tick','Timberborn.TickSystem.TickableBucketService.TickBuckets')
    } elseif($SearchKind -eq 'Decisions') {
        @('Timberborn.Hauling.DistrictHaulCandidates.GetWorkplaceBehaviorsOrdered','Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService.PickBestAction','Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService.AppraiseNeedBehaviors','Timberborn.NeedBehaviorSystem.DistrictNeedBehaviorService.PickShortestAction','Timberborn.TickSystem.TickableBucketService.TickBuckets')
    } else {
        @('Timberborn.YielderFinding.YielderFinder.FindLivingYielderWithoutAccessible','Timberborn.YielderFinding.YielderFinder.FindYielderWithAccessible','Timberborn.Planting.PlantingSpotFinder.FindClosest','Timberborn.Yielding.InRangeYielders.GetYielders','Timberborn.TickSystem.TickableBucketService.TickBuckets')
    }
    $expectedCount=$expectedMethods.Count
    if(@($lines | Where-Object {$_ -eq "[T3MPSEARCH] installed targets=$expectedCount"}).Count -ne 1) {throw 'Profiler installation missing'}
    $windows=0
    $seen=$null
    foreach($line in $lines) {
        if($line -match '^\[T3MPSEARCH\] window seconds=([0-9.]+)$') {
            if($null -ne $seen -and $seen.Count -ne $expectedCount) {throw 'Truncated profile window'}
            $windows++
            $seen=[Collections.Generic.HashSet[string]]::new()
        } elseif($line -like '[[]T3MPSEARCH] method=*') {
            if($null -eq $seen -or $line -notmatch '^\[T3MPSEARCH\] method=(\S+) calls=\d+ inclusiveMs=[0-9.]+ exceptions=0$') {throw 'Malformed profile record'}
            $method=$Matches[1]
            if($method -cnotin $expectedMethods -or -not $seen.Add($method)) {throw 'Unexpected or duplicate profile method'}
        }
    }
    if($windows -lt 3 -or $null -eq $seen -or $seen.Count -ne $expectedCount) {throw 'Incomplete search profile'}
    $manifest=Get-Content -LiteralPath (Join-Path $mods 'T3MP/manifest.json') -Raw | ConvertFrom-Json
    $allowed=@('- Harmony (v2.4.1)','- T3MP Test Driver (dev only) (v1.0.0)',"- $($manifest.Name) (v$($manifest.Version))")
    $loaded=@($lines | Where-Object {$_ -match '^- .+ \(v[^)]+\)$'})
    if($loaded.Count -ne $allowed.Count -or @($loaded | Where-Object {$_ -cnotin $allowed}).Count) {throw 'Unexpected loaded mods'}
    $lines | Where-Object {$_ -like '[[]T3MPSEARCH]*'} | Set-Content -LiteralPath (Join-Path $output 'search.log')
} catch {$failure=$_.ToString(); throw}
finally {
    $cleanup=[Collections.Generic.List[string]]::new()
    $remaining=@()
    try {$remaining=@(Get-CimInstance Win32_Process -Filter "Name='Timberborn.exe'")} catch {$cleanup.Add($_.ToString())}
    foreach($process in $remaining) {
        if($process.ExecutablePath -ne $exe -or $process.CommandLine -notlike "*$settlement*" -or $process.CommandLine -notlike "*$profileFlag*") {$cleanup.Add('Unrelated game; preserve recovery files'); continue}
        try {
            Stop-Process -Id $process.ProcessId -Force
            Wait-Process -Id $process.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
        } catch {if(Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) {$cleanup.Add($_.ToString())}}
    }
    # Process enumeration can briefly retain a just-terminated game. Observe
    # teardown without terminating any newly appearing or unrelated process.
    $teardownDeadline=[DateTime]::UtcNow.AddSeconds(5)
    while((Get-Process Timberborn -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $teardownDeadline) {Start-Sleep -Milliseconds 250}
    if(Get-Process Timberborn -ErrorAction SilentlyContinue) {$cleanup.Add('Game remains; do not overwrite live settings')}
    else {
        try {Copy-Item -LiteralPath (Join-Path $output 'original-Code.dll') -Destination $code -Force} catch {$cleanup.Add($_.ToString())}
        foreach($key in $keys) {
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
        try {if((Get-FileHash -LiteralPath $code).Hash -ne $originalHash) {throw 'DLL restoration mismatch'}} catch {$cleanup.Add($_.ToString())}
    }
    try {if((Get-FileHash -LiteralPath $SourceSave).Hash -ne $sourceHash) {throw 'Source changed'}} catch {$cleanup.Add($_.ToString())}
    [ordered]@{Failure=$failure;CleanupFailures=@($cleanup);SearchKind=$SearchKind;CodeHash=$candidateHash;DriverHash=$driverHash;SourceHash=$sourceHash;SourceSave=$SourceSave;GameRoot=$GameRoot;Speed=$Speed;SecondsAfterLoad=$SecondsAfterLoad;OriginalHash=$originalHash;Restored=($cleanup.Count -eq 0)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json')
    if($cleanup.Count) {throw ($cleanup -join '; ')}
    Write-Output "Search profile archive: $output"
}
