param(
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
if(Test-Path -LiteralPath $outputRoot){throw 'Selected acceptance directory must be new.'}
$latest=@{}
foreach($directory in Get-ChildItem -LiteralPath $inputRoot -Directory){
    if(!$directory.Name.StartsWith('acceptance-',[StringComparison]::Ordinal)){continue}
    if(($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $directory.Name -cnotmatch '^acceptance-(windows-latest|ubuntu-latest)-attempt-([1-9][0-9]*)$'){
        throw 'Invalid platform acceptance artifact directory.'
    }
    $runner=$Matches[1];$attempt=$Matches[2]
    Assert-SuiteAttemptAtOrBefore $attempt $WorkflowAttempt
    if(!$latest.ContainsKey($runner) -or [Numerics.BigInteger]::Parse($attempt) -gt
        [Numerics.BigInteger]::Parse($latest[$runner].attempt)){
        $latest[$runner]=[pscustomobject]@{attempt=$attempt;directory=$directory}
    }
}
if($latest.Count -ne 2){throw 'Both platform acceptance artifacts required.'}
# The newest artifact is authoritative even when it fails validation. Never
# search backwards for an older passing platform result.
$selected=@{}
foreach($runner in @('windows-latest','ubuntu-latest')){
    $item=$latest[$runner]
    $entries=@(Get-ChildItem -LiteralPath $item.directory.FullName -Force)
    if($entries.Count -ne 1 -or $entries[0].PSIsContainer -or $entries[0].Name -cne 'acceptance.json' -or
        ($entries[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected platform artifact contents.'}
    $receipt=Get-Content -LiteralPath $entries[0].FullName -Raw | ConvertFrom-Json
    $os=if($runner -ceq 'windows-latest'){'Windows'}else{'Linux'}
    if($receipt.workflowRun -cne $WorkflowRun -or $receipt.workflowAttempt -cne $item.attempt -or
        $receipt.operatingSystem -cne $os){throw 'Platform artifact envelope differs.'}
    $selected[$runner]=$entries[0]
}
[void][IO.Directory]::CreateDirectory($outputRoot)
foreach($runner in @('windows-latest','ubuntu-latest')){
    $destination=Join-Path $outputRoot $runner
    [void][IO.Directory]::CreateDirectory($destination)
    Copy-Item -LiteralPath $selected[$runner].FullName -Destination (Join-Path $destination 'acceptance.json')
}
