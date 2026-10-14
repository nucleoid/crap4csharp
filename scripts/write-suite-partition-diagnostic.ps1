param(
    [Parameter(Mandatory)][string]$DiscoveryPath,
    [Parameter(Mandatory)][ValidateRange(1,64)][int]$ShardCount,
    [Parameter(Mandatory)][ValidateRange(0,63)][int]$ShardIndex,
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][int]$ExitCode,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
# Diagnostic only. No names, arguments, stack traces, paths or raw messages leave this file.
# A failed or incomplete observation never supplies an acceptance receipt.
. "$PSScriptRoot/suite-partition-contract.ps1"
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
try {
    $cases=@(Get-Content -LiteralPath $DiscoveryPath -Raw|ConvertFrom-Json)
    $plan=@(Get-SuitePartitionPlan $cases $ShardCount)
    if($ShardIndex -ge $ShardCount){throw 'Invalid diagnostic partition'}
    $selected=@($plan[$ShardIndex].cases)
    $expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach($case in $selected){$expected.Add($case.DisplayName,$case)}
    if((Get-Item -LiteralPath $TrxPath).Length -gt 64MB){throw 'Bounded diagnostic TRX limit exceeded'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create((Resolve-Path -LiteralPath $TrxPath).ProviderPath,$settings)
    try{$doc=[Xml.XmlDocument]::new();$doc.XmlResolver=$null;$doc.Load($reader)}finally{$reader.Dispose()}
    $rows=@($doc.SelectNodes('//*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $counts=@{Passed=0;Failed=0;NotExecuted=0;Other=0}
    $failures=[Collections.Generic.List[object]]::new()
    foreach($row in $rows){
        $name=$row.GetAttribute('testName')
        if(!$expected.ContainsKey($name) -or !$seen.Add($name)){throw 'Unexpected or duplicate diagnostic case'}
        $outcome=$row.GetAttribute('outcome')
        if($counts.ContainsKey($outcome)){$counts[$outcome]++}else{$counts.Other++}
        if($outcome -cne 'Failed'){continue}
        $case=$expected[$name]
        $method=$case.Class+'.'+$case.Method
        $messageNode=$row.SelectSingleNode('*[local-name()="Output"]/*[local-name()="ErrorInfo"]/*[local-name()="Message"]')
        $message=if($null -eq $messageNode){''}else{[string]$messageNode.InnerText}
        $tokens=@(foreach($token in @('run.timedOut','run.cancelled','context.projectLoadFailed','context.inputsMutated','compiler.inputsMutated','compiler.referencesUnproven','coverage.unavailable','test.runnerUnsupported')){
            if($message.Contains($token,[StringComparison]::Ordinal)){$token}
        })
        $failures.Add([ordered]@{caseDigest=(Get-SuiteCaseDigest $case);
            methodDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($method))).ToLowerInvariant();
            outcome='Failed';observedDiagnosticTokens=$tokens})
    }
    [ordered]@{schemaVersion='suite-partition-diagnostic-v1';outcome='observed';shardIndex=$ShardIndex;exitCode=$ExitCode;
        expectedCases=$selected.Count;observedCases=$rows.Count;missingCases=$selected.Count-$rows.Count;counts=$counts;
        failures=@($failures);discoverySha256=(Get-FileHash -LiteralPath $DiscoveryPath).Hash.ToLowerInvariant();
        trxSha256=(Get-FileHash -LiteralPath $TrxPath).Hash.ToLowerInvariant();
        qualification='Diagnostic only. Tokens are observed substrings, not causal classifications. No acceptance, source/runtime identity or complete-suite claim.'}|
        ConvertTo-Json -Depth 7|Set-Content -LiteralPath $output
}catch{
    [ordered]@{schemaVersion='suite-partition-diagnostic-v1';outcome='unavailable';shardIndex=$ShardIndex;exitCode=$ExitCode;
        reason='testResults.diagnosticUnavailableOrInvalid'}|ConvertTo-Json|Set-Content -LiteralPath $output
}
