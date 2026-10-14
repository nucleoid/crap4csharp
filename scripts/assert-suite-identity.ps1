param(
    [Parameter(Mandatory)][string]$BeforePath,
    [Parameter(Mandatory)][string]$AfterPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
trap {
    [ordered]@{schemaVersion='suite-identity-binding-v1';outcome='failed';reason='testResults.identityChangedOrInvalid'} |
        ConvertTo-Json | Set-Content -LiteralPath $output
    throw 'Suite identity binding failed; sanitized refusal retained.'
}
$before=Get-Content -LiteralPath $BeforePath -Raw | ConvertFrom-Json
$after=Get-Content -LiteralPath $AfterPath -Raw | ConvertFrom-Json
. "$PSScriptRoot/suite-receipt-contract.ps1"
foreach($value in @($before,$after)){Assert-SuiteObservedIdentity $value}
foreach($field in @('configuration','operatingSystem','commit','tree','sdk','workflowRun','workflowAttempt','packageSha256','discoverySha256','worktreeSha256','runtimeInventorySha256')){
    if($before.$field -cne $after.$field){throw 'Observed identity changed.'}
}
foreach($entry in $before.files.PSObject.Properties){if($entry.Value -cne $after.files.($entry.Name)){throw 'Assembly inventory changed.'}}
[ordered]@{schemaVersion='suite-identity-binding-v1';outcome='passed';identity=$before;
    beforeSha256=(Get-FileHash -LiteralPath $BeforePath).Hash.ToLowerInvariant();
    afterSha256=(Get-FileHash -LiteralPath $AfterPath).Hash.ToLowerInvariant()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $output
