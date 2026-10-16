$ErrorActionPreference = 'Stop'
$ownedRoot = Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-platform-receipts-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $ownedRoot)
try {
    $candidateDirectory = Join-Path $ownedRoot 'candidate'
    $acceptanceDirectory = Join-Path $ownedRoot 'acceptance'
    [void](New-Item -ItemType Directory -Path $candidateDirectory, $acceptanceDirectory)
    $package = Join-Path $candidateDirectory 'fixture.nupkg'
    [IO.File]::WriteAllBytes($package, [byte[]](1,2,3))
    $candidate = [ordered]@{
        schemaVersion='candidate-preparation-v1'; version='0.1.0-receiptfixture'
        commit=('a'*40); tree=('b'*40); sha256=(Get-FileHash $package).Hash.ToLowerInvariant()
        package='fixture.nupkg'; publicationAuthorized=$false
    }
    $candidate | ConvertTo-Json | Set-Content (Join-Path $candidateDirectory 'candidate.json')
    $receipts = @('Windows','Linux') | ForEach-Object {
        $platform=$_
        [ordered]@{
            schemaVersion='candidate-acceptance-v1'; outcome='passed'; operatingSystem=$_
            version=$candidate.version; commit=$candidate.commit; tree=$candidate.tree; packageSha256=$candidate.sha256
            workflowRun='123'; workflowAttempt='1'; publicationAuthorized=$false; callableCollectorCapability='passed'
            sdk='10.0.103';configurations=@('Debug','Release')
            suites=@('Debug','Release' | ForEach-Object {
                [ordered]@{ schemaVersion='suite-receipt-v1'; configuration=$_; outcome='passed'; exitCode=0; trxSha256=('c'*64)
                    counts=@{ total=3; executed=2; passed=2; failed=0; error=0; timeout=0; aborted=0; inconclusive=0; notRunnable=0; notExecuted=0; skipped=1 } }
            })
            suiteIdentities=@('Debug','Release' | ForEach-Object {
                $runtime=[ordered]@{}
                foreach($file in @('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json',
                    'process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')){$runtime[$file]='d'*64}
                [ordered]@{schemaVersion='suite-identity-binding-v1';outcome='passed';beforeSha256=('f'*64);afterSha256=('f'*64);
                    identity=[ordered]@{schemaVersion='suite-identity-v1';outcome='observed';configuration=$_;operatingSystem=$platform;
                        commit=$candidate.commit;tree=$candidate.tree;sdk='10.0.103';workflowRun='123';workflowAttempt='1';
                        packageSha256=$candidate.sha256;discoverySha256=('e'*64);worktreeSha256=('1'*64);runtimeInventorySha256=('2'*64);files=$runtime}}
            })
            caseCoverage=@('Debug','Release' | ForEach-Object {
                [ordered]@{schemaVersion='suite-case-coverage-v1';configuration=$_;outcome='passed';exitCode=0;
                    discoverySha256=('e'*64);trxSha256=('c'*64);caseCount=3;passed=2;skipped=1;processControlsRequired=$true}
            })
        }
    }
    foreach ($case in @('valid','missing-platform','mismatched-package','missing-configuration','failed-suite','wrong-run','changed-package',
        'missing-case-coverage','duplicate-case-configuration','wrong-case-trx','wrong-case-total','wrong-case-passed','wrong-case-skipped','failed-case-coverage','wrong-sdk','missing-process-controls','missing-suite-identities','wrong-suite-head','wrong-suite-package','wrong-suite-os','wrong-suite-run','wrong-suite-discovery',
        'duplicate-suite-identity','string-count-concatenation','float-count','boolean-count','unknown-suite-field','unknown-coverage-field','unknown-binding-field','unknown-identity-field')) {
        [IO.File]::WriteAllBytes($package, [byte[]](1,2,3))
        $current = $receipts | ConvertTo-Json -Depth 8 | ConvertFrom-Json
        switch ($case) {
            'mismatched-package' { $current[1].packageSha256='d'*64 }
            'missing-configuration' { $current[1].suites=@($current[1].suites[0]) }
            'failed-suite' { $current[1].suites[0].counts.failed=1 }
            'wrong-run' { $current[1].workflowRun='999' }
            'changed-package' { [IO.File]::WriteAllBytes($package, [byte[]](4,5,6)) }
            'missing-case-coverage' { $current[1].caseCoverage=@() }
            'duplicate-case-configuration' { $current[1].caseCoverage[1].configuration='Debug' }
            'wrong-case-trx' { $current[1].caseCoverage[0].trxSha256='f'*64 }
            'wrong-case-total' { $current[1].caseCoverage[0].caseCount=2 }
            'wrong-case-passed' { $current[1].caseCoverage[0].passed=1 }
            'wrong-case-skipped' { $current[1].caseCoverage[0].skipped=0 }
            'failed-case-coverage' { $current[1].caseCoverage[0].outcome='failed' }
            'wrong-sdk' { $current[1].sdk='10.0.104' }
            'missing-process-controls' { $current[1].caseCoverage[0].processControlsRequired=$false }
            'missing-suite-identities' { $current[1].suiteIdentities=@() }
            'wrong-suite-head' { $current[1].suiteIdentities[0].identity.commit='e'*40 }
            'wrong-suite-package' { $current[1].suiteIdentities[0].identity.packageSha256='e'*64 }
            'wrong-suite-os' { $current[1].suiteIdentities[0].identity.operatingSystem='Windows' }
            'wrong-suite-run' { $current[1].suiteIdentities[0].identity.workflowRun='999' }
            'wrong-suite-discovery' { $current[1].suiteIdentities[0].identity.discoverySha256='f'*64 }
            'duplicate-suite-identity' { $current[1].suiteIdentities[1].identity.configuration='Debug' }
            'string-count-concatenation' {
                $current[1].suites[0].counts.executed='2';$current[1].suites[0].counts.passed='2';$current[1].suites[0].counts.skipped='1';$current[1].suites[0].counts.total='21'
                $current[1].caseCoverage[0].passed='2';$current[1].caseCoverage[0].skipped='1';$current[1].caseCoverage[0].caseCount='21'
            }
            'float-count' { $current[1].suites[0].counts.total=3.0 }
            'boolean-count' { $current[1].suites[0].counts.skipped=$true }
            'unknown-suite-field' { $current[1].suites[0] | Add-Member privateMetadata 'private-canary' }
            'unknown-coverage-field' { $current[1].caseCoverage[0] | Add-Member privateMetadata 'private-canary' }
            'unknown-binding-field' { $current[1].suiteIdentities[0] | Add-Member privateMetadata 'private-canary' }
            'unknown-identity-field' { $current[1].suiteIdentities[0].identity | Add-Member privateMetadata 'private-canary' }
        }
        $windowsDirectory=Join-Path $acceptanceDirectory 'Windows'
        $linuxDirectory=Join-Path $acceptanceDirectory 'Linux'
        [void](New-Item -ItemType Directory -Path $windowsDirectory, $linuxDirectory -Force)
        $current[0] | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $windowsDirectory 'acceptance.json')
        $current[1] | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $linuxDirectory 'acceptance.json')
        if ($case -eq 'missing-platform') { Remove-Item -LiteralPath (Join-Path $linuxDirectory 'acceptance.json') }
        $accepted=$true
        try {
            & "$PSScriptRoot/assert-platform-acceptance.ps1" -CandidateDirectory $candidateDirectory -AcceptanceDirectory $acceptanceDirectory -Version $candidate.version -Commit $candidate.commit -WorkflowRun '123' -WorkflowAttempt '1' | Out-Null
        } catch { $accepted=$false }
        if ($accepted -ne ($case -eq 'valid')) { throw "Unexpected platform acceptance for $case." }
    }
    Write-Output 'Platform receipt controls passed: thirty cases. Synthetic receipts test comparison only, not package acceptance.'
} finally {
    $resolved=[IO.Path]::GetFullPath($ownedRoot)
    $tempPrefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetFileName($resolved).StartsWith('crap4csharp-platform-receipts-', [StringComparison]::Ordinal)) { throw 'Cleanup target escaped owned temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
