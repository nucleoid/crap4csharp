param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$RepoRoot=(Split-Path $PSScriptRoot -Parent)
)
$ErrorActionPreference='Stop'
$package=(Resolve-Path -LiteralPath $PackagePath).Path
$before=(Get-FileHash -LiteralPath $package).Hash
$validator=Join-Path $PSScriptRoot assert-package-metadata.ps1
[void](& $validator -PackagePath $package -ExpectedVersion $ExpectedVersion -RepoRoot $RepoRoot)
$owned=[IO.Directory]::CreateTempSubdirectory('crap4csharp-native-package-').FullName
function ReadEntry([IO.Compression.ZipArchive]$zip,[string]$name){
    $stream=$zip.GetEntry($name).Open();$buffer=[IO.MemoryStream]::new()
    try{$stream.CopyTo($buffer);return ,$buffer.ToArray()}finally{$stream.Dispose();$buffer.Dispose()}
}
function ReplaceEntry([IO.Compression.ZipArchive]$zip,[string]$name,[byte[]]$bytes){
    $old=$zip.GetEntry($name);if($null -ne $old){$old.Delete()}
    $stream=$zip.CreateEntry($name).Open()
    try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
}
try{
    $controls=@('missing-group','wrong-platform','native-any','runtime-floor','settings','dependency-version','extra-group','duplicate-case','traversal','missing-dependency','changed-dependency')
    foreach($control in $controls){
        $copy=Join-Path $owned "$control.nupkg"
        [IO.File]::Copy($package,$copy)
        $zip=[IO.Compression.ZipFile]::Open($copy,[IO.Compression.ZipArchiveMode]::Update)
        try{
            switch($control){
                missing-group {
                    foreach($entry in @($zip.Entries|Where-Object {$_.FullName.StartsWith('tools/net10.0/linux-x64/',[StringComparison]::Ordinal)})){$entry.Delete()}
                    $expected='Missing or oversized package metadata: tools/net10.0/linux-x64/DotnetToolSettings.xml.'
                }
                wrong-platform {
                    ReplaceEntry $zip 'tools/net10.0/linux-x64/Crap4CSharp.Tool.dll' (ReadEntry $zip 'tools/net10.0/win-x64/Crap4CSharp.Tool.dll')
                    $expected='Native tool image has the wrong platform or lacks native code.'
                }
                native-any {
                    ReplaceEntry $zip 'tools/net10.0/any/Crap4CSharp.Tool.dll' (ReadEntry $zip 'tools/net10.0/win-x64/Crap4CSharp.Tool.dll')
                    $expected='The any group must contain portable IL images.'
                }
                runtime-floor {
                    $name='tools/net10.0/linux-x64/Crap4CSharp.Tool.runtimeconfig.json'
                    $runtime=[Text.Encoding]::UTF8.GetString((ReadEntry $zip $name))|ConvertFrom-Json
                    $runtime.runtimeOptions.framework.version='10.0.1'
                    ReplaceEntry $zip $name ([Text.Encoding]::UTF8.GetBytes(($runtime|ConvertTo-Json -Depth 10)))
                    $expected='Unexpected tool runtime floor.'
                }
                settings {
                    $name='tools/net10.0/linux-x64/DotnetToolSettings.xml'
                    $text=[Text.Encoding]::UTF8.GetString((ReadEntry $zip $name)).Replace('Runner="dotnet"','Runner="executable"')
                    ReplaceEntry $zip $name ([Text.Encoding]::UTF8.GetBytes($text))
                    $expected='Unexpected tool command settings.'
                }
                dependency-version {
                    $name='tools/net10.0/linux-x64/Crap4CSharp.Tool.deps.json'
                    $text=[Text.Encoding]::UTF8.GetString((ReadEntry $zip $name)).Replace("Crap4CSharp.Tool/$ExpectedVersion",'Crap4CSharp.Tool/0.0.0-wrong')
                    ReplaceEntry $zip $name ([Text.Encoding]::UTF8.GetBytes($text))
                    $expected='Tool dependency version differs from the package.'
                }
                extra-group {
                    ReplaceEntry $zip 'tools/net10.0/osx-x64/extra.txt' ([byte[]](1))
                    $expected='Tool assets must belong to the three declared net10.0 groups.'
                }
                duplicate-case {
                    ReplaceEntry $zip 'readme.md' ([byte[]](1))
                    $expected='Ambiguous or unsafe package path.'
                }
                traversal {
                    ReplaceEntry $zip 'tools/net10.0/linux-x64/../escape.txt' ([byte[]](1))
                    $expected='Ambiguous or unsafe package path.'
                }
                missing-dependency {
                    $zip.GetEntry('tools/net10.0/linux-x64/Microsoft.CodeAnalysis.Workspaces.MSBuild.dll').Delete()
                    $expected='Native package group inventory differs from prepared assets.'
                }
                changed-dependency {
                    ReplaceEntry $zip 'tools/net10.0/linux-x64/Microsoft.CodeAnalysis.Workspaces.MSBuild.dll' ([byte[]](1,2,3))
                    $expected='Packaged native bytes differ from prepared assets.'
                }
            }
        }finally{$zip.Dispose()}
        $failure=$null
        try{[void](& $validator -PackagePath $copy -ExpectedVersion $ExpectedVersion -RepoRoot $RepoRoot)}catch{$failure=$_.Exception.Message}
        if($failure -cne $expected){throw "Unexpected package mutation result: $control / $failure"}
    }
    if((Get-FileHash -LiteralPath $package).Hash -cne $before){throw 'Original package changed during controls.'}
    Write-Output "Native package controls passed: one complete valid package and $($controls.Count) independently mutated packages. Original bytes unchanged; no SDK commands."
}finally{
    $full=[IO.Path]::GetFullPath($owned)
    $prefix=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))+[IO.Path]::DirectorySeparatorChar
    if(!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notlike 'crap4csharp-native-package-*'){throw 'Unsafe owned package control cleanup.'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
