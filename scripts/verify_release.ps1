#requires -Version 7.0
# One candidate package, one test driver, six startup scenarios across 1.1/1.0.
# This gate checks package/startup compatibility, not the performance target or
# the complete Workshop regression matrix. No upload is performed.
[CmdletBinding()]
param(
    [string]$V10Install=(Join-Path $env:USERPROFILE 'Documents\TimberbornVersions\Timberborn-1.0-build23107127'),
    [string]$V11Install='C:\Program Files (x86)\Steam\steamapps\common\Timberborn',
    [string]$V10Settlement='m7c', [string]$V10Save='m7c',
    [string]$V11Settlement='n10c', [string]$V11Save='n10c',
    [string]$V11SaveRoot=(Join-Path $env:USERPROFILE 'Documents\Timberborn\Saves'),
    [string]$V11ExpectedVersion='1.1.2.4',
    [string]$NewGameMap='',
    [ValidateRange(45,3600)][int]$SecondsAfterLoad=45,
    [string]$OutputDir='',
    [switch]$SkipE2E
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'ReleaseVerification.ps1')
$documents=Join-Path $env:USERPROFILE 'Documents\Timberborn'
$mods=Join-Path $documents 'Mods'
$product=Join-Path $mods 'T3MP'
$driver=Join-Path $mods 'T3MPTestDriver'
$modSource=Join-Path $repo 'mod'
$runId=[guid]::NewGuid().ToString('N')
if(-not $OutputDir) {$OutputDir=Join-Path $repo ('testlogs/release-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+$runId.Substring(0,8))}
$output=[IO.Path]::GetFullPath($OutputDir)
if(Get-Process Timberborn -ErrorAction SilentlyContinue) {throw 'A game is already running'}
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $driver)) {throw 'Output or test driver already exists'}
if([IO.Path]::GetPathRoot($output) -ne [IO.Path]::GetPathRoot($mods)) {throw 'Restoration archives must share the Mods volume'}
$allowed=@('manifest.json','README.md','thumbnail.jpg')
$entries=@(Get-ChildItem -LiteralPath $modSource -Force)
if($entries.Count -ne $allowed.Count -or @($entries | Where-Object {$_.PSIsContainer -or $_.Name -cnotin $allowed}).Count) {throw 'mod/ must contain exactly the three shipping files'}
$manifest=Get-Content -LiteralPath (Join-Path $modSource 'manifest.json') -Raw | ConvertFrom-Json
$settings=Get-Content -LiteralPath (Join-Path $repo 'src/T3MP/ModSettings.cs') -Raw
if($manifest.Id -cne 'T3MP' -or $settings -notmatch ('public const string Version = "'+[regex]::Escape($manifest.Version)+'";')) {throw 'Manifest and product versions disagree'}
$versions=@(
    @{Name='v11';Install=$V11Install;GameVersion=$V11ExpectedVersion;SaveRoot=$V11SaveRoot;Settlement=$V11Settlement;Save=$V11Save},
    @{Name='v10';Install=$V10Install;GameVersion='1.0.13.1';SaveRoot=(Join-Path $documents 'Saves');Settlement=$V10Settlement;Save=$V10Save}
)
foreach($version in $versions) {
    $version.Install=(Resolve-Path -LiteralPath $version.Install).Path
    $version.Exe=Join-Path $version.Install 'Timberborn.exe'
    if(-not(Test-Path -LiteralPath $version.Exe)) {throw 'Missing game executable'}
    $version.SaveRoot=(Resolve-Path -LiteralPath $version.SaveRoot).Path
    if([IO.Path]::GetPathRoot($version.SaveRoot) -ne [IO.Path]::GetPathRoot($output)) {throw 'Save archives must share the output volume'}
    $version.Source=(Resolve-Path -LiteralPath (Join-Path $version.SaveRoot ($version.Settlement+'\'+$version.Save+'.timber'))).Path
    if(-not $version.Source.StartsWith($version.SaveRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Source save outside its root'}
    $version.SourceHash=(Get-FileHash -LiteralPath $version.Source).Hash
    $version.StageName='t3mp-verify-'+$runId+'-'+$version.Name
    $version.NewName=$version.StageName+'-new'
    $version.Stage=Join-Path $version.SaveRoot $version.StageName
    $version.NewStage=Join-Path $version.SaveRoot $version.NewName
    if((Test-Path -LiteralPath $version.Stage) -or (Test-Path -LiteralPath $version.NewStage)) {throw 'Unexpected existing stage'}
}
$prefs='HKCU:\Software\Mechanistry\Timberborn'
$prefItem=Get-Item -LiteralPath $prefs
$keys=@{'ModEnabled.Local.T3MP.T3MP_h1796111333'=1;'ModEnabled.Steam Workshop.3756656421.T3MP_h539883676'=0;'ModEnabled.Steam Workshop.3284904751.Harmony_h3757451374'=1}
foreach($key in @($prefItem.GetValueNames() | Where-Object {$_ -like 'ModEnabled.Local.T3MPTestDriver.T3MPTestDriver_h*'})) {$keys[$key]=1}
$originalPrefs=@{}
foreach($key in $keys.Keys) {
    $exists=$prefItem.GetValueNames() -contains $key
    if($exists -and $prefItem.GetValueKind($key) -ne [Microsoft.Win32.RegistryValueKind]::DWord) {throw 'Unexpected MOD preference type'}
    $originalPrefs[$key]=@{Exists=$exists;Value=if($exists){$prefItem.GetValue($key)}else{$null}}
}
$hadOriginal=Test-Path -LiteralPath $product
$originalHashes=if($hadOriginal){Get-ReleaseHashes $product}else{$null}
New-Item -ItemType Directory -Path $output | Out-Null
$originalPrefs | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'original-prefs.json')
$originalHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'original-product-hashes.json')
$results=[Collections.Generic.List[object]]::new()
$cleanup=[Collections.Generic.List[string]]::new()
$failure=$null; $candidateHash=$null; $driverHash=$null
$originalMoved=$false; $productCreated=$false; $driverCreated=$false; $settingsChanged=$false
$ownedStages=[Collections.Generic.List[object]]::new()
$backup=Join-Path $output 'original-T3MP'
try {
    # Compile both API targets, then freeze the 1.1 build. No builds/deploy.ps1
    # calls occur between game scenarios; both versions receive these bytes.
    foreach($entry in @(@('v10',$V10Install),@('v11',$V11Install))) {
        dotnet build (Join-Path $repo 'src/T3MP/T3MP.csproj') -c Release "-p:TimberbornInstall=$($entry[1])" *> (Join-Path $output ('build-'+$entry[0]+'.log'))
        if($LASTEXITCODE -ne 0) {throw "Product build failed for $($entry[0])"}
    }
    $package=Join-Path $output 'package-input'
    New-Item -ItemType Directory -Path $package | Out-Null
    foreach($leaf in $allowed) {Copy-Item -LiteralPath (Join-Path $modSource $leaf) -Destination $package}
    Copy-Item -LiteralPath (Join-Path $repo 'src/T3MP/bin/Release/netstandard2.1/Code.dll') -Destination $package
    $packageHashes=Get-ReleaseHashes $package
    $candidateHash=$packageHashes['Code.dll']
    $candidateMvid=Get-ReleaseMvid (Join-Path $package 'Code.dll')
    dotnet build (Join-Path $repo 'src/T3MPTestDriver/T3MPTestDriver.csproj') -c Release "-p:TimberbornInstall=$V11Install" *> (Join-Path $output 'driver-build.log')
    if($LASTEXITCODE -ne 0) {throw 'Driver build failed'}
    $driverInput=Join-Path $output 'T3MPTestDriver.dll'
    Copy-Item -LiteralPath (Join-Path $repo 'src/T3MPTestDriver/bin/Release/netstandard2.1/T3MPTestDriver.dll') -Destination $driverInput
    $driverHash=(Get-FileHash -LiteralPath $driverInput).Hash
    $driverMvid=Get-ReleaseMvid $driverInput
    if(-not $SkipE2E) {
        if(Get-Process Timberborn -ErrorAction SilentlyContinue) {throw 'A game started before deployment'}
        if($hadOriginal) {Move-ReleasePath $product $backup $mods $output; $originalMoved=$true}
        New-Item -ItemType Directory -Path $product | Out-Null; $productCreated=$true
        foreach($leaf in $packageHashes.Keys) {Copy-Item -LiteralPath (Join-Path $package $leaf) -Destination $product}
        New-Item -ItemType Directory -Path $driver | Out-Null; $driverCreated=$true
        Copy-Item -LiteralPath $driverInput,(Join-Path $repo 'testmod/manifest.json') -Destination $driver
        $settingsChanged=$true
        foreach($key in $keys.Keys) {New-ItemProperty -LiteralPath $prefs -Name $key -Value $keys[$key] -PropertyType DWord -Force | Out-Null}
        foreach($version in $versions) {
            New-Item -ItemType Directory -Path $version.Stage | Out-Null
            $ownedStages.Add(@{Path=$version.Stage;Root=$version.SaveRoot;Archive=$version.Name+'-save'})
            $ownedStages.Add(@{Path=$version.NewStage;Root=$version.SaveRoot;Archive=$version.Name+'-new-game'})
            foreach($scenario in @('LaunchArgs','MenuLoad','NewGame')) {
                if(Get-Process Timberborn -ErrorAction SilentlyContinue) {throw 'A game remains before the next scenario'}
                Copy-Item -LiteralPath $version.Source -Destination (Join-Path $version.Stage ($version.Save+'.timber')) -Force
                if((Get-ReleaseHashes $product | ConvertTo-Json -Compress) -cne ($packageHashes | ConvertTo-Json -Compress) -or
                   (Get-FileHash -LiteralPath (Join-Path $driver 'T3MPTestDriver.dll')).Hash -ne $driverHash) {throw 'Candidate package or driver changed'}
                $runDir=Join-Path $output ($version.Name+'-'+$scenario)
                New-Item -ItemType Directory -Path $runDir | Out-Null
                Write-Output "Verifying $($version.Name) $scenario with DLL $candidateHash"
                $scenarioSpeed=if($scenario -eq 'NewGame'){1}else{3}
                & (Join-Path $PSScriptRoot 'run_autoload_probe.ps1') -TimberbornExe $version.Exe -Scenario $scenario -SettlementName $version.StageName -SaveName $version.Save -MapName $NewGameMap -TestSpeed $scenarioSpeed -SecondsAfterLoad $SecondsAfterLoad -SkipModManager -AutoConfirmMods:$false -StopAfter -OutputDir $runDir -ExtraGameArgs @('-t3mpTestReleaseRun',$runId,'-t3mpTestNewSettlement',$version.NewName) | Out-Host
                $deadline=[DateTime]::UtcNow.AddSeconds(5)
                while((Get-Process Timberborn -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {Start-Sleep -Milliseconds 250}
                if(Get-Process Timberborn -ErrorAction SilentlyContinue) {throw 'A game remains after the probe'}
                $metadata=@(Get-ChildItem -LiteralPath $runDir -Filter 'probe-summary-*.json')
                $logs=@(Get-ChildItem -LiteralPath $runDir -Filter 'autoload-*.log' | Where-Object Name -NotLike 'autoload-previous-*')
                if($metadata.Count -ne 1 -or $logs.Count -ne 1) {throw 'Missing or duplicated probe records'}
                $probe=Get-Content -LiteralPath $metadata[0].FullName -Raw | ConvertFrom-Json
                if([IO.Path]::GetFullPath($probe.Log) -ne $logs[0].FullName) {throw 'Probe metadata points to a different log'}
                $result=Test-ReleaseScenario (Get-Content -LiteralPath $logs[0].FullName) $probe $scenario $version.GameVersion $manifest $SecondsAfterLoad $product $candidateMvid (Join-Path $driver 'T3MPTestDriver.dll') $driverMvid
                if((Get-ReleaseHashes $product | ConvertTo-Json -Compress) -cne ($packageHashes | ConvertTo-Json -Compress) -or
                   (Get-FileHash -LiteralPath (Join-Path $driver 'T3MPTestDriver.dll')).Hash -ne $driverHash) {throw 'Candidate changed during the scenario'}
                $result | Add-Member -NotePropertyName CodeHash -NotePropertyValue $candidateHash
                $result | Add-Member -NotePropertyName DriverHash -NotePropertyValue $driverHash
                $result | Add-Member -NotePropertyName Log -NotePropertyValue $logs[0].FullName
                $results.Add($result)
                $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'scenarios.json')
                Write-Output "PASS: $($version.Name) $scenario"
            }
        }
    }
} catch {$failure=$_.ToString()}
finally {
    # Stop only this run's token on one of the selected executables. If another
    # game exists, leave recovery files in place instead of changing live mods.
    try {
        foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='Timberborn.exe'")) {
            if($process.ExecutablePath -notin $versions.Exe -or $process.CommandLine -notlike "*$runId*") {continue}
            try {Stop-Process -Id $process.ProcessId -Force; Wait-Process -Id $process.ProcessId -Timeout 30 -ErrorAction SilentlyContinue}
            catch {if(Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) {$cleanup.Add($_.ToString())}}
        }
    } catch {$cleanup.Add($_.ToString())}
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    while((Get-Process Timberborn -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {Start-Sleep -Milliseconds 250}
    if(Get-Process Timberborn -ErrorAction SilentlyContinue) {$cleanup.Add('Game remains; restore from the recorded backup after it exits')}
    else {
        if($productCreated) {try {Move-ReleasePath $product (Join-Path $output 'tested-package') $mods $output} catch {$cleanup.Add($_.ToString())}}
        if($originalMoved) {try {Move-ReleasePath $backup $product $output $mods} catch {$cleanup.Add($_.ToString())}}
        if($driverCreated) {try {Move-ReleasePath $driver (Join-Path $output 'driver-archive') $mods $output} catch {$cleanup.Add($_.ToString())}}
        foreach($stage in $ownedStages) {try {if(Test-Path -LiteralPath $stage.Path) {Move-ReleasePath $stage.Path (Join-Path $output $stage.Archive) $stage.Root $output}} catch {$cleanup.Add($_.ToString())}}
        if($settingsChanged) {
            foreach($key in $originalPrefs.Keys) {
                try {
                    if($originalPrefs[$key].Exists) {New-ItemProperty -LiteralPath $prefs -Name $key -Value $originalPrefs[$key].Value -PropertyType DWord -Force | Out-Null}
                    else {Remove-ItemProperty -LiteralPath $prefs -Name $key -ErrorAction SilentlyContinue}
                    $restored=Get-Item -LiteralPath $prefs
                    if(($restored.GetValueNames() -contains $key) -ne $originalPrefs[$key].Exists -or
                       ($originalPrefs[$key].Exists -and $restored.GetValue($key) -ne $originalPrefs[$key].Value)) {throw 'Preference restoration mismatch'}
                } catch {$cleanup.Add($_.ToString())}
            }
        }
        try {
            if($hadOriginal) {if((Get-ReleaseHashes $product | ConvertTo-Json -Compress) -cne ($originalHashes | ConvertTo-Json -Compress)) {throw 'Original product restoration mismatch'}}
            elseif(Test-Path -LiteralPath $product) {throw 'Candidate remains installed'}
        } catch {$cleanup.Add($_.ToString())}
    }
    foreach($version in $versions) {try {if((Get-FileHash -LiteralPath $version.Source).Hash -ne $version.SourceHash) {throw "Source save changed: $($version.Source)"}} catch {$cleanup.Add($_.ToString())}}
    $ready=(-not $failure -and $cleanup.Count -eq 0 -and -not $SkipE2E -and $results.Count -eq 6)
    [ordered]@{Scope='Package and six startup scenarios; performance and full regression gates remain separate';Ready=$ready;CompileOnly=[bool]$SkipE2E;Failure=$failure;CleanupFailures=$cleanup.ToArray();CandidateHash=$candidateHash;DriverHash=$driverHash;ManifestVersion=$manifest.Version;PackageHashes=$packageHashes;OriginalProductHashes=$originalHashes;Versions=$versions;Scenarios=$results.ToArray()} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'summary.json')
}
Write-Output "Verification archive: $output"
if($failure -or $cleanup.Count) {Write-Output ('NOT READY: '+$failure+' '+($cleanup -join '; ')); exit 1}
if($SkipE2E) {Write-Output 'COMPILE ONLY: startup scenarios were not run.'; exit 0}
if(-not $ready) {Write-Output 'NOT READY: six successful scenarios are required.'; exit 1}
Write-Output 'READY (package/startup gate): the same candidate passed all six scenarios. Performance and remaining regression checks are separate.'
exit 0
