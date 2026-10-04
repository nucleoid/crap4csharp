param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$DebugResults,
    [Parameter(Mandatory = $true)][string]$ReleaseResults
)

$ErrorActionPreference = 'Stop'
$reports = @(
    Get-ChildItem -Path $DebugResults, $ReleaseResults -Recurse -File |
        Where-Object { $_.Name -in @('coverage.opencover.xml', 'coverage.cobertura.xml') } |
        Sort-Object FullName
)
if ($reports.Count -ne 4) {
    throw "Expected four real Coverlet reports, found $($reports.Count)."
}

$source = Join-Path $RepoRoot 'samples/Fixture/Fixture'
$project = Join-Path $RepoRoot 'src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj'
$normalized = @()
foreach ($report in $reports) {
    $text = (& dotnet run --project $project -c Release --no-build -- analyze --syntax-only --format json --coverage $report.FullName $source 2>$null | Out-String)
    if ($LASTEXITCODE -ne 1) {
        throw "Expected fail-closed exit 1 for unsupported generated fixture entries in $($report.FullName); got $LASTEXITCODE."
    }
    $document = $text | ConvertFrom-Json
    if ($document.complexityRulesetVersion -ne 'callables-v1') {
        throw "Unexpected ruleset in $($report.FullName)."
    }
    $known = @($document.evaluation.callables | Where-Object coverageStatus -eq 'known')
    $unknown = @($document.evaluation.callables | Where-Object coverageStatus -eq 'unknown')
    if ($known.Count -eq 0 -or $unknown.Count -eq 0) {
        throw "Expected both proven and fail-closed callable rows in $($report.FullName)."
    }
    if (@($known | Where-Object { $_.mappingEvidenceKind -ne 'semanticSourceIdentity' }).Count -ne 0) {
        throw "Every successful mapping must carry semantic source identity evidence."
    }
    if (@($known | Where-Object { @($_.documents).Count -eq 0 }).Count -ne 0) {
        throw "Every successful mapping must identify its authoritative source document."
    }

    $requiredKinds = @('constructor', 'indexer-get', 'indexer-set', 'operator', 'conversion')
    foreach ($kind in $requiredKinds) {
        if (@($document.evaluation.callables | Where-Object kind -eq $kind).Count -eq 0) {
            throw "Missing required real-fixture callable kind '$kind'."
        }
    }
    if (@($document.evaluation.callables | Where-Object { $_.semanticSignature -like '*IProbe*Read*' }).Count -eq 0) {
        throw 'Missing explicit-interface implementation from real fixture inventory.'
    }
    if (@($document.evaluation.callables | Where-Object { $_.semanticSignature -like '*Box*Echo*' }).Count -eq 0) {
        throw 'Missing nested generic type method from real fixture inventory.'
    }
    $requiredKnown = @('*Box*Echo*', '*ArrayLength*', '*Sum*List*', '*Describe*Int32*', '*Describe*String*')
    foreach ($signature in $requiredKnown) {
        if (@($known | Where-Object { $_.semanticSignature -like $signature }).Count -eq 0) {
            throw "Real Coverlet semantic mapping was not known for '$signature'."
        }
    }

    $table = @($document.evaluation.callables | ForEach-Object {
        [ordered]@{
            kind = $_.kind
            signature = $_.semanticSignature
            status = $_.coverageStatus
            reason = $_.coverageReason
            capability = $_.coverageCapability
            evidence = $_.mappingEvidenceKind
        }
    } | Sort-Object kind, signature | ConvertTo-Json -Depth 5 -Compress)
    $normalized += ($table -join '')
}

if (@($normalized | Select-Object -Unique).Count -ne 1) {
    throw 'Debug/Release and OpenCover/Cobertura callable capability tables differ.'
}

Write-Host "Validated four real Coverlet callable capability tables."
exit 0
