param(
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$WorkflowRun,
    [Parameter(Mandatory)][string]$WorkflowAttempt,
    [string]$PackagePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
$root=(Get-Location).ProviderPath
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Build artifact output requires a new directory.'}
Assert-SuiteBuildNoLinks $root $output
[void][IO.Directory]::CreateDirectory($output)
$discovery=Join-Path $output 'discovery.json'
& "$PSScriptRoot/write-suite-discovery.ps1" -AssemblyPath "tests/Crap4CSharp.Tests/bin/$Configuration/net10.0/Crap4CSharp.Tests.dll" `
    -OutputPath $discovery -InitializeAssemblyIdentity
$identityPath=Join-Path $output 'identity.json'
& "$PSScriptRoot/write-suite-identity.ps1" -Configuration $Configuration -Commit $Commit -WorkflowRun $WorkflowRun `
    -WorkflowAttempt $WorkflowAttempt -DiscoveryPath $discovery -PackagePath $PackagePath -OutputPath $identityPath
$identity=Get-Content -LiteralPath $identityPath -Raw|ConvertFrom-Json
$files=@(foreach($relative in Get-SuiteBuildOutputRoots $Configuration){
    $directory=Join-Path $root $relative
    if(!(Test-Path -LiteralPath $directory)){throw 'Required project output directory missing.'}
    Assert-SuiteBuildNoLinks $root $directory
    $entries=@(Get-ChildItem -LiteralPath $directory -Recurse -Force)
    if(@($entries|Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){throw 'Build output links refused.'}
    foreach($entry in $entries|Where-Object {!$_.PSIsContainer}){
        $path=[IO.Path]::GetRelativePath($root,$entry.FullName).Replace('\','/')
        Assert-SuiteBuildLocator $path $Configuration
        Assert-SuiteBuildNoLinks $root $entry.FullName
        [ordered]@{path=$path;length=$entry.Length;sha256=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant();
            unixMode=$(if($IsWindows){420}else{[int][IO.File]::GetUnixFileMode($entry.FullName)})}
    }
})
if(!$files.Count -or $files.Count -gt 10000){throw 'Build output inventory count unsupported.'}
$archive=Join-Path $output 'runtime.zip'
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in $files){
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $root $file.path),$file.path,[IO.Compression.CompressionLevel]::Optimal)
    }
}finally{$zip.Dispose()}
foreach($file in $files){
    if((Get-FileHash -LiteralPath (Join-Path $root $file.path)).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Build bytes changed during export.'}
}
[ordered]@{schemaVersion='suite-build-artifact-v1';identity=$identity;archiveSha256=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant();files=$files}|
    ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'build.json')
