# Helpers for verify_release.ps1. Loading this file has no side effects.
function Get-ReleaseMvid([string]$Path) {
    $stream=[IO.File]::OpenRead($Path)
    try {
        $pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $reader=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            return $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString('D')
        } finally {$pe.Dispose()}
    } finally {$stream.Dispose()}
}

function Get-ReleaseHashes([string]$Directory) {
    $root=(Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\')
    $hashes=[ordered]@{}
    foreach($file in @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object FullName)) {
        $hashes[$file.FullName.Substring($root.Length+1)]=(Get-FileHash -LiteralPath $file.FullName).Hash
    }
    return $hashes
}

function Move-ReleasePath([string]$Source,[string]$Destination,[string]$SourceRoot,[string]$DestinationRoot) {
    $resolved=(Resolve-Path -LiteralPath $Source).Path
    $target=[IO.Path]::GetFullPath($Destination)
    foreach($pair in @(@($resolved,$SourceRoot),@($target,$DestinationRoot))) {
        $root=[IO.Path]::GetFullPath($pair[1]).TrimEnd('\')+'\'
        if(-not $pair[0].StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) {throw 'Archive path is outside its intended root'}
    }
    if(Test-Path -LiteralPath $target) {throw "Archive destination exists: $target"}
    Move-Item -LiteralPath $resolved -Destination $target
}

function Test-ReleaseScenario($Lines,$Probe,[string]$Scenario,[string]$GameVersion,$Manifest,[int]$Seconds,[string]$ProductPath,[string]$CandidateMvid,[string]$DriverPath,[string]$DriverMvid) {
    if(-not $Probe.DirectLaunch -or -not $Probe.SawLoadTime -or -not $Probe.ObservationCompleted -or
       $Probe.CompletionMatched -or $Probe.RequestedSeconds -ne $Seconds -or $Probe.ObservedSeconds -lt $Seconds -or
       $Probe.Failure -or $Probe.FinalizationFailures.Count -or -not $Probe.LogCaptured -or $Probe.SawException) {
        throw 'The full observation interval did not complete on the launched game'
    }
    $versions=@($Lines | Where-Object {$_ -match '^Starting game version '})
    if($versions.Count -ne 1 -or $versions[0] -notmatch ('^Starting game version '+[regex]::Escape($GameVersion)+'-')) {throw 'Unexpected game version'}
    if($Lines -match 'Failed to load asset bundle|First uncaught exception|^Rethrow as Exception:|\[T3MPTEST\] ERROR|^\s*(?:[\w]+\.)*[\w]*Exception\s*:') {throw 'Game or driver failure'}
    if(@($Lines | Where-Object {$_ -match '^\[T3MP\] Loaded\. Version='}).Count -ne 1 -or
       -not ($Lines -match ('^\[T3MP\] Loaded\. Version='+[regex]::Escape($Manifest.Version)+' '))) {throw 'Unexpected product version'}
    $modLine=@($Lines | Where-Object {$_ -match '^\[T3MP\] Loaded\. Version='})[0]
    if($modLine -notmatch ' ModPath=(.+)$' -or [IO.Path]::GetFullPath($Matches[1]).TrimEnd('\') -ne [IO.Path]::GetFullPath($ProductPath).TrimEnd('\')) {throw 'Product loaded from a different mod directory'}
    $identities=@($Lines | Where-Object {$_ -like '[[]T3MPTEST] Release identity *'} | ForEach-Object {($_ -replace '^\[T3MPTEST\] Release identity ','') | ConvertFrom-Json})
    if($identities.Count -ne 2) {throw 'Expected two loaded assembly identities'}
    foreach($identity in @(@('product',(Join-Path $ProductPath 'Code.dll'),$CandidateMvid),@('driver',$DriverPath,$DriverMvid))) {
        $actual=@($identities | Where-Object kind -CEQ $identity[0])
        if($actual.Count -ne 1 -or $actual[0].mvid -cne $identity[2] -or
           ($actual[0].location -and [IO.Path]::GetFullPath($actual[0].location) -ne [IO.Path]::GetFullPath($identity[1]))) {throw 'Loaded assembly differs from the verified candidate or driver'}
    }
    # Native new-game startup sets speed 1 after spawning beavers. Exercise
    # that normal startup; existing-save scenarios exercise requested speed 3.
    $expectedSpeed=if($Scenario -eq 'NewGame'){1}else{3}
    $minimumScale=if($expectedSpeed -eq 1){1.0}else{1.8}
    if(@($Lines | Where-Object {$_ -ceq "[T3MPTEST] Game scene loaded OK. Unpausing (speed $expectedSpeed)."}).Count -ne 1) {throw 'Requested scenario speed was not initialized'}
    if(@($Lines | Where-Object {$_ -like '[[]T3MP] Speed meter ready*'}).Count -ne 1) {throw 'Speed meter did not initialize exactly once'}
    if(@($Lines | Where-Object {$_ -match '^Load time:.*scene index: 2'}).Count -ne 1) {throw 'Game scene did not load exactly once'}
    if($Scenario -ne 'LaunchArgs' -and @($Lines | Where-Object {$_ -like "[[]T3MPTEST] ${Scenario}: starting*"}).Count -ne 1) {throw 'Requested menu scenario did not execute'}
    foreach($marker in @('EventBus fast delegates installed.','Water upload de-duplication installed','Tick frontier installed.','Tube visit fix installed.')) {
        if(@($Lines | Where-Object {$_.Contains('[T3MP] '+$marker)}).Count -ne 1) {throw "Missing or duplicated runtime feature: $marker"}
    }
    if($Lines -match '\[T3MP\].*(removed:|Tick traversal installed|passing.*through unchanged)') {throw 'Runtime patch was replaced or removed'}
    $allowed=@('- Harmony (v2.4.1)','- T3MP Test Driver (dev only) (v1.0.0)',"- $($Manifest.Name) (v$($Manifest.Version))")
    $loaded=@($Lines | Where-Object {$_ -match '^- .+ \(v[^)]+\)$'})
    if($loaded.Count -ne $allowed.Count -or @($loaded | Where-Object {$_ -cnotin $allowed}).Count -or @($loaded | Select-Object -Unique).Count -ne $allowed.Count) {throw 'Unexpected loaded mods'}
    $rates=@($Lines | ForEach-Object {
        if($_ -match '^\[T3MPTEST\] Simulation rate ([0-9.]+) ticks/s .*ticks=(\d+) timeScale=([0-9.]+).* elapsed=([0-9.]+)$') {
            [pscustomobject]@{Rate=[double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture);Ticks=[long]$Matches[2];TimeScale=[double]::Parse($Matches[3],[Globalization.CultureInfo]::InvariantCulture);Seconds=[double]::Parse($Matches[4],[Globalization.CultureInfo]::InvariantCulture)}
        }
    })
    if($rates.Count -lt 2 -or @($rates | Where-Object {$_.Ticks -le 0 -or $_.Seconds -le 0 -or $_.Rate -le 0 -or $_.TimeScale -lt $minimumScale -or $_.TimeScale -gt $expectedSpeed}).Count -or
       @($Lines | Where-Object {$_ -like '[[]T3MPTEST] Simulation rate *'}).Count -ne $rates.Count) {throw 'Insufficient simulation progress or unexpected speed telemetry'}
    return [pscustomobject]@{Game=$versions[0];Scenario=$Scenario;RequestedSpeed=$expectedSpeed;Windows=$rates.Count;Ticks=($rates.Ticks | Measure-Object -Sum).Sum;ObservationSeconds=$Probe.ObservedSeconds}
}
