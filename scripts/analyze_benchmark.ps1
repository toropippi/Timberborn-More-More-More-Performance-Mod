[CmdletBinding()]
param(
    [string] $LogPath = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn\Player.log'),
    [int] $Session = -1,
    [int] $FromAggregate = 0,
    [int] $ToAggregate = [int]::MaxValue,
    [int] $SkipAggregates = 0,
    [int] $LastAggregates = 0,
    [int] $MinYielderCallsPerMode = 0,
    [string] $CsvPath = ''
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log file was not found: $LogPath"
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Convert-ToInt64OrZero {
    param([object] $Value)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string] $Value)) {
        return [int64] 0
    }

    return [int64]::Parse([string] $Value, $invariant)
}

function Convert-ToDoubleOrZero {
    param([object] $Value)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string] $Value)) {
        return [double] 0
    }

    return [double]::Parse([string] $Value, $invariant)
}

function Divide-OrZero {
    param(
        [double] $Numerator,
        [double] $Denominator
    )

    if ($Denominator -le 0) {
        return [double] 0
    }

    return $Numerator / $Denominator
}

function Parse-BenchmarkFields {
    param([string] $Line)

    $text = $Line -replace '^.*\[T3MP\] Bench ', ''
    $fields = [ordered] @{}
    foreach ($part in ($text -split ',\s*')) {
        $pair = $part -split '=', 2
        if ($pair.Count -eq 2) {
            $fields[$pair[0].Trim()] = $pair[1].Trim()
        }
    }

    return $fields
}

$rows = New-Object 'System.Collections.Generic.List[object]'
$sessionId = 0

