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
if((Test-Path -LiteralPath $raw) -or (Test-Path -LiteralPath $output)){throw 'Discovery output must be new.'}
& dotnet $assembly -noColor -preEnumerateTheories -list full/json > $raw
if($LASTEXITCODE -ne 0){throw 'Complete discovery failed.'}
$cases=@(Get-Content -LiteralPath $raw -Raw|ConvertFrom-Json)
@(Convert-SuiteDiscoveredCases $cases $assembly)|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $output
if((Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant() -cne $hash -or [IO.File]::ReadAllText($seed) -cne $hash){
    throw 'Assembly/discovery identity changed during enumeration.'
}
