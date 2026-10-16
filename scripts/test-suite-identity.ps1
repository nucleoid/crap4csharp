$ErrorActionPreference='Stop'
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-suite-identity-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
try{
    $files=[ordered]@{}
    foreach($file in @('Crap4CSharp.Tests.dll','Crap4CSharp.Core.dll','Crap4CSharp.Tool.dll','xunit.runner.json',
        'process-helper/Helper.dll','process-helper/Helper.runtimeconfig.json')){$files[$file]='d'*64}
    $identity=[ordered]@{schemaVersion='suite-identity-v1';outcome='observed';configuration='Release';operatingSystem='Windows';
        commit=('a'*40);tree=('b'*40);sdk='10.0.103';workflowRun='123';workflowAttempt='1';packageSha256=('c'*64);discoverySha256=('f'*64);worktreeSha256=('1'*64);runtimeInventorySha256=('2'*64);files=$files}
    $before=Join-Path $owned 'before.json';$after=Join-Path $owned 'after.json';$receipt=Join-Path $owned 'nested/binding.json'
    $identity | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $before
    $controls=@('positive','configuration','operatingSystem','commit','tree','sdk','workflowRun','workflowAttempt','packageSha256',
        'missing-file','changed-test-assembly','changed-core-assembly','changed-tool-assembly','changed-runner','changed-helper','changed-helper-runtime',
        'extra-file','schema','failed-observation','malformed-json','missing-json','missing-head','invalid-hash','missing-package','changed-discovery','unknown-root-field','changed-worktree','changed-runtime-inventory',
        'numeric-run','numeric-attempt','array-commit','array-file-hash')
    foreach($control in $controls){
        $current=$identity | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        switch($control){
            numeric-run {$current.workflowRun=123}
            numeric-attempt {$current.workflowAttempt=1}
            array-commit {$current.commit=@('a'*40)}
            array-file-hash {$current.files.'Crap4CSharp.Core.dll'=@('d'*64)}
            configuration {$current.configuration='Debug'}
            operatingSystem {$current.operatingSystem='Linux'}
            commit {$current.commit='e'*40}
            tree {$current.tree='e'*40}
            sdk {$current.sdk='10.0.104'}
            workflowRun {$current.workflowRun='999'}
            workflowAttempt {$current.workflowAttempt='2'}
            packageSha256 {$current.packageSha256='e'*64}
            missing-file {$current.files.PSObject.Properties.Remove('Crap4CSharp.Tests.dll')}
            changed-test-assembly {$current.files.'Crap4CSharp.Tests.dll'='e'*64}
            changed-core-assembly {$current.files.'Crap4CSharp.Core.dll'='e'*64}
            changed-tool-assembly {$current.files.'Crap4CSharp.Tool.dll'='e'*64}
            changed-runner {$current.files.'xunit.runner.json'='e'*64}
            changed-helper {$current.files.'process-helper/Helper.dll'='e'*64}
            changed-helper-runtime {$current.files.'process-helper/Helper.runtimeconfig.json'='e'*64}
            extra-file {$current.files | Add-Member extra ('e'*64)}
            schema {$current.schemaVersion='unknown'}
            failed-observation {$current.outcome='failed'}
            missing-head {$current.commit=$null}
            invalid-hash {$current.files.'Crap4CSharp.Core.dll'='private-invalid-canary'}
            missing-package {$current.packageSha256=$null}
            changed-discovery {$current.discoverySha256='e'*64}
            unknown-root-field {$current | Add-Member privateMetadata 'private-secret-canary'}
            changed-worktree {$current.worktreeSha256='e'*64}
            changed-runtime-inventory {$current.runtimeInventorySha256='e'*64}
        }
        $current | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $after
        if($control -eq 'malformed-json'){Set-Content -LiteralPath $after 'private-malformed-canary'}
        if($control -eq 'missing-json'){Remove-Item -LiteralPath $after}
        $accepted=$true
        try{& "$PSScriptRoot/assert-suite-identity.ps1" -BeforePath $before -AfterPath $after -OutputPath $receipt}catch{$accepted=$false}
        if($accepted -ne ($control -eq 'positive')){throw "Unexpected identity binding: $control"}
        $text=Get-Content -LiteralPath $receipt -Raw
        if($text.Contains('private-') -or $text.Contains($owned)){throw 'Private diagnostics leaked into identity receipt.'}
        $binding=$text | ConvertFrom-Json
        if($accepted -and ($binding.beforeSha256 -ne (Get-FileHash $before).Hash.ToLowerInvariant() -or
            $binding.afterSha256 -ne (Get-FileHash $after).Hash.ToLowerInvariant())){throw 'Identity file hashes disagree.'}
        if(!$accepted -and ($binding.outcome -ne 'failed' -or $null -ne $binding.identity)){throw 'Invalid identity invented acceptance.'}
    }
    Write-Output "Suite identity controls passed ($($controls.Count)); synthetic binding tests only."
}finally{
    $resolved=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'crap4csharp-suite-identity-*'){throw 'Unsafe identity fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
