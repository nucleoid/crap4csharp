using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class FreshProjectPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryAndExplicitRestorePrecedeWorkspace(bool noRestore)
    {
        var calls = new List<string>();
        var request = new ProjectContextLoadRequest("App.csproj", "Release", "x64", ["net10.0"], true, false, TimeSpan.FromMinutes(1));
        var result = await FreshProjectPreparation.LoadAsync(request, noRestore, TestContext.Current.CancellationToken,
            (actual, _) => { Assert.Equal(request, actual); calls.Add("discover"); return Task.FromResult(new ProjectContextLoadResult(true, [], [])); },
            (target, timeout, _) => { Assert.Equal(request.Target, target); Assert.Equal(request.Timeout, timeout); calls.Add("restore"); return Task.FromResult(new ProcessResult(0, "", "")); },
            (actual, _) => { Assert.Equal(request, actual); calls.Add("workspace"); return Task.FromResult(new ProjectContextLoadResult(true, [], [])); });
        Assert.True(result.Success);
        Assert.Equal(noRestore ? ["discover", "workspace"] : new[] { "discover", "restore", "workspace" }, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedDiscoveryOrRestoreNeverLoadsWorkspace(bool discoveryFails)
    {
        var calls = new List<string>();
        var request = new ProjectContextLoadRequest("App.csproj", "Debug", null, [], true, false, TimeSpan.FromMinutes(1));
        var result = await FreshProjectPreparation.LoadAsync(request, false, TestContext.Current.CancellationToken,
            (_, _) => { calls.Add("discover"); return Task.FromResult(new ProjectContextLoadResult(!discoveryFails, [], [], discoveryFails ? "context.targetNotFound" : null)); },
            (_, _, _) => { calls.Add("restore"); return Task.FromResult(new ProcessResult(23, "", "fixture restore failure")); },
            (_, _) => { calls.Add("workspace"); return Task.FromResult(new ProjectContextLoadResult(true, [], [])); });
        Assert.False(result.Success);
        Assert.Equal(discoveryFails ? "context.targetNotFound" : "context.restoreFailed", result.FailureReason);
        Assert.Equal(discoveryFails ? ["discover"] : new[] { "discover", "restore" }, calls);
    }

    [Fact]
    public async Task ActualColdProjectRestoresBeforeWorkspaceAndPreservesAuthoredFiles()
    {
        using var fixture = TestDirectory.Create("crap4csharp-fresh-preparation");
        var project = fixture.Write("Cold.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var source = fixture.Write("Code.cs", "public class Code { public int M() => 1; }");
        var before = new[] { project, source }.ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Mtime: File.GetLastWriteTimeUtc(path)));
        var request = new ProjectContextLoadRequest(project, "Debug", null, [], true, false, TimeSpan.FromMinutes(2));
        var refused = await FreshProjectPreparation.LoadAsync(request, true, TestContext.Current.CancellationToken);
        Assert.False(refused.Success);
        Assert.Equal("context.assetsUnavailable", refused.FailureReason);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
        var result = await FreshProjectPreparation.LoadAsync(request, false, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Single(result.Contexts);
        Assert.True(File.Exists(Path.Combine(fixture.Path, "obj", "project.assets.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "bin"))); // no declared build at this stage
        foreach (var (path, value) in before)
        {
            Assert.Equal(value.Bytes, File.ReadAllBytes(path));
            Assert.Equal(value.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }
}
