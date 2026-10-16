$ErrorActionPreference='Stop'
. "$PSScriptRoot/workflow-artifact-selection-contract.ps1"
function Artifact([long]$Id,[string]$Name,[bool]$Expired=$false){[pscustomobject]@{id=$Id;name=$Name;expired=$Expired}}
function Select-Parts($Rows,[int]$Attempt=2){Select-WorkflowArtifactMetadata -Artifacts $Rows -Kind partition -Configuration Release -RunnerOS windows-latest -WorkflowAttempt $Attempt -ShardCount 16}
function Must-Refuse([scriptblock]$Action,[string]$Expected){
    $refused=$false
    try{[void](& $Action)}catch{
        $refused=$true
        if(!$_.Exception.Message.Contains($Expected,[StringComparison]::Ordinal)){throw "Wrong refusal: $($_.Exception.Message); expected $Expected"}
    }
    if(!$refused){throw "Expected refusal: $Expected"}
}
$current=@(0..15|ForEach-Object {Artifact (100+$_) "partition-Release-windows-latest-$_-attempt-2"})
# The older named artifact is deliberately irrelevant and may be unavailable.
$old=Artifact 12 'partition-Release-windows-latest-12-attempt-1'
$full=Select-Parts (@($old)+$current)
if($full.selected.Count -ne 16 -or '12' -cin $full.selected.id -or @($full.selected|Where-Object attempt -ne 2).Count){throw 'Full rerun selected an irrelevant earlier generation.'}
$partial=Select-Parts ($current+@(Artifact 300 'partition-Release-windows-latest-3-attempt-3')) 3
if($partial.selected.Count -ne 16 -or $partial.selected[3].id -cne '300' -or @($partial.selected|Where-Object attempt -eq 2).Count -ne 15){throw 'Partial rerun did not preserve the correct per-slot generations.'}
Must-Refuse {Select-Parts ($current+@(Artifact 400 'partition-Release-windows-latest-3-attempt-4')) 3} 'newer than the current attempt'
Must-Refuse {Select-Parts ($current+@(Artifact 400 'partition-Release-windows-latest-3-attempt-03')) 3} 'Invalid workflow artifact name'
Must-Refuse {Select-Parts @($current|Where-Object id -ne 103)} 'slot is missing'
Must-Refuse {Select-Parts @()} 'slot is missing'
Must-Refuse {Select-Parts ($current+@(Artifact 301 'partition-Release-windows-latest-3-attempt-3' $true)) 3} 'expired or unproven'
Must-Refuse {Select-Parts ($current+@(Artifact 301 'partition-Release-windows-latest-3-attempt-3')+@(Artifact 302 'partition-Release-windows-latest-3-attempt-3')) 3} 'slot is ambiguous'
Must-Refuse {Select-Parts ($current+@(Artifact 400 'partition-Release-windows-latest-16-attempt-2'))} 'outside the declared partition inventory'
$invalid=$current|ForEach-Object {if($_.id -eq 103){[pscustomobject]@{id='103,999';name=$_.name;expired=$false}}else{$_}}
Must-Refuse {Select-Parts $invalid} 'positive canonical integer'
$unproven=$current|ForEach-Object {if($_.id -eq 103){[pscustomobject]@{id=$_.id;name=$_.name;expired='false'}}else{$_}}
Must-Refuse {Select-Parts $unproven} 'expired or unproven'
$duplicate=$current|ForEach-Object {if($_.id -eq 103){[pscustomobject]@{id=104;name=$_.name;expired=$false}}else{$_}}
Must-Refuse {Select-Parts $duplicate} 'IDs are not unique'
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts $current -Kind partition -Configuration Debug -RunnerOS windows-latest -WorkflowAttempt 2} 'slot is missing'
$builds=@((Artifact 1 'suite-build-Release-windows-latest-attempt-1'),(Artifact 2 'suite-build-Release-windows-latest-attempt-2'))
$build=Select-WorkflowArtifactMetadata -Artifacts $builds -Kind build -Configuration Release -RunnerOS windows-latest -WorkflowAttempt 3
if($build.ids -cne '2' -or $build.selected.Count -ne 1){throw 'Build selection did not choose the newest available generation.'}
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts ($builds+@(Artifact 3 'suite-build-Release-windows-latest-attempt-3' $true)) -Kind build -Configuration Release -RunnerOS windows-latest -WorkflowAttempt 3} 'expired or unproven'
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts $builds -Kind build -RunnerOS windows-latest -WorkflowAttempt 3} 'require a configuration'
$acceptance=@((Artifact 10 'acceptance-windows-latest-attempt-1'),(Artifact 11 'acceptance-ubuntu-latest-attempt-1'),(Artifact 12 'acceptance-windows-latest-attempt-2'))
$win=Select-WorkflowArtifactMetadata -Artifacts $acceptance -Kind acceptance -RunnerOS windows-latest -WorkflowAttempt 2
$linux=Select-WorkflowArtifactMetadata -Artifacts $acceptance -Kind acceptance -RunnerOS ubuntu-latest -WorkflowAttempt 2
if($win.ids -cne '12' -or $linux.ids -cne '11'){throw 'Platform acceptance partial selection differs.'}
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts @($acceptance|Where-Object id -ne 11) -Kind acceptance -RunnerOS ubuntu-latest -WorkflowAttempt 2} 'slot is missing'
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts ($acceptance+@(Artifact 13 'acceptance-windows-latest-attempt-2')) -Kind acceptance -RunnerOS windows-latest -WorkflowAttempt 2} 'slot is ambiguous'
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts ($acceptance+@(Artifact 13 'acceptance-ubuntu-latest-attempt-3')) -Kind acceptance -RunnerOS windows-latest -WorkflowAttempt 2} 'newer than the current attempt'
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts ($acceptance+@(Artifact 13 'acceptance-plan9-attempt-2')) -Kind acceptance -RunnerOS windows-latest -WorkflowAttempt 2} 'Invalid workflow artifact name'
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts $acceptance -Kind acceptance -Configuration Release -RunnerOS windows-latest -WorkflowAttempt 2} 'must not specify a configuration'

