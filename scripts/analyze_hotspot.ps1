[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $LogPath,
    # Skip early warmup windows (window index < MinWindow).
    [int] $MinWindow = 2,
    [int] $Top = 30
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LogPath)) {
    throw "Log not found: $LogPath"
}

$headerPattern = '^\[T3MP\] Hotspot window (?<win>\d+) \((?<secs>[\d.]+)s realtime\): componentTickTotal=(?<total>[\d.]+)ms calls=(?<calls>\d+)'
$rowPattern = '^\[T3MP\] Hotspot \| (?<type>\S+) totalMs=(?<ms>[\d.]+) share=(?<share>[\d.]+)% calls=(?<calls>\d+) avgUs=(?<avg>[\d.]+)'

$currentWindow = 0
$windowSeconds = 0.0
$grandTotalMs = 0.0
$usedWindows = New-Object System.Collections.Generic.List[int]
$byType = @{}

Get-Content -LiteralPath $LogPath | ForEach-Object {
    $line = $_
    $m = [regex]::Match($line, $headerPattern)
    if ($m.Success) {
        $currentWindow = [int]$m.Groups['win'].Value
        if ($currentWindow -ge $MinWindow) {
            $usedWindows.Add($currentWindow)
            $windowSeconds += [double]$m.Groups['secs'].Value
            $grandTotalMs += [double]$m.Groups['total'].Value
        }
        return
    }

    if ($currentWindow -lt $MinWindow) { return }

    $m = [regex]::Match($line, $rowPattern)
    if ($m.Success) {
        $type = $m.Groups['type'].Value
        if (-not $byType.ContainsKey($type)) {
            $byType[$type] = [pscustomobject]@{ Type = $type; TotalMs = 0.0; Calls = [long]0 }
        }
        $slot = $byType[$type]
        $slot.TotalMs += [double]$m.Groups['ms'].Value
        $slot.Calls += [long]$m.Groups['calls'].Value
    }
}

if ($usedWindows.Count -eq 0) {
    throw "No hotspot windows >= $MinWindow found in $LogPath"
}

Write-Host ("Windows used: {0} ({1})  realtime={2:0.0}s  componentTickTotal={3:0.0}s" -f `
    $usedWindows.Count, ($usedWindows -join ','), $windowSeconds, ($grandTotalMs / 1000.0))
Write-Host ""

$byType.Values |
    Sort-Object -Property TotalMs -Descending |
    Select-Object -First $Top |
    ForEach-Object {
        [pscustomobject]@{
            Type    = $_.Type
            TotalS  = [math]::Round($_.TotalMs / 1000.0, 2)
            Share   = [math]::Round(100.0 * $_.TotalMs / $grandTotalMs, 1)
            Calls   = $_.Calls
            AvgUs   = if ($_.Calls -gt 0) { [math]::Round($_.TotalMs * 1000.0 / $_.Calls, 2) } else { 0 }
        }
    } |
    Format-Table -AutoSize
