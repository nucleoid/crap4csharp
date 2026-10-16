function Select-WorkflowArtifactMetadata {
    param(
        [Parameter(Mandatory)][object[]]$Artifacts,
        [Parameter(Mandatory)][ValidateSet('build','partition')][string]$Kind,
        [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
        [Parameter(Mandatory)][ValidateSet('ubuntu-latest','windows-latest')][string]$RunnerOS,
        [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$WorkflowAttempt,
        [ValidateRange(1,64)][int]$ShardCount=16
    )
    $ErrorActionPreference='Stop'
    $prefix=if($Kind -ceq 'build'){"suite-build-$Configuration-$RunnerOS-attempt-"}else{"partition-$Configuration-$RunnerOS-"}
    $pattern=if($Kind -ceq 'build'){'^'+[regex]::Escape($prefix)+'([1-9][0-9]*)$'}else{'^'+[regex]::Escape($prefix)+'(0|[1-9][0-9]*)-attempt-([1-9][0-9]*)$'}
    $slots=@{}
    foreach($artifact in $Artifacts){
        $match=[regex]::Match([string]$artifact.name,$pattern,[Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if(!$match.Success){continue}
        $slot=if($Kind -ceq 'build'){0}else{[int]::Parse($match.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)}
        if($slot -ge $ShardCount){throw 'Artifact shard is outside the declared partition inventory.'}
        $attemptIndex=if($Kind -ceq 'build'){1}else{2}
        $attempt=[int]::Parse($match.Groups[$attemptIndex].Value,[Globalization.CultureInfo]::InvariantCulture)
        if($attempt -gt $WorkflowAttempt){continue}
        if(!$slots.ContainsKey($slot)){$slots[$slot]=[Collections.Generic.List[object]]::new()}
        $slots[$slot].Add([pscustomobject]@{artifact=$artifact;attempt=$attempt;slot=$slot})
    }
    $required=if($Kind -ceq 'build'){@(0)}else{@(0..($ShardCount-1))}
    $selected=@(foreach($slot in $required){
        if(!$slots.ContainsKey($slot)){throw 'Required workflow artifact slot is missing.'}
        $latest=($slots[$slot]|Measure-Object attempt -Maximum).Maximum
        $matches=@($slots[$slot]|Where-Object attempt -EQ $latest)
        if($matches.Count -ne 1){throw 'Latest workflow artifact slot is ambiguous.'}
        $row=$matches[0];$artifact=$row.artifact
        if($artifact.expired -isnot [bool] -or $artifact.expired){throw 'Selected latest workflow artifact is expired or unproven.'}
        $id=[string]$artifact.id
        [long]$parsedId=0
        if($id -cnotmatch '^[1-9][0-9]*$' -or ![long]::TryParse($id,[ref]$parsedId)){throw 'Selected artifact ID is not a positive canonical integer.'}
        [pscustomobject]@{id=$id;name=[string]$artifact.name;slot=$slot;attempt=[int]$row.attempt}
    })
    if(@($selected.id|Sort-Object -Unique).Count -ne $required.Count){throw 'Selected artifact IDs are not unique.'}
    return [pscustomobject]@{ids=($selected.id -join ',');selected=$selected}
}