Get-Content -LiteralPath $LogPath | ForEach-Object {
    $line = $_
    if ($line.Contains('[T3MP] Loaded.')) {
        $script:sessionId++
        return
    }

    if (-not $line.Contains('[T3MP] Bench mode=')) {
        return
    }

    $fields = Parse-BenchmarkFields -Line $line
    if (-not $fields.Contains('mode') -or -not $fields.Contains('aggregate')) {
        return
    }

    $sampleFrames = Convert-ToInt64OrZero $fields['sampleFrames']
    $sampleSeconds = Convert-ToDoubleOrZero $fields['sampleSeconds']
    $yielderCalls = Convert-ToInt64OrZero $fields['yielderCalls']
    $yielderMs = Convert-ToDoubleOrZero $fields['yielderMs']
    $inRangeCalls = Convert-ToInt64OrZero $fields['inRangeCalls']
    $inRangeMs = Convert-ToDoubleOrZero $fields['inRangeMs']
    $navCalls = Convert-ToInt64OrZero $fields['navCalls']
    $navMs = Convert-ToDoubleOrZero $fields['navMs']
    $farmCalls = Convert-ToInt64OrZero $fields['farmCalls']
    $farmMs = Convert-ToDoubleOrZero $fields['farmMs']

    $rows.Add([pscustomobject] @{
        Session = $sessionId
        Aggregate = [int] (Convert-ToInt64OrZero $fields['aggregate'])
        Mode = [string] $fields['mode']
        SampleFrames = $sampleFrames
        SampleSeconds = $sampleSeconds
        YielderCalls = $yielderCalls
        YielderCandidates = Convert-ToInt64OrZero $fields['yielderCandidates']
        YielderAvgCandidates = Convert-ToDoubleOrZero $fields['yielderAvgCandidates']
        YielderMs = $yielderMs
        YielderAvgUs = Convert-ToDoubleOrZero $fields['yielderAvgUs']
        YielderMaxMs = Convert-ToDoubleOrZero $fields['yielderMaxMs']
        YielderActiveFrames = Convert-ToInt64OrZero $fields['yielderActiveFrames']
        YielderFrameAvgMs = Convert-ToDoubleOrZero $fields['yielderFrameAvgMs']
        YielderFrameP95Ms = Convert-ToDoubleOrZero $fields['yielderFrameP95Ms']
        YielderFrameMaxMs = Convert-ToDoubleOrZero $fields['yielderFrameMaxMs']
        FastAttempts = Convert-ToInt64OrZero $fields['fastAttempts']
        FastHandled = Convert-ToInt64OrZero $fields['fastHandled']
        FastFallbacks = Convert-ToInt64OrZero $fields['fastFallbacks']
        FastCandidates = Convert-ToInt64OrZero $fields['fastCandidates']
        FastYieldingCandidates = Convert-ToInt64OrZero $fields['fastYieldingCandidates']
        FastPathCalls = Convert-ToInt64OrZero $fields['fastPathCalls']
        FastCacheHits = Convert-ToInt64OrZero $fields['fastCacheHits']
        FastCacheMisses = Convert-ToInt64OrZero $fields['fastCacheMisses']
        FastCacheReachable = Convert-ToInt64OrZero $fields['fastCacheReachable']
        FarmCalls = $farmCalls
        FarmMs = $farmMs
        FarmAvgUs = Convert-ToDoubleOrZero $fields['farmAvgUs']
        FarmMaxMs = Convert-ToDoubleOrZero $fields['farmMaxMs']
        FarmHandled = Convert-ToInt64OrZero $fields['farmHandled']
        FarmFallbacks = Convert-ToInt64OrZero $fields['farmFallbacks']
        FarmIndexBuilds = Convert-ToInt64OrZero $fields['farmIndexBuilds']
        FarmIndexCells = Convert-ToInt64OrZero $fields['farmIndexCells']
        FarmPathCalls = Convert-ToInt64OrZero $fields['farmPathCalls']
        FarmDynamicRefreshes = Convert-ToInt64OrZero $fields['farmDynamicRefreshes']
        FarmIndexBuildMs = Convert-ToDoubleOrZero $fields['farmIndexBuildMs']
        FarmDynamicRefreshMs = Convert-ToDoubleOrZero $fields['farmDynamicRefreshMs']
        FarmReadyQueries = Convert-ToInt64OrZero $fields['farmReadyQueries']
        FarmLiveRejects = Convert-ToInt64OrZero $fields['farmLiveRejects']
        FarmBitClears = Convert-ToInt64OrZero $fields['farmBitClears']
        FarmFound = Convert-ToInt64OrZero $fields['farmFound']
        FarmEmpty = Convert-ToInt64OrZero $fields['farmEmpty']
        FarmNoYielderInRange = Convert-ToInt64OrZero $fields['farmNoYielderInRange']
        Found = Convert-ToInt64OrZero $fields['found']
        Empty = Convert-ToInt64OrZero $fields['empty']
        NoYielderInRange = Convert-ToInt64OrZero $fields['noYielderInRange']
        InRangeCalls = $inRangeCalls
        InRangeReturned = Convert-ToInt64OrZero $fields['inRangeReturned']
        InRangeAvgReturned = Convert-ToDoubleOrZero $fields['inRangeAvgReturned']
        InRangeMs = $inRangeMs
        InRangeAvgUs = Convert-ToDoubleOrZero $fields['inRangeAvgUs']
        InRangeMaxMs = Convert-ToDoubleOrZero $fields['inRangeMaxMs']
        NavCalls = $navCalls
        NavMs = $navMs
        NavAvgUs = Convert-ToDoubleOrZero $fields['navAvgUs']
        NavMaxMs = Convert-ToDoubleOrZero $fields['navMaxMs']
        NavTerrain = Convert-ToInt64OrZero $fields['navTerrain']
        NavRoad = Convert-ToInt64OrZero $fields['navRoad']
        NavReachable = Convert-ToInt64OrZero $fields['navReachable']
        NavPathUnlimited = Convert-ToInt64OrZero $fields['navPathUnlimited']
        NavTerrainMs = Convert-ToDoubleOrZero $fields['navTerrainMs']
        NavRoadMs = Convert-ToDoubleOrZero $fields['navRoadMs']
        NavReachableMs = Convert-ToDoubleOrZero $fields['navReachableMs']
        NavPathUnlimitedMs = Convert-ToDoubleOrZero $fields['navPathUnlimitedMs']
        NavOtherMs = Convert-ToDoubleOrZero $fields['navOtherMs']
        YielderTerrainPath = Convert-ToInt64OrZero $fields['yielderTerrainPath']
        YielderRoadPath = Convert-ToInt64OrZero $fields['yielderRoadPath']
        YielderTerrainPathMs = Convert-ToDoubleOrZero $fields['yielderTerrainPathMs']
        YielderRoadPathMs = Convert-ToDoubleOrZero $fields['yielderRoadPathMs']
        YielderCallsPerSecond = Divide-OrZero $yielderCalls $sampleSeconds
        YielderMsPerSecond = Divide-OrZero $yielderMs $sampleSeconds
        FarmMsPerSecond = Divide-OrZero $farmMs $sampleSeconds
        FarmIndexBuildMsPerSecond = Divide-OrZero (Convert-ToDoubleOrZero $fields['farmIndexBuildMs']) $sampleSeconds
        FarmDynamicRefreshMsPerSecond = Divide-OrZero (Convert-ToDoubleOrZero $fields['farmDynamicRefreshMs']) $sampleSeconds
        InRangeCallsPerSecond = Divide-OrZero $inRangeCalls $sampleSeconds
        InRangeMsPerSecond = Divide-OrZero $inRangeMs $sampleSeconds
        NavCallsPerSecond = Divide-OrZero $navCalls $sampleSeconds
        NavMsPerSecond = Divide-OrZero $navMs $sampleSeconds
    }) | Out-Null
}

