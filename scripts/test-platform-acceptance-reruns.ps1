$ErrorActionPreference='Stop'
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-platform-reruns-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
try{
    $controls=@('positive','mixed-attempt','latest-failed','future-attempt','missing-platform','wrong-run','wrong-envelope',
        'wrong-os','malformed-latest','extra-file','missing-receipt','invalid-name','existing-output')
    foreach($control in $controls){
        $fixture=Join-Path $owned $control
        $inputRoot=Join-Path $fixture 'input';$outputRoot=Join-Path $fixture 'output'
        [void][IO.Directory]::CreateDirectory($inputRoot)
        foreach($runner in @('windows-latest','ubuntu-latest')){
            if($control -ceq 'missing-platform' -and $runner -ceq 'ubuntu-latest'){continue}
            $directory=Join-Path $inputRoot "acceptance-$runner-attempt-1"
            [void][IO.Directory]::CreateDirectory($directory)
            [ordered]@{workflowRun='123';workflowAttempt='1';operatingSystem=$(if($runner -ceq 'windows-latest'){'Windows'}else{'Linux'});outcome='passed'} |
                ConvertTo-Json | Set-Content (Join-Path $directory 'acceptance.json')
        }
        if($control -cnotin @('positive','missing-platform','existing-output')){
            $attempt=if($control -ceq 'future-attempt'){'3'}else{'2'}
            $directory=Join-Path $inputRoot "acceptance-windows-latest-attempt-$attempt"
            [void][IO.Directory]::CreateDirectory($directory)
            $receipt=[ordered]@{workflowRun='123';workflowAttempt=$attempt;operatingSystem='Windows';outcome='passed'}
            switch($control){
                latest-failed {$receipt.outcome='failed'}
                wrong-run {$receipt.workflowRun='999'}
                wrong-envelope {$receipt.workflowAttempt='1'}
                wrong-os {$receipt.operatingSystem='Linux'}
            }
            $receipt | ConvertTo-Json | Set-Content (Join-Path $directory 'acceptance.json')
            switch($control){
                malformed-latest {Set-Content (Join-Path $directory 'acceptance.json') '{'}
                extra-file {Set-Content (Join-Path $directory 'private.txt') 'private-canary'}
                missing-receipt {Remove-Item -LiteralPath (Join-Path $directory 'acceptance.json')}
                invalid-name {Rename-Item -LiteralPath $directory -NewName 'acceptance-windows-latest-attempt-02'}
            }
        }
        if($control -ceq 'existing-output'){[void][IO.Directory]::CreateDirectory($outputRoot)}
        $accepted=$true
        try{& "$PSScriptRoot/select-platform-acceptance.ps1" -WorkflowRun '123' -WorkflowAttempt '2' -InputDirectory $inputRoot -OutputDirectory $outputRoot}catch{$accepted=$false}
        if($accepted -ne ($control -cin @('positive','mixed-attempt','latest-failed'))){throw "Unexpected platform selection: $control"}
        if($control -cin @('mixed-attempt','latest-failed')){
            $selected=Get-Content (Join-Path $outputRoot 'windows-latest/acceptance.json') -Raw | ConvertFrom-Json
            if($selected.workflowAttempt -cne '2'){throw 'Newest receipt not selected.'}
            if($control -ceq 'latest-failed' -and $selected.outcome -cne 'failed'){throw 'Newer failure hidden by older pass.'}
        }
    }
    Write-Output "Platform artifact selection controls passed ($($controls.Count)); synthetic evidence only."
}finally{
    $absolute=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-platform-reruns-*'){throw 'Unsafe fixture cleanup target.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
