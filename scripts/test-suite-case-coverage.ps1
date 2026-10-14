$ErrorActionPreference='Stop'
$ownedRoot=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-case-coverage-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($ownedRoot)
$names=@('private-case-canary-one','private-case-canary-two')
$discovery=@([ordered]@{ID='one';DisplayName=$names[0]},[ordered]@{ID='two';DisplayName=$names[1]})
$baseTrx=@"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
<UnitTestResult testId="one" testName="$($names[0])" outcome="Passed"><Output><StdOut>private-log-canary</StdOut></Output></UnitTestResult>
<UnitTestResult testId="two" testName="$($names[1])" outcome="NotExecuted" />
</Results><ResultSummary><Counters total="2" executed="1" passed="1" failed="0" notExecuted="0" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" /></ResultSummary></TestRun>
"@
$cases=@('positive','nonzero','missing-case','unexpected-case','duplicate-name','duplicate-result-id','duplicate-discovery-id',
    'duplicate-discovery-name','empty-discovery','wrong-type','forged-counter','failed-row','malformed-trx','external-entity',
    'missing-trx','malformed-discovery')
try{
    foreach($name in $cases){
        $expected=$name -eq 'positive';$data=$discovery | ConvertTo-Json -Depth 3;$trx=$baseTrx;$exitCode=0
        switch($name){
            nonzero {$exitCode=23}
            missing-case {$data=@($discovery[0]) | ConvertTo-Json -AsArray}
            unexpected-case {$trx=$trx.Replace($names[1],'unexpected-private-canary')}
            duplicate-name {$trx=$trx.Replace($names[1],$names[0])}
            duplicate-result-id {$trx=$trx.Replace('testId="two"','testId="one"')}
            duplicate-discovery-id {$data=$data.Replace('"two"','"one"')}
            duplicate-discovery-name {$data=$data.Replace($names[1],$names[0])}
            empty-discovery {$data='[]'}
            wrong-type {$data='[{"ID":1,"DisplayName":"private-case-canary-one"}]'}
            forged-counter {$trx=$trx.Replace('passed="1"','passed="2"')}
            failed-row {$trx=$trx.Replace('outcome="Passed"','outcome="Failed"')}
            malformed-trx {$trx='<broken-private-canary>'}
            external-entity {$trx='<!DOCTYPE TestRun [<!ENTITY secret SYSTEM "file:///private-entity-canary">]><TestRun>&secret;</TestRun>'}
            malformed-discovery {$data='[private-discovery-canary'}
        }
        $discoveryPath=Join-Path $ownedRoot ($name+'.discovery.json');$trxPath=Join-Path $ownedRoot ($name+'.trx');$receipt=Join-Path $ownedRoot ('nested/'+$name+'.json')
        [IO.File]::WriteAllText($discoveryPath,$data)
        if($name -ne 'missing-trx'){[IO.File]::WriteAllText($trxPath,$trx)}
        $accepted=$true
        try{& "$PSScriptRoot/assert-suite-case-coverage.ps1" -DiscoveryPath $discoveryPath -TrxPath $trxPath -ExitCode $exitCode -Configuration Release -OutputPath $receipt}catch{$accepted=$false}
        if($accepted -ne $expected){throw "Unexpected case-coverage result: $name"}
        $text=Get-Content -LiteralPath $receipt -Raw
        foreach($canary in @('private-case-canary','private-log-canary','private-entity-canary','private-discovery-canary',$ownedRoot)){
            if($text.Contains($canary)){throw 'Private data leaked into coverage receipt.'}
        }
        $parsed=$text | ConvertFrom-Json
        if($parsed.exitCode -ne $exitCode){throw 'Actual process exit lost.'}
        if($expected){
            if($parsed.caseCount -ne 2 -or $parsed.passed -ne 1 -or $parsed.skipped -ne 1 -or
                $parsed.discoverySha256 -ne (Get-FileHash $discoveryPath).Hash.ToLowerInvariant() -or
                $parsed.trxSha256 -ne (Get-FileHash $trxPath).Hash.ToLowerInvariant()){throw 'Coverage totals/hash mismatch.'}
        }elseif($parsed.outcome -ne 'failed' -or $parsed.caseCount -ne $null -or $parsed.discoverySha256 -ne $null -or $parsed.trxSha256 -ne $null){throw 'Invalid results invented valid evidence.'}
    }
    $processNames=@('Crap4CSharp.Tests.ProcessRunnerTests.Control','Crap4CSharp.Tests.ProcessRunnerGrandchildTests.Control',
        'Crap4CSharp.Tests.ProcessLogTests.Control','Crap4CSharp.Tests.ProcessProtocolTests.Control')
    foreach($control in @('passed','skipped','missing-class')){
        $selected=if($control -eq 'missing-class'){@($processNames[0..2])}else{$processNames}
        $processDiscovery=@(for($index=0;$index -lt $selected.Count;$index++){
            [ordered]@{ID="process-$index";DisplayName=$selected[$index]}
        })
        $rows=for($index=0;$index -lt $selected.Count;$index++){
            $outcome=if($control -eq 'skipped' -and $index -eq 0){'NotExecuted'}else{'Passed'}
            '<UnitTestResult testId="process-'+$index+'" testName="'+$selected[$index]+'" outcome="'+$outcome+'" />'
        }
        $passed=$selected.Count-$(if($control -eq 'skipped'){1}else{0})
        $processTrx='<TestRun><Results>'+($rows -join '')+'</Results><ResultSummary><Counters total="'+$selected.Count+'" executed="'+$passed+'" passed="'+$passed+'" failed="0" notExecuted="0" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" /></ResultSummary></TestRun>'
        $discoveryPath=Join-Path $ownedRoot 'process.discovery.json';$trxPath=Join-Path $ownedRoot 'process.trx';$receipt=Join-Path $ownedRoot 'process.receipt.json'
        $processDiscovery | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $discoveryPath
        [IO.File]::WriteAllText($trxPath,$processTrx)
        $accepted=$true
        try{& "$PSScriptRoot/assert-suite-case-coverage.ps1" -DiscoveryPath $discoveryPath -TrxPath $trxPath -ExitCode 0 -Configuration Release -RequireProcessControls -OutputPath $receipt}catch{$accepted=$false}
        if($accepted -ne ($control -eq 'passed')){throw "Unexpected required process coverage: $control"}
        $parsed=Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
        if($parsed.processControlsRequired -ne $true){throw 'Required process policy absent from receipt.'}
    }
    Write-Output "Suite case coverage controls passed ($($cases.Count+3))."
}finally{
    $resolved=[IO.Path]::GetFullPath($ownedRoot)
    $temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'crap4csharp-case-coverage-*'){throw 'Unsafe fixture cleanup target.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
