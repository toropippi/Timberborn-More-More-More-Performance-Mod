$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$probeRoot=Join-Path $repo 'testlogs/offline-bool-20260913'
$runRoot=Join-Path $probeRoot 'Benchmark-c27e4bc7a0fc4763a454526ce440781a'
function Read-Json([string]$path){Get-Content -LiteralPath $path -Raw | ConvertFrom-Json}
$runs=@(Read-Json (Join-Path $runRoot 'runs.json'))
if($runs.Count -ne 4 -or ($runs | Where-Object {-not $_.Result.valid})){throw 'Invalid runs'}
$bSeconds=($runs | Where-Object Arm -eq B | ForEach-Object {$_.Result.elapsedSeconds} | Measure-Object -Sum).Sum
$oSeconds=($runs | Where-Object Arm -eq O | ForEach-Object {$_.Result.elapsedSeconds} | Measure-Object -Sum).Sum
$report=[ordered]@{
    Status='Not adopted: small pooled gain, second pair approximately unchanged'
    Game='Steam 1.1.2.4 copied installation';Save='n10c';Order='BOOB'
    PooledBaselineTps=4096/$bSeconds;PooledCandidateTps=4096/$oSeconds;PooledRatio=$bSeconds/$oSeconds
    Pairs=@(($runs[1].Result.ticksPerSecond/$runs[0].Result.ticksPerSecond),($runs[2].Result.ticksPerSecond/$runs[3].Result.ticksPerSecond))
    Runs=$runs
    Design=(Read-Json (Join-Path $runRoot 'design.json'))
    Restoration=(Read-Json (Join-Path $runRoot 'restoration.json'))
    Lifetime=[ordered]@{
        Cases=10;Version='1.1.2.4';Result='PASS'
        Log='testlogs/offline-bool-20260913/Validate-b87f681d7eb64841b8eb91a5e8b300e2/Player.log'
        Restoration=(Read-Json (Join-Path $probeRoot 'Validate-b87f681d7eb64841b8eb91a5e8b300e2/restoration.json'))
        Scope='Managed null/wrapper, live and DestroyImmediate GameObject/Transform/Texture2D/TextAsset; native comparison oracle. No whole-world or delayed-Destroy claim.'
    }
    GeneratorAudits=@((Read-Json (Join-Path $probeRoot 'candidate-v11-final/result.json')),(Read-Json (Join-Path $probeRoot 'candidate-v10-final/result.json')))
    Reviews=@('testlogs/offline-bool-20260913/review.txt','testlogs/offline-bool-20260913/lifecycle-review.txt')
    Cleanup=[ordered]@{
        NormalDriverHash=(Get-FileHash -LiteralPath (Join-Path $repo 'src/T3MPTestDriver/bin/Release/netstandard2.1/T3MPTestDriver.dll')).Hash
        ExperimentArchived='experiments/offline-unity-bool/runtime';BothNormalDriverBuilds='PASS'
        ProductHash=(Get-FileHash -LiteralPath (Join-Path $repo 'src/T3MP/bin/Release/netstandard2.1/Code.dll')).Hash
        InstalledHash=(Get-FileHash -LiteralPath (Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods\T3MP\Code.dll')).Hash
    }
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $repo 'docs/measurements/offline-unity-bool-2026-09-13.json')
[pscustomobject]$report | Select-Object Status,PooledBaselineTps,PooledCandidateTps,PooledRatio,Pairs | ConvertTo-Json
