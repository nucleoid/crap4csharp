$ErrorActionPreference='Stop'
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
$owned=[IO.Directory]::CreateTempSubdirectory('crap4csharp-build-controls-').FullName
Push-Location $owned
try{
    Set-Content -LiteralPath '.gitignore' "bin/`nartifacts/"
    Set-Content -LiteralPath 'Source.cs' 'class Source {}'
    & git init -q
    if($LASTEXITCODE -ne 0){throw 'Fixture init failed.'}
    & git add .gitignore Source.cs
    if($LASTEXITCODE -ne 0){throw 'Fixture add failed.'}
    & git -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm fixture
    if($LASTEXITCODE -ne 0){throw 'Fixture commit failed.'}
    $head=(& git rev-parse HEAD).Trim();$tree=(& git rev-parse 'HEAD^{tree}').Trim()
    $sdk=(& dotnet --version).Trim();if($sdk -cne (Get-SuiteAcceptanceSdkVersion)){throw 'Pinned SDK required.'}
    $artifact=Join-Path $owned 'artifacts/build';[void][IO.Directory]::CreateDirectory($artifact)
    Set-Content -LiteralPath (Join-Path $artifact 'discovery.json') '[]'
    $names=@('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json',
        'process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')
    $payload=@(foreach($name in $names){
        [ordered]@{path="tests/Crap4CSharp.Tests/bin/Release/net10.0/$name";length=3;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]](1,2,3))).ToLowerInvariant();unixMode=420}
    })
    $hashes=[ordered]@{};foreach($name in $names){$hashes[$name]=$payload[0].sha256}
    $inventory=@(foreach($name in ($names|Sort-Object -CaseSensitive)){[ordered]@{path=$name;sha256=$payload[0].sha256}})
    $identity=[ordered]@{schemaVersion='suite-identity-v1';outcome='observed';configuration='Release';commit=$head;tree=$tree;sdk=$sdk;
        operatingSystem=$(if($IsWindows){'Windows'}else{'Linux'});workflowRun='123';workflowAttempt='1';packageSha256=$null;
        discoverySha256=(Get-FileHash (Join-Path $artifact 'discovery.json')).Hash.ToLowerInvariant();
        worktreeSha256=(Get-SuiteTrackedWorktreeHash $owned);
        runtimeInventorySha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($inventory|ConvertTo-Json -Depth 3 -Compress)))).ToLowerInvariant();files=$hashes}
    $controls=@('positive','archive-tamper','entry-tamper','traversal','wrong-root','duplicate','future-attempt','wrong-run','wrong-source',
        'wrong-runtime','wrong-package','wrong-mode','symlink-entry','existing-output')
    foreach($control in $controls){
        $manifest=[ordered]@{schemaVersion='suite-build-artifact-v1';identity=($identity|ConvertTo-Json -Depth 5|ConvertFrom-Json);
            archiveSha256='';files=@($payload|ConvertTo-Json -Depth 4|ConvertFrom-Json)}
        switch($control){
            traversal {$manifest.files[0].path='tests/Crap4CSharp.Tests/bin/Release/net10.0/../../outside.dll'}
            wrong-root {$manifest.files[0].path='Source.cs'}
            duplicate {$manifest.files+=@($manifest.files[0])}
            future-attempt {$manifest.identity.workflowAttempt='2'}
            wrong-run {$manifest.identity.workflowRun='999'}
            wrong-source {$manifest.identity.worktreeSha256='a'*64}
            wrong-runtime {$manifest.identity.runtimeInventorySha256='a'*64}
            wrong-package {$manifest.identity.packageSha256='a'*64}
            wrong-mode {$manifest.files[0].unixMode=4095}
        }
        $archive=Join-Path $artifact "$control.zip"
        $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
        try{
            foreach($file in $manifest.files){
                $entry=$zip.CreateEntry($file.path)
                if($control -ceq 'symlink-entry'){$entry.ExternalAttributes=-1610612736}
                $stream=$entry.Open()
                try{$bytes=if($control -ceq 'entry-tamper'){[byte[]](4,5,6)}else{[byte[]](1,2,3)};$stream.Write([byte[]]$bytes,0,3)}finally{$stream.Dispose()}
            }
        }finally{$zip.Dispose()}
        $manifest.archiveSha256=(Get-FileHash $archive).Hash.ToLowerInvariant()
        Copy-Item -LiteralPath $archive -Destination (Join-Path $artifact 'runtime.zip') -Force
        if($control -ceq 'archive-tamper'){[IO.File]::AppendAllText((Join-Path $artifact 'runtime.zip'),'tamper')}
        $manifest|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $artifact 'build.json')
        if($control -ceq 'existing-output'){
            [void][IO.Directory]::CreateDirectory((Join-Path $owned 'tests/Crap4CSharp.Tests/bin/Release/net10.0'))
        }
        $accepted=$true
        try{& "$PSScriptRoot/import-suite-build.ps1" -Configuration Release -Commit $head -WorkflowRun '123' -WorkflowAttempt '1' -ArtifactDirectory $artifact}catch{$accepted=$false}
        if($accepted -ne ($control -ceq 'positive')){throw "Unexpected shared artifact acceptance: $control"}
        foreach($relative in Get-SuiteBuildOutputRoots 'Release'){
            $directory=[IO.Path]::GetFullPath((Join-Path $owned $relative))
            $prefix=[IO.Path]::TrimEndingDirectorySeparator($owned)+[IO.Path]::DirectorySeparatorChar
            if(!$directory.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'Unsafe fixture cleanup.'}
            if(Test-Path -LiteralPath $directory){Assert-SuiteBuildNoLinks $owned $directory;Remove-Item -LiteralPath $directory -Recurse -Force}
        }
    }
    Write-Output "Shared build artifact controls passed ($($controls.Count)); synthetic transport/identity evidence only."
}finally{
    Pop-Location
    $absolute=[IO.Path]::GetFullPath($owned);$prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-build-controls-*'){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
