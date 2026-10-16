param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$AcceptanceDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-partition-contract.ps1"
$candidate=Get-Content -LiteralPath (Join-Path $CandidateDirectory 'candidate.json') -Raw | ConvertFrom-Json
foreach($field in @('schemaVersion','version','commit','tree','sha256','package')){
    if($candidate.$field -isnot [string]){throw 'Candidate identity fields must be strings.'}
}
if($candidate.schemaVersion -cne 'candidate-preparation-v1' -or $candidate.version -cne $Version -or
    $candidate.commit -cne $Commit -or $Commit -cnotmatch '^[0-9a-f]{40}$' -or $candidate.tree -cnotmatch '^[0-9a-f]{40}$' -or
    $candidate.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $candidate.publicationAuthorized -isnot [bool] -or
    $candidate.publicationAuthorized -or $candidate.package -isnot [string] -or [string]::IsNullOrWhiteSpace($candidate.package) -or
    $candidate.package -cne [IO.Path]::GetFileName($candidate.package)){throw 'Invalid immutable candidate.'}
if((Get-FileHash -LiteralPath (Join-Path $CandidateDirectory $candidate.package)).Hash.ToLowerInvariant() -cne $candidate.sha256){
    throw 'Candidate bytes differ.'
}
$paths=@(Get-ChildItem -LiteralPath $AcceptanceDirectory -Filter acceptance.json -File -Recurse)
if($paths.Count -ne 2){throw 'Exactly two platform acceptance receipts required.'}
$platforms=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($path in $paths){
    $receipt=Get-Content -LiteralPath $path.FullName -Raw | ConvertFrom-Json
    Assert-ReceiptFields $receipt @('schemaVersion','outcome','version','commit','tree','packageSha256','operatingSystem','sdk',
        'configurations','partitionCoverage','callableCollectorCapability','workflowRun','workflowAttempt','publicationAuthorized')
    foreach($field in @('schemaVersion','outcome','version','commit','tree','packageSha256','operatingSystem','sdk',
        'callableCollectorCapability','workflowRun','workflowAttempt')){
        if($receipt.$field -isnot [string]){throw 'Platform identity fields must be strings.'}
    }
    if($receipt.schemaVersion -cne 'candidate-acceptance-v2' -or $receipt.outcome -cne 'passed' -or
        $receipt.version -cne $Version -or $receipt.commit -cne $Commit -or $receipt.tree -cne $candidate.tree -or
        $receipt.packageSha256 -cne $candidate.sha256 -or $receipt.operatingSystem -cnotin @('Windows','Linux') -or
        !$platforms.Add($receipt.operatingSystem) -or $receipt.sdk -cne (Get-SuiteAcceptanceSdkVersion) -or
        $receipt.workflowRun -cne $WorkflowRun -or
        $receipt.callableCollectorCapability -cne 'passed' -or $receipt.publicationAuthorized -isnot [bool] -or
        $receipt.publicationAuthorized){throw 'Invalid platform acceptance identity or outcomes.'}
    Assert-SuiteAttemptAtOrBefore $receipt.workflowAttempt $WorkflowAttempt
    $configurations=@($receipt.configurations)
    if($configurations.Count -ne 2 -or 'Debug' -cnotin $configurations -or 'Release' -cnotin $configurations){throw 'Both configurations required.'}
    $coverages=@($receipt.partitionCoverage)
    if($coverages.Count -ne 2){throw 'Both complete partition unions required.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($coverage in $coverages){
        Assert-SuitePartitionCoverage $coverage
        $identity=$coverage.identity
        if(!$seen.Add($identity.configuration)){throw 'Duplicate accepted configuration.'}
        foreach($field in @('commit','tree','sdk','operatingSystem','workflowRun','packageSha256')){
            if($identity.$field -cne $receipt.$field){throw 'Partition identity does not bind platform/candidate/workflow.'}
        }
        Assert-SuiteAttemptAtOrBefore $identity.workflowAttempt $receipt.workflowAttempt
        if($coverage.shardCount -ne 16){throw 'Expected complete sixteen-shard union.'}
    }
    if(!$seen.SetEquals([string[]]@('Debug','Release'))){throw 'Both Debug and Release required.'}
}
if(!$platforms.SetEquals([string[]]@('Windows','Linux'))){throw 'Both platforms required.'}
Write-Output 'Both platforms accepted the same immutable package and complete Debug/Release partition unions.'
