# Product purity gate (docs/DIAGNOSTICS.md). Throws when diagnostic code,
# diagnostic switches or experiment sources would reach the shipped product.
# The source checks always run; -Dll scans a built Code.dll; -Package checks a
# package or deployed mod folder. Runs on Windows PowerShell 5.1 and pwsh 7.
# deploy.ps1, verify_release.ps1 and measure.ps1 call it; run it alone after
# editing anything under src/T3MP or src/Shared.
[CmdletBinding()]
param(
    [string]$Dll = '',
    [string]$Package = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$failures = New-Object System.Collections.Generic.List[string]

# Allowlists. Adding an entry is a reviewed decision recorded in docs/DIAGNOSTICS.md;
# the legacy switch list only shrinks.
$allowedSymbols = @('MOVEMENT_SUBSTEPS')
$featureOff = '^-t3mpTest(No[A-Z]\w*|\w*Baseline)$'
$legacySwitches = @(
    '-t3mpTestProductionBlockValidate', '-t3mpTestInitialNavParallel', '-t3mpTestInitialNavSequential',
    '-t3mpTestNavShapeValidate', '-t3mpTestNavShapePlans', '-t3mpTestNavRemovalView', '-t3mpTestBlockEventRouting',
    '-t3mpTestCarriedPreparation', '-t3mpTestCarriedMetadata', '-t3mpTestNaturalVisualPreparation',
    '-t3mpTestBuildingVisualPreparation', '-t3mpTestLazyGoodStack', '-t3mpTestTransputRouting',
    '-t3mpTestTransputIncremental', '-t3mpTestTransputRoutingValidate'
)
# A bare BCL name such as System.Threading.Monitor is not a diagnostic type: a prefix is required.
$forbiddenNames = '.(Probe|Profiler|Benchmark|(?<!Re)Validation|Validator|Replay|Monitor|Experiment|Audit)$'
$driverMarks = 'T3MPTestDriver|\[T3MPTEST\]|T3MPTEST\.|InternalsVisibleTo'

function Test-Switch([string]$Name) { return ($Name -match $featureOff) -or ($legacySwitches -ccontains $Name) }

# 1. Product sources.
$sourceRoots = @((Join-Path $repo 'src\T3MP'), (Join-Path $repo 'src\Shared'))
$sources = @(Get-ChildItem -LiteralPath $sourceRoots -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
foreach ($file in $sources) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $rel = $file.FullName.Substring($repo.Length + 1)
    foreach ($m in [regex]::Matches($text, '(?m)^\s*#(?:if|elif)\s+(.+)$')) {
        foreach ($sym in [regex]::Matches($m.Groups[1].Value, '[A-Za-z_]\w*')) {
            if ($sym.Value -notin @('true', 'false') -and $allowedSymbols -cnotcontains $sym.Value) { $failures.Add("$rel : preprocessor symbol $($sym.Value)") }
        }
    }
    foreach ($m in [regex]::Matches($text, '-t3mpTest[A-Za-z0-9]+')) {
        if (-not (Test-Switch $m.Value)) { $failures.Add("$rel : diagnostic switch $($m.Value)") }
    }
    foreach ($m in [regex]::Matches($text, '\b(?:class|struct|interface|enum)\s+(\w+)')) {
        if ($m.Groups[1].Value -match $forbiddenNames) { $failures.Add("$rel : diagnostic type name $($m.Groups[1].Value)") }
    }
    if ($text -match $driverMarks) { $failures.Add("$rel : refers to the test driver") }
}

# 2. Product project file.
$projectPath = Join-Path $repo 'src\T3MP\T3MP.csproj'
$project = [regex]::Replace((Get-Content -LiteralPath $projectPath -Raw), '<!--.*?-->', '', 'Singleline')
if ($project -match 'experiments[/\\]|tests[/\\]|T3MPTestDriver|ProjectReference|benchmark[/\\]') { $failures.Add('T3MP.csproj : compiles or references non-product sources') }
if ($project -match '\$\(\w*Experiment\)') { $failures.Add('T3MP.csproj : experiment build property') }
foreach ($m in [regex]::Matches($project, '<DefineConstants>([^<]*)</DefineConstants>')) {
    foreach ($entry in ($m.Groups[1].Value -split ';')) {
        $sym = $entry.Trim()
        if ($sym -and $sym -ne '$(DefineConstants)' -and $allowedSymbols -cnotcontains $sym) { $failures.Add("T3MP.csproj : preprocessor symbol $sym") }
    }
}

# 3. Shipping folder sources.
$allowedMod = @('manifest.json', 'README.md', 'thumbnail.jpg')
$modEntries = @(Get-ChildItem -LiteralPath (Join-Path $repo 'mod') -Force)
if ($modEntries.Count -ne $allowedMod.Count -or @($modEntries | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $allowedMod }).Count) { $failures.Add('mod/ : must contain exactly manifest.json, README.md and thumbnail.jpg') }
$manifest = Get-Content -LiteralPath (Join-Path $repo 'mod\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$settings = Get-Content -LiteralPath (Join-Path $repo 'src\T3MP\ModSettings.cs') -Raw
if ($settings -notmatch ('public const string Version = "' + [regex]::Escape($manifest.Version) + '";')) { $failures.Add('mod/manifest.json : version differs from ModSettings.Version') }

# 4. Built assembly: metadata strings (UTF-8 heap) and user strings (UTF-16 heap).
$dllSummary = 'not scanned'
if ($Dll) {
    $Dll = (Resolve-Path -LiteralPath $Dll).Path
    $bytes = [IO.File]::ReadAllBytes($Dll)
    $views = @(
        [Text.Encoding]::GetEncoding(28591).GetString($bytes),
        [Text.Encoding]::Unicode.GetString($bytes),
        [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    )
    foreach ($view in $views) {
        foreach ($m in [regex]::Matches($view, '-t3mpTest[A-Za-z0-9]+')) {
            if (-not (Test-Switch $m.Value)) { $failures.Add("Code.dll : diagnostic switch $($m.Value)") }
        }
        foreach ($m in [regex]::Matches($view, $driverMarks)) { $failures.Add("Code.dll : driver reference $($m.Value)") }
        if ($view -match 'experiments[/\\]') { $failures.Add('Code.dll : experiment path') }
    }
    foreach ($m in [regex]::Matches($views[0], '\x00([A-Za-z_]\w*)(?=\x00)')) {
        if ($m.Groups[1].Value -match $forbiddenNames) { $failures.Add("Code.dll : diagnostic identifier $($m.Groups[1].Value)") }
    }
    $dllSummary = (Get-FileHash -LiteralPath $Dll).Hash.Substring(0, 8) + ' (' + $bytes.Length + ' bytes)'
}

# 5. Package or deployed folder.
$packageSummary = 'not checked'
if ($Package) {
    $Package = (Resolve-Path -LiteralPath $Package).Path
    $allowedPackage = @('manifest.json', 'README.md', 'thumbnail.jpg', 'Code.dll', 'workshop_data.json')
    $entries = @(Get-ChildItem -LiteralPath $Package -Force)
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer) { $failures.Add("package : folder $($entry.Name) (asset bundles and driver folders never ship)") }
        elseif ($entry.Name -cnotin $allowedPackage) { $failures.Add("package : unexpected file $($entry.Name)") }
    }
    foreach ($required in @('manifest.json', 'Code.dll')) { if (-not (Test-Path -LiteralPath (Join-Path $Package $required) -PathType Leaf)) { $failures.Add("package : missing $required") } }
    $packageSummary = ($entries | ForEach-Object { $_.Name }) -join ','
}

if ($failures.Count) {
    $failures | ForEach-Object { Write-Output ('FAIL ' + $_) }
    throw ('Product purity gate failed: ' + $failures.Count + ' finding(s)')
}
Write-Output ('Product purity: PASS (' + $sources.Count + ' product sources; DLL ' + $dllSummary + '; package ' + $packageSummary + ')')
