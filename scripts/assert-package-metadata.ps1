param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion,
    [string] $RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$repository = (Resolve-Path -LiteralPath $RepoRoot).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $documents = @{}
    foreach ($name in @('README.md', 'LICENSE', 'ATTRIBUTION.md')) {
        $entry = $archive.GetEntry($name)
        if ($null -eq $entry) { throw "Package is missing $name." }
        if ($entry.Length -gt 2MB) { throw "Package document $name exceeds its limit." }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $expected = (Get-FileHash -LiteralPath (Join-Path $repository $name) -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Package document $name differs from the repository original." }
        $documents[$name] = $actual.ToLowerInvariant()
    }
    $specs = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
    if ($specs.Count -ne 1) { throw 'Package must contain exactly one nuspec.' }
    $stream = $specs[0].Open()
    try { $spec = [System.Xml.Linq.XDocument]::Load($stream) }
    finally { $stream.Dispose() }
    $metadata = @($spec.Descendants() | Where-Object { $_.Name.LocalName -eq 'metadata' })
    if ($metadata.Count -ne 1) { throw 'Package metadata is ambiguous.' }
    function MetadataValue([string] $name) {
        $values = @($metadata[0].Elements() | Where-Object { $_.Name.LocalName -eq $name })
        if ($values.Count -ne 1) { throw "Package must declare exactly one $name." }
        return $values[0]
    }
    if ((MetadataValue 'id').Value -ne 'Crap4CSharp.Tool') { throw 'Unexpected package ID.' }
    if ((MetadataValue 'version').Value -cne $ExpectedVersion) { throw 'Package version differs from the exact candidate.' }
    $license = MetadataValue 'license'
    if ($license.Value -cne 'MIT' -or $license.Attribute('type').Value -cne 'expression') { throw 'Expected MIT license expression.' }
    if ((MetadataValue 'readme').Value -cne 'README.md') { throw 'Expected packaged README.' }
    $toolEntries = @($archive.Entries | Where-Object { $_.FullName.StartsWith('tools/', [StringComparison]::Ordinal) })
    if ($toolEntries.Count -eq 0 -or @($toolEntries | Where-Object {
        -not $_.FullName.StartsWith('tools/net10.0/any/', [StringComparison]::Ordinal)
    }).Count -ne 0) { throw 'Every tool asset must target net10.0.' }
    foreach ($name in @('Crap4CSharp.Tool.dll', 'Crap4CSharp.Core.dll', 'DotnetToolSettings.xml')) {
        if ($null -eq $archive.GetEntry("tools/net10.0/any/$name")) { throw "Missing tool asset $name." }
    }
    [pscustomobject]@{
        packageId = 'Crap4CSharp.Tool'
        version = $ExpectedVersion
        targetFramework = 'net10.0'
        sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        documents = $documents
    } | ConvertTo-Json -Depth 3
}
finally { $archive.Dispose() }
