[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $TimberbornExe,
    [Parameter(Mandatory)][string] $SourceSave,
    [Parameter(Mandatory)][string] $ModDll,
    [Parameter(Mandatory)][string] $DriverDll,
    [Parameter(Mandatory)][string] $SaveRoot,
    [switch] $Baseline,
    [switch] $ProfileOnly,
    [switch] $Validate,
    [switch] $Smoke,
    [switch] $VisibleWindow,
    [switch] $ConstructionProfile,
    [switch] $EntityCreationProfile,
    [switch] $LazyConstructionStages,
    [switch] $LazyConstructionStagesValidate,
    [switch] $ConstructionStageBoundsValidate,
    [switch] $ResourceProfile,
    [switch] $SingletonProfile,
    [switch] $ServiceWorkProfile,
    [switch] $ServiceTerrain,
    [switch] $ServiceTerrainValidate,
    [switch] $ServiceAtlas,
    [switch] $ServiceAtlasValidate,
    [switch] $ServiceTerrainBaseline,
    [switch] $ServiceAtlasBaseline,
    [switch] $SteamLoadBaseline,
    [switch] $LoadEventProfile,
    [switch] $NavNotificationProfile,
    [switch] $EmptyFlowBaseline,
    [switch] $EmptyFlowValidate,
    [switch] $EventSubscriptionProfile,
    [switch] $EventRegistrationPlans,
    [switch] $EventRegistrationValidate,
    [switch] $WaterColumnIndex,
    [switch] $WaterColumnValidate,
    [switch] $LayeredObstacleIndex,
    [switch] $LayeredObstacleValidate,
    [switch] $BlockEventRouting,
    [switch] $BlockEventValidate,
    [switch] $BlockRoutingBaseline,
    [switch] $ProductionBlockValidate,
    [switch] $LoadMemoryBundle,
    [switch] $LazyBuildingPreview,
    [switch] $LazyBuildingPreviewValidate,
    [switch] $BuildingPreviewGuardValidate,
    [switch] $LazyPlantablePreview,
    [switch] $LazyPlantablePreviewValidate,
    [switch] $NavReplay,
    [switch] $NavLive,
    [switch] $NavLiveValidate,
    [switch] $NavInitialBuild,
    [switch] $InitialNavBaseline,
    [switch] $InitialNavSequential,
    [switch] $InitialNavParallel,
    [switch] $InitialNavValidate,
    [switch] $NavPackedSource,
    [switch] $NavInitialGraph,
    [switch] $NavRemovalView,
    [switch] $NavRemovalValidate,
    [switch] $ConstructionPlan,
    [switch] $ConstructionBaseline,
    [switch] $ProductionRecipesValidate,
    [switch] $ConstructionPlanValidate,
    [switch] $ConstructionInlineChecks,
    [switch] $ConstructionRecipes,
    [switch] $ConstructionRecipesValidate,
    [switch] $ConstructionBoundArguments,
    [switch] $ConstructionBoundArgumentsValidate,
    [switch] $ComponentRecipes,
    [switch] $ComponentRecipesValidate,
    [switch] $AdapterTemplates,
    [switch] $AdapterTemplatesValidate,
    [switch] $DeferredUpdateAdapters,
    [switch] $DeferredUpdateAdaptersValidate,
    [switch] $ConstructionLinks,
    [switch] $InitializationProfile,
    [switch] $InitializationDetailProfile,
    [switch] $InitializationMemoryProfile,
    [switch] $RangedEvents,
    [switch] $RangedEventsValidate,
    [switch] $StatusSpriteCache,
    [switch] $StatusSpriteCacheValidate,
    [switch] $StatusFinalState,
    [switch] $StatusIconStaging,
    [switch] $StatusIconBaseline,
    [switch] $StatusStateSnapshot,
    [switch] $LoadGcBatch,
    [switch] $LoadGcDisabled,
    [switch] $LoadGcHeadroom,
    [switch] $LoadGcBaseline,
    [switch] $LoadGcBudgetValidate,
    [switch] $TransputRouting,
    [switch] $TransputRoutingValidate,
    [switch] $ModelLayout,
    [switch] $ModelLayoutValidate,
    [switch] $ModelPrehide,
    [switch] $NaturalVisualPreparation,
    [switch] $BuildingVisualPreparation,
    [switch] $VisualPreparationBaseline,
    [switch] $ConstructionPrehide,
    [switch] $ModelVisualSnapshot,
    [switch] $ConstructionVisualSnapshot,
    [switch] $ModelVisualSeed,
    [switch] $FullStateSnapshot,
    [switch] $SelectionSnapshot,
    [switch] $MemoryCensus,
    [switch] $ModelVariantCensus,
    [switch] $LazyPathVariants,
    [switch] $LazyPathVariantsValidate,
    [switch] $LazyTubeVariants,
    [switch] $LazyTubeVariantsValidate,
    [switch] $TubeLightingSnapshot,
    [switch] $PathBoundsValidate,
    [switch] $MeshCapacity,
    [switch] $MeshSnapshot,
    [switch] $MeshCapacityValidate,
    [switch] $PrefabProfile,
    [switch] $ModelInputProfile,
    [switch] $AsyncCloneProbe,
    [switch] $ModelVisibilityProfile,
    [switch] $ModelWorkProfile,
    [switch] $VisibilityWrites,
    [switch] $VisibilityValidate,
    [switch] $AllModelVisualSnapshot,
    [switch] $NaturalModelTransition,
    [switch] $NaturalModelValidate,
    [switch] $LazyGoodStack,
    [switch] $LazyGoodStackValidate,
    [switch] $GoodStackExercise,
    [switch] $CarriedPreparation,
    [switch] $CarriedMetadata,
    [switch] $NavShapePlans,
    [switch] $NavShapeValidate,
    [switch] $TransputIncremental,
    [switch] $LayeredOccupierProbe,
    [switch] $InitializationBaseline,
    [switch] $InitGuardsValidate,
    [ValidateSet('LaunchArgs', 'MenuLoad', 'NewGame')][string] $Scenario = 'LaunchArgs',
    [string] $WorkshopRoot = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\1062090',
    [int] $TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
if ($ConstructionBoundArguments -or $ConstructionBoundArgumentsValidate) { $ConstructionRecipes = $true }
if ($LoadMemoryBundle) {
    $RangedEvents = $true
    $NavRemovalView = $true
    $BlockEventRouting = $true
}
if (Get-Process Timberborn -ErrorAction SilentlyContinue) {
    throw 'A Timberborn process is already running; leave it untouched.'
}
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$runName = 't3mp-load-' + [guid]::NewGuid().ToString('N')
$output = Join-Path $repo ('testlogs\' + $runName)
$saves = (Resolve-Path -LiteralPath $SaveRoot).Path
$stage = Join-Path $saves $runName
$mods = Join-Path $env:USERPROFILE 'Documents\Timberborn\Mods'
$driver = Join-Path $mods $runName
$targetDll = Join-Path $mods 'T3MP\Code.dll'
$game = (Resolve-Path -LiteralPath $TimberbornExe).Path
$source = (Resolve-Path -LiteralPath $SourceSave).Path
$modInput = (Resolve-Path -LiteralPath $ModDll).Path
$driverInput = (Resolve-Path -LiteralPath $DriverDll).Path
if (-not (Test-Path -LiteralPath $targetDll)) { throw "Missing installed T3MP DLL: $targetDll" }
$targetDlls = @($targetDll)
# Timberborn may prefer Workshop over the local mod with the same Id. Back up
# and temporarily replace every installed T3MP DLL, then verify the loaded MVID.
# Manifests and Workshop identity files are never changed.
if (Test-Path -LiteralPath $WorkshopRoot) {
    foreach ($manifest in Get-ChildItem -LiteralPath $WorkshopRoot -Filter manifest.json -Recurse) {
        if ((Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json).Id -eq 'T3MP') {
            $installed = Join-Path $manifest.DirectoryName 'Code.dll'
            if (-not (Test-Path -LiteralPath $installed)) { throw "Missing Workshop DLL: $installed" }
            $targetDlls += $installed
        }
    }
}
$expectedMvid = [System.Reflection.Assembly]::LoadFile($modInput).ManifestModule.ModuleVersionId.ToString()
foreach ($manifest in Get-ChildItem -LiteralPath $mods -Filter manifest.json -Recurse) {
    if ((Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json).Id -eq 'T3MPTestDriver') {
        throw 'An existing T3MPTestDriver is installed; do not load two copies.'
    }
}
$log = Join-Path $output 'game.log'
$process = $null
$replaced = @()
New-Item -ItemType Directory -Path $output | Out-Null
$sourceHash = (Get-FileHash -LiteralPath $source).Hash
try {
    New-Item -ItemType Directory -Path $stage, $driver | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'test.timber')
    for ($i = 0; $i -lt $targetDlls.Count; $i++) {
        $target = $targetDlls[$i]
        $backup = Join-Path $output "original-Code-$i.dll"
        $hash = (Get-FileHash -LiteralPath $target).Hash
        Copy-Item -LiteralPath $target -Destination $backup
        $replaced += [PSCustomObject]@{ Target = $target; Backup = $backup; Hash = $hash }
        Copy-Item -LiteralPath $modInput -Destination $target -Force
    }
    $replaced | Export-Clixml -LiteralPath (Join-Path $output 'restoration.xml')
    Copy-Item -LiteralPath $driverInput -Destination (Join-Path $driver 'Code.dll')
    Copy-Item -LiteralPath (Join-Path $repo 'testmod\manifest.json') -Destination $driver
    Write-Output "Output: $output"
    Write-Output "Game: $game"
    Write-Output "SourceSHA256: $sourceHash"
    Write-Output "ModSHA256: $((Get-FileHash -LiteralPath $modInput).Hash)"
    Write-Output "ExpectedModMvid: $expectedMvid"
    $arguments = @('-skipModManager', '-t3mpTestLoadRouting', '-t3mpTestExpectedModMvid', $expectedMvid,
        '-logFile', ('"' + $log + '"'))
    if ($Baseline) { $arguments += '-t3mpTestLoadBaseline' }
    if ($ProfileOnly) { $arguments += '-t3mpTestProfileOnly' }
    if ($ServiceWorkProfile) { $arguments += '-t3mpTestServiceWorkProfile' }
    if ($ServiceTerrain) { $arguments += '-t3mpTestServiceTerrain' }
    if ($ServiceTerrainValidate) { $arguments += '-t3mpTestServiceTerrainValidate' }
    if ($ServiceAtlas) { $arguments += '-t3mpTestServiceAtlas' }
    if ($ServiceAtlasValidate) { $arguments += '-t3mpTestServiceAtlasValidate' }
    if ($ServiceTerrainBaseline) { $arguments += '-t3mpTestServiceTerrainBaseline' }
    if ($ServiceAtlasBaseline) { $arguments += '-t3mpTestServiceAtlasBaseline' }
    if ($SteamLoadBaseline) { $arguments += '-t3mpTestSteamLoadBaseline' }
    if ($Validate) { $arguments += '-t3mpTestLoadValidate' }
    if ($Smoke) { $arguments += '-t3mpTestLoadSmoke' }
    if ($ConstructionProfile) { $arguments += '-t3mpTestConstructionProfile' }
    if ($EntityCreationProfile) { $arguments += '-t3mpTestEntityCreationProfile' }
    if ($LazyConstructionStages -or $LazyConstructionStagesValidate) { $arguments += '-t3mpTestLazyConstructionStages' }
    if ($LazyConstructionStagesValidate) { $arguments += '-t3mpTestLazyConstructionStagesValidate' }
    if ($ConstructionStageBoundsValidate) { $arguments += '-t3mpTestConstructionStageBoundsValidate' }
    if ($ResourceProfile -or $SingletonProfile -or $LoadEventProfile -or $EventSubscriptionProfile -or $LoadGcBatch -or $LoadGcDisabled -or $LoadGcHeadroom) { $arguments += '-t3mpTestResourceProfile' }
    if ($SingletonProfile -or $LoadEventProfile -or $EventSubscriptionProfile) { $arguments += '-t3mpTestSingletonProfile' }
    if ($LoadEventProfile) { $arguments += '-t3mpTestLoadEventProfile' }
    if ($NavNotificationProfile) { $arguments += @('-t3mpTestNavNotificationProfile','-t3mpTestResourceProfile','-t3mpTestSingletonProfile') }
    if ($EmptyFlowBaseline) { $arguments += '-t3mpTestEmptyFlowBaseline' }
    if ($EmptyFlowValidate) { $arguments += '-t3mpTestEmptyFlowValidate' }
    if ($EventSubscriptionProfile) { $arguments += '-t3mpTestEventSubscriptionProfile' }
    if ($EventRegistrationPlans) { $arguments += '-t3mpTestEventRegistrationPlans' }
    if ($EventRegistrationValidate) { $arguments += '-t3mpTestEventRegistrationValidate' }
    if ($WaterColumnIndex -or $WaterColumnValidate) { $arguments += '-t3mpTestWaterColumnIndex' }
    if ($WaterColumnValidate) { $arguments += '-t3mpTestWaterColumnValidate' }
    if ($LayeredObstacleIndex -or $LayeredObstacleValidate) { $arguments += '-t3mpTestLayeredObstacleIndex' }
    if ($LayeredObstacleValidate) { $arguments += '-t3mpTestLayeredObstacleValidate' }
    if ($BlockEventRouting -or $BlockEventValidate) { $arguments += '-t3mpTestBlockEventRouting' }
    if ($BlockEventValidate) { $arguments += '-t3mpTestBlockEventValidate' }
    if ($BlockRoutingBaseline) { $arguments += '-t3mpTestBlockRoutingBaseline' }
    if ($ProductionBlockValidate) { $arguments += '-t3mpTestProductionBlockValidate' }
    if ($LazyBuildingPreview -or $LazyBuildingPreviewValidate) { $arguments += '-t3mpTestLazyBuildingPreview' }
    if ($LazyBuildingPreviewValidate) { $arguments += '-t3mpTestLazyBuildingPreviewValidate' }
    if ($BuildingPreviewGuardValidate) { $arguments += '-t3mpTestBuildingPreviewGuardValidate' }
    if ($LazyPlantablePreview -or $LazyPlantablePreviewValidate) { $arguments += '-t3mpTestLazyPlantablePreview' }
    if ($LazyPlantablePreviewValidate) { $arguments += '-t3mpTestLazyPlantablePreviewValidate' }
    if ($NavReplay) { $arguments += '-t3mpTestNavReplay' }
    if ($NavLive -or $NavLiveValidate -or $NavInitialBuild -or $NavPackedSource -or $NavInitialGraph) { $arguments += '-t3mpTestNavLive' }
    if ($NavInitialGraph) { $arguments += '-t3mpTestNavInitialGraph' }
    if ($NavRemovalView -or $NavRemovalValidate) { $arguments += '-t3mpTestNavRemovalView' }
    if ($NavRemovalValidate) { $arguments += '-t3mpTestNavRemovalValidate' }
    if ($InitializationMemoryProfile) { $arguments += '-t3mpTestInitializationMemoryProfile' }
    if ($RangedEvents -or $RangedEventsValidate) { $arguments += '-t3mpTestRangedEvents' }
    if ($RangedEventsValidate) { $arguments += '-t3mpTestRangedEventsValidate' }
    if ($NavPackedSource) { $arguments += '-t3mpTestNavPackedSource' }
    if ($NavInitialBuild) { $arguments += '-t3mpTestNavInitialBuild' }
    if ($NavLiveValidate) { $arguments += '-t3mpTestNavLiveValidate' }
    if ($ConstructionPlan -or $ConstructionPlanValidate -or $ConstructionLinks -or $ConstructionInlineChecks -or $ConstructionRecipes -or $ConstructionRecipesValidate) { $arguments += '-t3mpTestConstructionPlan' }
    if ($ConstructionInlineChecks) { $arguments += '-t3mpTestConstructionInlineChecks' }
    if ($ConstructionRecipes -or $ConstructionRecipesValidate) { $arguments += '-t3mpTestConstructionRecipes' }
    if ($ConstructionRecipesValidate) { $arguments += '-t3mpTestConstructionRecipesValidate' }
    if ($ConstructionBoundArguments -or $ConstructionBoundArgumentsValidate) { $arguments += '-t3mpTestConstructionBoundArguments' }
    if ($ConstructionBoundArgumentsValidate) { $arguments += '-t3mpTestConstructionBoundArgumentsValidate' }
    if ($ComponentRecipes -or $ComponentRecipesValidate) { $arguments += '-t3mpTestComponentRecipes' }
    if ($ComponentRecipesValidate) { $arguments += '-t3mpTestComponentRecipesValidate' }
    if ($AdapterTemplates -or $AdapterTemplatesValidate) { $arguments += '-t3mpTestAdapterTemplates' }
    if ($AdapterTemplatesValidate) { $arguments += '-t3mpTestAdapterTemplatesValidate' }
    if ($DeferredUpdateAdapters -or $DeferredUpdateAdaptersValidate) { $arguments += '-t3mpTestDeferredUpdateAdapters' }
    if ($DeferredUpdateAdaptersValidate) { $arguments += '-t3mpTestDeferredUpdateAdaptersValidate' }
    if ($ConstructionLinks) { $arguments += '-t3mpTestConstructionLinks' }
    if ($ConstructionPlanValidate) { $arguments += '-t3mpTestConstructionPlanValidate' }
    if ($InitializationProfile -or $InitializationDetailProfile) { $arguments += '-t3mpTestInitializationProfile' }
    if ($InitializationDetailProfile) { $arguments += '-t3mpTestInitializationDetailProfile' }
    if ($StatusSpriteCache -or $StatusSpriteCacheValidate) { $arguments += '-t3mpTestStatusSpriteCache' }
    if ($StatusSpriteCacheValidate) { $arguments += '-t3mpTestStatusSpriteCacheValidate' }
    if ($StatusFinalState) { $arguments += '-t3mpTestStatusFinalState' }
    if ($StatusIconStaging) { $arguments += '-t3mpTestStatusIconStaging' }
    if ($StatusIconBaseline) { $arguments += '-t3mpTestStatusIconBaseline' }
    if ($StatusStateSnapshot) { $arguments += '-t3mpTestStatusStateSnapshot' }
    if ($LoadGcBatch) { $arguments += '-t3mpTestLoadGcBatch' }
    if ($LoadGcDisabled -or $LoadGcHeadroom) { $arguments += '-t3mpTestLoadGcDisabled' }
    if ($LoadGcHeadroom) { $arguments += '-t3mpTestLoadGcHeadroom' }
    if ($LoadGcBaseline) { $arguments += '-t3mpTestLoadGcBaseline' }
    if ($LoadGcBudgetValidate) { $arguments += '-t3mpTestLoadGcBudgetValidate' }
    if ($ConstructionBaseline) { $arguments += '-t3mpTestConstructionBaseline' }
    if ($ProductionRecipesValidate) { $arguments += '-t3mpTestProductionRecipesValidate' }
    if ($InitialNavBaseline) { $arguments += '-t3mpTestInitialNavBaseline' }
    if ($InitialNavSequential) { $arguments += '-t3mpTestInitialNavSequential' }
    if ($InitialNavParallel) { $arguments += '-t3mpTestInitialNavParallel' }
    if ($InitialNavValidate) { $arguments += '-t3mpTestInitialNavValidate' }
    if ($TransputRouting -or $TransputRoutingValidate) { $arguments += '-t3mpTestTransputRouting' }
    if ($TransputRoutingValidate) { $arguments += '-t3mpTestTransputRoutingValidate' }
    if ($ModelLayout -or $ModelLayoutValidate -or $ModelPrehide -or $ConstructionPrehide -or $ModelVisualSeed -or $NaturalVisualPreparation -or $BuildingVisualPreparation) { $arguments += '-t3mpTestModelLayout' }
    if ($ModelLayoutValidate) { $arguments += '-t3mpTestModelLayoutValidate' }
    if ($ModelPrehide) { $arguments += '-t3mpTestModelPrehide' }
    if ($NaturalVisualPreparation) { $arguments += '-t3mpTestNaturalVisualPreparation' }
    if ($BuildingVisualPreparation) { $arguments += '-t3mpTestBuildingVisualPreparation' }
    if ($VisualPreparationBaseline) { $arguments += '-t3mpTestVisualPreparationBaseline' }
    if ($ConstructionPrehide) { $arguments += '-t3mpTestConstructionPrehide' }
    if ($ModelVisualSnapshot) { $arguments += '-t3mpTestModelVisualSnapshot' }
    if ($ConstructionVisualSnapshot) { $arguments += '-t3mpTestConstructionVisualSnapshot' }
    if ($ModelVisualSeed) { $arguments += '-t3mpTestModelVisualSeed' }
    if ($FullStateSnapshot) { $arguments += '-t3mpTestFullStateSnapshot' }
    if ($SelectionSnapshot) { $arguments += '-t3mpTestSelectionSnapshot' }
    if ($MemoryCensus) { $arguments += '-t3mpTestMemoryCensus' }
    if ($ModelVariantCensus) { $arguments += '-t3mpTestModelVariantCensus' }
    if ($LazyPathVariants -or $LazyPathVariantsValidate -or $PathBoundsValidate) { $arguments += '-t3mpTestLazyPathVariants' }
    if ($LazyPathVariantsValidate) { $arguments += '-t3mpTestLazyPathVariantsValidate' }
    if ($LazyTubeVariants -or $LazyTubeVariantsValidate) { $arguments += '-t3mpTestLazyTubeVariants' }
    if ($LazyTubeVariantsValidate) { $arguments += '-t3mpTestLazyTubeVariantsValidate' }
    if ($TubeLightingSnapshot) { $arguments += '-t3mpTestTubeLightingSnapshot' }
    if ($PathBoundsValidate) { $arguments += '-t3mpTestPathBoundsValidate' }
    if ($MeshCapacity -or $MeshCapacityValidate) { $arguments += '-t3mpTestMeshCapacity' }
    if ($MeshCapacityValidate) { $arguments += '-t3mpTestMeshCapacityValidate' }
    if ($MeshSnapshot) { $arguments += '-t3mpTestMeshSnapshot' }
    if ($PrefabProfile) { $arguments += '-t3mpTestPrefabProfile' }
    if ($ModelInputProfile) { $arguments += '-t3mpTestModelInputProfile' }
    if ($AsyncCloneProbe) { $arguments += '-t3mpTestAsyncCloneProbe' }
    if ($ModelVisibilityProfile) { $arguments += '-t3mpTestModelVisibilityProfile' }
    if ($ModelWorkProfile) { $arguments += '-t3mpTestModelWorkProfile' }
    if ($VisibilityWrites -or $VisibilityValidate) { $arguments += '-t3mpTestVisibilityWrites' }
    if ($VisibilityValidate) { $arguments += '-t3mpTestVisibilityValidate' }
    if ($AllModelVisualSnapshot) { $arguments += '-t3mpTestAllModelVisualSnapshot' }
    if ($NaturalModelTransition -or $NaturalModelValidate) { $arguments += '-t3mpTestNaturalModelTransition' }
    if ($NaturalModelValidate) { $arguments += '-t3mpTestNaturalModelValidate' }
    if ($LazyGoodStack -or $LazyGoodStackValidate) { $arguments += '-t3mpTestLazyGoodStack' }
    if ($LazyGoodStackValidate) { $arguments += '-t3mpTestLazyGoodStackValidate' }
    if ($GoodStackExercise) { $arguments += '-t3mpTestGoodStackExercise' }
    if ($CarriedPreparation) { $arguments += '-t3mpTestCarriedPreparation' }
    if ($CarriedMetadata) { $arguments += '-t3mpTestCarriedMetadata' }
    if ($NavShapePlans) { $arguments += '-t3mpTestNavShapePlans' }
    if ($NavShapeValidate) { $arguments += '-t3mpTestNavShapeValidate' }
    if ($TransputIncremental) { $arguments += '-t3mpTestTransputIncremental' }
    if ($LayeredOccupierProbe) { $arguments += '-t3mpTestLayeredOccupierProbe' }
    if ($InitializationBaseline) { $arguments += @('-t3mpTestTransputBaseline','-t3mpTestNavShapeBaseline','-t3mpTestLazyGoodStackBaseline') }
    if ($InitGuardsValidate) { $arguments += '-t3mpTestInitGuardsValidate' }
    switch ($Scenario) {
        'LaunchArgs' { $arguments += @('-settlementName', $runName, '-saveName', 'test') }
        'MenuLoad' { $arguments += @('-t3mpTestMenuLoad', '-t3mpTestSettlement', $runName, '-t3mpTestSave', 'test') }
        'NewGame' { $arguments += @('-t3mpTestNewGame', '-t3mpTestMap', '_Mini', '-t3mpTestNewSettlement', $runName) }
    }
    $testWindowStyle = if ($VisibleWindow) { 'Normal' } else { 'Hidden' }
    $process = Start-Process -FilePath $game -WorkingDirectory (Split-Path -Parent $game) `
        -ArgumentList $arguments -WindowStyle $testWindowStyle -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Seconds 2
        if (Test-Path -LiteralPath $log) {
            $content = Get-Content -LiteralPath $log -Raw
            if ($content -match '\[T3MPLOAD\] ERROR|First uncaught exception') { throw "Diagnostic failed: $log" }
            if ($content -match '\[T3MPLOAD\] COMPLETE' -and $content -match 'Load time:.*scene index: 2') {
                $content -split "`n" | Where-Object { $_ -match '\[T3MPLOAD\]|Starting game version|Load event routing|Load time:.*scene index: 2|LoadStage SingletonSystem.SingletonLifecycleService.PostLoadSingletons' } | Write-Output
                if ($content -match 'First uncaught exception|Failed to patch|Failed to load asset bundle|Travel distance cache disabled|Load event routing disabled|compatibility check failed') {
                    throw "Scenario completed with errors; inspect $log"
                }
                if ($VisibleWindow) { Start-Sleep -Seconds 10 }
                return
            }
        }
        $process.Refresh()
    }
    throw "Diagnostic did not complete: $log"
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit()
    }
    foreach ($original in $replaced) {
        Copy-Item -LiteralPath $original.Backup -Destination $original.Target -Force
        if ((Get-FileHash -LiteralPath $original.Target).Hash -ne $original.Hash) { throw 'Installed DLL restoration failed' }
    }
    foreach ($item in @(@($stage, $saves), @($driver, $mods))) {
        if (-not (Test-Path -LiteralPath $item[0])) { continue }
        $resolved = (Resolve-Path -LiteralPath $item[0]).Path
        $parent = (Resolve-Path -LiteralPath $item[1]).Path.TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -ne $runName) { throw 'Unsafe temporary-directory cleanup path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    if ((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash) { throw 'Source save changed' }
    Write-Output 'Restored all installed DLLs; removed test driver and copied settlement; source save unchanged.'
}
