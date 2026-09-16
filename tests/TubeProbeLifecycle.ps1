# Isolated filesystem/process fault injection; never launches or stops a game.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtureRoot = Join-Path $repo ('testlogs\tube-lifecycle-' + [Guid]::NewGuid().ToString('N'))
$savedProfile = $env:USERPROFILE
$results = @()
function Get-Process {
    param($Name, $ErrorAction)
    if ($Name -ne 'Timberborn') { throw 'Unexpected process query' }
    $global:T3MPTubeFixture.Checks++
    if ($global:T3MPTubeFixture.Name -eq 'running' -or
        ($global:T3MPTubeFixture.Name -eq 'cleanup-running' -and $global:T3MPTubeFixture.Checks -ge 3)) {
        [pscustomobject]@{Id=12345;ProcessName='Timberborn'}
    }
}
function Stop-Process { throw 'The wrapper must never stop a process' }
try {
    foreach ($name in @('running','existing-driver','missing-manifest','launch-failure','incomplete','disabled-monitor','success','cleanup-running')) {
        $global:T3MPTubeFixture = @{Name=$name;Checks=0}
        $fixture = Join-Path $fixtureRoot $name
        $env:USERPROFILE = Join-Path $fixture 'profile'
        $scripts = Join-Path $fixture 'scripts'
        $mods = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
        $built = Join-Path $fixture 'src\T3MPTestDriver\bin\Release\netstandard2.1'
        foreach ($path in @($scripts,$mods,$built,(Join-Path $fixture 'testmod'))) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }
        Copy-Item -LiteralPath (Join-Path $repo 'scripts\run_tube_probe.ps1') -Destination $scripts
        Copy-Item -LiteralPath (Join-Path $repo 'scripts\ReleaseVerification.ps1') -Destination $scripts
        Set-Content -LiteralPath (Join-Path $built 'T3MPTestDriver.dll') -Value 'fixture-driver'
        if ($name -ne 'missing-manifest') {
            Set-Content -LiteralPath (Join-Path $fixture 'testmod\manifest.json') -Value '{}'
        }
        $exe = Join-Path $fixture 'Timberborn.exe'
        Set-Content -LiteralPath $exe -Value 'not executable'
        $driver = Join-Path $mods 'T3MPTestDriver'
        if ($name -eq 'existing-driver') {
            New-Item -ItemType Directory -Path $driver | Out-Null
            Set-Content -LiteralPath (Join-Path $driver 'original.txt') -Value 'preserve'
        }
        @'
param($TimberbornExe,$SettlementName,$SaveName,$Scenario,$TestSpeed,[switch]$SkipModManager,$OutputDir,$SecondsAfterLoad,[switch]$StopAfter,$ExtraGameArgs)
if (-not $StopAfter -or $ExtraGameArgs -notcontains '-t3mpTestTubeLights') { throw 'Missing required probe options' }
if ($global:T3MPTubeFixture.Name -eq 'launch-failure') { throw 'Injected launch failure' }
$log = Join-Path $OutputDir 'autoload-fixture.log'
$line = if ($global:T3MPTubeFixture.Name -eq 'disabled-monitor') {'[T3MPTEST] tubelight disabled reason=fixture'} else {'[T3MPTEST] tubelight summary sample=1 errors=0'}
Set-Content -LiteralPath $log -Value $line
@{DirectLaunch=$true;SawLoadTime=$true;ObservationCompleted=($global:T3MPTubeFixture.Name -ne 'incomplete');LogCaptured=$true;SawException=$false;Failure=$null;FinalizationFailures=@();ObservedSeconds=$SecondsAfterLoad;Log=$log} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir 'probe-summary-fixture.json')
'@ | Set-Content -LiteralPath (Join-Path $scripts 'run_autoload_probe.ps1')
        $errorText = $null
        try { & (Join-Path $scripts 'run_tube_probe.ps1') -SkipBuild -TimberbornExe $exe -SecondsAfterLoad 1 | Out-Null }
        catch { $errorText = $_.ToString() }
        if (($name -eq 'success') -ne ($null -eq $errorText)) { throw "Unexpected outcome for ${name}: $errorText" }
        $outputs = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'testlogs') -Directory -ErrorAction SilentlyContinue)
        if ($name -in @('running','existing-driver')) {
            if ($outputs.Count) { throw 'Preflight created probe files' }
        } else {
            if ($outputs.Count -ne 1) { throw "Expected one output for ${name}: $errorText" }
            $summary = Get-Content -LiteralPath (Join-Path $outputs[0].FullName 'tube-probe-summary.json') -Raw | ConvertFrom-Json
            if ($name -eq 'cleanup-running') {
                if (-not $summary.CleanupFailure -or $summary.DriverArchived -or -not (Test-Path -LiteralPath $driver)) { throw 'Unsafe cleanup during a running game' }
            } elseif (-not $summary.DriverArchived -or (Test-Path -LiteralPath $driver)) { throw "Driver cleanup failed for $name" }
        }
        if ($name -eq 'existing-driver' -and (Get-Content -LiteralPath (Join-Path $driver 'original.txt')) -ne 'preserve') { throw 'Existing driver changed' }
        $results += [pscustomobject]@{Case=$name;Passed=$true;ExpectedFailure=$errorText}
    }
} finally { $env:USERPROFILE = $savedProfile }
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'results.json')
Write-Output "PASS: $($results.Count) tube lifecycle cases; records: $fixtureRoot"
