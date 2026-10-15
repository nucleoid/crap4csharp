$ErrorActionPreference='Stop'
$preparer=Join-Path $PSScriptRoot 'prepare-native-tool-assets.ps1'
$owned=[IO.Directory]::CreateTempSubdirectory('crap4csharp-native-guards-').FullName
$script:CrapNativeGuardSdkCalls=0
function dotnet {
    $script:CrapNativeGuardSdkCalls++
    throw 'SDK commands are forbidden in native preparation guard controls.'
}
try{
    $existing=Join-Path $owned existing
    [void][IO.Directory]::CreateDirectory($existing)
    $marker=Join-Path $existing retained.txt
    [IO.File]::WriteAllText($marker,'retain previous invocation')
    $repo=Join-Path $owned source
    [void][IO.Directory]::CreateDirectory($repo)
    Push-Location $repo
    try{
        & git init -q
        if($LASTEXITCODE -ne 0){throw 'Cannot initialize owned guard fixture.'}
        [IO.File]::WriteAllText((Join-Path $repo 'Source.cs'),'class Source {}')
        foreach($case in @('existing-output','invalid-version','dirty-source')){
            $out=if($case -ceq 'existing-output'){$existing}else{Join-Path $owned $case}
            $version=if($case -ceq 'invalid-version'){'0.1.0;Injected=true'}else{'0.1.0-guard'}
            $expected=switch($case){
                existing-output {'Native asset preparation requires a new owned output directory.'}
                invalid-version {'An explicit package version is required.'}
                dirty-source {'Native assets require a clean committed source tree.'}
            }
            $failure=$null
            try{& $preparer -Repository $repo -Version $version -OutputDirectory $out}catch{$failure=$_.Exception.Message}
            if($failure -cne $expected){throw "Unexpected native preparation guard result: $case / $failure"}
            if($case -cne 'existing-output' -and (Test-Path -LiteralPath $out)){throw 'Refused preparation created its output.'}
        }
    }finally{Pop-Location}
    if($script:CrapNativeGuardSdkCalls -ne 0){throw 'Refused preparation launched an SDK command.'}
    if([IO.File]::ReadAllText($marker) -cne 'retain previous invocation'){throw 'Earlier output was modified.'}
    Write-Output 'Native preparation guards passed: earlier output retained; invalid version and dirty source refused before SDK launch.'
}finally{
    $full=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'crap4csharp-native-guards-*'){throw 'Unsafe owned guard fixture cleanup.'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
