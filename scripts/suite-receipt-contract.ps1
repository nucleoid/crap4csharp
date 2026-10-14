. "$PSScriptRoot/sdk-acceptance-contract.ps1"

function Assert-ReceiptFields($Value,[string[]]$Names){
    if($null -eq $Value){throw 'Receipt object missing.'}
    $allowed=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($name in $Names){[void]$allowed.Add($name)}
    $properties=@($Value.PSObject.Properties)
    if($properties.Count -ne $allowed.Count){throw 'Receipt fields incomplete or unexpected.'}
    foreach($property in $properties){if(!$allowed.Contains($property.Name)){throw 'Unexpected receipt field.'}}
}
function Assert-ReceiptInteger($Value){
    if(($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt 0){throw 'Nonnegative integer required.'}
}
function Assert-SuiteAttemptAtOrBefore($Observed,$Current){
    foreach($attempt in @($Observed,$Current)){
        if($attempt -isnot [string] -or $attempt -cnotmatch '^[1-9][0-9]*$'){
            throw 'Workflow attempt must be a positive decimal string.'
        }
    }
    if([Numerics.BigInteger]::Parse($Observed) -gt [Numerics.BigInteger]::Parse($Current)){
        throw 'Receipt belongs to a future workflow attempt.'
    }
}
function Assert-SuiteObservedIdentity($Value){
    Assert-ReceiptFields $Value @('schemaVersion','outcome','configuration','commit','tree','sdk','operatingSystem',
        'workflowRun','workflowAttempt','packageSha256','discoverySha256','worktreeSha256','runtimeInventorySha256','files')
    foreach($name in @('schemaVersion','outcome','configuration','commit','tree','sdk','operatingSystem',
        'workflowRun','workflowAttempt','discoverySha256','worktreeSha256','runtimeInventorySha256')){
        if($Value.$name -isnot [string]){throw 'Identity field must be a string.'}
    }
    if($null -ne $Value.packageSha256 -and $Value.packageSha256 -isnot [string]){throw 'Package identity must be a string.'}
    if($Value.schemaVersion -cne 'suite-identity-v1' -or $Value.outcome -cne 'observed' -or
        $Value.configuration -cnotin @('Debug','Release') -or $Value.operatingSystem -cnotin @('Windows','Linux') -or
        $Value.commit -cnotmatch '^[0-9a-f]{40}$' -or $Value.tree -cnotmatch '^[0-9a-f]{40}$' -or
        $Value.sdk -cne (Get-SuiteAcceptanceSdkVersion) -or $Value.workflowRun -cnotmatch '^[0-9]+$' -or
        $Value.workflowAttempt -cnotmatch '^[1-9][0-9]*$' -or
        ($null -ne $Value.packageSha256 -and $Value.packageSha256 -cnotmatch '^[0-9a-f]{64}$')){throw 'Invalid observed identity.'}
    foreach($name in @('discoverySha256','worktreeSha256','runtimeInventorySha256')){
        if($Value.$name -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid inventory hash.'}
    }
    $required=@('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json',
        'process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')
    Assert-ReceiptFields $Value.files $required
    foreach($file in $required){if($Value.files.$file -isnot [string] -or $Value.files.$file -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid required assembly identity.'}}
}
function Assert-SuiteIdentityBinding($Value){
    Assert-ReceiptFields $Value @('schemaVersion','outcome','identity','beforeSha256','afterSha256')
    if($Value.schemaVersion -cne 'suite-identity-binding-v1' -or $Value.outcome -cne 'passed' -or
        $Value.beforeSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Value.afterSha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid identity binding.'}
    Assert-SuiteObservedIdentity $Value.identity
}
function Assert-SuiteTerminalReceipt($Value){
    Assert-ReceiptFields $Value @('schemaVersion','configuration','exitCode','outcome','counts','trxSha256')
    Assert-ReceiptInteger $Value.exitCode
    if($Value.schemaVersion -cne 'suite-receipt-v1' -or $Value.configuration -cnotin @('Debug','Release') -or
        $Value.outcome -cne 'passed' -or $Value.exitCode -ne 0 -or $Value.trxSha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid suite terminal receipt.'}
    $names=@('total','executed','passed','failed','error','timeout','aborted','inconclusive','notExecuted','notRunnable','skipped')
    Assert-ReceiptFields $Value.counts $names
    foreach($name in $names){Assert-ReceiptInteger $Value.counts.$name}
    $counts=$Value.counts
    if($counts.passed -le 0 -or $counts.failed -ne 0 -or $counts.error -ne 0 -or $counts.timeout -ne 0 -or
        $counts.aborted -ne 0 -or $counts.inconclusive -ne 0 -or $counts.notRunnable -ne 0 -or
        $counts.executed -ne $counts.passed -or $counts.total -ne $counts.executed+$counts.skipped -or
        $counts.notExecuted -gt $counts.skipped){throw 'Invalid suite counts.'}
}
function Assert-SuiteCaseReceipt($Value){
    Assert-ReceiptFields $Value @('schemaVersion','configuration','exitCode','outcome','caseCount','passed','skipped',
        'processControlsRequired','discoverySha256','trxSha256')
    foreach($name in @('exitCode','caseCount','passed','skipped')){Assert-ReceiptInteger $Value.$name}
    if($Value.schemaVersion -cne 'suite-case-coverage-v1' -or $Value.configuration -cnotin @('Debug','Release') -or
        $Value.outcome -cne 'passed' -or $Value.exitCode -ne 0 -or $Value.caseCount -le 0 -or
        $Value.processControlsRequired -isnot [bool] -or !$Value.processControlsRequired -or
        $Value.discoverySha256 -cnotmatch '^[0-9a-f]{64}$' -or $Value.trxSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $Value.passed+$Value.skipped -ne $Value.caseCount){throw 'Invalid complete case receipt.'}
}
