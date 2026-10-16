param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$Repository=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
$repo=(Resolve-Path -LiteralPath $Repository).Path
$out=[IO.Path]::GetFullPath($OutputDirectory,$repo)
if(Test-Path -LiteralPath $out){throw 'Native asset preparation requires a new owned output directory.'}
if($Version -cnotmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$'){throw 'An explicit package version is required.'}
Push-Location $repo
try{
    $status=@(& git --no-optional-locks status --porcelain --untracked-files=all)
    if($LASTEXITCODE -ne 0 -or $status.Count){throw 'Native assets require a clean committed source tree.'}
    $head=(& git rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0){throw 'Cannot identify native asset source commit.'}
    $tree=(& git rev-parse 'HEAD^{tree}').Trim()
    if($LASTEXITCODE -ne 0){throw 'Cannot identify native asset source tree.'}
    $sourceHash=Get-SuiteTrackedWorktreeHash $repo
    $sdk=(& dotnet --version).Trim()
    if($LASTEXITCODE -ne 0 -or $sdk -cne (Get-SuiteAcceptanceSdkVersion (Join-Path $repo global.json))){throw 'Native preparation requires the declared acceptance SDK.'}
    [void][IO.Directory]::CreateDirectory($out)
    $groups=@()
    foreach($rid in @('win-x64','linux-x64')){
        $destination=Join-Path $out "tools/$rid"
        $properties=@('-p:PackAsTool=false','-p:PublishReadyToRun=true','-p:PublishReadyToRunComposite=false',
            '-p:SelfContained=false','-p:RuntimeFrameworkVersion=10.0.0','-p:UseSharedCompilation=false',
            '-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$out/build-$rid","-p:Version=$Version",
            '-p:PublishReadyToRunCrossgen2ExtraArgs=--parallelism=1')
        $project='src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj'
        & dotnet restore $project -r $rid --disable-parallel -nr:false @properties *> (Join-Path $out "restore-$rid.log")
        if($LASTEXITCODE -ne 0){throw "Native asset restore failed for $rid."}
        & dotnet publish $project -c $Configuration -r $rid --self-contained false --no-restore -m:1 -nr:false @properties -o $destination *> (Join-Path $out "publish-$rid.log")
        if($LASTEXITCODE -ne 0){throw "Native asset preparation failed for $rid."}
        $runtime=Get-Content -LiteralPath (Join-Path $destination 'Crap4CSharp.Tool.runtimeconfig.json') -Raw|ConvertFrom-Json
        if($runtime.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App' -or $runtime.runtimeOptions.framework.version -cne '10.0.0'){
            throw 'Native assets changed the declared runtime floor.'
        }
        $headers=[ordered]@{}
        foreach($name in @('Crap4CSharp.Tool.dll','Crap4CSharp.Core.dll','Microsoft.CodeAnalysis.dll',
            'Microsoft.CodeAnalysis.CSharp.dll','Microsoft.CodeAnalysis.Workspaces.dll','Microsoft.CodeAnalysis.CSharp.Workspaces.dll')){
            $reader=[Reflection.PortableExecutable.PEReader]::new([IO.File]::OpenRead((Join-Path $destination $name)))
            try{$headers[$name]=$reader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size}finally{$reader.Dispose()}
            if($headers[$name] -le 0){throw "Native image missing: $rid/$name."}
        }
        $files=[ordered]@{}
        $entries=@(Get-ChildItem -LiteralPath $destination -Force -Recurse)
        foreach($entry in $entries){Assert-SuiteBuildNoLinks $destination $entry.FullName}
        foreach($file in $entries|Where-Object {!$_.PSIsContainer}){
            $relative=[IO.Path]::GetRelativePath($destination,$file.FullName).Replace('\','/')
            $files[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $groups+=@([ordered]@{rid=$rid;runtimeFloor='10.0.0';nativeHeaders=$headers;files=$files})
    }
    foreach($group in $groups){
        $destination=Join-Path $out ('tools/'+$group.rid)
        foreach($relative in $group.files.Keys){
            if((Get-FileHash -LiteralPath (Join-Path $destination $relative)).Hash.ToLowerInvariant() -cne $group.files[$relative]){
                throw 'Native asset bytes changed during preparation.'
            }
        }
    }
    $status=@(& git --no-optional-locks status --porcelain --untracked-files=all)
    if($LASTEXITCODE -ne 0 -or $status.Count -or (& git rev-parse HEAD).Trim() -cne $head -or
        (Get-SuiteTrackedWorktreeHash $repo) -cne $sourceHash){throw 'Source changed during native preparation.'}
    [ordered]@{schemaVersion='native-tool-assets-v1';commit=$head;tree=$tree;worktreeSha256=$sourceHash;
        sdk=$sdk;version=$Version;configuration=$Configuration;groups=$groups;publicationAuthorized=$false}|
        ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $out manifest.json)
}finally{Pop-Location}
