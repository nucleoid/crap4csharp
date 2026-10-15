param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion,
    [string] $NativeAssetsDirectory,
    [string] $RepoRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$repository = (Resolve-Path -LiteralPath $RepoRoot).Path
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $archive.Entries){
        if(!$names.Add($entry.FullName) -or $entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or
            $entry.FullName.Contains(':') -or @($entry.FullName.Split('/')|Where-Object {$_ -ceq '.' -or $_ -ceq '..'}).Count){
            throw 'Ambiguous or unsafe package path.'
        }
    }
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
    $groups=@('any','win-x64','linux-x64')
    if ($toolEntries.Count -eq 0 -or @($toolEntries | Where-Object {
        $path=$_.FullName
        @($groups|Where-Object {$path.StartsWith("tools/net10.0/$_/",[StringComparison]::Ordinal)}).Count -ne 1
    }).Count -ne 0) { throw 'Tool assets must belong to the three declared net10.0 groups.' }
    function ReadPackageText([string]$name){
        $entry=$archive.GetEntry($name)
        if($null -eq $entry -or $entry.Length -gt 2MB){throw "Missing or oversized package metadata: $name."}
        $reader=[IO.StreamReader]::new($entry.Open())
        try{return $reader.ReadToEnd()}finally{$reader.Dispose()}
    }
    $nativeNames=@('Crap4CSharp.Tool.dll','Crap4CSharp.Core.dll','Microsoft.CodeAnalysis.dll',
        'Microsoft.CodeAnalysis.CSharp.dll','Microsoft.CodeAnalysis.Workspaces.dll','Microsoft.CodeAnalysis.CSharp.Workspaces.dll')
    $groupCounts=[ordered]@{}
    $settingsText=$null
    foreach($group in $groups){
        $prefix="tools/net10.0/$group/"
        $groupCounts[$group]=@($toolEntries|Where-Object {$_.FullName.StartsWith($prefix,[StringComparison]::Ordinal)}).Count
        $text=ReadPackageText ($prefix+'DotnetToolSettings.xml')
        $settings=[Xml.Linq.XDocument]::Parse($text)
        $commands=@($settings.Descendants()|Where-Object {$_.Name.LocalName -ceq 'Command'})
        if($commands.Count -ne 1 -or $commands[0].Attribute('Name').Value -cne 'crap4csharp' -or
            $commands[0].Attribute('Runner').Value -cne 'dotnet' -or $commands[0].Attribute('EntryPoint').Value -cne 'Crap4CSharp.Tool.dll'){
            throw 'Unexpected tool command settings.'
        }
        if($null -eq $settingsText){$settingsText=$text}elseif($settingsText -cne $text){throw 'Tool settings differ between groups.'}
        $runtime=ReadPackageText ($prefix+'Crap4CSharp.Tool.runtimeconfig.json')|ConvertFrom-Json
        if($runtime.runtimeOptions.tfm -cne 'net10.0' -or $runtime.runtimeOptions.framework.name -cne 'Microsoft.NETCore.App' -or
            $runtime.runtimeOptions.framework.version -cne '10.0.0'){throw 'Unexpected tool runtime floor.'}
        $deps=ReadPackageText ($prefix+'Crap4CSharp.Tool.deps.json')|ConvertFrom-Json
        if(@($deps.libraries.PSObject.Properties.Name|Where-Object {$_ -ceq "Crap4CSharp.Tool/$ExpectedVersion"}).Count -ne 1){
            throw 'Tool dependency version differs from the package.'
        }
        foreach($name in $nativeNames){
            $entry=$archive.GetEntry($prefix+$name)
            if($null -eq $entry -or $entry.Length -gt 64MB){throw "Missing or oversized tool image: $group/$name."}
            $buffer=[IO.MemoryStream]::new()
            $stream=$entry.Open()
            try{$stream.CopyTo($buffer)}finally{$stream.Dispose()}
            $buffer.Position=0
            $pe=[Reflection.PortableExecutable.PEReader]::new($buffer)
            try{
                $size=$pe.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size
                $machine=[int]$pe.PEHeaders.CoffHeader.Machine
                if($group -ceq 'any'){
                    if($size -ne 0 -or $machine -ne 0x14c){throw 'The any group must contain portable IL images.'}
                }else{
                    $expectedMachine=if($group -ceq 'win-x64'){0x8664}else{0xfd1d}
                    if($size -le 0 -or $machine -ne $expectedMachine){throw 'Native tool image has the wrong platform or lacks native code.'}
                }
            }finally{$pe.Dispose()}
        }
    }
    $preparedText=ReadPackageText 'native-tool-assets.json'
    $prepared=$preparedText|ConvertFrom-Json
    if($NativeAssetsDirectory -and (Get-Content -LiteralPath (Join-Path $NativeAssetsDirectory manifest.json) -Raw) -cne $preparedText){
        throw 'Embedded native inventory differs from prepared assets.'
    }
    . "$PSScriptRoot/sdk-acceptance-contract.ps1"
    $declaredSdk=Get-SuiteAcceptanceSdkVersion (Join-Path $repository global.json)
    $tree=(& git -C $repository rev-parse 'HEAD^{tree}').Trim()
    if($LASTEXITCODE -ne 0){throw 'Package source tree unavailable.'}
        $head=(& git -C $repository rev-parse HEAD).Trim()
        if($LASTEXITCODE -ne 0 -or $prepared.schemaVersion -cne 'native-tool-assets-v1' -or $prepared.commit -cne $head -or
            $prepared.tree -cne $tree -or $prepared.sdk -cne $declaredSdk -or
            $prepared.version -cne $ExpectedVersion -or $prepared.configuration -cne 'Release' -or
            @($prepared.groups).Count -ne 2 -or @($prepared.groups.rid|Sort-Object -Unique).Count -ne 2){throw 'Prepared native source/version binding differs.'}
        foreach($group in $prepared.groups){
            if($group.rid -cnotin @('win-x64','linux-x64') -or $group.runtimeFloor -cne '10.0.0'){throw 'Unexpected prepared native RID.'}
            $prefix='tools/net10.0/'+$group.rid+'/'
            $files=@($group.files.PSObject.Properties)
            if($groupCounts[$group.rid] -ne $files.Count+1){throw 'Native package group inventory differs from prepared assets.'}
            foreach($file in $files){
                $entry=$archive.GetEntry($prefix+$file.Name)
                if($null -eq $entry){throw 'Prepared native file missing from package.'}
                $stream=$entry.Open()
                try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}finally{$stream.Dispose()}
                if($hash -cne $file.Value){throw 'Packaged native bytes differ from prepared assets.'}
            }
        }
    [pscustomobject]@{
        packageId = 'Crap4CSharp.Tool'
        version = $ExpectedVersion
        targetFramework = 'net10.0'
        toolGroups = $groupCounts
        sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        documents = $documents
    } | ConvertTo-Json -Depth 3
}
finally { $archive.Dispose() }
