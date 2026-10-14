$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-partition-contract.ps1"
$owned=[IO.Directory]::CreateTempSubdirectory('crap4csharp-rerun-controls-').FullName
try{
    foreach($pair in @(@('1','2'),@('2','2'),@('100000000000000000000','100000000000000000001'))){
        Assert-SuiteAttemptAtOrBefore $pair[0] $pair[1]
    }
    foreach($pair in @(@('2','1'),@('0','2'),@('01','2'),@('1','0'),@(1,'2'),@('1',2))){
        $refused=$false;try{Assert-SuiteAttemptAtOrBefore $pair[0] $pair[1]}catch{$refused=$true}
        if(!$refused){throw 'Invalid/future attempt accepted.'}
    }
    $controls=@('positive','latest-failed','future','missing','wrong-run','wrong-envelope','extra-file','bad-name')
    foreach($control in $controls){
        $input=Join-Path $owned "input-$control";$output=Join-Path $owned "output-$control"
        [void][IO.Directory]::CreateDirectory($input)
        foreach($index in 0..1){
            $directory=Join-Path $input "partition-Release-windows-latest-$index-attempt-1"
            [void][IO.Directory]::CreateDirectory($directory)
            [ordered]@{outcome='passed';identityBinding=@{identity=@{workflowRun='123';workflowAttempt='1'}}} |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory "Release-$index.partition.receipt.json")
        }
        $new=Join-Path $input 'partition-Release-windows-latest-1-attempt-2'
        [void][IO.Directory]::CreateDirectory($new)
        $receipt=[ordered]@{outcome='passed';identityBinding=@{identity=@{workflowRun='123';workflowAttempt='2'}}}
        switch($control){
            latest-failed {$receipt=[ordered]@{outcome='failed';reason='testResults.incomplete'}}
            wrong-run {$receipt.identityBinding.identity.workflowRun='999'}
            wrong-envelope {$receipt.identityBinding.identity.workflowAttempt='1'}
        }
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $new 'Release-1.partition.receipt.json')
        switch($control){
            future {Rename-Item -LiteralPath $new -NewName 'partition-Release-windows-latest-1-attempt-3'}
            missing {Rename-Item -LiteralPath (Join-Path $input 'partition-Release-windows-latest-0-attempt-1') -NewName 'missing-shard'}
            extra-file {Set-Content -LiteralPath (Join-Path $new 'unexpected.txt') 'unexpected'}
            bad-name {Rename-Item -LiteralPath $new -NewName 'partition-Release-windows-latest-1-attempt-02'}
        }
        $accepted=$true
        try{& "$PSScriptRoot/select-suite-partition-receipts.ps1" -Configuration Release -RunnerOS windows-latest -ShardCount 2 `
            -WorkflowRun '123' -WorkflowAttempt '2' -InputDirectory $input -OutputDirectory $output}catch{$accepted=$false}
        if($accepted -ne ($control -cin @('positive','latest-failed'))){throw "Unexpected selection: $control"}
        if($accepted){
            $selected=Get-Content -LiteralPath (Join-Path $output 'Release-1.partition.receipt.json') -Raw|ConvertFrom-Json
            if($control -ceq 'latest-failed'){
                if($selected.outcome -cne 'failed'){throw 'Newer failure hidden by older pass.'}
            }elseif($selected.identityBinding.identity.workflowAttempt -cne '2'){throw 'Latest attempt not selected.'}
            if((Get-Content -LiteralPath (Join-Path $output 'Release-0.partition.receipt.json') -Raw|ConvertFrom-Json).identityBinding.identity.workflowAttempt -cne '1'){
                throw 'Unchanged older partition was discarded.'
            }
        }
    }
    Write-Output 'Partition rerun controls passed (17); selector evidence only, no complete-suite/hosted acceptance.'
}finally{
    $absolute=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-rerun-controls-*'){
        throw 'Unsafe owned fixture cleanup.'
    }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
