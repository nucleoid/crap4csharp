param([string]$GitHubEnvironmentPath=$env:GITHUB_ENV)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
if([string]::IsNullOrWhiteSpace($GitHubEnvironmentPath) -or !(Test-Path -LiteralPath $GitHubEnvironmentPath -PathType Leaf)){
    throw 'An existing runner environment file is required.'
}
$command=Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1
$hostPath=$command.Source
if(![IO.Path]::IsPathFullyQualified($hostPath) -or $hostPath.Contains("`n") -or $hostPath.Contains("`r")){
    throw 'The selected SDK host must have one absolute path.'
}
$hostPath=[IO.Path]::GetFullPath($hostPath)
$root=[IO.Path]::GetDirectoryName($hostPath)
$comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
if([string]::IsNullOrWhiteSpace($env:DOTNET_ROOT) -or
   ![IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:DOTNET_ROOT)).Equals($root,$comparison)){
    throw 'The selected SDK host and runtime root differ.'
}
$version=(& $hostPath --version).Trim()
if($LASTEXITCODE -ne 0 -or $version -cne (Get-SuiteAcceptanceSdkVersion)){
    throw 'The selected SDK host does not match the exact acceptance SDK.'
}
"DOTNET_HOST_PATH=$hostPath" | Out-File -LiteralPath $GitHubEnvironmentPath -Encoding utf8 -Append
