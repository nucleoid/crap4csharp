param(
    [Parameter(Mandatory)][string] $DotnetRoot,
    [string] $Version = '9.0.20'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^9\.0\.\d+$') { throw 'Only an explicit .NET 9 consumer runtime version is supported.' }
$root = (Resolve-Path -LiteralPath $DotnetRoot).Path
$destination = Join-Path $root "shared/Microsoft.NETCore.App/$Version"
if (Test-Path -LiteralPath $destination) {
    throw 'Consumer runtime destination already exists; use a fresh acceptance SDK root.'
}
$rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } else { throw 'Acceptance supports Windows/Linux x64 only.' }
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'Acceptance requires an x64 host.'
}
$releases = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json'
$release = @($releases.releases | Where-Object { $_.runtime.version -ceq $Version })
if ($release.Count -ne 1) { throw 'Exact consumer runtime release is missing or ambiguous.' }
$suffix = if ($IsWindows) { '.zip' } else { '.tar.gz' }
$files = @($release[0].runtime.files | Where-Object { $_.rid -ceq $rid -and $_.name.EndsWith($suffix, [StringComparison]::Ordinal) })
if ($files.Count -ne 1 -or $files[0].hash -notmatch '^[a-fA-F0-9]{128}$' -or
    ([Uri]$files[0].url).Host -cne 'builds.dotnet.microsoft.com' -or ([Uri]$files[0].url).Scheme -cne 'https') {
    throw 'Official runtime archive identity is unproven.'
}
$owned = Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-runtime-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($owned) | Out-Null
$archive = Join-Path $owned ('runtime' + $suffix)
Invoke-WebRequest $files[0].url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -cne $files[0].hash.ToLowerInvariant()) {
    throw 'Consumer runtime archive checksum mismatch.'
}
$extracted = Join-Path $owned 'extracted'
[IO.Directory]::CreateDirectory($extracted) | Out-Null
if ($IsWindows) { Expand-Archive -LiteralPath $archive -DestinationPath $extracted }
else {
    & tar -xzf $archive -C $extracted
    if ($LASTEXITCODE -ne 0) { throw 'Consumer runtime extraction failed.' }
}
$runtime = Join-Path $extracted "shared/Microsoft.NETCore.App/$Version"
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'System.Private.CoreLib.dll'))) { throw 'Extracted runtime is incomplete.' }
# Copy only the shared runtime. Preserve the selected SDK, host and hostfxr.
[IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
Copy-Item -LiteralPath $runtime -Destination $destination -Recurse
[ordered]@{ version = $Version; rid = $rid; sha512 = $files[0].hash.ToLowerInvariant(); sdkHostPreserved = $true } | ConvertTo-Json
# Retain this invocation's bounded archive for receipt/debugging; no consumer cleanup.