if ($rows.Count -eq 0) {
    Write-Host "No benchmark mode rows found in: $LogPath"
    exit 0
}

$selectedSession = $Session
if ($selectedSession -lt 0) {
    $selectedSession = ($rows | Measure-Object -Property Session -Maximum).Maximum
}

$filtered = @($rows |
    Where-Object { $_.Session -eq $selectedSession } |
    Where-Object { $_.Aggregate -ge $FromAggregate -and $_.Aggregate -le $ToAggregate } |
    Sort-Object Aggregate, Mode)

$aggregateIds = @($filtered | Select-Object -ExpandProperty Aggregate -Unique | Sort-Object)
if ($SkipAggregates -gt 0) {
    $aggregateIds = @($aggregateIds | Select-Object -Skip $SkipAggregates)
}

if ($LastAggregates -gt 0 -and $aggregateIds.Count -gt $LastAggregates) {
    $aggregateIds = @($aggregateIds | Select-Object -Last $LastAggregates)
}

$selectedIds = New-Object 'System.Collections.Generic.HashSet[int]'
foreach ($id in $aggregateIds) {
    [void] $selectedIds.Add([int] $id)
}

$filtered = @($filtered | Where-Object { $selectedIds.Contains([int] $_.Aggregate) } | Sort-Object Aggregate, Mode)

if ($MinYielderCallsPerMode -gt 0) {
    $eligibleIds = $filtered |
        Group-Object Aggregate |
        Where-Object {
            $modes = @($_.Group)
            $modes.Count -eq 2 -and ($modes | Where-Object { $_.YielderCalls -ge $MinYielderCallsPerMode }).Count -eq 2
        } |
        ForEach-Object { [int] $_.Name }

    $eligibleSet = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($id in $eligibleIds) {
        [void] $eligibleSet.Add([int] $id)
    }

    $filtered = @($filtered | Where-Object { $eligibleSet.Contains([int] $_.Aggregate) } | Sort-Object Aggregate, Mode)
    $aggregateIds = @($aggregateIds | Where-Object { $eligibleSet.Contains([int] $_) })
}

if ($filtered.Count -eq 0) {
    Write-Host "No benchmark rows matched the selected range."
    exit 0
}

