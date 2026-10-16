param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z][0-9A-Za-z.-]*$') { throw 'Use a new explicit prerelease SemVer candidate.' }
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    $dirty = & git --no-optional-locks status --porcelain
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Candidate preparation requires a clean committed source tree.' }
    $commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
    $tree = & git rev-parse 'HEAD^{tree}'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source tree.' }
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -cne (Get-SuiteAcceptanceSdkVersion)) { throw 'Candidate acceptance requires the exact declared SDK version.' }
    $destination = [IO.Path]::GetFullPath($OutputDirectory, $repository)
    if (Test-Path -LiteralPath $destination) { throw 'Candidate output must be a new owned directory.' }
    New-Item -ItemType Directory -Path $destination | Out-Null
    & dotnet restore Crap4CSharp.slnx --disable-parallel -nr:false "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Candidate restore failed.' }
    & dotnet build Crap4CSharp.slnx -c Release --no-restore -m:1 -nr:false "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }
    $native = Join-Path $destination 'native-assets'
    & "$PSScriptRoot/prepare-native-tool-assets.ps1" -Version $Version -Configuration Release -OutputDirectory $native
    & dotnet pack src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj -c Release --no-build --no-restore -m:1 -nr:false -o $destination "-p:Version=$Version" "-p:CrapNativeToolAssetsRoot=$native/tools"
    if ($LASTEXITCODE -ne 0) { throw 'Candidate pack failed.' }
    $packages = @(Get-ChildItem -LiteralPath $destination -Filter '*.nupkg')
    if ($packages.Count -ne 1) { throw 'Expected exactly one candidate nupkg.' }
    $metadata = & "$PSScriptRoot/assert-package-metadata.ps1" -PackagePath $packages[0].FullName -ExpectedVersion $Version -NativeAssetsDirectory $native
    & "$PSScriptRoot/test-native-tool-preparation.ps1"
    & "$PSScriptRoot/test-native-tool-package.ps1" -PackagePath $packages[0].FullName -ExpectedVersion $Version
    & "$PSScriptRoot/test-native-tool-pack-binding.ps1" -NativeAssetsDirectory $native -Version $Version -OutputDirectory (Join-Path $destination 'pack-refusals')
    $afterStatus = & git --no-optional-locks status --porcelain
    if ($LASTEXITCODE -ne 0 -or $afterStatus) { throw 'Candidate preparation changed source or could not verify its state.' }
    $afterCommit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $afterCommit -ne $commit) { throw 'Candidate source commit changed during preparation.' }
    $afterTree = & git rev-parse 'HEAD^{tree}'
    if ($LASTEXITCODE -ne 0 -or $afterTree -ne $tree) { throw 'Candidate source tree changed during preparation.' }
    $receipt = [ordered]@{
        schemaVersion = 'candidate-preparation-v1'; version = $Version; commit = $commit; tree = $tree
        sdk = $sdk; package = $packages[0].Name
        sha256 = (Get-FileHash -LiteralPath $packages[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        metadata = $metadata; acceptance = 'pending'; publicationAuthorized = $false
    }
    $receipt | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $destination 'candidate.json')
} finally { Pop-Location }
