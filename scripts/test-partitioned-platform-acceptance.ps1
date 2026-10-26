$ErrorActionPreference='Stop'
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-partition-platform-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
try{
    $candidateDir=Join-Path $owned 'candidate';$acceptanceDir=Join-Path $owned 'acceptance'
    [void][IO.Directory]::CreateDirectory($candidateDir)
    [IO.File]::WriteAllBytes((Join-Path $candidateDir 'fixture.nupkg'),[byte[]](1,2,3))
    $candidate=[ordered]@{schemaVersion='candidate-preparation-v1';version='0.1.0-fixture';commit=('a'*40);tree=('b'*40);
        sha256=(Get-FileHash (Join-Path $candidateDir 'fixture.nupkg')).Hash.ToLowerInvariant();package='fixture.nupkg';publicationAuthorized=$false}
    $candidate | ConvertTo-Json | Set-Content (Join-Path $candidateDir 'candidate.json')
    $receipts=@(foreach($os in @('Windows','Linux')){
        [ordered]@{schemaVersion='candidate-acceptance-v2';outcome='passed';version=$candidate.version;commit=$candidate.commit;
            tree=$candidate.tree;packageSha256=$candidate.sha256;operatingSystem=$os;sdk='10.0.103';configurations=@('Debug','Release');
            workflowRun='123';workflowAttempt='1';publicationAuthorized=$false;callableCollectorCapability='passed';
            partitionCoverage=@(foreach($configuration in @('Debug','Release')){
                $files=[ordered]@{}
                foreach($file in @('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json','process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')){$files[$file]='d'*64}
                [ordered]@{schemaVersion='suite-partition-coverage-v1';outcome='passed';shardCount=16;caseCount=120;
                    passed=$(if($os -ceq 'Linux'){120}else{119});skipped=$(if($os -ceq 'Linux'){0}else{1});
                    processControlsRequired=$true;caseSetSha256=('5'*64);receiptSetSha256=('6'*64);
                    identity=[ordered]@{schemaVersion='suite-identity-v1';outcome='observed';configuration=$configuration;operatingSystem=$os;
                        commit=$candidate.commit;tree=$candidate.tree;sdk='10.0.103';workflowRun='123';workflowAttempt='1';packageSha256=$candidate.sha256;
                        discoverySha256=('7'*64);worktreeSha256=('8'*64);runtimeInventorySha256=('9'*64);files=$files}}
            })}
    })
    $controls=@('positive','windows-max-skips','older-attempt','mixed-attempt','future-union','missing-os','duplicate-os','legacy-schema','wrong-package','wrong-head','wrong-tree','wrong-run','wrong-attempt',
        'wrong-sdk','missing-config','missing-union','duplicate-config','wrong-union-os','wrong-union-head','wrong-union-package',
        'wrong-union-run','failed-union','unknown-field','private-union-field','private-identity-field','private-assembly-field',
        'string-count','float-count','boolean-count','forged-count','no-passes','invalid-case-hash','invalid-receipt-hash',
        'missing-process-policy','wrong-shard-count','collector-failed','publication-enabled','string-publication','changed-package',
        'numeric-run','numeric-attempt','array-head','linux-skipped','too-many-windows-skips')
    foreach($control in $controls){
        [IO.File]::WriteAllBytes((Join-Path $candidateDir 'fixture.nupkg'),[byte[]](1,2,3))
        $current=@($receipts | ConvertTo-Json -Depth 9 | ConvertFrom-Json)
        $coverage=$current[1].partitionCoverage[0]
        $expectedAttempt=if($control -cin @('older-attempt','mixed-attempt','future-union')){'2'}else{'1'}
        switch($control){
            mixed-attempt {$current[1].workflowAttempt='2';$current[1].partitionCoverage[1].identity.workflowAttempt='2'}
            future-union {$current[1].workflowAttempt='2';$coverage.identity.workflowAttempt='3'}
            numeric-run {$current[1].workflowRun=123}
            numeric-attempt {$current[1].workflowAttempt=1}
            array-head {$current[1].commit=@('a'*40)}
            linux-skipped {$coverage.passed=119;$coverage.skipped=1}
            windows-max-skips {
                foreach($union in $current[0].partitionCoverage){$union.passed=109;$union.skipped=11}
            }
            too-many-windows-skips {$current[0].partitionCoverage[0].passed=108;$current[0].partitionCoverage[0].skipped=12}
            duplicate-os {$current[1].operatingSystem='Windows'}
            legacy-schema {$current[1].schemaVersion='candidate-acceptance-v1'}
            wrong-package {$current[1].packageSha256='e'*64}
            wrong-head {$current[1].commit='e'*40}
            wrong-tree {$current[1].tree='e'*40}
            wrong-run {$current[1].workflowRun='999'}
            wrong-attempt {$current[1].workflowAttempt='2'}
            wrong-sdk {$current[1].sdk='10.0.104'}
            missing-config {$current[1].configurations=@('Debug')}
            missing-union {$current[1].partitionCoverage=@($coverage)}
            duplicate-config {$current[1].partitionCoverage[1].identity.configuration='Debug'}
            wrong-union-os {$coverage.identity.operatingSystem='Windows'}
            wrong-union-head {$coverage.identity.commit='e'*40}
            wrong-union-package {$coverage.identity.packageSha256='e'*64}
            wrong-union-run {$coverage.identity.workflowRun='999'}
            failed-union {$coverage.outcome='failed'}
            unknown-field {$current[1] | Add-Member privateMetadata 'private-canary'}
            private-union-field {$coverage | Add-Member privateMetadata 'private-canary'}
            private-identity-field {$coverage.identity | Add-Member privateMetadata 'private-canary'}
            private-assembly-field {$coverage.identity.files | Add-Member privateMetadata 'private-canary'}
            string-count {$coverage.caseCount='120'}
            float-count {$coverage.passed=119.0}
            boolean-count {$coverage.skipped=$true}
            forged-count {$coverage.caseCount=121}
            no-passes {$coverage.passed=0;$coverage.skipped=120}
            invalid-case-hash {$coverage.caseSetSha256='private-canary'}
            invalid-receipt-hash {$coverage.receiptSetSha256=$null}
            missing-process-policy {$coverage.processControlsRequired=$false}
            wrong-shard-count {$coverage.shardCount=15}
            collector-failed {$current[1].callableCollectorCapability='failed'}
            publication-enabled {$current[1].publicationAuthorized=$true}
            string-publication {$current[1].publicationAuthorized='false'}
            changed-package {[IO.File]::WriteAllBytes((Join-Path $candidateDir 'fixture.nupkg'),[byte[]](4,5,6))}
        }
        foreach($i in 0..1){
            $directory=Join-Path $acceptanceDir ([string]$i);[void][IO.Directory]::CreateDirectory($directory)
            $current[$i] | ConvertTo-Json -Depth 9 | Set-Content (Join-Path $directory 'acceptance.json')
        }
        if($control -eq 'missing-os'){Remove-Item -LiteralPath (Join-Path $acceptanceDir '1/acceptance.json')}
        $accepted=$true
        try{& "$PSScriptRoot/assert-partitioned-platform-acceptance.ps1" -CandidateDirectory $candidateDir -AcceptanceDirectory $acceptanceDir `
            -Version $candidate.version -Commit $candidate.commit -WorkflowRun '123' -WorkflowAttempt $expectedAttempt | Out-Null}catch{$accepted=$false}
        if($accepted -ne ($control -cin @('positive','windows-max-skips','older-attempt','mixed-attempt'))){throw "Unexpected partitioned platform acceptance: $control"}
    }
    Write-Output "Partitioned platform controls passed ($($controls.Count)); synthetic verifier evidence only."
}finally{
    $absolute=[IO.Path]::GetFullPath($owned);$prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-partition-platform-*'){throw 'Unsafe fixture cleanup target.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
