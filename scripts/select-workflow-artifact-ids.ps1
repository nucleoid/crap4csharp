param(
    [Parameter(Mandatory)][ValidateSet('build','partition','acceptance')][string]$Kind,
    [ValidateSet('Debug','Release')][string[]]$Configuration=@(),
    [Parameter(Mandatory)][ValidateSet('ubuntu-latest','windows-latest')][string[]]$RunnerOS,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$WorkflowRun,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$WorkflowAttempt,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1,64)][int]$ShardCount=16
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/workflow-artifact-selection-contract.ps1"
if($env:GITHUB_REPOSITORY -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
    [string]::IsNullOrWhiteSpace($env:GH_TOKEN) -or [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)){
    throw 'Explicit current repository, read-only API token and action output destination required.'
}
if(@($RunnerOS|Sort-Object -Unique).Count -ne $RunnerOS.Count -or
    @($Configuration|Sort-Object -Unique).Count -ne @($Configuration).Count){throw 'Artifact selection contexts must be unique.'}
if($Kind -ceq 'acceptance'){
    if($Configuration.Count){throw 'Platform acceptance selection must not specify a configuration.'}
}elseif(!$Configuration.Count){throw 'Build and partition selection require a configuration.'}
# Only inconsistent metadata observations retry. No workflow/test rerun,
# artifact deletion, download fallback or receipt relaxation.
$snapshot=Read-WorkflowArtifactMetadata -ReadSnapshot {
    $raw=& gh api --hostname github.com --paginate --slurp "repos/$env:GITHUB_REPOSITORY/actions/runs/$WorkflowRun/artifacts?per_page=100"
    $code=$LASTEXITCODE
    $pages=@()
    if($code -eq 0){$pages=@($raw|ConvertFrom-Json)}
    [pscustomobject]@{exitCode=$code;pages=@($pages)}
}
$metadata=@($snapshot.artifacts)
$results=@(foreach($os in $RunnerOS){
    if($Kind -ceq 'acceptance'){
        Select-WorkflowArtifactMetadata -Artifacts $metadata -Kind acceptance -RunnerOS $os -WorkflowAttempt $WorkflowAttempt
    }else{
        foreach($config in $Configuration){
            Select-WorkflowArtifactMetadata -Artifacts $metadata -Kind $Kind -Configuration $config -RunnerOS $os -WorkflowAttempt $WorkflowAttempt -ShardCount $ShardCount
        }
    }
})
$selected=@($results|ForEach-Object selected)
if(!$selected.Count -or @($selected.id|Sort-Object -Unique).Count -ne $selected.Count){throw 'Combined artifact selection is empty or ambiguous.'}
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if(Test-Path -LiteralPath $output){throw 'Artifact selection receipt already exists.'}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
[ordered]@{schemaVersion='workflow-artifact-selection-v2';workflowRun=$WorkflowRun;workflowAttempt=$WorkflowAttempt;
    configurations=@($Configuration);runnerOS=@($RunnerOS);kind=$Kind;selected=$selected;metadataObservations=$snapshot.observations;
    qualification='Newest named generation per required slot only. Download and all existing source/runtime/SDK/package/partition receipt validators must still succeed; unavailable latest artifacts never fall back.'}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath $output
[IO.File]::AppendAllText($env:GITHUB_OUTPUT,"ids=$($selected.id -join ',')`n")
