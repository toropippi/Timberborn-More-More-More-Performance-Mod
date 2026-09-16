param([Parameter(Mandatory)][string]$CodeDll, [Parameter(Mandatory)][string[]]$ManagedDirectories)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'TickDispatch.csproj'
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
foreach ($managed in $ManagedDirectories) {
    foreach ($mode in @('equivalence','frontier','before','before-base','after-enabled','after-tick','after-loop','during-enabled','during-tick','during-base-enabled','getter-hook','getter-skip','install-failure','unknown')) {
        dotnet run --project $project -c Release --no-build -- $CodeDll $managed $mode
        if ($LASTEXITCODE -ne 0) { throw "Failed: $managed / $mode" }
    }
}
