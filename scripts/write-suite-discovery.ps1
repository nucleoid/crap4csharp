param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$InitializeAssemblyIdentity
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
. "$PSScriptRoot/suite-discovery-contract.ps1"
$assembly=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($AssemblyPath)
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$seed=[IO.Path]::ChangeExtension($assembly,'.uniqueid')
$hash=(Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant()
if($InitializeAssemblyIdentity -and !(Test-Path -LiteralPath $seed)){
    [IO.File]::WriteAllText($seed,$hash,[Text.UTF8Encoding]::new($false))
}
if(!(Test-Path -LiteralPath $seed) -or [IO.File]::ReadAllText($seed) -cne $hash){throw 'Assembly discovery identity is not bound to its bytes.'}
Assert-SuiteBuildNoLinks ([IO.Path]::GetDirectoryName($assembly)) $seed
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$raw=$output+'.raw.json'
$stderr=$output+'.stderr.txt'
$diagnostic=$output+'.diagnostic.json'
if((Test-Path -LiteralPath $raw) -or (Test-Path -LiteralPath $output) -or
    (Test-Path -LiteralPath $stderr) -or (Test-Path -LiteralPath $diagnostic)){throw 'Discovery output must be new.'}
$exitCode=$null;$accepted=$false
try{
    & dotnet $assembly -noColor -preEnumerateTheories -list full/json > $raw 2> $stderr
    $exitCode=$LASTEXITCODE
    if($exitCode -ne 0){throw 'Complete discovery failed.'}
    $cases=@(Convert-SuiteDiscoveryOutput ([IO.File]::ReadAllText($raw)))
    @(Convert-SuiteDiscoveredCases $cases $assembly)|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $output
    $accepted=$true
}finally{
    # No case names, raw text, paths or messages are uploaded with this diagnostic.
    $rawText=if(Test-Path -LiteralPath $raw){[IO.File]::ReadAllText($raw)}else{''}
    [ordered]@{schemaVersion='suite-discovery-diagnostic-v1';accepted=$accepted;exitCode=$exitCode;
        stdoutSha256=if(Test-Path -LiteralPath $raw){(Get-FileHash -LiteralPath $raw).Hash.ToLowerInvariant()}else{$null};
        stderrSha256=if(Test-Path -LiteralPath $stderr){(Get-FileHash -LiteralPath $stderr).Hash.ToLowerInvariant()}else{$null};
        knownShutdownNotice=$rawText.TrimEnd().EndsWith("`nWaiting 10 seconds for foreground threads to exit...",[StringComparison]::Ordinal);
        qualification='Diagnostic only; no acceptance or causal claim.'}|ConvertTo-Json|Set-Content -LiteralPath $diagnostic
}
if((Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant() -cne $hash -or [IO.File]::ReadAllText($seed) -cne $hash){
    throw 'Assembly/discovery identity changed during enumeration.'
}
