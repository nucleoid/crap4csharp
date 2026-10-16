function Get-SuiteAcceptanceSdkVersion([string]$GlobalJsonPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'global.json')) {
    # One declaration for release acceptance, independent of the tool's bounded
    # native provider support. Every invocation still observes its actual SDK.
    $configuration=Get-Content -LiteralPath $GlobalJsonPath -Raw | ConvertFrom-Json
    $version=$configuration.sdk.version
    if($version -isnot [string] -or $version -cnotmatch '^10\.0\.[0-9]{3}$'){
        throw 'Release acceptance requires a declared stable .NET 10 SDK version.'
    }
    return $version
}
