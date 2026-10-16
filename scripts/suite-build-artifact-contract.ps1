. "$PSScriptRoot/suite-receipt-contract.ps1"

function Get-SuiteBuildOutputRoots([string]$Configuration){
    foreach($project in @('src/Crap4CSharp.Core','src/Crap4CSharp.Tool','tests/Crap4CSharp.Tests',
        'tests/Crap4CSharp.ProcessFixture','tests/Crap4CSharp.ProvenanceFixture')){
        "$project/bin/$Configuration/net10.0/"
    }
}

function Get-InventoryHash([string]$Root,[string[]]$Paths){
    $absolute=[IO.Path]::GetFullPath($Root)
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    $prefix=[IO.Path]::TrimEndingDirectorySeparator($absolute)+[IO.Path]::DirectorySeparatorChar
    $ordered=[string[]]$Paths.Clone();[Array]::Sort($ordered,[StringComparer]::Ordinal)
    $records=@(foreach($relative in $ordered){
        if([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathFullyQualified($relative)){throw 'Invalid inventory locator.'}
        $full=[IO.Path]::GetFullPath((Join-Path $absolute $relative))
        if(!$full.StartsWith($prefix,$comparison)){throw 'Inventory locator escaped root.'}
        Assert-SuiteBuildNoLinks $absolute $full
        [ordered]@{path=$relative.Replace('\','/');sha256=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    if(!$records.Count){throw 'Empty inventory.'}
    $bytes=[Text.Encoding]::UTF8.GetBytes(($records|ConvertTo-Json -Depth 3 -Compress))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-SuiteBuildNoLinks([string]$Root,[string]$Path){
    $rootPath=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    $node=[IO.Path]::GetFullPath($Path)
    if($node -cne $rootPath -and !$node.StartsWith($rootPath+[IO.Path]::DirectorySeparatorChar,$comparison)){throw 'Path escaped root.'}
    while($node){
        if(Test-Path -LiteralPath $node){
            if(([IO.File]::GetAttributes($node) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse entry refused.'}
        }
        if($node.Equals($rootPath,$comparison)){break}
        $node=[IO.Path]::GetDirectoryName($node)
    }
}

function Get-SuiteTrackedWorktreeHash([string]$Root){
    $tracked=@(& git --no-optional-locks ls-files -z 2>$null)
    if($LASTEXITCODE -ne 0 -or $tracked.Count -ne 1){throw 'Unsupported tracked inventory framing.'}
    $paths=$tracked[0].Split([char]0,[StringSplitOptions]::RemoveEmptyEntries)
    if(@($paths|Where-Object {$_ -match '[\r\n]'}).Count){throw 'Unsupported tracked filename.'}
    Get-InventoryHash $Root $paths
}

function Assert-SuiteBuildLocator([string]$Path,[string]$Configuration){
    if([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('\') -or $Path.Contains(':') -or $Path.Contains([char]0) -or
        $Path.Split('/') -contains '..' -or $Path.Split('/') -contains '.' -or $Path.Contains('//') -or
        $Path.EndsWith('/') -or $Path.EndsWith('.binlog',[StringComparison]::OrdinalIgnoreCase) -or
        $Path.EndsWith('.log',[StringComparison]::OrdinalIgnoreCase)){
        throw 'Invalid build output locator.'
    }
    if(!@((Get-SuiteBuildOutputRoots $Configuration)|Where-Object {$Path.StartsWith($_,[StringComparison]::Ordinal)}).Count){
        throw 'Build output is outside explicit project roots.'
    }
}

function Get-SuiteStreamHash([IO.Stream]$Stream){
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant()
}
