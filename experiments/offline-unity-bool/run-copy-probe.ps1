[CmdletBinding()]
param([ValidateSet('Validate','Benchmark')][string]$Mode='Validate')
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $repo 'scripts/ReleaseVerification.ps1')
$root=Join-Path $repo 'testlogs/offline-bool-20260913'
$copy=(Resolve-Path -LiteralPath (Join-Path $root 'game-v11')).Path
$manifest=Get-Content -LiteralPath (Join-Path $root 'game-copy.json') -Raw | ConvertFrom-Json
if($copy -ne [IO.Path]::GetFullPath((Join-Path $root 'game-v11')) -or $copy -ne $manifest.Copy -or $copy -eq $manifest.Source){throw 'Unexpected copied game path'}
$coreRelative='Timberborn_Data\Managed\UnityEngine.CoreModule.dll'
$core=Join-Path $copy $coreRelative
$nativeHash='26ACDBA17A3122C04E4640BD5D30833C56C8338700DBBBCD8E139FAC339568CB'
$candidate=Join-Path $root 'candidate-v11-final/UnityEngine.CoreModule.dll'
$candidateHash='1BF09B56F0ABC61A14955C598F6E8EC9259A32AFB1A333F8B3BBD9943BA42098'
$candidateBody='F127C3FB128390E7F8652AAA3927C27120D5097BAC54363BA5BA6E4F16A699E6'
$mods=(Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods')
$modRoot=Join-Path $mods 'T3MP'
$installed=Join-Path $modRoot 'Code.dll'
$product=Join-Path $repo 'src/T3MP/bin/Release/netstandard2.1/Code.dll'
$driver=Join-Path $mods 'T3MPTestDriver'
$builtDriver=Join-Path $repo 'src/T3MPTestDriver/bin/Release/netstandard2.1/T3MPTestDriver.dll'
$exe=Join-Path $copy 'Timberborn.exe'
$run=Join-Path $root ($Mode+'-'+[Guid]::NewGuid().ToString('N'))
if(Get-Process Timberborn -ErrorAction SilentlyContinue){throw 'Existing game'}
if(Test-Path -LiteralPath $driver){throw 'Existing driver'}
if((Get-FileHash -LiteralPath $candidate).Hash -ne $candidateHash -or (Get-FileHash -LiteralPath $core).Hash -ne $nativeHash){throw 'Unexpected CoreModule hash'}
if((Get-FileHash -LiteralPath $product).Hash -ne 'E2199B6EB29E998AE3557E124BF2136149F26DFC2F3504D99B58B31196A09FDC'){throw 'Unexpected product'}
function Assert-GameCopy([string]$expectedCore){
    foreach($entry in $manifest.Files.PSObject.Properties){
        $relative=$entry.Name
        if([IO.Path]::GetFullPath((Join-Path $copy $relative)) -notlike ($copy+'\*')){throw 'Manifest path escapes copy'}
        $expected=if($relative.Replace('/','\') -eq $coreRelative){$expectedCore}else{$entry.Value}
        if((Get-FileHash -LiteralPath (Join-Path $copy $relative)).Hash -ne $expected){throw "Copied file changed: $relative"}
        if((Get-FileHash -LiteralPath (Join-Path $manifest.Source $relative)).Hash -ne $entry.Value){throw "Source file changed: $relative"}
    }
}
Assert-GameCopy $nativeHash
$modHashes=Get-ReleaseHashes $modRoot
New-Item -ItemType Directory -Path $run | Out-Null
Copy-Item -LiteralPath $core -Destination (Join-Path $run 'native-CoreModule.dll')
Copy-Item -LiteralPath $installed -Destination (Join-Path $run 'original-Code.dll')
$process=$null; $driverCreated=$false; $failure=$null; $restored=$false; $runs=@()
[ordered]@{Mode=$Mode;Order='BOOB';ProductHash=(Get-FileHash $product).Hash;DriverHash=(Get-FileHash $builtDriver).Hash;NativeCore=$nativeHash;CandidateCore=$candidateHash;Source=$manifest.Source;Copy=$copy;Started=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'design.json')
try {
    Copy-Item -LiteralPath $product -Destination $installed -Force
    if($Mode -eq 'Validate'){
        Copy-Item -LiteralPath $candidate -Destination $core -Force
        Assert-GameCopy $candidateHash
        New-Item -ItemType Directory -Path $driver | Out-Null
        $driverCreated=$true
        Copy-Item -LiteralPath $builtDriver -Destination $driver
        Copy-Item -LiteralPath (Join-Path $repo 'testmod/manifest.json') -Destination $driver
        $log=Join-Path $run 'Player.log'
        $flag='-t3mpTestOfflineBoolValidate'
        $process=Start-Process -FilePath $exe -WorkingDirectory $copy -ArgumentList @('-skipModManager',$flag,'-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
        $deadline=[DateTime]::UtcNow.AddSeconds(150)
        do {
            Start-Sleep -Seconds 2
            $process.Refresh()
            $content=if(Test-Path -LiteralPath $log){Get-Content -LiteralPath $log -Raw}else{''}
            if($content -match 'Offline bool validation (PASS|FAIL)'){break}
            if($process.HasExited){throw 'Game exited before validation'}
        }while([DateTime]::UtcNow -lt $deadline)
        if($content -notmatch ('Offline bool validation PASS cases=10 file='+$candidateHash+' body='+$candidateBody)){throw 'Lifetime validation failed or missing'}
        Write-Host "Offline bool lifetime PASS: $run"
    }else{
        foreach($arm in 'BOOB'.ToCharArray()){
            if(Get-Process Timberborn -ErrorAction SilentlyContinue){throw 'Game remains before arm'}
            $selected=if($arm -eq 'O'){$candidate}else{Join-Path $run 'native-CoreModule.dll'}
            $expected=if($arm -eq 'O'){$candidateHash}else{$nativeHash}
            Copy-Item -LiteralPath $selected -Destination $core -Force
            Assert-GameCopy $expected
            $previous=@(Get-ChildItem -LiteralPath (Join-Path $repo 'testlogs') -Directory -Filter 'fixed-ab-*' | Select-Object -ExpandProperty FullName)
            & (Join-Path $repo 'scripts/run_fixed_tick_ab.ps1') -Order B -TimberbornExe $exe -Speed 99 -WarmupTicks 256 -MeasuredTicks 2048 -TimeoutSeconds 900
            $new=@(Get-ChildItem -LiteralPath (Join-Path $repo 'testlogs') -Directory -Filter 'fixed-ab-*' | Where-Object FullName -NotIn $previous)
            if($new.Count -ne 1){throw 'Ambiguous fixed benchmark output'}
            $result=Get-Content -LiteralPath (Join-Path $new[0].FullName 'runs.json') -Raw | ConvertFrom-Json
            $result=@($result)[0]
            $logText=Get-Content -LiteralPath $result.Log -Raw
            if($logText -notmatch ('Offline bool identity file='+$expected+' body=([A-F0-9]{64})')){throw 'Core identity missing'}
            $body=$Matches[1]
            if($arm -eq 'O' -and $body -ne $candidateBody){throw 'In-memory candidate mismatch'}
            if($runs.Count -gt 0){
                $first=$runs[0].Result
                foreach($property in @('display','timeScale','GameVersion','originTick','focused','patches')){if($result.$property -cne $first.$property){throw "Arm differs: $property"}}
            }
            $runs += [pscustomobject]@{Arm=[string]$arm;CoreHash=$expected;BodyHash=$body;Output=$new[0].FullName;Result=$result}
            $runs | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'runs.json')
        }
    }
}catch{$failure=$_.ToString();throw}
finally{
    if($process){
        $process.Refresh()
        if(-not $process.HasExited){
            $owned=Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
            if($owned.ExecutablePath -ne $exe -or $owned.CommandLine -notlike '*-t3mpTestOfflineBoolValidate*'){throw 'Unowned process; preserve files'}
            Stop-Process -Id $process.Id
            if(-not $process.WaitForExit(30000)){throw 'Owned process did not exit; preserve files'}
        }
        $process.Dispose()
    }
    if(Get-CimInstance Win32_Process -Filter "Name='Timberborn.exe'"){throw 'Game remains; preserve deployed files'}
    Copy-Item -LiteralPath (Join-Path $run 'native-CoreModule.dll') -Destination $core -Force
    Copy-Item -LiteralPath (Join-Path $run 'original-Code.dll') -Destination $installed -Force
    if($driverCreated){
        if((Resolve-Path -LiteralPath $driver).Path -ne [IO.Path]::GetFullPath((Join-Path $mods 'T3MPTestDriver'))){throw 'Unexpected archive path'}
        Move-Item -LiteralPath $driver -Destination (Join-Path $run 'driver-archive')
    }
    Assert-GameCopy $nativeHash
    $actual=Get-ReleaseHashes $modRoot
    $restored=$actual.Count -eq $modHashes.Count
    foreach($key in $modHashes.Keys){if(-not $actual.Contains($key) -or $actual[$key] -ne $modHashes[$key]){$restored=$false}}
    [ordered]@{Failure=$failure;Restored=$restored;CopyCoreHash=(Get-FileHash $core).Hash;Mode=$Mode} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'restoration.json')
    Write-Host "Offline probe restoration: $run"
}
if(-not $restored){throw 'Restoration failed'}
