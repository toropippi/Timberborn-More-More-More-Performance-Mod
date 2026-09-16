[CmdletBinding()]
param(
    [string] $Output = 'docs/measurements/legacy-source-inventory-v1.2.1.json'
)

# Source census, not an automatic safety or reachability verdict.
# Keep historical defaults separate from evidence that a hook actually ran.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    function GitText([string[]] $Arguments) {
        $lines = @(& git @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "git failed: $($Arguments -join ' ')" }
        return $lines -join "`n"
    }
    function DescribeSource([string] $Path, [string] $Source, [string] $Identity) {
        $refs = @([regex]::Matches($Source, '\b(?:BenchmarkSettings|ModSettings)\.(\w+)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $definitions = @()
        if ($Path -match '/(?:BenchmarkSettings|ModSettings)\.cs$') {
            $definitions = @([regex]::Matches($Source,
                '(?m)^[ \t]*public\s+(?:static\s+readonly|const)\s+(bool|int|float|double|string)\s+(\w+)\s*=\s*([\s\S]*?);') |
                ForEach-Object { [ordered]@{
                    Name = $_.Groups[2].Value
                    Type = $_.Groups[1].Value
                    Expression = ($_.Groups[3].Value -replace '\s+', ' ').Trim()
                    Line = 1 + ([regex]::Matches($Source.Substring(0, $_.Index), "`n")).Count
                } })
        }
        return [ordered]@{
            Path = $Path
            Identity = $Identity
            Lines = ($Source -split "`n").Count
            SettingsReferenced = $refs
            SettingDefinitions = $definitions
        }
    }

    $snapshots = @()
    foreach ($entry in @(
        @{ Name = 'v1.1.7-source'; Ref = '4637137' },
        @{ Name = 'before-v1.2-rebuild'; Ref = 'a3e8d90^' }
    )) {
        $commit = GitText @('rev-parse', $entry.Ref)
        $paths = @(git ls-tree -r --name-only $commit -- src/T3MP src/Shared |
            Where-Object { $_ -like '*.cs' } | Sort-Object)
        if ($LASTEXITCODE -ne 0) { throw 'source tree listing failed' }
        $sources = foreach ($path in $paths) {
            $object = "${commit}:$path"
            DescribeSource $path (GitText @('show', $object)) (GitText @('rev-parse', $object))
        }
        $snapshots += [ordered]@{ Name = $entry.Name; Commit = $commit; Sources = @($sources) }
    }

    $currentPaths = @(git ls-files --cached --others --exclude-standard -- src/T3MP src/Shared |
        Where-Object { $_ -like '*.cs' -and (Test-Path -LiteralPath $_) } | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'working source listing failed' }
    $current = foreach ($path in $currentPaths) {
        DescribeSource $path (Get-Content -LiteralPath $path -Raw) ((Get-FileHash -LiteralPath $path).Hash)
    }
    $snapshots += [ordered]@{ Name = 'working-tree'; Commit = (GitText @('rev-parse', 'HEAD')); Sources = @($current) }

    $history = @(git log --all --format= --name-only -- src/T3MP src/Shared |
        Where-Object { $_ -like '*.cs' } | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'history listing failed' }
    $covered = @($snapshots | ForEach-Object { $_.Sources } | ForEach-Object { $_.Path } | Sort-Object -Unique)
    $extras = foreach ($path in $history | Where-Object { $_ -notin $covered }) {
        $commit = GitText @('log', '--all', '--diff-filter=AM', '-1', '--format=%H', '--', $path)
        if (-not $commit) { throw "No content revision for $path" }
        $object = "${commit}:$path"
        [ordered]@{ Commit = $commit; Source = (DescribeSource $path (GitText @('show', $object)) (GitText @('rev-parse', $object))) }
    }
    $allCovered = @($covered) + @($extras | ForEach-Object { $_.Source.Path })
    $missing = @($history | Where-Object { $_ -notin $allCovered })
    if ($missing.Count) { throw "Uncovered historical paths: $($missing -join ', ')" }
    # A feature can disappear inside an otherwise surviving source file.
    # Enumerate every settings revision as well as the file-path census.
    $settingRevisions = @(git log --all --diff-filter=AM --format=%H -- src/T3MP/BenchmarkSettings.cs)
    if ($LASTEXITCODE -ne 0) { throw 'settings history listing failed' }
    $settingHistory = @{}
    foreach ($commit in $settingRevisions) {
        $path = 'src/T3MP/BenchmarkSettings.cs'
        $description = DescribeSource $path (GitText @('show', "${commit}:$path")) $commit
        foreach ($definition in $description.SettingDefinitions) {
            if (-not $settingHistory.ContainsKey($definition.Name)) {
                $settingHistory[$definition.Name] = [ordered]@{ Name = $definition.Name; Type = $definition.Type; Variants = @() }
            }
            $setting = $settingHistory[$definition.Name]
            $variant = @($setting.Variants | Where-Object Expression -eq $definition.Expression)
            if ($variant.Count) {
                $variant[0].Commits += $commit
            } else {
                $setting.Variants += [ordered]@{ Expression = $definition.Expression; Commits = @($commit) }
            }
        }
    }
    $report = [ordered]@{
        Schema = 1
        Scope = 'All historical C# paths under src/T3MP and src/Shared, plus current untracked sources. Git paths are a source census, not proof that all code shipped or executed.'
        HistoricalPathCount = $history.Count
        CoveredHistoricalPathCount = @($history | Where-Object { $_ -in $allCovered }).Count
        Snapshots = $snapshots
        AdditionalHistoricalSources = @($extras)
        SettingsRevisionCount = $settingRevisions.Count
        HistoricalSettings = @($settingHistory.Values | Sort-Object { $_.Name })
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Output -Encoding utf8
    Write-Output "Covered $($history.Count) historical source paths; output: $Output"
    foreach ($snapshot in $snapshots) {
        Write-Output "$($snapshot.Name): $($snapshot.Sources.Count) files"
    }
} finally {
    Pop-Location
}
