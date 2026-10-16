using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProjectDiscoveryTests
{
    [Theory]
    [InlineData("net10.0", "Debug", "AnyCPU")]
    [InlineData("net10.0;netstandard2.1", "Release", "x64")]
    public async Task ColdDiscoveryEvaluatesWithoutRestoreWorkspaceOrBuild(string frameworks, string configuration, string platform)
    {
        using var fixture = TestDirectory.Create("crap4csharp-cold-discovery");
        var project = fixture.Write("Cold.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>{{frameworks}}</TargetFrameworks></PropertyGroup>
              <Target Name="RefuseExecution" BeforeTargets="Restore;Build;Compile;CompileDesignTime">
                <Error Text="Metadata discovery must never execute targets." />
              </Target>
            </Project>
            """);
        fixture.Write("Code.cs", "public class Code { public int M() => 1; }");
        var before = Directory.GetFiles(fixture.Path).ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Mtime: File.GetLastWriteTimeUtc(path)));
        var result = await ProjectContextLoader.DiscoverAsync(new(project, configuration, platform, [], false, false,
            TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Empty(result.Contexts); // Workspace inventory is deliberately unavailable at this phase.
        var metadata = Assert.Single(result.Metadata);
        Assert.Equal(project, metadata.ProjectPath);
        Assert.Equal(configuration, metadata.Configuration);
        Assert.Equal(platform, metadata.Platform);
        Assert.Equal(frameworks.Split(';'), metadata.Frameworks);
        Assert.False(File.Exists(metadata.AssetsFile));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, value) in before)
        {
            Assert.Equal(value.Bytes, File.ReadAllBytes(path));
            Assert.Equal(value.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task OrdinaryLoaderStillRefusesColdAssets()
    {
        using var fixture = TestDirectory.Create("crap4csharp-cold-loader-control");
        var project = fixture.Write("Cold.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var result = await ProjectContextLoader.LoadAsync(new(project, "Debug", null, [], false, false,
            TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("context.assetsUnavailable", result.FailureReason);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
    }
}
