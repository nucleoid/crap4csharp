$ErrorActionPreference = 'Stop'
$ownedRoot = Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-suite-receipts-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $ownedRoot)
try {
    $cases = @(
        @{ Name='passed'; Passed=1; Failed=0; Skipped=0; Exit=0; Accept=$true },
        @{ Name='named-skip'; Passed=1; Failed=0; Skipped=1; Exit=0; Accept=$true },
        @{ Name='xunit-skip-counter'; Passed=1; Failed=0; Skipped=1; CounterSkip=0; Exit=0; Accept=$true },
        @{ Name='false-zero-exit'; Passed=0; Failed=1; Skipped=0; Exit=0; Accept=$false },
        @{ Name='nonzero-exit'; Passed=1; Failed=0; Skipped=0; Exit=23; Accept=$false },
        @{ Name='empty-suite'; Passed=0; Failed=0; Skipped=0; Exit=0; Accept=$false }
    )
    foreach ($case in $cases) {
        $total = $case.Passed + $case.Failed + $case.Skipped
        $executed = $case.Passed + $case.Failed
        $counterSkip = if ($case.ContainsKey('CounterSkip')) { $case.CounterSkip } else { $case.Skipped }
        $results = @()
        foreach ($outcome in @('Passed', 'Failed', 'NotExecuted')) {
            $count = switch ($outcome) { Passed { $case.Passed }; Failed { $case.Failed }; NotExecuted { $case.Skipped } }
            for ($i=0; $i -lt $count; $i++) {
                $results += "<UnitTestResult testName='private-consumer-canary' outcome='$outcome'><Output><StdOut>private-feed-canary</StdOut></Output></UnitTestResult>"
            }
        }
        $trx = Join-Path $ownedRoot ($case.Name + '.trx')
        $receipt = Join-Path $ownedRoot ($case.Name + '.json')
        @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<Results>$($results -join '')</Results><ResultSummary>
<Counters total="$total" executed="$executed" passed="$($case.Passed)" failed="$($case.Failed)" notExecuted="$counterSkip" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" />
</ResultSummary></TestRun>
"@ | Set-Content -LiteralPath $trx
        $accepted = $true
        try { & "$PSScriptRoot/write-suite-receipt.ps1" -TrxPath $trx -ExitCode $case.Exit -Configuration Release -OutputPath $receipt }
        catch { $accepted = $false }
        if ($accepted -ne $case.Accept) { throw "Unexpected acceptance for $($case.Name)." }
        if (Test-Path -LiteralPath $receipt) {
            $text = Get-Content -LiteralPath $receipt -Raw
            if ($text.Contains('private-consumer-canary') -or $text.Contains('private-feed-canary') -or $text.Contains($ownedRoot)) { throw 'Private diagnostics leaked into receipt.' }
            $parsed = $text | ConvertFrom-Json
            if ($case.Name -eq 'empty-suite') {
                if ($parsed.exitCode -ne $case.Exit -or $parsed.counts -ne $null -or
                    $parsed.reason -ne 'testResults.unavailableOrInvalid') { throw 'Empty suite invented valid evidence.' }
                continue
            }
            if ($parsed.exitCode -ne $case.Exit -or $parsed.counts.total -ne $total -or
                $parsed.trxSha256 -ne (Get-FileHash -LiteralPath $trx).Hash.ToLowerInvariant()) { throw 'Receipt lost observed identity or counts.' }
        }
    }
    # A green counter record cannot override a failed individual result.
    $forged = (Get-Content -LiteralPath (Join-Path $ownedRoot 'passed.trx') -Raw).Replace("outcome='Passed'", "outcome='Failed'")
    $forgedPath = Join-Path $ownedRoot 'forged.trx'
    $forged | Set-Content -LiteralPath $forgedPath
    $accepted = $true
    try { & "$PSScriptRoot/write-suite-receipt.ps1" -TrxPath $forgedPath -ExitCode 0 -Configuration Debug -OutputPath (Join-Path $ownedRoot 'forged.json') }
    catch { $accepted = $false }
    if ($accepted) { throw 'Forged counters accepted.' }
    foreach ($case in @('missing', 'malformed', 'external-entity')) {
        $path = Join-Path $ownedRoot ($case + '.trx')
        $output = if ($case -eq 'missing') { Join-Path $ownedRoot 'new-results/missing.json' } else { Join-Path $ownedRoot ($case + '.json') }
        if ($case -eq 'malformed') { '<TestRun>private-malformed-canary' | Set-Content -LiteralPath $path }
        if ($case -eq 'external-entity') {
            '<!DOCTYPE TestRun [<!ENTITY secret SYSTEM "file:///private-entity-canary">]><TestRun>&secret;</TestRun>' | Set-Content -LiteralPath $path
        }
        $accepted = $true
        try { & "$PSScriptRoot/write-suite-receipt.ps1" -TrxPath $path -ExitCode 17 -Configuration Debug -OutputPath $output }
        catch { $accepted = $false }
        if ($accepted -or !(Test-Path -LiteralPath $output)) { throw 'Missing/invalid results did not retain a failed receipt.' }
        $text = Get-Content -LiteralPath $output -Raw
        $receipt = $text | ConvertFrom-Json
        if ($receipt.outcome -ne 'failed' -or $receipt.exitCode -ne 17 -or $receipt.counts -ne $null -or
            $receipt.reason -ne 'testResults.unavailableOrInvalid' -or $text.Contains('canary') -or $text.Contains($ownedRoot)) { throw 'Invalid-result receipt leaked diagnostics or invented evidence.' }
    }
    Push-Location -LiteralPath $ownedRoot
    try {
        & "$PSScriptRoot/write-suite-receipt.ps1" -TrxPath 'passed.trx' -ExitCode 0 -Configuration Release -OutputPath 'relative-results/passed.json'
        $relative = Get-Content -LiteralPath 'relative-results/passed.json' -Raw | ConvertFrom-Json
        if ($relative.outcome -ne 'passed' -or $relative.trxSha256 -ne (Get-FileHash -LiteralPath 'passed.trx').Hash.ToLowerInvariant()) { throw 'Relative location lost observed results.' }
    } finally { Pop-Location }
    Write-Output 'Suite receipt controls passed: eleven cases, including relative location, absent result directories, missing/malformed/entity results, xUnit skips, contradictory outcomes and private diagnostic sentinels.'
} finally {
    $resolved = [IO.Path]::GetFullPath($ownedRoot)
    $tempPrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetFileName($resolved).StartsWith('crap4csharp-suite-receipts-', [StringComparison]::Ordinal)) { throw 'Cleanup target escaped owned temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
