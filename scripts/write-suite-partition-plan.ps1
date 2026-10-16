param(
    [Parameter(Mandatory)][string]$DiscoveryPath,
    [Parameter(Mandatory)][ValidateRange(1,64)][int]$ShardCount,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-partition-contract.ps1"
$cases=@(Get-Content -LiteralPath $DiscoveryPath -Raw | ConvertFrom-Json)
$partitions=@(Get-SuitePartitionPlan $cases $ShardCount)
$digests=@($cases | ForEach-Object {Get-SuiteCaseDigest $_})
$caseSet=Get-SuiteCaseSetDigest $digests
# This plan is private runner input. Upload the sanitized plan summary, never
# this file's discovery names or filters as failure diagnostics.
$plan=[ordered]@{schemaVersion='suite-partition-plan-v1';shardCount=$ShardCount;caseCount=$cases.Count;
    discoverySha256=(Get-FileHash -LiteralPath $DiscoveryPath).Hash.ToLowerInvariant();caseSetSha256=$caseSet;
    partitions=@($partitions | ForEach-Object {
        [ordered]@{index=$_.index;filter=$_.filter;caseDigests=@($_.cases | ForEach-Object {Get-SuiteCaseDigest $_})}
    })}
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$plan | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $output
[ordered]@{schemaVersion='suite-partition-plan-summary-v1';shardCount=$ShardCount;caseCount=$cases.Count;
    discoverySha256=$plan.discoverySha256;caseSetSha256=$caseSet;
    planSha256=(Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant()} | ConvertTo-Json
