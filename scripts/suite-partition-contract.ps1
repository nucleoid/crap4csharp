. "$PSScriptRoot/suite-receipt-contract.ps1"

function Test-SuiteAllowedPlatformSkip($Case,[string]$OperatingSystem){
    if($OperatingSystem -cne 'Windows'){return $false}
    # These ten existing cases explicitly require Linux utilities/special files
    # or Windows file-symlink privileges. Process cleanup cases are never allowed.
    $allowed=@(
        'Crap4CSharp.Tests.CompilerInputBindingTests.BoundReadsRefuseUnsupportedInputs(defect: "symlink-source")',
        'Crap4CSharp.Tests.CompilerOutputBindingTests.UnsupportedOutputFilesRefuse(defect: "symlink")',
        'Crap4CSharp.Tests.CompilerReferenceSnapshotTests.SymlinkReferenceRefusesWithoutFollowingItsTarget',
        'Crap4CSharp.Tests.SavedBundleFootprintTests.LinkedFileCannotBecomeAProvenBundleFootprint',
        'Crap4CSharp.Tests.LinuxSessionUtilityTests.OwnedSessionResolvesUtilitiesFromEffectiveChildPath',
        'Crap4CSharp.Tests.LinuxSessionUtilityTests.MissingRequiredSessionUtilityFailsBeforeConsumerLaunch(missingShell: False)',
        'Crap4CSharp.Tests.LinuxSessionUtilityTests.MissingRequiredSessionUtilityFailsBeforeConsumerLaunch(missingShell: True)',
        'Crap4CSharp.Tests.TestExecutionPlanTests.SavedRootRefusesSpecialFilesAndReparseComponentsBeforeAnyOpening(kind: "fifo")',
        'Crap4CSharp.Tests.TestExecutionPlanTests.SavedRootRefusesSpecialFilesAndReparseComponentsBeforeAnyOpening(kind: "file-link")',
        'Crap4CSharp.Tests.TestExecutionPlanTests.SavedRootRefusesSpecialFilesAndReparseComponentsBeforeAnyOpening(kind: "parent-link")'
    )
    return $Case.DisplayName -cin $allowed
}

function Get-SuitePartitionPlan([object[]]$Cases,[int]$ShardCount){
    if($ShardCount -lt 1 -or $ShardCount -gt 64 -or !$Cases.Count){throw 'Invalid partition size or empty discovery.'}
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $buckets=@(0..($ShardCount-1) | ForEach-Object {
        [pscustomobject]@{index=$_;methods=[Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal);
            cases=[Collections.Generic.List[object]]::new()}
    })
    foreach($case in $Cases){
        foreach($field in @('ID','DisplayName','Class','Method')){
            if($case.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($case.$field)){throw 'Incomplete case discovery.'}
        }
        if(!$ids.Add($case.ID) -or !$names.Add($case.DisplayName)){throw 'Ambiguous case identity.'}
        $method=$case.Class+'.'+$case.Method
        # Validate a discovered runner filter token, never parse C# with this pattern.
        # Escaped/nested names require an independently verified filter contract.
        if($method -cnotmatch '^[A-Za-z_][A-Za-z0-9_.]*$'){throw 'Unsupported exact runner filter identity.'}
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($method)))
        $index=[Convert]::ToUInt32($hash.Substring(0,8),16)%$ShardCount
        [void]$buckets[$index].methods.Add($method)
        $buckets[$index].cases.Add($case)
    }
    foreach($bucket in $buckets){
        if(!$bucket.cases.Count){throw 'Empty partition refused.'}
        $filter=($bucket.methods | ForEach-Object {'FullyQualifiedName='+$_}) -join '|'
        if($filter.Length -gt 20000){throw 'Runner filter exceeds verified size bound.'}
        [pscustomobject]@{index=$bucket.index;filter=$filter;cases=@($bucket.cases)}
    }
}

function Get-SuiteCaseDigest($Case){
    foreach($field in @('ID','DisplayName')){
        if($Case.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($Case.$field) -or $Case.$field.Contains([char]0)){
            throw 'Invalid complete case identity.'
        }
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($Case.ID+[char]0+$Case.DisplayName))).ToLowerInvariant()
}

function Get-SuiteCaseSetDigest([string[]]$Digests){
    if(!$Digests.Count){throw 'Empty complete case set.'}
    $ordered=[string[]]$Digests.Clone();[Array]::Sort($ordered,[StringComparer]::Ordinal)
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($digest in $ordered){if($digest -cnotmatch '^[0-9a-f]{64}$' -or !$seen.Add($digest)){throw 'Invalid or duplicate case digest.'}}
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes(($ordered -join "`n")))).ToLowerInvariant()
}

function Assert-SuitePartitionCoverage($Value){
    Assert-ReceiptFields $Value @('schemaVersion','outcome','identity','shardCount','caseCount','passed','skipped',
        'processControlsRequired','caseSetSha256','receiptSetSha256')
    Assert-SuiteObservedIdentity $Value.identity
    foreach($field in @('shardCount','caseCount','passed','skipped')){Assert-ReceiptInteger $Value.$field}
    if($Value.schemaVersion -cne 'suite-partition-coverage-v1' -or $Value.outcome -cne 'passed' -or
        $Value.shardCount -lt 1 -or $Value.shardCount -gt 64 -or $Value.caseCount -lt $Value.shardCount -or
        $Value.passed -le 0 -or $Value.caseCount -ne $Value.passed+$Value.skipped -or
        $Value.processControlsRequired -isnot [bool] -or !$Value.processControlsRequired -or
        ($Value.identity.operatingSystem -ceq 'Linux' -and $Value.skipped -ne 0) -or
        ($Value.identity.operatingSystem -ceq 'Windows' -and $Value.skipped -gt 10) -or
        $Value.caseSetSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Value.receiptSetSha256 -cnotmatch '^[0-9a-f]{64}$'){
        throw 'Invalid complete partition coverage.'
    }
}
