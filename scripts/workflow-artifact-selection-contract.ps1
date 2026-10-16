function Read-WorkflowArtifactMetadata {
    param(
        [Parameter(Mandatory)][scriptblock]$ReadSnapshot,
        [scriptblock]$WaitForNextObservation = { Start-Sleep -Seconds 2 }
    )
    $ErrorActionPreference='Stop'
    for($observation=0;$observation -lt 3;$observation++){
        $snapshot=& $ReadSnapshot
        if($null -eq $snapshot -or $snapshot.exitCode -ne 0 -or $null -eq $snapshot.pages){throw 'Workflow artifact metadata unavailable.'}
        $pages=@($snapshot.pages)
        if(!$pages.Count){throw 'Workflow artifact metadata unavailable.'}
        $rows=@($pages|ForEach-Object artifacts)
        if(@($pages|Where-Object {$_.total_count -ne $pages[0].total_count}).Count -eq 0 -and
            $rows.Count -eq $pages[0].total_count -and @($rows.id|Sort-Object -Unique).Count -eq $rows.Count){
            return [pscustomobject]@{artifacts=$rows;observations=$observation+1}
        }
        if($observation -lt 2){[void](& $WaitForNextObservation)}
    }
    throw 'Workflow artifact metadata remained inconsistent.'
}

function Select-WorkflowArtifactMetadata {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Artifacts,
        [Parameter(Mandatory)][ValidateSet('build','partition','acceptance')][string]$Kind,
        [ValidateSet('Debug','Release')][string]$Configuration,
        [Parameter(Mandatory)][ValidateSet('ubuntu-latest','windows-latest')][string]$RunnerOS,
        [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$WorkflowAttempt,
        [ValidateRange(1,64)][int]$ShardCount=16
    )
    $ErrorActionPreference='Stop'
    if($Kind -ceq 'acceptance'){
        if(![string]::IsNullOrEmpty($Configuration)){throw 'Platform acceptance selection must not specify a configuration.'}
        $prefix='acceptance-'
        $pattern='^acceptance-(ubuntu-latest|windows-latest)-attempt-([1-9][0-9]*)$'
    }else{
        if([string]::IsNullOrEmpty($Configuration)){throw 'Build and partition selection require a configuration.'}
        $prefix=if($Kind -ceq 'build'){"suite-build-$Configuration-$RunnerOS-attempt-"}else{"partition-$Configuration-$RunnerOS-"}
        $pattern=if($Kind -ceq 'build'){'^'+[regex]::Escape($prefix)+'([1-9][0-9]*)$'}else{'^'+[regex]::Escape($prefix)+'(0|[1-9][0-9]*)-attempt-([1-9][0-9]*)$'}
    }
    $slots=@{}
    foreach($artifact in $Artifacts){
        $name=[string]$artifact.name
        if(!$name.StartsWith($prefix,[StringComparison]::Ordinal)){continue}
        $match=[regex]::Match($name,$pattern,[Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if(!$match.Success){throw 'Invalid workflow artifact name.'}
        $slot=if($Kind -ceq 'partition'){[int]::Parse($match.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture)}else{0}
        if($slot -ge $ShardCount){throw 'Artifact shard is outside the declared partition inventory.'}
        $attemptIndex=if($Kind -ceq 'build'){1}else{2}
        $attempt=[int]::Parse($match.Groups[$attemptIndex].Value,[Globalization.CultureInfo]::InvariantCulture)
        if($attempt -gt $WorkflowAttempt){throw 'Workflow artifact is newer than the current attempt.'}
        if($Kind -ceq 'acceptance' -and $match.Groups[1].Value -cne $RunnerOS){continue}
        if(!$slots.ContainsKey($slot)){$slots[$slot]=[Collections.Generic.List[object]]::new()}
        $slots[$slot].Add([pscustomobject]@{artifact=$artifact;attempt=$attempt;slot=$slot})
    }
    $required=if($Kind -ceq 'partition'){@(0..($ShardCount-1))}else{@(0)}
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
