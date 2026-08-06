[CmdletBinding()]
param(
    [string] $UnityEditor = '',
    [string] $TimberbornInstall = 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'shaderbuild'
$generated = Join-Path $project 'Assets\OfficialShaders'
$output = Join-Path $repoRoot 'mod\AssetBundles'
$shaderZip = Join-Path $TimberbornInstall 'Timberborn_Data\StreamingAssets\Modding\Shaders.zip'

# Keep shaderbuild/Packages/manifest.json aligned with Mechanistry's official
# Unity project. A URP-only manifest produces bundles that Unity 6000.5.2f1 can
# build, but Timberborn rejects at runtime as an incompatible older bundle.

if ([string]::IsNullOrWhiteSpace($UnityEditor)) {
    $editorCandidates = @(
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
        (Join-Path $env:LOCALAPPDATA 'Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe')
    )
    $UnityEditor = $editorCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not (Test-Path -LiteralPath $UnityEditor)) {
    throw "Unity 6000.5.2f1 Editor was not found. Pass -UnityEditor with its Unity.exe path."
}

# Unity 6000.5.2f1's Windows editor payload omits .meta files for two source
# files in the built-in Core RP package. Unity consequently ignores those
# files and its own editor assembly fails to compile. Supply stable metadata
# only when the affected payload is detected; later fixed editors are left
# untouched.
$editorDirectory = Split-Path -Parent $UnityEditor
$readonlyConverter = Join-Path $editorDirectory 'Data\Resources\PackageManager\BuiltInPackages\com.unity.render-pipelines.core\Editor-PrivateShared\Tools\Converter\ReadonlyMaterialConverter'
$metadataFixes = @{
    'ReadonlyMaterialConverter.MaterialReferenceBuilder.cs.meta' = 'a7c130d9ed7546d3ab8566ad070787bf'
    'ReadonlyMaterialConverter.MaterialReferenceChanger.cs.meta' = 'b8f241eafe8657e4bc9677be181898c0'
}
foreach ($entry in $metadataFixes.GetEnumerator()) {
    $metaPath = Join-Path $readonlyConverter $entry.Key
    $sourcePath = $metaPath.Substring(0, $metaPath.Length - '.meta'.Length)
    # The full built-in-package path exceeds legacy MAX_PATH on Windows.
    $longSourcePath = '\\?\' + $sourcePath
    $longMetaPath = '\\?\' + $metaPath
    if ([IO.File]::Exists($longSourcePath) -and -not [IO.File]::Exists($longMetaPath)) {
        [IO.File]::WriteAllText($longMetaPath, "fileFormatVersion: 2`nguid: $($entry.Value)`n", [Text.UTF8Encoding]::new($false))
    }
}
if (-not (Test-Path -LiteralPath $shaderZip)) {
    throw "Timberborn official shader archive was not found: $shaderZip"
}

New-Item -ItemType Directory -Force -Path $generated | Out-Null
New-Item -ItemType Directory -Force -Path $output | Out-Null

# The official graph and its GUID-linked custom-function include are generated
# build inputs. They are deliberately sourced from the installed game so the
# mod tracks the exact Timberborn shader version instead of vendoring a stale
# copy of a large Shader Graph.
tar -xf $shaderZip -C $generated BotURP.shadergraph BotURP.shadergraph.meta AnimateVAT.hlsl AnimateVAT.hlsl.meta
if ($LASTEXITCODE -ne 0) {
    throw "Failed to extract official Timberborn shader sources."
}

$graphPath = Join-Path $generated 'BotURP.shadergraph'
$graph = [System.IO.File]::ReadAllText($graphPath)
$graph = $graph.Replace('"m_Path": "Shader Graphs"', '"m_Path": "T3MP"')
$animationProperty = '(?s)("m_Name":\s*"AnimationTime".*?"overrideHLSLDeclaration":\s*)false(,\s*"hlslDeclarationOverride":\s*)0'
$modified = [regex]::Replace($graph, $animationProperty, '${1}true${2}3', 1)
if ($modified -eq $graph) {
    throw "AnimationTime property was not found or was already modified."
}
[System.IO.File]::WriteAllText($graphPath, $modified, [System.Text.UTF8Encoding]::new($false))

$env:T3MP_SHADER_OUTPUT = $output
try {
    $buildLog = Join-Path $repoRoot 'shaderbuild\shader-bundle-build.log'
    $arguments = '-batchmode -quit -projectPath "' + $project + '" -executeMethod BuildT3MPShaderBundle.Build -logFile "' + $buildLog + '"'
    $process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $buildLog) {
            Get-Content -LiteralPath $buildLog -Tail 120
        }
        throw "Unity AssetBundle build failed with exit code $($process.ExitCode)."
    }
}
finally {
    Remove-Item Env:T3MP_SHADER_OUTPUT -ErrorAction SilentlyContinue
}

$bundle = Join-Path $output 't3mp-bot-instancing'
if (-not (Test-Path -LiteralPath $bundle)) {
    throw "Expected AssetBundle was not produced: $bundle"
}

# BuildPipeline also emits a tiny root AssetBundleManifest file named after
# the output directory. Timberborn attempts to load every non-.manifest file
# in a mod's AssetBundles folder, while this root file is metadata rather than
# a loadable content bundle. Keep only the named content bundle.
$rootManifestBundle = Join-Path $output (Split-Path -Leaf $output)
if (Test-Path -LiteralPath $rootManifestBundle) {
    Remove-Item -LiteralPath $rootManifestBundle -Force
}
Write-Host "Built shader bundle: $bundle"
