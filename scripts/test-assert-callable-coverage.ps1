param(
    [Parameter(Mandatory = $true)][string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
$pwsh = (Get-Process -Id $PID).Path
$target = Join-Path $RepoRoot 'scripts/assert-callable-coverage.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("crap4csharp-coverage-script-{0}" -f [Guid]::NewGuid().ToString('N'))

$validDocument = @{
    complexityRulesetVersion = 'callables-v1'
    evaluation = @{
        callables = @(
            @{ kind = 'constructor'; semanticSignature = 'Fixture.Sample..ctor()'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'indexer-get'; semanticSignature = 'Fixture.Sample.this.get(System.Int32)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'indexer-set'; semanticSignature = 'Fixture.Sample.this.set(System.Int32,System.String)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'operator'; semanticSignature = 'Fixture.Sample.operator +(Fixture.Sample,Fixture.Sample)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'conversion'; semanticSignature = 'Fixture.Sample.conversion(Fixture.Sample)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'method'; semanticSignature = 'Fixture.IProbe.Read()'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'method'; semanticSignature = 'Fixture.Box.Echo()'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'method'; semanticSignature = 'Fixture.Sample.ArrayLength()'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'method'; semanticSignature = 'Fixture.Sample.Sum(System.Collections.Generic.List)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'method'; semanticSignature = 'Fixture.Sample.Describe(System.Int32)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'method'; semanticSignature = 'Fixture.Sample.Describe(System.String)'; coverageStatus = 'known'; coverageReason = $null; coverageCapability = 'supported'; mappingEvidenceKind = 'semanticSourceIdentity'; documents = @('Sample.cs') }
            @{ kind = 'lambda'; semanticSignature = 'Fixture.Sample.lambda'; coverageStatus = 'unknown'; coverageReason = 'coverage.unsupportedGeneratedMapping'; coverageCapability = 'unsupported'; mappingEvidenceKind = $null; documents = @() }
        )
    }
}

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$DotNetExit,
        [Parameter(Mandatory = $true)][string]$Payload,
        [Parameter(Mandatory = $true)][int]$ExpectedExit
    )

    $caseRoot = Join-Path $root $Name
    $debug = Join-Path $caseRoot 'debug'
    $release = Join-Path $caseRoot 'release'
    New-Item -ItemType Directory -Path $debug, $release -Force | Out-Null
    foreach ($path in @(
        (Join-Path $debug 'coverage.opencover.xml'),
        (Join-Path $debug 'coverage.cobertura.xml'),
        (Join-Path $release 'coverage.opencover.xml'),
        (Join-Path $release 'coverage.cobertura.xml')
    )) {
        Set-Content -LiteralPath $path -Value '<coverage />' -NoNewline
    }

    $payloadBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Payload))
    $runner = Join-Path $caseRoot 'runner.ps1'
    @"
`$ErrorActionPreference = 'Stop'
function global:dotnet {
    Write-Output ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$payloadBase64')))
    `$global:LASTEXITCODE = $DotNetExit
}
& '$($target.Replace("'", "''"))' -RepoRoot '$($RepoRoot.Replace("'", "''"))' -DebugResults '$($debug.Replace("'", "''"))' -ReleaseResults '$($release.Replace("'", "''"))'
if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit `$LASTEXITCODE }
"@ | Set-Content -LiteralPath $runner -NoNewline

    & $pwsh -NoLogo -NoProfile -NonInteractive -File $runner *> (Join-Path $caseRoot 'output.log')
    $actualExit = $LASTEXITCODE
    if ($actualExit -ne $ExpectedExit) {
        $output = Get-Content -LiteralPath (Join-Path $caseRoot 'output.log') -Raw
        throw "Case '$Name' expected exit $ExpectedExit, got $actualExit.`n$output"
    }
}

try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $validJson = $validDocument | ConvertTo-Json -Depth 8 -Compress
    Invoke-Case -Name 'expected-one-is-script-success' -DotNetExit 1 -Payload $validJson -ExpectedExit 0
    Invoke-Case -Name 'unexpected-native-exit-still-fails' -DotNetExit 2 -Payload $validJson -ExpectedExit 1
    Invoke-Case -Name 'malformed-json-still-fails' -DotNetExit 1 -Payload '{' -ExpectedExit 1

    $wrongRuleset = $validDocument.Clone()
    $wrongRuleset.complexityRulesetVersion = 'ordinary-methods-v1'
    Invoke-Case -Name 'wrong-ruleset-still-fails' -DotNetExit 1 -Payload ($wrongRuleset | ConvertTo-Json -Depth 8 -Compress) -ExpectedExit 1

    $invalidIdentity = $validDocument | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    $invalidIdentity.evaluation.callables[0].mappingEvidenceKind = 'lineProximity'
    Invoke-Case -Name 'invalid-mapping-identity-still-fails' -DotNetExit 1 -Payload ($invalidIdentity | ConvertTo-Json -Depth 8 -Compress) -ExpectedExit 1
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

Write-Host 'Validated callable coverage assertion exit semantics.'
