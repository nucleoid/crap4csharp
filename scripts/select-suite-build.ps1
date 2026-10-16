param(
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][ValidateSet('windows-latest','ubuntu-latest')][string]$RunnerOS,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt,
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-receipt-contract.ps1"
$inputRoot=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InputDirectory)
$outputRoot=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
Assert-SuiteAttemptAtOrBefore $WorkflowAttempt $WorkflowAttempt
if($WorkflowRun -cnotmatch '^[0-9]+$'){throw 'Invalid workflow run.'}
$prefix="suite-build-$Configuration-$RunnerOS-attempt-"
$pattern='^'+[regex]::Escape($prefix)+'([1-9][0-9]*)$'
$latest=$null
foreach($directory in Get-ChildItem -LiteralPath $inputRoot -Directory){
    if(!$directory.Name.StartsWith($prefix,[StringComparison]::Ordinal)){continue}
    if(($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $directory.Name -cnotmatch $pattern){throw 'Invalid build artifact directory.'}
    $attempt=$Matches[1]
    Assert-SuiteAttemptAtOrBefore $attempt $WorkflowAttempt
    if($null -eq $latest -or [Numerics.BigInteger]::Parse($attempt) -gt [Numerics.BigInteger]::Parse($latest.attempt)){
        $latest=[pscustomobject]@{attempt=$attempt;directory=$directory}
    }
}
if($null -eq $latest){throw 'Required shared build artifact missing.'}
$entries=@(Get-ChildItem -LiteralPath $latest.directory.FullName -Force)
if($entries.Count -ne 3 -or @($entries|Where-Object {$_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){
    throw 'Unexpected shared build contents.'
}
foreach($name in @('build.json','identity.json','runtime.zip')){
    if($name -cnotin $entries.Name){throw 'Shared build artifact incomplete.'}
}
$manifest=Get-Content -LiteralPath (Join-Path $latest.directory.FullName 'build.json') -Raw|ConvertFrom-Json
if($manifest.identity.workflowRun -cne $WorkflowRun -or $manifest.identity.workflowAttempt -cne $latest.attempt -or
    $manifest.identity.configuration -cne $Configuration){throw 'Build artifact envelope differs.'}
if(Test-Path -LiteralPath $outputRoot){throw 'Selected build output directory must be new.'}
[void][IO.Directory]::CreateDirectory($outputRoot)
foreach($entry in $entries){Copy-Item -LiteralPath $entry.FullName -Destination $outputRoot}
