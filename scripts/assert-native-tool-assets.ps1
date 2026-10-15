param(
    [string]$NativeAssetsDirectory=$env:CRAP4CSHARP_NATIVE_ASSETS,
    [string]$Version=$env:CRAP4CSHARP_NATIVE_VERSION,
    [string]$Configuration=$env:CRAP4CSHARP_NATIVE_CONFIGURATION,
    [string]$Repository=$env:CRAP4CSHARP_NATIVE_REPOSITORY,
    [string]$Sdk=$env:CRAP4CSHARP_NATIVE_SDK
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
. "$PSScriptRoot/suite-build-artifact-contract.ps1"
if(!$NativeAssetsDirectory -or !$Version -or $Configuration -cnotin @('Debug','Release') -or !$Repository -or !$Sdk){throw 'Native asset validation inputs are incomplete.'}
$repo=(Resolve-Path -LiteralPath $Repository).Path
$assets=(Resolve-Path -LiteralPath $NativeAssetsDirectory).Path
$manifest=Get-Content -LiteralPath (Join-Path $assets manifest.json) -Raw|ConvertFrom-Json
Push-Location $repo
try{
    $head=(& git rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0){throw 'Native asset source unavailable.'}
    $status=@(& git --no-optional-locks status --porcelain --untracked-files=all)
    if($LASTEXITCODE -ne 0 -or $status.Count){throw 'Native assets require clean source.'}
    if($manifest.schemaVersion -cne 'native-tool-assets-v1' -or $manifest.commit -cne $head -or
        $manifest.version -cne $Version -or $manifest.configuration -cne $Configuration -or
        $manifest.sdk -cne $Sdk -or $Sdk -cne (Get-SuiteAcceptanceSdkVersion (Join-Path $repo global.json)) -or
        $manifest.worktreeSha256 -cne (Get-SuiteTrackedWorktreeHash $repo)){
        throw 'Prepared native assets have stale source, version, SDK or configuration.'
    }
    $groups=@($manifest.groups)
    if($groups.Count -ne 2 -or @($groups.rid|Sort-Object -Unique).Count -ne 2){throw 'Native groups are incomplete or ambiguous.'}
    foreach($group in $groups){
        if($group.rid -cnotin @('win-x64','linux-x64') -or $group.runtimeFloor -cne '10.0.0'){throw 'Unexpected native group.'}
        $root=Join-Path $assets ('tools/'+$group.rid)
        $files=@($group.files.PSObject.Properties)
        $entries=@(Get-ChildItem -LiteralPath $root -Force -Recurse)
        foreach($entry in $entries){Assert-SuiteBuildNoLinks $root $entry.FullName}
        if($files.Count -lt 8 -or $files.Count -gt 10000 -or @($entries|Where-Object {!$_.PSIsContainer}).Count -ne $files.Count){throw 'Prepared native inventory differs.'}
        foreach($file in $files){
            if([string]::IsNullOrWhiteSpace($file.Name) -or [IO.Path]::IsPathFullyQualified($file.Name) -or $file.Name.Contains('\') -or $file.Name.Contains(':') -or
                @($file.Name.Split('/')|Where-Object {$_ -ceq '.' -or $_ -ceq '..'}).Count){throw 'Unsafe native asset locator.'}
            $path=Join-Path $root $file.Name
            Assert-SuiteBuildNoLinks $root $path
            if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.Value){throw 'Prepared native bytes changed.'}
        }
    }
}finally{Pop-Location}
