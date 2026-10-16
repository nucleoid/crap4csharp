param(
    [string]$NativeAssetsDirectory,
    [string]$PackagePath,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [string]$Repository=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
$repo=(Resolve-Path -LiteralPath $Repository).Path
$archive=$null;$packageHash=$null
if([bool]$NativeAssetsDirectory -eq [bool]$PackagePath){throw 'Choose exactly one native asset source.'}
if($PackagePath){
    if($Configuration -cne 'Release'){throw 'Packaged native bytes are Release only.'}
    $PackagePath=(Resolve-Path -LiteralPath $PackagePath).Path
    $packageHash=(Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant()
    & "$PSScriptRoot/assert-package-metadata.ps1" -PackagePath $PackagePath -ExpectedVersion $Version -RepoRoot $repo
    if((Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant() -cne $packageHash){throw 'Package changed during validation.'}
    $archive=[IO.Compression.ZipFile]::OpenRead($PackagePath)
    $stream=$archive.GetEntry('native-tool-assets.json').Open()
    try{
        $reader=[IO.StreamReader]::new($stream)
        $manifestText=$reader.ReadToEnd()
    }finally{$stream.Dispose()}
    $manifest=$manifestText|ConvertFrom-Json
    $manifestHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifestText))).ToLowerInvariant()
}else{
    $assets=(Resolve-Path -LiteralPath $NativeAssetsDirectory).Path
    $manifestPath=Join-Path $assets manifest.json
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    $manifestHash=(Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant()
}
Push-Location $repo
try{
    $head=(& git rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0){throw 'Cannot identify test runtime source.'}
    $status=@(& git --no-optional-locks status --porcelain --untracked-files=all)
    if($LASTEXITCODE -ne 0 -or $status.Count){throw 'Native test runtime requires clean committed source.'}
    $sourceHash=Get-SuiteTrackedWorktreeHash $repo
    if($manifest.schemaVersion -cne 'native-tool-assets-v1' -or $manifest.commit -cne $head -or
        (!$PackagePath -and $manifest.worktreeSha256 -cne (Get-SuiteTrackedWorktreeHash $repo)) -or $manifest.version -cne $Version -or
        $manifest.sdk -cne (Get-SuiteAcceptanceSdkVersion (Join-Path $repo global.json)) -or $manifest.configuration -cne $Configuration){
        throw 'Prepared assets do not match the test source/version/configuration.'
    }
    if([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64){throw 'Native acceptance requires an x64 host.'}
    $rid=if($IsWindows){'win-x64'}elseif($IsLinux){'linux-x64'}else{throw 'Native acceptance requires Windows or Linux.'}
    $groups=@($manifest.groups|Where-Object {$_.rid -ceq $rid})
    if($groups.Count -ne 1){throw 'Prepared assets have no unique current-platform group.'}
    if(!$PackagePath){$source=Join-Path $assets "tools/$rid"}
    $runtime=Join-Path $repo "tests/Crap4CSharp.Tests/bin/$Configuration/net10.0"
    Assert-SuiteBuildNoLinks $repo $runtime
    $assembly=Join-Path $runtime Crap4CSharp.Tests.dll
    $receiptPath=Join-Path $runtime native-tool-runtime.json
    if(!(Test-Path -LiteralPath $assembly) -or (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($assembly,'.uniqueid'))) -or
        (Test-Path -LiteralPath $receiptPath)){throw 'Overlay requires a built runtime before discovery or native identity creation.'}
    $files=@($groups[0].files.PSObject.Properties)
    if($files.Count -lt 8 -or $files.Count -gt 10000){throw 'Native runtime inventory is incomplete or oversized.'}
    if(!$PackagePath){
        $entries=@(Get-ChildItem -LiteralPath $source -Force -Recurse)
        foreach($entry in $entries){Assert-SuiteBuildNoLinks $source $entry.FullName}
        if(@($entries|Where-Object {!$_.PSIsContainer}).Count -ne $files.Count){throw 'Prepared native inventory changed.'}
    }
    foreach($file in $files){
        if([IO.Path]::IsPathFullyQualified($file.Name) -or $file.Name.Contains('\') -or $file.Name.Contains(':') -or
            @($file.Name.Split('/')|Where-Object {$_ -ceq '.' -or $_ -ceq '..'}).Count){throw 'Unsafe native asset locator.'}
        Assert-SuiteBuildNoLinks $runtime (Join-Path $runtime $file.Name)
        if(!$PackagePath){
            $path=Join-Path $source $file.Name
            Assert-SuiteBuildNoLinks $source $path
            if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.Value){throw 'Prepared native asset changed.'}
        }
    }
    foreach($file in $files){
        $destination=Join-Path $runtime $file.Name
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        if($PackagePath){
            $entry=$archive.GetEntry("tools/net10.0/$rid/"+$file.Name)
            if($null -eq $entry){throw 'Packaged native file missing.'}
            $inputStream=$entry.Open();$outputStream=[IO.File]::Create($destination)
            try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose();$inputStream.Dispose()}
        }else{[IO.File]::Copy((Join-Path $source $file.Name),$destination,$true)}
    }
    foreach($file in $files){
        if((Get-FileHash -LiteralPath (Join-Path $runtime $file.Name)).Hash.ToLowerInvariant() -cne $file.Value){throw 'Native runtime copy changed.'}
        if(!$PackagePath -and (Get-FileHash -LiteralPath (Join-Path $source $file.Name)).Hash.ToLowerInvariant() -cne $file.Value){throw 'Prepared native asset changed.'}
    }
    if($PackagePath){
        if((Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant() -cne $packageHash){throw 'Candidate package changed during overlay.'}
    }elseif((Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant() -cne $manifestHash -or
        (Get-SuiteTrackedWorktreeHash $repo) -cne $manifest.worktreeSha256){throw 'Source or asset identity changed during overlay.'}
    if((Get-SuiteTrackedWorktreeHash $repo) -cne $sourceHash -or (& git rev-parse HEAD).Trim() -cne $head){throw 'Test source changed during overlay.'}
    [ordered]@{schemaVersion='native-test-runtime-v1';commit=$head;version=$Version;configuration=$Configuration;
        rid=$rid;assetManifestSha256=$manifestHash;packageSha256=$packageHash;files=$groups[0].files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $receiptPath
}finally{if($archive){$archive.Dispose()};Pop-Location}
