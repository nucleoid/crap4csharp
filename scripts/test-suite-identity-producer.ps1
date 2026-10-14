$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$producer=Join-Path $repository 'scripts/write-suite-identity.ps1'
$binder=Join-Path $repository 'scripts/assert-suite-identity.ps1'
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-identity-producer-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
Push-Location $owned
try{
    [IO.File]::WriteAllText((Join-Path $owned '.gitignore'),"bin/`nartifacts/`n*.nupkg`n")
    $source=Join-Path $owned 'Source.cs';[IO.File]::WriteAllText($source,'class A { int Value() => 42; }')
    & git init --object-format=sha1 -q
    if($LASTEXITCODE -ne 0){throw 'Owned identity repository setup failed.'}
    & git add .gitignore Source.cs
    if($LASTEXITCODE -ne 0){throw 'Owned identity fixture staging failed.'}
    & git -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm fixture
    if($LASTEXITCODE -ne 0){throw 'Owned identity fixture commit failed.'}
    $commit=(& git rev-parse HEAD).Trim()
    $runtime=Join-Path $owned 'tests/Crap4CSharp.Tests/bin/Release/net10.0'
    foreach($file in @('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json',
        'process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json','Crap4CSharp.Tests.deps.json','Crap4CSharp.Tests.runtimeconfig.json','Crap4CSharp.ProvenanceFixture.dll')){
        $path=Join-Path $runtime $file;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path));[IO.File]::WriteAllBytes($path,[byte[]](1,2,3))
    }
    $artifacts=Join-Path $owned 'artifacts';[void][IO.Directory]::CreateDirectory($artifacts)
    $discovery=Join-Path $artifacts 'discovery.json';[IO.File]::WriteAllText($discovery,'[{"ID":"fixture","DisplayName":"fixture"}]')
    $package=Join-Path $owned 'fixture.nupkg';[IO.File]::WriteAllBytes($package,[byte[]](4,5,6))
    $before=Join-Path $artifacts 'before.json';$after=Join-Path $artifacts 'after.json';$receipt=Join-Path $artifacts 'binding.json'
    $arguments=@{Configuration='Release';Commit=$commit;WorkflowRun='123';WorkflowAttempt='1';DiscoveryPath=$discovery;PackagePath=$package}
    & $producer @arguments -OutputPath $before
    & $producer @arguments -OutputPath $after
    & $binder -BeforePath $before -AfterPath $after -OutputPath $receipt
    $cases=@('runtime-deps','runtime-config','runtime-provenance','runtime-membership','source-preserved-time','discovery','package')
    foreach($case in $cases){
        $target=switch($case){
            runtime-deps {Join-Path $runtime 'Crap4CSharp.Tests.deps.json'}
            runtime-config {Join-Path $runtime 'Crap4CSharp.Tests.runtimeconfig.json'}
            runtime-provenance {Join-Path $runtime 'Crap4CSharp.ProvenanceFixture.dll'}
            runtime-membership {Join-Path $runtime 'Unexpected.dll'}
            source-preserved-time {$source}
            discovery {$discovery}
            package {$package}
        }
        $existed=Test-Path -LiteralPath $target
        $original=if($existed){[IO.File]::ReadAllBytes($target)}else{$null}
        $stamp=if($existed){[IO.File]::GetLastWriteTimeUtc($target)}else{$null}
        if($case -eq 'source-preserved-time'){[IO.File]::WriteAllText($target,'class A { int Value() => 43; }')}
        elseif($existed){$changed=[byte[]]$original.Clone();$changed[0]=$changed[0] -bxor 1;[IO.File]::WriteAllBytes($target,$changed)}
        else{[IO.File]::WriteAllBytes($target,[byte[]](7,8,9))}
        if($existed){[IO.File]::SetLastWriteTimeUtc($target,$stamp)}
        $accepted=$true
        try{& $producer @arguments -OutputPath $after;& $binder -BeforePath $before -AfterPath $after -OutputPath $receipt}catch{$accepted=$false}
        if($accepted){throw "Owned identity mutation accepted: $case"}
        if($existed){[IO.File]::WriteAllBytes($target,$original);[IO.File]::SetLastWriteTimeUtc($target,$stamp)}else{Remove-Item -LiteralPath $target}
    }
    Write-Output 'Identity producer controls passed: unchanged fixture plus seven real file mutations. Synthetic files are not compiled/package acceptance.'
}finally{
    Pop-Location
    $resolved=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'crap4csharp-identity-producer-*'){throw 'Unsafe owned identity cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
