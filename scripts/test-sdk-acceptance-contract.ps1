$ErrorActionPreference='Stop'
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
$owned=Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-sdk-pin-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($owned)
try{
    $path=Join-Path $owned 'global.json'
    $cases=@(
        @{name='declared';value='10.0.103';accepted=$true},
        @{name='updated';value='10.0.104';accepted=$true},
        @{name='feature-band';value='10.0.200';accepted=$true},
        @{name='old-major';value='9.0.103';accepted=$false},
        @{name='future-major';value='11.0.103';accepted=$false},
        @{name='prerelease';value='10.0.103-preview';accepted=$false},
        @{name='short';value='10.0.1';accepted=$false},
        @{name='number';value=103;accepted=$false},
        @{name='array';value=@('10.0.103');accepted=$false},
        @{name='null';value=$null;accepted=$false}
    )
    foreach($case in $cases){
        @{sdk=@{version=$case.value;rollForward='latestPatch'}} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
        $accepted=$true;$observed=$null
        try{$observed=Get-SuiteAcceptanceSdkVersion $path}catch{$accepted=$false}
        if($accepted -ne $case.accepted -or ($accepted -and $observed -cne $case.value)){throw "Unexpected SDK pin control: $($case.name)"}
    }
    Write-Output "SDK declaration controls passed ($($cases.Count)); no SDK compatibility claim."
}finally{
    $absolute=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($absolute) -notlike 'crap4csharp-sdk-pin-*'){throw 'Unsafe fixture cleanup target.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