# The metadata reader receives complete API snapshots, never live network here.
$valid=[pscustomobject]@{exitCode=0;pages=@([pscustomobject]@{total_count=2;artifacts=$builds})}
$differentTotals=[pscustomobject]@{exitCode=0;pages=@([pscustomobject]@{total_count=2;artifacts=@($builds[0])},[pscustomobject]@{total_count=3;artifacts=@($builds[1])})}
$duplicatePages=[pscustomobject]@{exitCode=0;pages=@([pscustomobject]@{total_count=2;artifacts=@($builds[0])},[pscustomobject]@{total_count=2;artifacts=@($builds[0])})}
function Read-Fixture($Snapshots){
    $script:observations=@($Snapshots);$script:reads=0;$script:waits=0
    Read-WorkflowArtifactMetadata -ReadSnapshot {
        $value=$script:observations[[Math]::Min($script:reads,$script:observations.Count-1)];$script:reads++;$value
    } -WaitForNextObservation {$script:waits++}
}
$read=Read-Fixture @($differentTotals,$duplicatePages,$valid)
if($read.artifacts.Count -ne 2 -or $read.observations -ne 3 -or $script:reads -ne 3 -or $script:waits -ne 2){throw 'Metadata consistency recovery differs.'}
Must-Refuse {Read-Fixture @($differentTotals)} 'remained inconsistent'
if($script:reads -ne 3 -or $script:waits -ne 2){throw 'Metadata retry bound differs.'}
Must-Refuse {Read-Fixture @([pscustomobject]@{exitCode=1;pages=@()})} 'metadata unavailable'
if($script:reads -ne 1 -or $script:waits -ne 0){throw 'Transport failure retried.'}
Must-Refuse {Read-Fixture @([pscustomobject]@{exitCode=0;pages=@()})} 'metadata unavailable'
Must-Refuse {Read-WorkflowArtifactMetadata -ReadSnapshot {$null}} 'metadata unavailable'
$empty=Read-Fixture @([pscustomobject]@{exitCode=0;pages=@([pscustomobject]@{total_count=0;artifacts=@()})})
if($empty.artifacts.Count -ne 0 -or $empty.observations -ne 1){throw 'Empty complete metadata snapshot differs.'}

