[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $LogPath,
    [int] $FromAggregate = 2,
    [string] $Mode = 'Optimized'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log file was not found: $LogPath"
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$rows = @()

Get-Content -LiteralPath $LogPath | ForEach-Object {
    if ($_ -notmatch '\[T3MP\] SimProgress mode=([A-Za-z]+), aggregate=(\d+), sampleSeconds=([\d.]+), fullTicks=(\d+), fullTicksPerSecond=([\d.]+),.*avgTickerWorkMs=([\d.]+)') {
        return
    }

    $rows += [pscustomobject] @{
        Mode = $Matches[1]
        Aggregate = [int] $Matches[2]
        SampleSeconds = [double]::Parse($Matches[3], $invariant)
        FullTicks = [int64] $Matches[4]
        FullTicksPerSecond = [double]::Parse($Matches[5], $invariant)
        AvgTickerWorkMs = [double]::Parse($Matches[6], $invariant)
    }
}

$selected = @($rows | Where-Object { $_.Mode -eq $Mode -and $_.Aggregate -ge $FromAggregate })
if ($selected.Count -eq 0) {
    Write-Host "No SimProgress rows for mode=$Mode aggregate>=$FromAggregate in: $LogPath"
    exit 0
}

$selected | Format-Table Aggregate, SampleSeconds, FullTicks, FullTicksPerSecond, AvgTickerWorkMs -AutoSize

$totalTicks = ($selected | Measure-Object -Property FullTicks -Sum).Sum
$totalSeconds = ($selected | Measure-Object -Property SampleSeconds -Sum).Sum
$minRate = ($selected | Measure-Object -Property FullTicksPerSecond -Minimum).Minimum
$avgTicker = ($selected | Measure-Object -Property AvgTickerWorkMs -Average).Average

Write-Host ("Summary mode={0} aggregates {1}+ rows={2}" -f $Mode, $FromAggregate, $selected.Count)
Write-Host ("  weighted fullTicks/s : {0:F3}" -f ($totalTicks / $totalSeconds))
Write-Host ("  min fullTicks/s      : {0:F3}" -f $minRate)
Write-Host ("  avg tickerWorkMs     : {0:F1}" -f $avgTicker)
