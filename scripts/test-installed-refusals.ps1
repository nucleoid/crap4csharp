param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [Parameter(Mandatory)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
. "$PSScriptRoot/sdk-acceptance-contract.ps1"
if(-not ('InstalledRefusalAcceptance.BoundedText' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
namespace InstalledRefusalAcceptance {
    public static class BoundedText {
        public static async Task<string> ReadAsync(StreamReader reader, int maximum) {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0) {
                if (count > maximum - text.Length) throw new InvalidDataException("Acceptance output limit exceeded.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
    }
}
'@
}
function Invoke-BoundedAcceptanceProcess([string]$Program,[string[]]$Arguments,[string]$Directory,[string]$Stdout,[string]$Stderr){
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$Program
    $start.WorkingDirectory=$Directory
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    foreach($argument in $Arguments){[void]$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    $started=$false
    try{
        [void]$process.Start()
        $started=$true
        $outTask=[InstalledRefusalAcceptance.BoundedText]::ReadAsync($process.StandardOutput,4MB)
        $errTask=[InstalledRefusalAcceptance.BoundedText]::ReadAsync($process.StandardError,4MB)
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while(-not $process.WaitForExit(250)){
            if($outTask.IsFaulted -or $errTask.IsFaulted){throw 'Owned acceptance output exceeded its bound or failed.'}
            if($clock.Elapsed.TotalSeconds -ge 240){throw 'Owned installed acceptance process exceeded its outer deadline.'}
        }
        if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask),10000)){
            throw 'Owned acceptance output pumps did not settle.'
        }
        $outText=$outTask.GetAwaiter().GetResult();$errText=$errTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($Stdout,$outText)
        [IO.File]::WriteAllText($Stderr,$errText)
        return $process.ExitCode
    }finally{
        try{
            if($started -and -not $process.HasExited){
                $process.Kill($true)
                if(-not $process.WaitForExit(10000)){throw 'Owned acceptance process did not settle after termination.'}
            }
        }finally{$process.Dispose()}
    }
}
if($ExpectedVersion -cnotmatch '^\d+\.\d+\.\d+-[0-9A-Za-z][0-9A-Za-z.-]*$' -or $ExpectedCommit -cnotmatch '^[0-9a-f]{40}$'){
    throw 'Require one exact prerelease candidate version and source commit.'
}
$sdk=Get-SuiteAcceptanceSdkVersion
$dotnetHost=(Get-Command dotnet -CommandType Application|Select-Object -First 1).Source
if((& $dotnetHost --version).Trim() -cne $sdk){throw 'Installed refusal acceptance requires the exact declared SDK.'}
$inputPackage=(Resolve-Path -LiteralPath $PackagePath).ProviderPath
$owner=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $owner){throw 'Refuse to overwrite prior installed acceptance evidence.'}
$candidate=$owner
$receipt=[pscustomobject]@{version=$ExpectedVersion;commit=$ExpectedCommit;package=[IO.Path]::GetFileName($inputPackage);sha256=(Get-FileHash -LiteralPath $inputPackage).Hash.ToLowerInvariant()}
[void][IO.Directory]::CreateDirectory((Join-Path $owner 'feed'))
Copy-Item -LiteralPath $inputPackage -Destination (Join-Path $owner ('feed/'+$receipt.package))
$config=Join-Path $owner 'tool-package-source.config'
$feed=[Security.SecurityElement]::Escape((Join-Path $candidate 'feed'))
[IO.File]::WriteAllText($config,"<configuration><packageSources><clear /><add key=`"candidate`" value=`"$feed`" /></packageSources></configuration>")
$tools=Join-Path $owner 'tools'
$priorPackages=$env:NUGET_PACKAGES
$priorHttpCache=$env:NUGET_HTTP_CACHE_PATH
try {
    $env:NUGET_PACKAGES=Join-Path $owner 'tool-package-cache'
    $env:NUGET_HTTP_CACHE_PATH=Join-Path $owner 'tool-http-cache'
    $installExit=Invoke-BoundedAcceptanceProcess $dotnetHost @('tool','install','Crap4CSharp.Tool','--version',$receipt.version,'--tool-path',$tools,'--configfile',$config,'--no-cache') $owner (Join-Path $owner 'install.log') (Join-Path $owner 'install.stderr.log')
    if($installExit){throw 'Exact candidate install failed.'}
}finally{
    $env:NUGET_PACKAGES=$priorPackages
    $env:NUGET_HTTP_CACHE_PATH=$priorHttpCache
}
$toolName=if([OperatingSystem]::IsWindows()){'crap4csharp.exe'}else{'crap4csharp'}
$executable=Join-Path $tools $toolName
$rows=@()
foreach($mode in @('no-collector','mtp-property','mtp-global')){
    $root=[IO.Directory]::CreateTempSubdirectory('crap4csharp-installed-refusal-').FullName
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Library'))
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Tests'))
    $runner=if($mode -eq 'mtp-global'){'Microsoft.Testing.Platform'}else{'VSTest'}
    $files=[ordered]@{
        'global.json'="{`"sdk`":{`"version`":`"$sdk`",`"rollForward`":`"latestPatch`"},`"test`":{`"runner`":`"$runner`"}}"
        'Library/Library.csproj'='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
        'Library/Code.cs'='public static class Library { public static int Value() => 42; }'
        'Tests/Test.cs'='public class Test { [Xunit.Fact] public void Pass() { Xunit.Assert.Equal(42, Library.Value()); } }'
        'Directory.Build.targets'='<Project><Target Name="RefuseActualExecution" BeforeTargets="Build;CoreCompile;VSTest" Condition="''$(DesignTimeBuild)'' != ''true''"><WriteLinesToFile File="$(MSBuildProjectDirectory)/unexpected-execution.txt" Lines="executed" Overwrite="false" /><Error Text="Unsupported setup must refuse before real build/test." /></Target></Project>'
        'policy.json'='{"schemaVersion":"repository-policy-v1","mode":"strict","productionProjects":["Library/Library.csproj"],"testProjects":["Tests/Tests.csproj"],"configuration":"Debug","targetFrameworks":["net10.0"],"scope":"all","threshold":8,"missingCoverage":"fail","requiredChecks":["tests","coverage","crap"],"exclusions":[],"ruleset":"callables-v1"}'
    }
    $mtp=if($mode -eq 'mtp-property'){'<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>'}else{''}
    $collector=if($mode -eq 'no-collector'){''}else{'<PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />'}
    $files['Tests/Tests.csproj']='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject>'+ $mtp +'</PropertyGroup><ItemGroup><ProjectReference Include="../Library/Library.csproj" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" /><PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" /><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />'+$collector+'</ItemGroup></Project>'
    $hashes=@{}
    foreach($path in $files.Keys){$full=Join-Path $root $path;[IO.File]::WriteAllText($full,$files[$path]);$hashes[$path]=(Get-FileHash -LiteralPath $full).Hash}
    $terminal=Join-Path $owner ($mode+'-terminal.json')
    $stderr=Join-Path $owner ($mode+'-stderr.log')
    $exit=Invoke-BoundedAcceptanceProcess $executable @('check','--project','Tests/Tests.csproj','--policy','policy.json','--scope','all','--format','json','--timeout-seconds','180') $root $terminal $stderr
    $result=Get-Content $terminal -Raw|ConvertFrom-Json
    $expected=if($mode -eq 'no-collector'){'test.collectorUnsupported'}else{'test.runnerUnsupported'}
    $reasons=@($result.evaluation.checks|ForEach-Object reason)
    $changes=@(foreach($path in $files.Keys){if((Get-FileHash -LiteralPath (Join-Path $root $path)).Hash -cne $hashes[$path]){$path}})
    $markers=@(Get-ChildItem -LiteralPath $root -Recurse -File -Filter unexpected-execution.txt)
    if($exit -ne 1 -or $result.run.exitCode -ne 1 -or $result.schemaVersion -cne '1.0' -or $result.toolVersion -cne ($receipt.version+'+'+$receipt.commit) -or $expected -cnotin $reasons -or $changes.Count -or $markers.Count){throw "Installed refusal contract failed: $mode"}
    if($result.run.cancellation.cancelled -or $result.run.cancellation.timedOut -or
        $result.evaluation.decision.completed -or $result.evaluation.decision.policyDecision -cne 'unknown'){
        throw "Unsupported capability was not an ordinary incomplete refusal: $mode"
    }
    # Test outcomes are optional/null when refusal precedes execution. Strict
    # mode must still reject every present non-null outcome, including malformed
    # scalar/object values; absence must not throw before this oracle runs.
    $outcomesProperty=$result.run.PSObject.Properties['testOutcomes']
    $testOutcomes=@(if($null -ne $outcomesProperty){$outcomesProperty.Value|Where-Object {$null -ne $_}})
    if(@($result.run.artifacts|Where-Object {$_.complete -or $_.reusable}).Count -or
        $testOutcomes.Count){throw 'Unsupported setup produced execution/reusable evidence.'}
    $rows+=@([pscustomobject]@{mode=$mode;fixture=$root;terminal=$terminal;processExit=$exit;reason=$expected;sourceChanges=$changes;actualExecutionMarkers=$markers.Count;terminalSha256=(Get-FileHash -LiteralPath $terminal).Hash.ToLowerInvariant()})
}
if((Get-FileHash -LiteralPath (Join-Path $candidate ('feed/'+$receipt.package))).Hash.ToLowerInvariant() -cne $receipt.sha256){throw 'Package changed.'}
[ordered]@{commit=$receipt.commit;packageSha256=$receipt.sha256;rows=$rows;qualification='Installed modern unsupported collector/runner probes on owned fixtures only; no public-deadline or whole-suite acceptance claim.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $owner 'completed.json')
$rows|Select-Object mode,processExit,reason,actualExecutionMarkers,terminalSha256|ConvertTo-Json -Depth 4
