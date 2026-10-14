$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-partition-contract.ps1"
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-partition-controls-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
try{
    $classes=@('Crap4CSharp.Tests.ProcessRunnerTests','Crap4CSharp.Tests.ProcessRunnerGrandchildTests',
        'Crap4CSharp.Tests.ProcessLogTests','Crap4CSharp.Tests.ProcessProtocolTests','Crap4CSharp.Tests.OtherTests')
    $cases=@(for($i=0;$i -lt 60;$i++){
        $class=$classes[$i%$classes.Count];$method='Case'+$i
        [pscustomobject]@{ID='case-'+$i;DisplayName=$class+'.'+$method+'(private-canary)';Class=$class;Method=$method}
    })
    $discovery=Join-Path $owned 'discovery.json';$expected=Join-Path $owned 'identity.json';$bindingPath=Join-Path $owned 'binding.json'
    $cases | ConvertTo-Json -Depth 4 | Set-Content $discovery
    $files=[ordered]@{}
    foreach($name in @('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json','process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')){$files[$name]='d'*64}
    $identity=[ordered]@{schemaVersion='suite-identity-v1';outcome='observed';configuration='Release';operatingSystem='Windows';
        commit=('a'*40);tree=('b'*40);sdk='10.0.103';workflowRun='123';workflowAttempt='1';packageSha256=('c'*64);
        discoverySha256=(Get-FileHash $discovery).Hash.ToLowerInvariant();worktreeSha256=('1'*64);runtimeInventorySha256=('2'*64);files=$files}
    $identity | ConvertTo-Json -Depth 6 | Set-Content $expected
    [ordered]@{schemaVersion='suite-identity-binding-v1';outcome='passed';identity=$identity;beforeSha256=('3'*64);afterSha256=('4'*64)} |
        ConvertTo-Json -Depth 7 | Set-Content $bindingPath
    $plan=@(Get-SuitePartitionPlan $cases 2)
    $receipts=Join-Path $owned 'receipts';[void][IO.Directory]::CreateDirectory($receipts)
    foreach($shard in $plan){
        $doc=[Xml.XmlDocument]::new();$run=$doc.CreateElement('TestRun');[void]$doc.AppendChild($run)
        $results=$doc.CreateElement('Results');[void]$run.AppendChild($results)
        foreach($case in $shard.cases){
            $row=$doc.CreateElement('UnitTestResult');$row.SetAttribute('testId',$case.ID);$row.SetAttribute('testName',$case.DisplayName);$row.SetAttribute('outcome','Passed');[void]$results.AppendChild($row)
        }
        $summary=$doc.CreateElement('ResultSummary');[void]$run.AppendChild($summary);$counts=$doc.CreateElement('Counters');[void]$summary.AppendChild($counts)
        foreach($name in @('total','executed','passed')){$counts.SetAttribute($name,[string]$shard.cases.Count)}
        foreach($name in @('failed','error','timeout','aborted','inconclusive','notExecuted','notRunnable')){$counts.SetAttribute($name,'0')}
        $trx=Join-Path $owned ([string]$shard.index+'.trx');$doc.Save($trx)
        & "$PSScriptRoot/write-suite-partition-receipt.ps1" -DiscoveryPath $discovery -ShardCount 2 -ShardIndex $shard.index `
            -TrxPath $trx -ExitCode 0 -IdentityBindingPath $bindingPath -OutputPath (Join-Path $receipts ([string]$shard.index+'.partition.receipt.json'))
    }
    $baseline=@(Get-ChildItem $receipts -File | Sort-Object Name | ForEach-Object {Get-Content $_.FullName -Raw})
    $producerControls=@('nonzero','missing-trx','malformed-trx','external-entity','wrong-discovery','invalid-binding','missing-row')
    foreach($control in $producerControls){
        $trx=Join-Path $owned '0.trx';$candidateTrx=Join-Path $owned 'producer.trx';Copy-Item $trx $candidateTrx
        $candidateBinding=Join-Path $owned 'producer.binding.json';Copy-Item $bindingPath $candidateBinding
        $exitCode=0
        switch($control){
            nonzero {$exitCode=23}
            missing-trx {Remove-Item -LiteralPath $candidateTrx}
            malformed-trx {Set-Content $candidateTrx '<private-canary>'}
            external-entity {Set-Content $candidateTrx '<!DOCTYPE TestRun [<!ENTITY private SYSTEM "file:///private-canary">]><TestRun>&private;</TestRun>'}
            wrong-discovery {$b=Get-Content $candidateBinding -Raw | ConvertFrom-Json;$b.identity.discoverySha256='e'*64;$b | ConvertTo-Json -Depth 7 | Set-Content $candidateBinding}
            invalid-binding {$b=Get-Content $candidateBinding -Raw | ConvertFrom-Json;$b.outcome='failed';$b | ConvertTo-Json -Depth 7 | Set-Content $candidateBinding}
            missing-row {[xml]$badTrx=Get-Content $candidateTrx -Raw;[void]$badTrx.TestRun.Results.RemoveChild($badTrx.TestRun.Results.FirstChild);$badTrx.Save($candidateTrx)}
        }
        $out=Join-Path $owned 'producer.receipt.json';$accepted=$true
        try{& "$PSScriptRoot/write-suite-partition-receipt.ps1" -DiscoveryPath $discovery -ShardCount 2 -ShardIndex 0 `
            -TrxPath $candidateTrx -ExitCode $exitCode -IdentityBindingPath $candidateBinding -OutputPath $out}catch{$accepted=$false}
        if($accepted){throw "Invalid terminal partition accepted: $control"}
        $text=Get-Content $out -Raw
        if($text.Contains('private-canary') -or $text.Contains($owned)){throw 'Invalid producer leaked diagnostic detail.'}
    }
    $controls=@('positive','missing-shard','duplicate-index','missing-case','duplicate-case','wrong-assignment','failed-case','process-skip','unknown-skip',
        'nonzero','wrong-head','wrong-tree','wrong-config','wrong-os','wrong-sdk','wrong-run','wrong-attempt','wrong-package',
        'wrong-runtime','wrong-assembly','wrong-discovery','malformed','extra-private-field','extra-case-field','string-index','wrong-set-hash')
    foreach($control in $controls){
        foreach($i in 0..1){Set-Content (Join-Path $receipts ([string]$i+'.partition.receipt.json')) $baseline[$i]}
        $path=Join-Path $receipts '0.partition.receipt.json';$r=Get-Content $path -Raw | ConvertFrom-Json
        switch($control){
            missing-shard {Remove-Item -LiteralPath $path}
            duplicate-index {$r.shardIndex=1}
            missing-case {$r.cases=@($r.cases | Select-Object -Skip 1);$r.caseSetSha256=Get-SuiteCaseSetDigest @($r.cases.digest)}
            duplicate-case {$r.cases=@($r.cases)+@($r.cases[0])}
            wrong-assignment {$other=Get-Content (Join-Path $receipts '1.partition.receipt.json') -Raw | ConvertFrom-Json;$r.cases[0].digest=$other.cases[0].digest;$r.caseSetSha256=Get-SuiteCaseSetDigest @($r.cases.digest)}
            failed-case {$r.cases[0].outcome='Failed'}
            process-skip {$target=@($plan[0].cases | Where-Object {$_.Class -cin $classes[0..3]})[0];$digest=Get-SuiteCaseDigest $target;($r.cases | Where-Object {$_.digest -ceq $digest}).outcome='NotExecuted'}
            unknown-skip {$target=@($plan[0].cases | Where-Object {$_.Class -ceq $classes[4]})[0];$digest=Get-SuiteCaseDigest $target;($r.cases | Where-Object {$_.digest -ceq $digest}).outcome='NotExecuted'}
            nonzero {$r.exitCode=19}
            wrong-head {$r.identityBinding.identity.commit='e'*40}
            wrong-tree {$r.identityBinding.identity.tree='e'*40}
            wrong-config {$r.identityBinding.identity.configuration='Debug'}
            wrong-os {$r.identityBinding.identity.operatingSystem='Linux'}
            wrong-sdk {$r.identityBinding.identity.sdk='10.0.104'}
            wrong-run {$r.identityBinding.identity.workflowRun='999'}
            wrong-attempt {$r.identityBinding.identity.workflowAttempt='2'}
            wrong-package {$r.identityBinding.identity.packageSha256='e'*64}
            wrong-runtime {$r.identityBinding.identity.runtimeInventorySha256='e'*64}
            wrong-assembly {$r.identityBinding.identity.files.'Crap4CSharp.Tool.dll'='e'*64}
            wrong-discovery {$r.identityBinding.identity.discoverySha256='e'*64}
            extra-private-field {$r | Add-Member privateLog 'private-canary'}
            extra-case-field {$r.cases[0] | Add-Member privateName 'private-canary'}
            string-index {$r.shardIndex='0'}
            wrong-set-hash {$r.caseSetSha256='e'*64}
        }
        if($control -ne 'missing-shard'){$r | ConvertTo-Json -Depth 9 | Set-Content $path}
        if($control -eq 'malformed'){Set-Content $path '{private-canary'}
        $out=Join-Path $owned 'aggregate.json';$accepted=$true
        try{& "$PSScriptRoot/assert-suite-partitions.ps1" -DiscoveryPath $discovery -ShardCount 2 -ReceiptDirectory $receipts -ExpectedIdentityPath $expected -OutputPath $out}catch{$accepted=$false}
        if($accepted -ne ($control -eq 'positive')){throw "Unexpected complete partition union: $control"}
        $text=Get-Content $out -Raw
        if($text.Contains('private-canary') -or $text.Contains($owned) -or $text.Contains('Case0')){throw 'Private partition detail leaked.'}
        $aggregate=$text | ConvertFrom-Json
        if($accepted -and ($aggregate.caseCount -ne 60 -or $aggregate.passed -ne 60 -or $aggregate.skipped -ne 0)){throw 'Complete union count differs.'}
    }
    # A rerun retains exact source/runtime/case identity from unchanged shards;
    # an attempt records provenance, not a different source revision.
    $identity.workflowAttempt='2'
    $identity | ConvertTo-Json -Depth 5 | Set-Content $expected
    foreach($i in 0..1){Set-Content (Join-Path $receipts ([string]$i+'.partition.receipt.json')) $baseline[$i]}
    foreach($control in @('older-complete','mixed-complete')){
        if($control -ceq 'mixed-complete'){
            $path=Join-Path $receipts '1.partition.receipt.json'
            $receipt=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
            $receipt.identityBinding.identity.workflowAttempt='2'
            $receipt|ConvertTo-Json -Depth 9|Set-Content -LiteralPath $path
        }
        & "$PSScriptRoot/assert-suite-partitions.ps1" -DiscoveryPath $discovery -ShardCount 2 -ReceiptDirectory $receipts `
            -ExpectedIdentityPath $expected -OutputPath (Join-Path $owned "$control.json")
    }
    # Exercise downloaded artifact selection and the real union verifier together.
    foreach($control in @('selected-mixed-complete','selected-newer-failure')){
        $input=Join-Path $owned "$control-input";$selected=Join-Path $owned "$control-selected"
        foreach($i in 0..1){
            $directory=Join-Path $input "partition-Release-windows-latest-$i-attempt-1"
            [void][IO.Directory]::CreateDirectory($directory)
            Set-Content -LiteralPath (Join-Path $directory "Release-$i.partition.receipt.json") $baseline[$i]
        }
        $directory=Join-Path $input 'partition-Release-windows-latest-1-attempt-2'
        [void][IO.Directory]::CreateDirectory($directory)
        $receipt=$baseline[1]|ConvertFrom-Json
        $receipt.identityBinding.identity.workflowAttempt='2'
        if($control -ceq 'selected-newer-failure'){
            $receipt=[ordered]@{schemaVersion='suite-partition-receipt-v1';outcome='failed';shardIndex=1;exitCode=1;reason='testResults.partitionIncompleteOrInvalid'}
        }
        $receipt|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $directory 'Release-1.partition.receipt.json')
        & "$PSScriptRoot/select-suite-partition-receipts.ps1" -Configuration Release -RunnerOS windows-latest -ShardCount 2 `
            -WorkflowRun '123' -WorkflowAttempt '2' -InputDirectory $input -OutputDirectory $selected
        $accepted=$true
        try{& "$PSScriptRoot/assert-suite-partitions.ps1" -DiscoveryPath $discovery -ShardCount 2 -ReceiptDirectory $selected `
            -ExpectedIdentityPath $expected -OutputPath (Join-Path $owned "$control.json")}catch{$accepted=$false}
        if($accepted -ne ($control -ceq 'selected-mixed-complete')){throw "Unexpected selected complete union: $control"}
    }
    # Discovery refusals must occur before producing a usable filter plan.
    foreach($control in @('duplicate-id','duplicate-name','missing-method','filter-injection','empty')){
        $bad=@($cases | ConvertTo-Json -Depth 4 | ConvertFrom-Json)
        switch($control){
            duplicate-id {$bad[1].ID=$bad[0].ID}
            duplicate-name {$bad[1].DisplayName=$bad[0].DisplayName}
            missing-method {$bad[0].Method=$null}
            filter-injection {$bad[0].Method='Case|FullyQualifiedName~Other'}
            empty {$bad=@()}
        }
        $refused=$false;try{[void](Get-SuitePartitionPlan $bad 2)}catch{$refused=$true}
        if(!$refused){throw "Invalid discovery accepted: $control"}
    }
    # Theory rows stay together even if discovery order changes.
    $theories=@($cases)+@([pscustomobject]@{ID='additional-row';DisplayName='another-private-canary';Class=$cases[0].Class;Method=$cases[0].Method})
    $normal=@(Get-SuitePartitionPlan $theories 2)
    [Array]::Reverse($theories)
    $reversed=@(Get-SuitePartitionPlan $theories 2)
    foreach($i in 0..1){
        if($normal[$i].filter -cne $reversed[$i].filter -or
            (Get-SuiteCaseSetDigest @($normal[$i].cases | ForEach-Object {Get-SuiteCaseDigest $_})) -cne
            (Get-SuiteCaseSetDigest @($reversed[$i].cases | ForEach-Object {Get-SuiteCaseDigest $_}))){throw 'Partition depends on discovery order.'}
    }
    $theoryShard=@($normal | Where-Object {@($_.cases.ID | Where-Object {$_ -ceq 'additional-row'}).Count})
    if($theoryShard.Count -ne 1 -or $cases[0].ID -cnotin $theoryShard[0].cases.ID){throw 'Theory method split across shards.'}
    $skip=[pscustomobject]@{DisplayName='Crap4CSharp.Tests.CompilerReferenceSnapshotTests.SymlinkReferenceRefusesWithoutFollowingItsTarget'}
    if(!(Test-SuiteAllowedPlatformSkip $skip 'Windows') -or (Test-SuiteAllowedPlatformSkip $skip 'Linux')){throw 'Named platform skip policy differs.'}
    Write-Output "Complete partition controls passed ($($controls.Count+5+$producerControls.Count+6)); synthetic evidence, no full-suite execution acceptance."
}finally{
    $absolute=[IO.Path]::GetFullPath($owned);$prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-partition-controls-*'){throw 'Unsafe fixture cleanup target.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
