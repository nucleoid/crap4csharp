param(
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt,
    [string]$PackagePath,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
$root=(Get-Location).ProviderPath
$artifact=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactDirectory)
foreach($name in @('build.json','runtime.zip')){Assert-SuiteBuildNoLinks $artifact (Join-Path $artifact $name)}
$manifest=Get-Content -LiteralPath (Join-Path $artifact 'build.json') -Raw|ConvertFrom-Json
Assert-ReceiptFields $manifest @('schemaVersion','identity','archiveSha256','files')
if($manifest.schemaVersion -cne 'suite-build-artifact-v1'){throw 'Unsupported build artifact.'}
Assert-SuiteObservedIdentity $manifest.identity
$identity=$manifest.identity
$os=if($IsWindows){'Windows'}elseif($IsLinux){'Linux'}else{throw 'Unsupported OS.'}
if($identity.configuration -cne $Configuration -or $identity.commit -cne $Commit -or $identity.workflowRun -cne $WorkflowRun -or
    $identity.operatingSystem -cne $os){throw 'Build artifact identity differs.'}
Assert-SuiteAttemptAtOrBefore $identity.workflowAttempt $WorkflowAttempt
$head=(& git --no-optional-locks rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $head -cne $Commit){throw 'Source head differs.'}
$tree=(& git --no-optional-locks rev-parse 'HEAD^{tree}').Trim()
if($LASTEXITCODE -ne 0 -or $tree -cne $identity.tree){throw 'Source tree differs.'}
$status=@(& git --no-optional-locks status --porcelain --untracked-files=all)
if($LASTEXITCODE -ne 0 -or $status.Count){throw 'Source worktree is not clean.'}
$sdk=(& dotnet --version).Trim()
if($LASTEXITCODE -ne 0 -or $sdk -cne $identity.sdk){throw 'SDK differs.'}
if((Get-SuiteTrackedWorktreeHash $root) -cne $identity.worktreeSha256){throw 'Authored source bytes differ.'}
$packageHash=if($PackagePath){(Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant()}else{$null}
if($packageHash -cne $identity.packageSha256){throw 'Package differs.'}
$archive=Join-Path $artifact 'runtime.zip'
if((Get-Item -LiteralPath $archive).Length -gt 1073741824 -or $manifest.archiveSha256 -cnotmatch '^[0-9a-f]{64}$' -or
    (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -cne $manifest.archiveSha256){throw 'Archive bytes differ or exceed limit.'}
$files=@($manifest.files)
if(!$files.Count -or $files.Count -gt 10000){throw 'Unsupported build output count.'}
$expected=[Collections.Generic.Dictionary[string,object]]::new($(if($IsWindows){[StringComparer]::OrdinalIgnoreCase}else{[StringComparer]::Ordinal}))
$total=0L
foreach($relative in Get-SuiteBuildOutputRoots $Configuration){
    $directory=Join-Path $root $relative
    Assert-SuiteBuildNoLinks $root $directory
    if(Test-Path -LiteralPath $directory){throw 'Build output root must be absent before import.'}
}
foreach($file in $files){
    Assert-ReceiptFields $file @('path','length','sha256','unixMode')
    if($file.path -isnot [string] -or $file.sha256 -isnot [string] -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid output identity.'}
    Assert-ReceiptInteger $file.length;Assert-ReceiptInteger $file.unixMode
    if($file.unixMode -gt 511){throw 'Unsupported file permissions.'}
    Assert-SuiteBuildLocator $file.path $Configuration
    if(!$expected.TryAdd($file.path,$file)){throw 'Duplicate output path.'}
    $total+=$file.length
    if($total -gt 2147483648){throw 'Expanded artifact exceeds limit.'}
    $destination=Join-Path $root $file.path
    Assert-SuiteBuildNoLinks $root $destination
    if(Test-Path -LiteralPath $destination){throw 'Existing build output refused.'}
}
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
    if($zip.Entries.Count -ne $files.Count){throw 'Archive membership differs.'}
    $seen=[Collections.Generic.HashSet[string]]::new($expected.Comparer)
    foreach($entry in $zip.Entries){
        $fileType=($entry.ExternalAttributes -shr 16) -band 0xf000
        if($fileType -notin @(0,0x8000)){throw 'Nonregular archive entry refused.'}
        if(!$seen.Add($entry.FullName) -or !$expected.ContainsKey($entry.FullName)){throw 'Unexpected archive path.'}
        $file=$expected[$entry.FullName]
        if($entry.Length -ne $file.length){throw 'Archive length differs.'}
        $stream=$entry.Open()
        try{if((Get-SuiteStreamHash $stream) -cne $file.sha256){throw 'Archive entry bytes differ.'}}finally{$stream.Dispose()}
    }
    foreach($entry in $zip.Entries){
        $destination=Join-Path $root $entry.FullName
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        $source=$entry.Open();$target=[IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$source.CopyTo($target)}finally{$source.Dispose();$target.Dispose()}
        if(!$IsWindows){[IO.File]::SetUnixFileMode($destination,([IO.UnixFileMode]($expected[$entry.FullName].unixMode)))}
    }
}finally{$zip.Dispose()}
foreach($file in $files){
    if((Get-FileHash -LiteralPath (Join-Path $root $file.path)).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Imported output bytes differ.'}
}
$runtime=Join-Path $root "tests/Crap4CSharp.Tests/bin/$Configuration/net10.0"
$paths=@(Get-ChildItem -LiteralPath $runtime -File -Recurse -Force|ForEach-Object {[IO.Path]::GetRelativePath($runtime,$_.FullName)})
if((Get-InventoryHash $runtime $paths) -cne $identity.runtimeInventorySha256){throw 'Imported runtime inventory differs.'}
foreach($file in $identity.files.PSObject.Properties){
    if((Get-FileHash -LiteralPath (Join-Path $runtime $file.Name)).Hash.ToLowerInvariant() -cne $file.Value){throw 'Imported assembly differs.'}
}
