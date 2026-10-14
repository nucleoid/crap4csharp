param(
    [Parameter(Mandatory)][string]$DiscoveryPath,
    [Parameter(Mandatory)][ValidateRange(1,64)][int]$ShardCount,
    [Parameter(Mandatory)][ValidateRange(0,63)][int]$ShardIndex,
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][int]$ExitCode,
    [Parameter(Mandatory)][string]$IdentityBindingPath,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference='Stop'
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
trap {
    [ordered]@{schemaVersion='suite-partition-receipt-v1';outcome='failed';shardIndex=$ShardIndex;exitCode=$ExitCode;
        reason='testResults.partitionIncompleteOrInvalid'} | ConvertTo-Json | Set-Content -LiteralPath $output
    throw 'Partition result invalid; sanitized refusal retained.'
}
. "$PSScriptRoot/suite-partition-contract.ps1"
$binding=Get-Content -LiteralPath $IdentityBindingPath -Raw | ConvertFrom-Json
Assert-SuiteIdentityBinding $binding
if($binding.identity.discoverySha256 -cne (Get-FileHash -LiteralPath $DiscoveryPath).Hash.ToLowerInvariant()){
    throw 'Discovery identity differs from bound runtime.'
}
$cases=@(Get-Content -LiteralPath $DiscoveryPath -Raw | ConvertFrom-Json)
$plan=@(Get-SuitePartitionPlan $cases $ShardCount)
if($ShardIndex -ge $ShardCount){throw 'Partition index out of range.'}
$selected=$plan[$ShardIndex].cases
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-partition-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
try{
    $subset=Join-Path $owned 'discovery.json'
    $selected | ConvertTo-Json -Depth 8 -AsArray | Set-Content -LiteralPath $subset
    # Reuse the complete-case verifier for the exact selected subset; required
    # process coverage is enforced across the complete union by the aggregator.
    & "$PSScriptRoot/assert-suite-case-coverage.ps1" -DiscoveryPath $subset -TrxPath $TrxPath -ExitCode $ExitCode `
        -Configuration $binding.identity.configuration -OutputPath (Join-Path $owned 'coverage.json')
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create((Resolve-Path -LiteralPath $TrxPath).ProviderPath,$settings)
    try{$doc=[Xml.XmlDocument]::new();$doc.XmlResolver=$null;$doc.Load($reader)}finally{$reader.Dispose()}
    $byName=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($row in $doc.SelectNodes('//*[local-name()="Results"]/*[local-name()="UnitTestResult"]')){
        $byName[$row.GetAttribute('testName')]=$row.GetAttribute('outcome')
    }
    $terminal=@(foreach($case in $selected){
        if($byName[$case.DisplayName] -ceq 'NotExecuted' -and !(Test-SuiteAllowedPlatformSkip $case $binding.identity.operatingSystem)){
            throw 'Unapproved platform skip.'
        }
        if($case.Class -cin @('Crap4CSharp.Tests.ProcessRunnerTests','Crap4CSharp.Tests.ProcessRunnerGrandchildTests',
            'Crap4CSharp.Tests.ProcessLogTests','Crap4CSharp.Tests.ProcessProtocolTests') -and $byName[$case.DisplayName] -cne 'Passed'){
            throw 'Selected required process control did not pass.'
        }
        [ordered]@{digest=(Get-SuiteCaseDigest $case);outcome=$byName[$case.DisplayName]}
    })
    [ordered]@{schemaVersion='suite-partition-receipt-v1';outcome='passed';shardIndex=$ShardIndex;shardCount=$ShardCount;
        exitCode=$ExitCode;identityBinding=$binding;caseSetSha256=(Get-SuiteCaseSetDigest @($terminal.digest));
        trxSha256=(Get-FileHash -LiteralPath $TrxPath).Hash.ToLowerInvariant();cases=$terminal} |
        ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $output
}finally{
    $absolute=[IO.Path]::GetFullPath($owned);$temporary=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-partition-*'){throw 'Unsafe owned cleanup target.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
