param(
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][ValidateRange(1,64)][int]$ShardCount,
    [Parameter(Mandatory)][ValidateRange(0,63)][int]$ShardIndex,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt,
    [string]$PackagePath,
    [string]$BuildManifestPath,
    [Parameter(Mandatory)][string]$ResultsDirectory
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-partition-contract.ps1"
$results=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
if(Test-Path -LiteralPath $results){throw 'Partition results require a new owned directory.'}
[void][IO.Directory]::CreateDirectory($results)
$prefix=Join-Path $results ("$Configuration-$ShardIndex")
# This repository-owned fixture is analyzed by integration tests. A fresh shared
# bin import has no restore assets; restore only its declared dependencies, with
# no rebuild of the bound runtime and no changes to analyzed consumer fixtures.
& dotnet restore tests/Crap4CSharp.ProvenanceFixture/Crap4CSharp.ProvenanceFixture.csproj `
    --disable-parallel -nr:false *> "$prefix.fixture.restore.log"
if($LASTEXITCODE -ne 0){throw 'Repository-owned provenance fixture restore failed.'}
$assembly="tests/Crap4CSharp.Tests/bin/$Configuration/net10.0/Crap4CSharp.Tests.dll"
if($BuildManifestPath){
    & "$PSScriptRoot/write-suite-discovery.ps1" -AssemblyPath $assembly -OutputPath "$prefix.discovery.json"
}else{
    & dotnet $assembly -noColor -preEnumerateTheories -list full/json > "$prefix.discovery.json"
    if($LASTEXITCODE -ne 0){throw 'Complete theory discovery failed.'}
}
$cases=@(Get-Content -LiteralPath "$prefix.discovery.json" -Raw | ConvertFrom-Json)
$plan=@(Get-SuitePartitionPlan $cases $ShardCount)
if($ShardIndex -ge $ShardCount){throw 'Partition index out of range.'}
$identityArgs=@{Configuration=$Configuration;Commit=$Commit;WorkflowRun=$WorkflowRun;WorkflowAttempt=$WorkflowAttempt;
    DiscoveryPath="$prefix.discovery.json";PackagePath=$PackagePath}
& "$PSScriptRoot/write-suite-identity.ps1" @identityArgs -OutputPath "$prefix.before.identity.json"
if($BuildManifestPath){
    $build=(Get-Content -LiteralPath $BuildManifestPath -Raw|ConvertFrom-Json).identity
    $observed=Get-Content -LiteralPath "$prefix.before.identity.json" -Raw|ConvertFrom-Json
    Assert-SuiteObservedIdentity $build
    foreach($field in @('configuration','operatingSystem','commit','tree','sdk','workflowRun','packageSha256',
        'worktreeSha256','runtimeInventorySha256','discoverySha256')){
        if($observed.$field -cne $build.$field){throw 'Partition does not match its shared build.'}
    }
    Assert-SuiteAttemptAtOrBefore $build.workflowAttempt $WorkflowAttempt
}
# VSTest's exact method filters were verified with the pinned SDK. Running the
# assembly directly avoids an incidental restore/build in the filtered test step.
& dotnet vstest $assembly "--TestCaseFilter:$($plan[$ShardIndex].filter)" `
    "--Logger:trx;LogFileName=$Configuration-$ShardIndex.trx" "--ResultsDirectory:$results" *> "$prefix.log"
$actualExit=$LASTEXITCODE
# Separate diagnostic observation; never supplies or relaxes an acceptance receipt.
& "$PSScriptRoot/write-suite-partition-diagnostic.ps1" -DiscoveryPath "$prefix.discovery.json" -ShardCount $ShardCount `
    -ShardIndex $ShardIndex -TrxPath "$prefix.trx" -ExitCode $actualExit -OutputPath "$prefix.partition.diagnostic.json"
& "$PSScriptRoot/write-suite-identity.ps1" @identityArgs -OutputPath "$prefix.after.identity.json"
& "$PSScriptRoot/assert-suite-identity.ps1" -BeforePath "$prefix.before.identity.json" `
    -AfterPath "$prefix.after.identity.json" -OutputPath "$prefix.identity.receipt.json"
& "$PSScriptRoot/write-suite-partition-receipt.ps1" -DiscoveryPath "$prefix.discovery.json" -ShardCount $ShardCount `
    -ShardIndex $ShardIndex -TrxPath "$prefix.trx" -ExitCode $actualExit -IdentityBindingPath "$prefix.identity.receipt.json" `
    -OutputPath "$prefix.partition.receipt.json"
if($actualExit -ne 0){throw 'Partition process did not succeed.'}