Write-Host ("Selected session={0}, aggregates={1}, rows={2}" -f $selectedSession, ($aggregateIds -join ','), $filtered.Count)
Write-Host ''

$filtered |
    Select-Object Session, Aggregate, Mode, SampleSeconds, YielderCalls, YielderMs, YielderMsPerSecond, FarmCalls, FarmMs, FarmMsPerSecond, FarmIndexBuildMs, FarmDynamicRefreshMs, FarmHandled, FarmFallbacks, FarmIndexBuilds, FarmPathCalls, FarmReadyQueries, FarmLiveRejects, FarmBitClears, FastAttempts, FastHandled, FastFallbacks, FastPathCalls, FastCacheHits, FastCacheMisses, InRangeCalls, InRangeMs, NavCalls, NavMs, NavCallsPerSecond, NavMsPerSecond |
    Format-Table -AutoSize

$summary = $filtered |
    Group-Object Mode |
    ForEach-Object {
        $items = @($_.Group)
        [pscustomobject] @{
            Mode = $_.Name
            Rows = $items.Count
            SampleSeconds = ($items | Measure-Object -Property SampleSeconds -Sum).Sum
            YielderCalls = ($items | Measure-Object -Property YielderCalls -Sum).Sum
            YielderMs = ($items | Measure-Object -Property YielderMs -Sum).Sum
            YielderMsPerSecond = Divide-OrZero (($items | Measure-Object -Property YielderMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            YielderAvgUs = Divide-OrZero (($items | Measure-Object -Property YielderMs -Sum).Sum * 1000.0) (($items | Measure-Object -Property YielderCalls -Sum).Sum)
            YielderFrameP95MsMax = ($items | Measure-Object -Property YielderFrameP95Ms -Maximum).Maximum
            YielderFrameMaxMsMax = ($items | Measure-Object -Property YielderFrameMaxMs -Maximum).Maximum
            FastAttempts = ($items | Measure-Object -Property FastAttempts -Sum).Sum
            FastHandled = ($items | Measure-Object -Property FastHandled -Sum).Sum
            FastFallbacks = ($items | Measure-Object -Property FastFallbacks -Sum).Sum
            FastHandleRate = Divide-OrZero (($items | Measure-Object -Property FastHandled -Sum).Sum) (($items | Measure-Object -Property FastAttempts -Sum).Sum)
            FastPathCalls = ($items | Measure-Object -Property FastPathCalls -Sum).Sum
            FastCacheHits = ($items | Measure-Object -Property FastCacheHits -Sum).Sum
            FastCacheMisses = ($items | Measure-Object -Property FastCacheMisses -Sum).Sum
            FastCacheHitRate = Divide-OrZero (($items | Measure-Object -Property FastCacheHits -Sum).Sum) (($items | Measure-Object -Property FastCacheHits -Sum).Sum + ($items | Measure-Object -Property FastCacheMisses -Sum).Sum)
            FarmCalls = ($items | Measure-Object -Property FarmCalls -Sum).Sum
            FarmMs = ($items | Measure-Object -Property FarmMs -Sum).Sum
            FarmMsPerSecond = Divide-OrZero (($items | Measure-Object -Property FarmMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            FarmAvgUs = Divide-OrZero (($items | Measure-Object -Property FarmMs -Sum).Sum * 1000.0) (($items | Measure-Object -Property FarmCalls -Sum).Sum)
            FarmHandled = ($items | Measure-Object -Property FarmHandled -Sum).Sum
            FarmFallbacks = ($items | Measure-Object -Property FarmFallbacks -Sum).Sum
            FarmHandleRate = Divide-OrZero (($items | Measure-Object -Property FarmHandled -Sum).Sum) (($items | Measure-Object -Property FarmCalls -Sum).Sum)
            FarmIndexBuilds = ($items | Measure-Object -Property FarmIndexBuilds -Sum).Sum
            FarmPathCalls = ($items | Measure-Object -Property FarmPathCalls -Sum).Sum
            FarmIndexBuildMs = ($items | Measure-Object -Property FarmIndexBuildMs -Sum).Sum
            FarmIndexBuildMsPerSecond = Divide-OrZero (($items | Measure-Object -Property FarmIndexBuildMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            FarmIndexBuildAvgMs = Divide-OrZero (($items | Measure-Object -Property FarmIndexBuildMs -Sum).Sum) (($items | Measure-Object -Property FarmIndexBuilds -Sum).Sum)
            FarmDynamicRefreshMs = ($items | Measure-Object -Property FarmDynamicRefreshMs -Sum).Sum
            FarmDynamicRefreshMsPerSecond = Divide-OrZero (($items | Measure-Object -Property FarmDynamicRefreshMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            FarmDynamicRefreshAvgMs = Divide-OrZero (($items | Measure-Object -Property FarmDynamicRefreshMs -Sum).Sum) (($items | Measure-Object -Property FarmDynamicRefreshes -Sum).Sum)
            FarmReadyQueries = ($items | Measure-Object -Property FarmReadyQueries -Sum).Sum
            FarmLiveRejects = ($items | Measure-Object -Property FarmLiveRejects -Sum).Sum
            FarmBitClears = ($items | Measure-Object -Property FarmBitClears -Sum).Sum
            InRangeMsPerSecond = Divide-OrZero (($items | Measure-Object -Property InRangeMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            NavCallsPerSecond = Divide-OrZero (($items | Measure-Object -Property NavCalls -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            NavMs = ($items | Measure-Object -Property NavMs -Sum).Sum
            NavMsPerSecond = Divide-OrZero (($items | Measure-Object -Property NavMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            NavAvgUs = Divide-OrZero (($items | Measure-Object -Property NavMs -Sum).Sum * 1000.0) (($items | Measure-Object -Property NavCalls -Sum).Sum)
            NavMaxMs = ($items | Measure-Object -Property NavMaxMs -Maximum).Maximum
            NavTerrainMs = ($items | Measure-Object -Property NavTerrainMs -Sum).Sum
            NavTerrainMsPerSecond = Divide-OrZero (($items | Measure-Object -Property NavTerrainMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            NavRoadMs = ($items | Measure-Object -Property NavRoadMs -Sum).Sum
            NavRoadMsPerSecond = Divide-OrZero (($items | Measure-Object -Property NavRoadMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            NavReachableMs = ($items | Measure-Object -Property NavReachableMs -Sum).Sum
            NavReachableMsPerSecond = Divide-OrZero (($items | Measure-Object -Property NavReachableMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            NavPathUnlimitedMs = ($items | Measure-Object -Property NavPathUnlimitedMs -Sum).Sum
            NavPathUnlimitedMsPerSecond = Divide-OrZero (($items | Measure-Object -Property NavPathUnlimitedMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
            YielderTerrainPathMs = ($items | Measure-Object -Property YielderTerrainPathMs -Sum).Sum
            YielderTerrainPathMsPerSecond = Divide-OrZero (($items | Measure-Object -Property YielderTerrainPathMs -Sum).Sum) (($items | Measure-Object -Property SampleSeconds -Sum).Sum)
        }
    } |
    Sort-Object Mode

Write-Host ''
Write-Host 'Summary by mode:'
$summary | Format-Table -AutoSize

if (-not [string]::IsNullOrWhiteSpace($CsvPath)) {
    $resolvedCsvPath = $CsvPath
    if (-not [System.IO.Path]::IsPathRooted($resolvedCsvPath)) {
        $resolvedCsvPath = Join-Path (Get-Location) $resolvedCsvPath
    }

    $filtered | Export-Csv -LiteralPath $resolvedCsvPath -NoTypeInformation -Encoding UTF8
    Write-Host ''
    Write-Host "CSV written: $resolvedCsvPath"
}
