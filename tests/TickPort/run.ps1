param([Parameter(Mandatory)][string]$CodeDll, [Parameter(Mandatory)][string[]]$ManagedDirectories)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'TickPort.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
foreach ($managed in $ManagedDirectories) {
    foreach ($mode in @('equivalence', 'before', 'after', 'during', 'during-tick', 'during-tick-skip', 'warm-loop', 'frontier')) {
        dotnet run --project $project -c Release --no-build -- $CodeDll $managed $mode
        if ($LASTEXITCODE -ne 0) { throw "Failed: $managed / $mode" }
    }
}
