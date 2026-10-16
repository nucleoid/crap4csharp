$ErrorActionPreference='Stop'
. "$PSScriptRoot/workflow-artifact-selection-contract.ps1"
function Artifact([long]$Id,[string]$Name,[bool]$Expired=$false){[pscustomobject]@{id=$Id;name=$Name;expired=$Expired}}
function Select-Parts($Rows,[int]$Attempt=2){Select-WorkflowArtifactMetadata -Artifacts $Rows -Kind partition -Configuration Release -RunnerOS windows-latest -WorkflowAttempt $Attempt -ShardCount 16}
function Must-Refuse([scriptblock]$Action){$refused=$false;try{[void](& $Action)}catch{$refused=$true};if(!$refused){throw 'Expected fail-closed artifact selection.'}}
$current=@(0..15|ForEach-Object {Artifact (100+$_) "partition-Release-windows-latest-$_-attempt-2"})
# Reproduce the actual failed rerun: one unexpired but unavailable old generation
# appeared alongside all sixteen current receipts. It must never be downloaded.
$old=Artifact 12 'partition-Release-windows-latest-12-attempt-1'
$full=Select-Parts (@($old)+$current)
if($full.selected.Count -ne 16 -or '12' -cin $full.selected.id -or @($full.selected|Where-Object attempt -ne 2).Count){throw 'Full rerun selected an irrelevant earlier generation.'}
$partial=Select-Parts ($current+@(Artifact 300 'partition-Release-windows-latest-3-attempt-3')) 3
if($partial.selected.Count -ne 16 -or $partial.selected[3].id -cne '300' -or @($partial.selected|Where-Object attempt -eq 2).Count -ne 15){throw 'Partial rerun did not preserve the correct per-slot generations.'}
$future=Select-Parts ($current+@(Artifact 400 'partition-Release-windows-latest-3-attempt-4')) 3
if($future.ids -cne ($current.id -join ',')){throw 'Future attempt was admitted.'}
Must-Refuse {Select-Parts @($current|Where-Object id -ne 103)}
Must-Refuse {Select-Parts ($current+@(Artifact 301 'partition-Release-windows-latest-3-attempt-3' $true)) 3}
Must-Refuse {Select-Parts ($current+@(Artifact 301 'partition-Release-windows-latest-3-attempt-3')+@(Artifact 302 'partition-Release-windows-latest-3-attempt-3')) 3}
$invalid=$current|ForEach-Object {if($_.id -eq 103){[pscustomobject]@{id='103,999';name=$_.name;expired=$false}}else{$_}}
Must-Refuse {Select-Parts $invalid}
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts $current -Kind partition -Configuration Debug -RunnerOS windows-latest -WorkflowAttempt 2}
$build=Select-WorkflowArtifactMetadata -Artifacts @((Artifact 1 'suite-build-Release-windows-latest-attempt-1'),(Artifact 2 'suite-build-Release-windows-latest-attempt-2')) -Kind build -Configuration Release -RunnerOS windows-latest -WorkflowAttempt 3
if($build.ids -cne '2' -or $build.selected.Count -ne 1){throw 'Build selection did not choose the newest available generation.'}
Must-Refuse {Select-WorkflowArtifactMetadata -Artifacts @((Artifact 1 'suite-build-Release-windows-latest-attempt-1'),(Artifact 2 'suite-build-Release-windows-latest-attempt-2' $true)) -Kind build -Configuration Release -RunnerOS windows-latest -WorkflowAttempt 3}
'Workflow artifact generation selection controls passed.'
