param(
    [Parameter(Mandatory)][string]$NativeAssetsDirectory,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Repository=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath $Repository).Path
$assets=(Resolve-Path -LiteralPath $NativeAssetsDirectory).Path
$out=[IO.Path]::GetFullPath($OutputDirectory,$repo)
if(Test-Path -LiteralPath $out){throw 'Pack refusal controls require a new output directory.'}
[void][IO.Directory]::CreateDirectory($out)
$owned=[IO.Directory]::CreateTempSubdirectory('crap4csharp-native-pack-').FullName
$original=(Get-FileHash -LiteralPath (Join-Path $assets manifest.json)).Hash
Push-Location $repo
try{
    $head=(& git rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0){throw 'Pack control source unavailable.'}
    foreach($case in @('stale-commit','wrong-configuration','changed-bytes')){
        $fixture=Join-Path $owned $case
        [void][IO.Directory]::CreateDirectory($fixture)
        Copy-Item -LiteralPath (Join-Path $assets tools) -Destination (Join-Path $fixture tools) -Recurse
        $manifest=Get-Content -LiteralPath (Join-Path $assets manifest.json) -Raw|ConvertFrom-Json
        $expected='Prepared native assets have stale source, version, SDK or configuration.'
        switch($case){
            stale-commit {$manifest.commit='0'*40}
            wrong-configuration {$manifest.configuration='Debug'}
            changed-bytes {
                [IO.File]::WriteAllBytes((Join-Path $fixture 'tools/linux-x64/Microsoft.CodeAnalysis.Workspaces.MSBuild.dll'),[byte[]](1,2,3))
                $expected='Prepared native bytes changed.'
            }
        }
        $manifest|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $fixture manifest.json)
        $log=Join-Path $out "$case.log"
        $feed=Join-Path $out "$case-feed"
        & dotnet pack src/Crap4CSharp.Tool/Crap4CSharp.Tool.csproj -c Release --no-build --no-restore -m:1 -nr:false -o $feed "-p:Version=$Version" "-p:CrapNativeToolAssetsRoot=$fixture/tools" *> $log
        $code=$LASTEXITCODE
        if($code -eq 0 -or !(Get-Content -LiteralPath $log -Raw).Contains($expected) -or
            @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -ErrorAction SilentlyContinue).Count){throw "Normal pack failed to refuse $case precisely."}
    }
    if((Get-FileHash -LiteralPath (Join-Path $assets manifest.json)).Hash -cne $original){throw 'Original native manifest changed.'}
    $status=@(& git --no-optional-locks status --porcelain --untracked-files=all)
    if($LASTEXITCODE -ne 0 -or $status.Count -or (& git rev-parse HEAD).Trim() -cne $head){throw 'Pack controls changed source.'}
    [ordered]@{schemaVersion='native-pack-refusals-v1';passed=3;originalManifestUnchanged=$true;
        cases=@('stale-commit','wrong-configuration','changed-bytes')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out receipt.json)
}finally{
    Pop-Location
    $full=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'crap4csharp-native-pack-*'){throw 'Unsafe owned pack fixture cleanup.'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
