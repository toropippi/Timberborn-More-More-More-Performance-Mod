[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $LogPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log file was not found: $LogPath"
}

# Aggregates the '[T3MP] TopoUI window=...s | Name n=X tot=Yms max=Zms | ...'
# report lines: per method, the summed call count / total ms across all
# windows, the worst single call (max ms), and the busiest window total.
$stats = @{}
$windowCount = 0

foreach ($line in Get-Content -LiteralPath $LogPath) {
    if ($line -notmatch '\[T3MP\] TopoUI window=') {
        continue
    }

    $windowCount++
    $segments = $line -split '\|'
    foreach ($segment in $segments) {
        if ($segment -match '(?<name>[\w\.]+)\s+n=(?<n>\d+)\s+tot=(?<tot>[\d\.]+)ms\s+max=(?<max>[\d\.]+)ms') {
            $name = $Matches['name']
            $n = [long]$Matches['n']
            $tot = [double]$Matches['tot']
            $max = [double]$Matches['max']

            if (-not $stats.ContainsKey($name)) {
                $stats[$name] = [pscustomobject]@{
                    Method = $name
                    Calls = [long]0
                    TotalMs = [double]0
                    MaxCallMs = [double]0
                    MaxWindowMs = [double]0
                }
            }

            $entry = $stats[$name]
            $entry.Calls += $n
            $entry.TotalMs += $tot
            if ($max -gt $entry.MaxCallMs) { $entry.MaxCallMs = $max }
            if ($tot -gt $entry.MaxWindowMs) { $entry.MaxWindowMs = $tot }
        }
    }
}

Write-Host "TopoUI report windows: $windowCount"
$stats.Values |
    Sort-Object TotalMs -Descending |
    Format-Table Method,
        Calls,
        @{ Name = 'TotalMs'; Expression = { [math]::Round($_.TotalMs, 1) } },
        @{ Name = 'AvgMs'; Expression = { if ($_.Calls -gt 0) { [math]::Round($_.TotalMs / $_.Calls, 3) } else { 0 } } },
        @{ Name = 'MaxCallMs'; Expression = { [math]::Round($_.MaxCallMs, 2) } },
        @{ Name = 'MaxWindowMs'; Expression = { [math]::Round($_.MaxWindowMs, 1) } } -AutoSize
