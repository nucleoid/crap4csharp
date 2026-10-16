param(
    [Parameter(Mandatory)][ValidateSet('build','partition')][string]$Kind,
    [Parameter(Mandatory)][ValidateSet('Debug','Release')][string]$Configuration,
    [Parameter(Mandatory)][ValidateSet('ubuntu-latest','windows-latest')][string]$RunnerOS,
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
$metadata=$null
# Only metadata reads retry: unrelated artifact publication can change pagination.
# No workflow/test rerun, artifact deletion, download fallback or receipt relaxation.
for($observation=0;$observation -lt 3;$observation++){
    $pages=@(& gh api --hostname github.com --paginate --slurp "repos/$env:GITHUB_REPOSITORY/actions/runs/$WorkflowRun/artifacts?per_page=100"|ConvertFrom-Json)
    if($LASTEXITCODE -ne 0 -or !$pages.Count){throw 'Workflow artifact metadata unavailable.'}
    $rows=@($pages|ForEach-Object artifacts)
    if(@($pages|Where-Object {$_.total_count -ne $pages[0].total_count}).Count -eq 0 -and
        $rows.Count -eq $pages[0].total_count -and @($rows.id|Sort-Object -Unique).Count -eq $rows.Count){$metadata=$rows;break}
    if($observation -lt 2){Start-Sleep -Seconds 2}
}
if($null -eq $metadata){throw 'Workflow artifact metadata remained inconsistent.'}
$result=Select-WorkflowArtifactMetadata -Artifacts $metadata -Kind $Kind -Configuration $Configuration -RunnerOS $RunnerOS -WorkflowAttempt $WorkflowAttempt -ShardCount $ShardCount
$output=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if(Test-Path -LiteralPath $output){throw 'Artifact selection receipt already exists.'}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
[ordered]@{schemaVersion='workflow-artifact-selection-v1';workflowRun=$WorkflowRun;workflowAttempt=$WorkflowAttempt;
    configuration=$Configuration;runnerOS=$RunnerOS;kind=$Kind;selected=$result.selected;
    qualification='Newest named generation per required slot only. Download and all existing source/runtime/SDK/partition receipt validators must still succeed; unavailable latest artifacts never fall back.'}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath $output
[IO.File]::AppendAllText($env:GITHUB_OUTPUT,"ids=$($result.ids)`n")
