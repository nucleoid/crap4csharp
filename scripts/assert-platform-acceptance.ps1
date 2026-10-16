param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [Parameter(Mandatory = $true)][string]$AcceptanceDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$WorkflowRun,
    [Parameter(Mandatory = $true)][string]$WorkflowAttempt
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/suite-receipt-contract.ps1"
$candidate = Get-Content -Raw -LiteralPath (Join-Path $CandidateDirectory 'candidate.json') | ConvertFrom-Json
if ($candidate.schemaVersion -ne 'candidate-preparation-v1' -or $candidate.version -cne $Version -or
    $candidate.commit -cne $Commit -or $candidate.commit -cnotmatch '^[0-9a-f]{40}$' -or
    $candidate.tree -cnotmatch '^[0-9a-f]{40}$' -or $candidate.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    [string]::IsNullOrWhiteSpace($candidate.package) -or $candidate.package -ne [IO.Path]::GetFileName($candidate.package) -or
    $candidate.publicationAuthorized -ne $false) { throw 'Invalid prepared candidate identity.' }
$package = Join-Path $CandidateDirectory $candidate.package
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -cne $candidate.sha256) { throw 'Prepared candidate package changed.' }
$files = @(Get-ChildItem -LiteralPath $AcceptanceDirectory -Recurse -Filter acceptance.json)
if ($files.Count -ne 2) { throw 'Both platform acceptance receipts are required.' }
$receipts = @($files | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json })
if (@($receipts.operatingSystem | Sort-Object -Unique).Count -ne 2 -or
    'Windows' -notin $receipts.operatingSystem -or 'Linux' -notin $receipts.operatingSystem) { throw 'Expected Windows and Linux acceptance.' }
foreach ($receipt in $receipts) {
    Assert-ReceiptFields $receipt @('schemaVersion','outcome','version','commit','tree','packageSha256','operatingSystem','sdk',
        'configurations','suites','caseCoverage','suiteIdentities','callableCollectorCapability','workflowRun','workflowAttempt','publicationAuthorized')
    if($receipt.publicationAuthorized -isnot [bool] -or $receipt.publicationAuthorized){throw 'Invalid publication authorization.'}
    $configurations=@($receipt.configurations)
    if($configurations.Count -ne 2 -or @($configurations | Sort-Object -Unique).Count -ne 2 -or
        'Debug' -cnotin $configurations -or 'Release' -cnotin $configurations){throw 'Invalid accepted configurations.'}
    if ($receipt.schemaVersion -ne 'candidate-acceptance-v1' -or $receipt.outcome -ne 'passed' -or
        $receipt.version -cne $candidate.version -or $receipt.commit -cne $candidate.commit -or
        $receipt.tree -cne $candidate.tree -or $receipt.packageSha256 -cne $candidate.sha256 -or
        $receipt.workflowRun -ne $WorkflowRun -or $receipt.workflowAttempt -ne $WorkflowAttempt -or
        $receipt.sdk -cne (Get-SuiteAcceptanceSdkVersion) -or
        $receipt.publicationAuthorized -ne $false -or $receipt.callableCollectorCapability -ne 'passed') { throw 'Platform acceptance does not bind the prepared candidate.' }
    $suites = @($receipt.suites)
    if ($suites.Count -ne 2 -or @($suites.configuration | Sort-Object -Unique).Count -ne 2 -or
        'Debug' -notin $suites.configuration -or 'Release' -notin $suites.configuration) { throw 'Both suite configurations are required.' }
    foreach ($suite in $suites) {
        Assert-SuiteTerminalReceipt $suite
        if ($suite.schemaVersion -ne 'suite-receipt-v1' -or $suite.outcome -ne 'passed' -or $suite.exitCode -ne 0 -or
            $suite.trxSha256 -cnotmatch '^[0-9a-f]{64}$' -or $suite.counts.passed -le 0 -or
            $suite.counts.failed -ne 0 -or $suite.counts.error -ne 0 -or $suite.counts.timeout -ne 0 -or
            $suite.counts.aborted -ne 0 -or $suite.counts.inconclusive -ne 0 -or $suite.counts.notRunnable -ne 0 -or
            $suite.counts.executed -ne $suite.counts.passed -or $suite.counts.skipped -lt 0 -or
            $suite.counts.total -ne $suite.counts.executed + $suite.counts.skipped) { throw 'Invalid suite acceptance evidence.' }
    }
    $coverage = @($receipt.caseCoverage)
    if ($coverage.Count -ne 2 -or @($coverage.configuration | Sort-Object -Unique).Count -ne 2 -or
        'Debug' -notin $coverage.configuration -or 'Release' -notin $coverage.configuration) { throw 'Both complete case inventories are required.' }
    foreach ($cases in $coverage) {
        Assert-SuiteCaseReceipt $cases
        $suite = @($suites | Where-Object { $_.configuration -ceq $cases.configuration })
        if ($suite.Count -ne 1 -or $cases.schemaVersion -cne 'suite-case-coverage-v1' -or
            $cases.outcome -cne 'passed' -or $cases.exitCode -ne 0 -or
            $cases.processControlsRequired -ne $true -or
            $cases.discoverySha256 -cnotmatch '^[0-9a-f]{64}$' -or $cases.trxSha256 -cne $suite[0].trxSha256 -or
            $cases.caseCount -ne $suite[0].counts.total -or $cases.passed -ne $suite[0].counts.passed -or
            $cases.skipped -ne $suite[0].counts.skipped) { throw 'Complete case coverage does not bind the accepted suite.' }
    }
    $bindings=@($receipt.suiteIdentities)
    if($bindings.Count -ne 2 -or @($bindings.identity.configuration | Sort-Object -Unique).Count -ne 2 -or
        'Debug' -cnotin $bindings.identity.configuration -or 'Release' -cnotin $bindings.identity.configuration){throw 'Both suite identity bindings required.'}
    foreach($binding in $bindings){
        Assert-SuiteIdentityBinding $binding
        $identity=$binding.identity
        foreach($field in @('commit','tree','sdk','operatingSystem','workflowRun','workflowAttempt','packageSha256')){
            if($identity.$field -cne $receipt.$field){throw 'Suite identity differs from candidate/platform/workflow.'}
        }
        $matched=@($coverage | Where-Object {$_.configuration -ceq $identity.configuration})
        if($matched.Count -ne 1 -or $matched[0].discoverySha256 -cne $identity.discoverySha256){throw 'Suite discovery identity differs.'}
    }
}
Write-Output 'Windows and Linux accepted the exact prepared package, commit and tree in Debug and Release.'
