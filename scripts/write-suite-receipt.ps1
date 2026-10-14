param(
    [Parameter(Mandatory = $true)][string]$TrxPath,
    [Parameter(Mandatory = $true)][int]$ExitCode,
    [Parameter(Mandatory = $true)][ValidateSet('Debug', 'Release')][string]$Configuration,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput))
$OutputPath = $resolvedOutput
$receiptWritten = $false
trap {
    if (!$receiptWritten) {
        [ordered]@{
            schemaVersion='suite-receipt-v1'; configuration=$Configuration; exitCode=$ExitCode
            outcome='failed'; counts=$null; trxSha256=$null; reason='testResults.unavailableOrInvalid'
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath
    }
    throw 'Suite acceptance failed; sanitized receipt retained.'
}
# Only counters and hashes leave the runner. TRX messages, attachments, paths
# and parameterized test names may contain private consumer diagnostics.
$settings = [System.Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$resolvedTrx = (Resolve-Path -LiteralPath $TrxPath).ProviderPath
$reader = [System.Xml.XmlReader]::Create($resolvedTrx, $settings)
try {
    $document = [System.Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($reader)
} finally { $reader.Dispose() }
$counters = @($document.SelectNodes('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]'))
if ($counters.Count -ne 1) { throw 'Expected exactly one TRX counter record.' }
$counts = [ordered]@{}
foreach ($name in @('total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted', 'notRunnable')) {
    $value = 0
    if (![int]::TryParse($counters[0].GetAttribute($name), [ref]$value) -or $value -lt 0) { throw 'Invalid TRX counters.' }
    $counts[$name] = $value
}
$results = @($document.SelectNodes('//*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))
$counts['skipped'] = $counts.total - $counts.executed
if ($counts.total -le 0 -or $counts.executed -le 0 -or $results.Count -ne $counts.total -or
    $counts.skipped -lt 0 -or $counts.notExecuted -gt $counts.skipped -or
    $counts.executed -ne $counts.passed + $counts.failed) { throw 'Empty or inconsistent TRX results.' }
$observed = @{}
foreach ($result in $results) {
    $outcome = $result.GetAttribute('outcome')
    if ($outcome -notin @('Passed', 'Failed', 'NotExecuted')) { throw 'Unsupported TRX result outcome.' }
    if (!$observed.ContainsKey($outcome)) { $observed[$outcome] = 0 }
    $observed[$outcome]++
}
foreach ($pair in @(@('Passed', 'passed'), @('Failed', 'failed'), @('NotExecuted', 'skipped'))) {
    $actual = if ($observed.ContainsKey($pair[0])) { $observed[$pair[0]] } else { 0 }
    if ($actual -ne $counts[$pair[1]]) { throw 'TRX outcomes disagree with counters.' }
}
$passed = $ExitCode -eq 0 -and $counts.failed -eq 0 -and $counts.error -eq 0 -and
    $counts.timeout -eq 0 -and $counts.aborted -eq 0 -and $counts.inconclusive -eq 0 -and $counts.notRunnable -eq 0
[ordered]@{
    schemaVersion = 'suite-receipt-v1'; configuration = $Configuration
    exitCode = $ExitCode; outcome = $(if ($passed) { 'passed' } else { 'failed' })
    counts = $counts; trxSha256 = (Get-FileHash -LiteralPath $TrxPath -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath
$receiptWritten = $true
if (!$passed) { throw 'Suite acceptance failed; sanitized receipt retained.' }
