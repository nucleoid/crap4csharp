param(
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt,
    [Parameter(Mandatory)][string]$DiscoveryPath,
    [string]$PackagePath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$suiteIdentityStage='workflowIdentity'
trap {
    [ordered]@{schemaVersion='suite-identity-v1';outcome='failed';configuration=$Configuration;
        reason='testResults.sourceOrAssemblyIdentityUnavailable'} | ConvertTo-Json | Set-Content -LiteralPath $output
    Write-Warning ('Suite identity refused at stage: '+$suiteIdentityStage)
    throw 'Suite identity unavailable; sanitized refusal retained.'
}
if($Commit -cnotmatch '^[0-9a-f]{40}$' -or $WorkflowRun -cnotmatch '^[0-9]+$' -or
    $WorkflowAttempt -cnotmatch '^[1-9][0-9]*$'){throw 'Invalid expected workflow identity.'}
$suiteIdentityStage='sourceHead'
$head=(& git --no-optional-locks rev-parse HEAD 2>$null)
if($LASTEXITCODE -ne 0 -or $head -cne $Commit){throw 'Unexpected source head.'}
$suiteIdentityStage='sourceTree'
$tree=(& git --no-optional-locks rev-parse 'HEAD^{tree}' 2>$null)
if($LASTEXITCODE -ne 0 -or $tree -cnotmatch '^[0-9a-f]{40}$'){throw 'Source tree unavailable.'}
$suiteIdentityStage='sourceClean'
$status=@(& git --no-optional-locks status --porcelain --untracked-files=all 2>$null)
if($LASTEXITCODE -ne 0 -or $status.Count){throw 'Source worktree is not clean.'}
$suiteIdentityStage='sdk'
$sdk=(& dotnet --version 2>$null)
if($LASTEXITCODE -ne 0 -or $sdk -cne (Get-SuiteAcceptanceSdkVersion)){throw 'Unexpected SDK.'}
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
# Raw byte inventories supplement the trusted CI checkout/build. A stat-cached
# git status alone cannot detect same-length edits with preserved timestamps.
$suiteIdentityStage='trackedBytes'
$worktreeHash=Get-SuiteTrackedWorktreeHash (Get-Location).ProviderPath
$root=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath("tests/Crap4CSharp.Tests/bin/$Configuration/net10.0")
$files=@('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json',
    'process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')
$hashes=[ordered]@{}
$suiteIdentityStage='requiredRuntimeFiles'
foreach($relative in $files){$hashes[$relative]=(Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant()}
$suiteIdentityStage='runtimeInventory'
$runtimeEntries=@(Get-ChildItem -LiteralPath $root -Force -Recurse)
if(@($runtimeEntries | Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){throw 'Runtime inventory reparse entry refused.'}
$runtimePaths=@($runtimeEntries | Where-Object {!$_.PSIsContainer} | ForEach-Object {[IO.Path]::GetRelativePath([IO.Path]::GetFullPath($root),$_.FullName)})
$runtimeHash=Get-InventoryHash $root $runtimePaths
$suiteIdentityStage='packageBytes'
$packageHash=if($PackagePath){(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null}
$suiteIdentityStage='discoveryBytes'
$discoveryHash=(Get-FileHash -LiteralPath $DiscoveryPath -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{schemaVersion='suite-identity-v1';outcome='observed';configuration=$Configuration;
    commit=$head;tree=$tree;sdk=$sdk;operatingSystem=$(if($IsWindows){'Windows'}elseif($IsLinux){'Linux'}else{throw 'Unsupported acceptance OS.'});
    workflowRun=$WorkflowRun;workflowAttempt=$WorkflowAttempt;packageSha256=$packageHash;discoverySha256=$discoveryHash;
    worktreeSha256=$worktreeHash;runtimeInventorySha256=$runtimeHash;files=$hashes} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output
