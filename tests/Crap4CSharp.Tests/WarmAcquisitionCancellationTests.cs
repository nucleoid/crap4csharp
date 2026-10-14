using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class WarmAcquisitionCancellationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SelectedSdkSubmissionReachesCallbackEndBuildAndDisposalBeforeTerminal(bool cancel, bool fail)
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-live-cancel");
        var target = fixture.Write("Probe.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <UsingTask TaskName="WarmLiveTask" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
            <ParameterGroup><TracePath ParameterType="System.String" Required="true" /><WaitForCancel ParameterType="System.Boolean" /></ParameterGroup>
            <Task><Code Type="Class" Language="cs"><![CDATA[
            public sealed class WarmLiveTask : Microsoft.Build.Utilities.Task, Microsoft.Build.Framework.ICancelableTask
            {
                public string TracePath { get; set; }
                public bool WaitForCancel { get; set; }
                private readonly System.Threading.ManualResetEvent signal = new System.Threading.ManualResetEvent(false);
                public override bool Execute()
                {
                    System.IO.File.AppendAllText(TracePath, "entered\n");
                    Log.LogMessage(Microsoft.Build.Framework.MessageImportance.High, "CRAP_WARM_DIAGNOSTIC_LIVE");
                    bool canceled = WaitForCancel && signal.WaitOne(System.TimeSpan.FromSeconds(20));
                    System.IO.File.AppendAllText(TracePath, canceled ? "returned-canceled\n" : "returned-natural\n");
                    signal.Dispose();
                    return !canceled;
                }
                public void Cancel()
                {
                    System.IO.File.AppendAllText(TracePath, "cancel-called\n");
                    signal.Set();
                }
            }
            ]]></Code></Task></UsingTask>
            <Target Name="LiveDiagnostic" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/hooks.txt" Lines="once" Overwrite="false" />
            <WarmLiveTask TracePath="$(MSBuildProjectDirectory)/task-trace.txt" WaitForCancel="{{cancel}}" />
            {{(fail ? "<Error Text=\"Deliberate diagnostic failure\" />" : "")}}
            </Target></Project>
            """);
        fixture.Write("Code.cs", "public class Code { public int Value => 1; }");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(target, "Debug", "AnyCPU", ["net10.0"], false, false, TimeSpan.FromMinutes(2));
        var restore = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var sdk = await ProjectContextLoader.ResolveSdkAsync(fixture.Path, request.Timeout, token);
        request = request with { SdkPath = sdk.Path, DotNetRoot = sdk.DotNetRoot, DotNetHostPath = sdk.HostPath };
        var input = fixture.Write("diagnostic-request.json", JsonSerializer.Serialize(
            new WarmAcquisitionDiagnosticBootstrap.Request(request, cancel), ProjectContextLoader.JsonOptions));
        var output = Path.Combine(fixture.Path, "diagnostic-response.json");
        var run = await ProcessRunner.RunWithEnvironmentAsync(sdk.HostPath,
            [typeof(ProjectContextLoader).Assembly.Location, WarmAcquisitionDiagnosticBootstrap.Command, input, output],
            fixture.Path, request.Timeout, token, new Dictionary<string, string>
            {
                ["DOTNET_ROOT"] = sdk.DotNetRoot, ["DOTNET_HOST_PATH"] = sdk.HostPath,
                ["PATH"] = sdk.DotNetRoot + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
            });
        Assert.Equal(0, run.ExitCode); // The worker must exit naturally, not parent-tree termination.
        var result = JsonSerializer.Deserialize<WarmAcquisitionDiagnosticBootstrap.Result>(await File.ReadAllTextAsync(output, token), ProjectContextLoader.JsonOptions)!;
        Assert.True(result.Status == (cancel ? "canceled" : fail ? "failed" : "success"), JsonSerializer.Serialize(result));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "hooks.txt")));
        Assert.Equal(cancel ? new[] { "entered", "cancel-called", "returned-canceled" } : new[] { "entered", "returned-natural" },
            File.ReadAllLines(Path.Combine(fixture.Path, "task-trace.txt")));
        var trace = result.Trace;
        Assert.Single(trace, value => value == "task.live");
        var callback = Assert.Single(trace, value => value.StartsWith("submission.callback.", StringComparison.Ordinal));
        Assert.Equal(cancel || fail ? "submission.callback.Failure" : "submission.callback.Success", callback);
        Assert.True(Array.IndexOf(trace, "task.live") < Array.IndexOf(trace, callback));
        Assert.True(Array.IndexOf(trace, callback) < Array.IndexOf(trace, "build.end.returned"));
        Assert.True(Array.IndexOf(trace, "build.end.returned") < Array.IndexOf(trace, "manager.disposed"));
        Assert.True(Array.IndexOf(trace, "manager.disposed") < Array.IndexOf(trace, "collection.disposed"));
        Assert.Equal("diagnostic.terminal", trace[^1]);
        if (cancel)
        {
            Assert.Single(trace, value => value == "submission.cancel.requested");
            Assert.True(Array.IndexOf(trace, "collection.disposed") < Array.IndexOf(trace, "acquisition.canceled.returned"));
            Assert.DoesNotContain("project.acquired", trace);
        }
        else if (!fail) Assert.Contains("workspace.disposed", trace);
    }

    [Fact]
    public async Task MissingSelectedSdkIsSetupFailureNotCancellationOrCallbackProof()
    {
        using var fixture = TestDirectory.Create("crap4csharp-warm-cancel-setup");
        var request = new ProjectContextLoadRequest("absent.csproj", "Debug", "AnyCPU", ["net10.0"], false, false, TimeSpan.FromMinutes(1));
        var input = fixture.Write("request.json", JsonSerializer.Serialize(new WarmAcquisitionDiagnosticBootstrap.Request(request, true), ProjectContextLoader.JsonOptions));
        var output = Path.Combine(fixture.Path, "response.json");
        Assert.Equal(0, await WarmAcquisitionDiagnosticBootstrap.RunAsync(input, output));
        var result = JsonSerializer.Deserialize<WarmAcquisitionDiagnosticBootstrap.Result>(await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken), ProjectContextLoader.JsonOptions)!;
        Assert.Equal("setup-failed", result.Status);
        Assert.Empty(result.Trace);
    }
}
