param(
    [Parameter(Mandatory)][string]$DiscoveryPath,
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][int]$ExitCode,
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [switch]$RequireProcessControls,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$written=$false
trap {
    if(!$written){
        [ordered]@{schemaVersion='suite-case-coverage-v1';configuration=$Configuration;exitCode=$ExitCode;
            outcome='failed';caseCount=$null;discoverySha256=$null;trxSha256=$null;
            processControlsRequired=[bool]$RequireProcessControls;
            reason='testResults.caseCoverageUnavailableOrInvalid'} | ConvertTo-Json | Set-Content -LiteralPath $output
    }
    throw 'Complete suite case coverage failed; sanitized receipt retained.'
}
if($ExitCode -ne 0){throw 'Nonzero suite process exit.'}
$discovery=(Resolve-Path -LiteralPath $DiscoveryPath).ProviderPath
$cases=@(Get-Content -LiteralPath $discovery -Raw | ConvertFrom-Json)
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if(!$cases.Count){throw 'Empty discovery.'}
foreach($case in $cases){
    if($case.ID -isnot [string] -or $case.DisplayName -isnot [string] -or
        [string]::IsNullOrWhiteSpace($case.ID) -or [string]::IsNullOrWhiteSpace($case.DisplayName) -or
        !$ids.Add($case.ID) -or !$expected.Add($case.DisplayName)){throw 'Missing or ambiguous discovered case identity.'}
}
$processPrefixes=@('Crap4CSharp.Tests.ProcessRunnerTests.','Crap4CSharp.Tests.ProcessRunnerGrandchildTests.',
    'Crap4CSharp.Tests.ProcessLogTests.','Crap4CSharp.Tests.ProcessProtocolTests.')
if($RequireProcessControls){
    foreach($prefix in $processPrefixes){
        if(!@($cases | Where-Object {$_.DisplayName.StartsWith($prefix,[StringComparison]::Ordinal)}).Count){
            throw 'Required process control class missing from discovery.'
        }
    }
}
$trx=(Resolve-Path -LiteralPath $TrxPath).ProviderPath
$settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
$reader=[Xml.XmlReader]::Create($trx,$settings)
try{$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)}finally{$reader.Dispose()}
$counters=@($document.SelectNodes('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]'))
if($counters.Count -ne 1){throw 'Invalid counter records.'}
$counts=@{}
foreach($name in @('total','executed','passed','failed','error','timeout','aborted','inconclusive','notExecuted','notRunnable')){
    $number=0
    if(![int]::TryParse($counters[0].GetAttribute($name),[ref]$number) -or $number -lt 0){throw 'Invalid counters.'}
    $counts[$name]=$number
}
$rows=@($document.SelectNodes('//*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
if($counts.total -ne $expected.Count -or $rows.Count -ne $counts.total -or $counts.executed -le 0 -or
    $counts.executed -ne $counts.passed -or $counts.failed -ne 0 -or $counts.error -ne 0 -or $counts.timeout -ne 0 -or
    $counts.aborted -ne 0 -or $counts.inconclusive -ne 0 -or $counts.notRunnable -ne 0){throw 'Incomplete or failed suite.'}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$resultIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$passed=0;$skipped=0
foreach($row in $rows){
    $name=$row.GetAttribute('testName');$id=$row.GetAttribute('testId');$outcome=$row.GetAttribute('outcome')
    if(!$expected.Contains($name) -or !$seen.Add($name) -or [string]::IsNullOrWhiteSpace($id) -or !$resultIds.Add($id)){
        throw 'Missing, duplicate or unexpected terminal case.'
    }
    if($RequireProcessControls -and $outcome -cne 'Passed' -and
        @($processPrefixes | Where-Object {$name.StartsWith($_,[StringComparison]::Ordinal)}).Count){
        throw 'Required process control did not pass.'
    }
    switch -CaseSensitive ($outcome){'Passed'{$passed++} 'NotExecuted'{$skipped++} default{throw 'Invalid terminal outcome.'}}
}
if(!$expected.SetEquals($seen) -or $passed -ne $counts.passed -or $skipped -ne $counts.total-$counts.executed -or
    $counts.notExecuted -gt $skipped){throw 'Discovered and terminal cases disagree.'}
# Names, arguments, diagnostics and source paths remain private. Only numeric
# totals and content hashes leave the runner; this is not package/head identity.
[ordered]@{schemaVersion='suite-case-coverage-v1';configuration=$Configuration;exitCode=$ExitCode;outcome='passed';
    caseCount=$expected.Count;passed=$passed;skipped=$skipped;
    processControlsRequired=[bool]$RequireProcessControls;
    discoverySha256=(Get-FileHash -LiteralPath $discovery).Hash.ToLowerInvariant();
    trxSha256=(Get-FileHash -LiteralPath $trx).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content -LiteralPath $output
$written=$true
