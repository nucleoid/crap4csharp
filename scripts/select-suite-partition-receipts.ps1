param(
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][ValidateSet('windows-latest','ubuntu-latest')][string]$RunnerOS,
    [Parameter(Mandatory)][ValidateRange(1,64)][int]$ShardCount,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt,
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-receipt-contract.ps1"
Assert-SuiteAttemptAtOrBefore $WorkflowAttempt $WorkflowAttempt
if($WorkflowRun -cnotmatch '^[0-9]+$'){throw 'Invalid workflow run.'}
$inputRoot=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InputDirectory)
$outputRoot=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if(Test-Path -LiteralPath $outputRoot){throw 'Selected receipt directory must be new.'}
$prefix="partition-$Configuration-$RunnerOS-"
$pattern='^'+[regex]::Escape($prefix)+'(0|[1-9][0-9]*)-attempt-([1-9][0-9]*)$'
$selected=@{}
foreach($directory in Get-ChildItem -LiteralPath $inputRoot -Directory){
    if(!$directory.Name.StartsWith($prefix,[StringComparison]::Ordinal)){continue}
    if(($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $directory.Name -cnotmatch $pattern){
        throw 'Invalid partition artifact directory.'
    }
    $index=[Numerics.BigInteger]::Parse($Matches[1]);$attempt=$Matches[2]
    if($index -ge $ShardCount){throw 'Unexpected partition index.'}
    Assert-SuiteAttemptAtOrBefore $attempt $WorkflowAttempt
    $entries=@(Get-ChildItem -LiteralPath $directory.FullName -Force)
    $expected="$Configuration-$index.partition.receipt.json"
    if($entries.Count -ne 1 -or $entries[0].PSIsContainer -or $entries[0].Name -cne $expected -or
        ($entries[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected partition artifact contents.'}
    $receipt=Get-Content -LiteralPath $entries[0].FullName -Raw | ConvertFrom-Json
    if($receipt.outcome -ceq 'passed'){
        $identity=$receipt.identityBinding.identity
        if($identity.workflowRun -cne $WorkflowRun -or $identity.workflowAttempt -cne $attempt){
            throw 'Artifact attempt/run does not bind its receipt.'
        }
    }
    $key=[int]$index
    if(!$selected.ContainsKey($key) -or [Numerics.BigInteger]::Parse($attempt) -gt
        [Numerics.BigInteger]::Parse($selected[$key].attempt)){
        $selected[$key]=[pscustomobject]@{attempt=$attempt;file=$entries[0]}
    }
}
if($selected.Count -ne $ShardCount){throw 'Latest complete partition set is missing.'}
[void][IO.Directory]::CreateDirectory($outputRoot)
foreach($index in 0..($ShardCount-1)){
    # Select latest regardless of outcome. Never hide a newer failure behind an
    # older pass. The complete-union verifier still validates all selected fields.
    Copy-Item -LiteralPath $selected[$index].file.FullName -Destination (Join-Path $outputRoot "$Configuration-$index.partition.receipt.json")
}