# Exercise the real entrypoint's combined context binding and output receipt.
# This script-scoped function shadows gh only in these child script calls.
$script:entryRows=@($acceptance)
$nextId=1000
foreach($os in @('windows-latest','ubuntu-latest')){
    foreach($config in @('Debug','Release')){
        $script:entryRows+=Artifact ($nextId++) "suite-build-$config-$os-attempt-2"
        foreach($slot in 0..15){$script:entryRows+=Artifact ($nextId++) "partition-$config-$os-$slot-attempt-2"}
    }
}
$script:entryJson=ConvertTo-Json -InputObject @([pscustomobject]@{total_count=$script:entryRows.Count;artifacts=$script:entryRows}) -Depth 6 -Compress
if(Get-Variable -Name CrapWorkflowSelectionFixture -Scope Global -ErrorAction SilentlyContinue){throw 'Fixture global collision.'}
$global:CrapWorkflowSelectionFixture=[pscustomobject]@{reads=0;json=$script:entryJson}
function gh {
    if($args[0] -cne 'api' -or $args[-1] -cne 'repos/fixture/repo/actions/runs/123/artifacts?per_page=100'){throw 'Unexpected fixture API request.'}
    $global:CrapWorkflowSelectionFixture.reads++;$global:LASTEXITCODE=0;$global:CrapWorkflowSelectionFixture.json
}
$saved=@{}
foreach($name in @('GITHUB_REPOSITORY','GH_TOKEN','GITHUB_OUTPUT')){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
# Only this invocation's small fake metadata receipts, never consumer data.
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('crap-workflow-selection-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
try{
    $env:GITHUB_REPOSITORY='fixture/repo';$env:GH_TOKEN='fixture-not-a-credential';$env:GITHUB_OUTPUT=Join-Path $fixture outputs.txt
    $entry=Join-Path $PSScriptRoot select-workflow-artifact-ids.ps1
    & $entry -Kind acceptance -RunnerOS windows-latest,ubuntu-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/acceptance.json"
    $receipt=Get-Content "$fixture/acceptance.json" -Raw|ConvertFrom-Json
    if($receipt.schemaVersion -cne 'workflow-artifact-selection-v2' -or $receipt.selected.Count -ne 2 -or $receipt.configurations.Count -ne 0 -or $receipt.runnerOS.Count -ne 2 -or $receipt.metadataObservations -ne 1 -or ($receipt.selected.id -join ',') -cne '12,11'){throw 'Combined platform entrypoint receipt differs.'}
    & $entry -Kind build -Configuration Debug,Release -RunnerOS windows-latest,ubuntu-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/build.json"
    $receipt=Get-Content "$fixture/build.json" -Raw|ConvertFrom-Json
    if($receipt.selected.Count -ne 4 -or $receipt.configurations.Count -ne 2 -or $receipt.runnerOS.Count -ne 2){throw 'Combined build entrypoint receipt differs.'}
    & $entry -Kind partition -Configuration Debug,Release -RunnerOS windows-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/partition.json"
    $receipt=Get-Content "$fixture/partition.json" -Raw|ConvertFrom-Json
    if($receipt.selected.Count -ne 32 -or @($receipt.selected.id|Sort-Object -Unique).Count -ne 32){throw 'Combined partition entrypoint receipt differs.'}
    if($global:CrapWorkflowSelectionFixture.reads -ne 3 -or @(Get-Content $env:GITHUB_OUTPUT).Count -ne 3){throw 'Entrypoint metadata/output call count differs.'}
    Must-Refuse {& $entry -Kind acceptance -Configuration Debug -RunnerOS windows-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/invalid.json"} 'must not specify a configuration'
    Must-Refuse {& $entry -Kind build -Configuration Debug,Debug -RunnerOS windows-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/duplicate.json"} 'contexts must be unique'
    Must-Refuse {& $entry -Kind acceptance -RunnerOS windows-latest,ubuntu-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/acceptance.json"} 'receipt already exists'
    if(@(Get-Content $env:GITHUB_OUTPUT).Count -ne 3){throw 'A refused entrypoint appended an accepted selection.'}
    $global:CrapWorkflowSelectionFixture.json='[]'
    $before=$global:CrapWorkflowSelectionFixture.reads
    Must-Refuse {& $entry -Kind acceptance -RunnerOS windows-latest,ubuntu-latest -WorkflowRun 123 -WorkflowAttempt 2 -OutputPath "$fixture/empty-api.json"} 'metadata unavailable'
    if($global:CrapWorkflowSelectionFixture.reads -ne $before+1){throw 'Empty API pages retried.'}
}finally{
    Remove-Variable -Name CrapWorkflowSelectionFixture -Scope Global
    foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
}
'Workflow artifact generation selection controls passed.'
