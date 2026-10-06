using System.Reflection;
using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestRunnerDiscoveryTests
{
    [Theory]
    [InlineData("supported", true, null)]
    [InlineData("mtp-property", false, "test.runnerUnsupported")]
    [InlineData("mtp-global", false, "test.runnerUnsupported")]
    [InlineData("custom", false, "test.runnerUnsupported")]
    [InlineData("no-collector", false, "test.collectorUnsupported")]
    [InlineData("custom-console", false, "test.runnerUnsupported")]
    [InlineData("custom-task", false, "test.runnerUnsupported")]
    public async Task RestoredRunnerDiscoveryNeverBuildsOrTests(string mode, bool success, string? reason)
    {
        using var fixture = TestDirectory.Create("crap4csharp-runner-discovery");
        fixture.Write("global.json", $$"""
            { "sdk": { "version": "10.0.103", "rollForward": "latestPatch" },
              "test": { "runner": "{{(mode == "mtp-global" ? "Microsoft.Testing.Platform" : "VSTest")}}" } }
            """);
        var project = fixture.Write("Tests.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            {{(mode == "mtp-property" ? "<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>" : "")}}
            {{(mode == "custom-console" ? "<VSTestConsolePath>custom.dll</VSTestConsolePath>" : "")}}
            {{(mode == "custom-task" ? "<VSTestTaskAssemblyFile>custom.dll</VSTestTaskAssemblyFile>" : "")}}
            </PropertyGroup><ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
            <PackageReference Include="xunit.v3" Version="3.2.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
            {{(mode == "no-collector" ? "" : "<PackageReference Include=\"coverlet.collector\" Version=\"6.0.4\" />")}}
            </ItemGroup><Target Name="RefuseExecution" BeforeTargets="Build;CoreCompile;VSTest">
            <Error Text="Runner discovery must not execute consumer targets." /></Target>
            {{(mode == "custom" ? "<Target Name=\"VSTest\"><Error Text=\"Custom runner\" /></Target>" : "")}}
            </Project>
            """);
        if (mode == "custom")
        {
            // Implicit SDK targets overwrite inline VSTest; a real late override must follow Sdk.targets.
            var text = File.ReadAllText(project).Replace("<Project Sdk=\"Microsoft.NET.Sdk\">",
                "<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" />", StringComparison.Ordinal)
                .Replace("<Target Name=\"VSTest\">", "<Import Project=\"Sdk.targets\" Sdk=\"Microsoft.NET.Sdk\" /><Target Name=\"VSTest\">", StringComparison.Ordinal);
            File.WriteAllText(project, text);
        }
        fixture.Write("Test.cs", "public class Tests { [Xunit.Fact] public void Pass() { } }");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var before = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var method = typeof(ProjectContextLoader).GetMethod("DiscoverTestRunnersAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(method is not null, "Missing post-restore effective runner metadata entrypoint.");
        var result = await (Task<ProjectContextLoadResult>)method.Invoke(null, [request, TestContext.Current.CancellationToken])!;
        Assert.Equal(success, result.Success);
        Assert.Equal(reason, result.FailureReason);
        Assert.Empty(result.Contexts); // No Workspace, build or test execution at preflight.
        if (success)
        {
            var json = JsonSerializer.SerializeToElement(result, ProjectContextLoader.JsonOptions);
            var runners = json.GetProperty("testRunners");
            Assert.Equal(1, runners.GetArrayLength());
            Assert.Equal(project, runners[0].GetProperty("projectPath").GetString());
            Assert.Equal("Release", runners[0].GetProperty("configuration").GetString());
            Assert.Equal("net10.0", runners[0].GetProperty("targetFramework").GetString());
            Assert.Equal("VSTest", runners[0].GetProperty("runner").GetString());
            Assert.Equal("18.0.1", runners[0].GetProperty("testSdkVersion").GetString());
            Assert.Equal("6.0.4", runners[0].GetProperty("collectorVersion").GetString());
            Assert.False(JsonSerializer.Serialize(result).Contains("testRunners", StringComparison.OrdinalIgnoreCase));
        }
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, value) in before)
        {
            Assert.Equal(value.Bytes, File.ReadAllBytes(path));
            Assert.Equal(value.Mtime, File.GetLastWriteTimeUtc(path));
        }
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.pdb", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TestRunnersPreserveMappedDependencyConfigurationWithoutWorkspace()
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-runners");
        const string packages = "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.1\" /><PackageReference Include=\"coverlet.collector\" Version=\"6.0.4\" /></ItemGroup>";
        var dep = fixture.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + packages + "</Project>");
        var root = fixture.Write("Root/Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + packages +
            "<ItemGroup><ProjectReference Include=\"../Dep/Dep.csproj\" AdditionalProperties=\"Configuration=Debug;Flavor=left\" /></ItemGroup></Project>");
        var request = new ProjectContextLoadRequest(root, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var before = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var result = await ProjectContextLoader.DiscoverTestRunnersAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Empty(result.Contexts);
        Assert.Equal(2, result.TestRunners.Count);
        var dependency = Assert.Single(result.TestRunners, r => r.ProjectPath == dep);
        Assert.Equal("Debug", dependency.Configuration);
        var entry = Assert.Single(result.TestRunners, r => r.ProjectPath == root);
        Assert.Equal("Release", entry.Configuration);
        Assert.Equal("left", Assert.Single(result.BuildGraph, n => n.Id == dependency.GraphId).GlobalProperties["Flavor"]);
        foreach (var (path, value) in before)
        {
            Assert.Equal(value.Bytes, File.ReadAllBytes(path));
            Assert.Equal(value.Mtime, File.GetLastWriteTimeUtc(path));
        }
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task ColdRunnerPreflightDoesNotRestore()
    {
        using var fixture = TestDirectory.Create("crap4csharp-cold-runners");
        var project = fixture.Write("Cold.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        var result = await ProjectContextLoader.DiscoverTestRunnersAsync(new(project, "Release", null, [], false, false,
            TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("context.assetsUnavailable", result.FailureReason);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
    }
}
