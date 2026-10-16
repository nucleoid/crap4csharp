param(
    [Parameter(Mandatory)][string]$DiscoveryPath,
    [Parameter(Mandatory)][ValidateRange(1,64)][int]$ShardCount,
    [Parameter(Mandatory)][string]$ReceiptDirectory,
    [Parameter(Mandatory)][string]$ExpectedIdentityPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
trap{
    [ordered]@{schemaVersion='suite-partition-coverage-v1';outcome='failed';reason='testResults.partitionUnionIncompleteOrInvalid'} |
        ConvertTo-Json | Set-Content -LiteralPath $output
    throw 'Complete partition union refused; sanitized receipt retained.'
}
. "$PSScriptRoot/suite-partition-contract.ps1"
$identity=Get-Content -LiteralPath $ExpectedIdentityPath -Raw | ConvertFrom-Json
Assert-SuiteObservedIdentity $identity
$discoveryHash=(Get-FileHash -LiteralPath $DiscoveryPath).Hash.ToLowerInvariant()
if($identity.discoverySha256 -cne $discoveryHash){throw 'Expected discovery identity differs.'}
$cases=@(Get-Content -LiteralPath $DiscoveryPath -Raw | ConvertFrom-Json)
$plan=@(Get-SuitePartitionPlan $cases $ShardCount)
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$processClasses=@('Crap4CSharp.Tests.ProcessRunnerTests','Crap4CSharp.Tests.ProcessRunnerGrandchildTests',
    'Crap4CSharp.Tests.ProcessLogTests','Crap4CSharp.Tests.ProcessProtocolTests')
foreach($class in $processClasses){if(!@($cases | Where-Object {$_.Class -ceq $class}).Count){throw 'Required process class absent.'}}
$process=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$allowedSkips=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($case in $cases){$digest=Get-SuiteCaseDigest $case;[void]$expected.Add($digest);if($case.Class -cin $processClasses){[void]$process.Add($digest)}}
foreach($case in $cases){if(Test-SuiteAllowedPlatformSkip $case $identity.operatingSystem){[void]$allowedSkips.Add((Get-SuiteCaseDigest $case))}}
$files=@(Get-ChildItem -LiteralPath $ReceiptDirectory -File -Filter '*.partition.receipt.json' -Recurse)
if($files.Count -ne $ShardCount){throw 'Required partition receipt count differs.'}
$indices=[Collections.Generic.HashSet[int]]::new()
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$passed=0;$skipped=0;$receiptHashes=[Collections.Generic.List[string]]::new()
foreach($file in $files){
    $receipt=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    Assert-ReceiptFields $receipt @('schemaVersion','outcome','shardIndex','shardCount','exitCode','identityBinding','caseSetSha256','trxSha256','cases')
    foreach($field in @('shardIndex','shardCount','exitCode')){Assert-ReceiptInteger $receipt.$field}
    if($receipt.schemaVersion -cne 'suite-partition-receipt-v1' -or $receipt.outcome -cne 'passed' -or
        $receipt.exitCode -ne 0 -or $receipt.shardCount -ne $ShardCount -or $receipt.shardIndex -ge $ShardCount -or
        !$indices.Add($receipt.shardIndex) -or $receipt.trxSha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid terminal partition.'}
    Assert-SuiteIdentityBinding $receipt.identityBinding
    $observed=$receipt.identityBinding.identity
    foreach($field in @('configuration','operatingSystem','commit','tree','sdk','workflowRun',
        'packageSha256','discoverySha256','worktreeSha256','runtimeInventorySha256')){
        if($observed.$field -cne $identity.$field){throw 'Partition source/runtime/workflow identity differs.'}
    }
    Assert-SuiteAttemptAtOrBefore $observed.workflowAttempt $identity.workflowAttempt
    foreach($entry in $identity.files.PSObject.Properties){if($observed.files.($entry.Name) -cne $entry.Value){throw 'Partition assembly differs.'}}
    $terminal=@($receipt.cases)
    $assigned=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($case in $plan[$receipt.shardIndex].cases){[void]$assigned.Add((Get-SuiteCaseDigest $case))}
    if($terminal.Count -ne $assigned.Count -or $receipt.caseSetSha256 -cne (Get-SuiteCaseSetDigest @($terminal.digest))){throw 'Partition case set differs.'}
    foreach($row in $terminal){
        Assert-ReceiptFields $row @('digest','outcome')
        if(!$assigned.Contains($row.digest) -or !$expected.Contains($row.digest) -or !$seen.Add($row.digest)){throw 'Missing, overlapping or unexpected case.'}
        switch -CaseSensitive ($row.outcome){
            'Passed' {$passed++}
            'NotExecuted' {if($process.Contains($row.digest) -or !$allowedSkips.Contains($row.digest)){throw 'Unapproved or required-process skip.'};$skipped++}
            default {throw 'Case did not reach accepted terminal outcome.'}
        }
    }
    $receiptHashes.Add((Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant())
}
if(!$expected.SetEquals($seen) -or $passed -le 0){throw 'Complete discovered union absent.'}
[ordered]@{schemaVersion='suite-partition-coverage-v1';outcome='passed';identity=$identity;shardCount=$ShardCount;
    caseCount=$expected.Count;passed=$passed;skipped=$skipped;processControlsRequired=$true;
    caseSetSha256=(Get-SuiteCaseSetDigest @($expected));receiptSetSha256=(Get-SuiteCaseSetDigest @($receiptHashes))} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $output
