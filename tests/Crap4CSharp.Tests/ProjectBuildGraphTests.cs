using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProjectBuildGraphTests
{
    [Theory]
    [InlineData("AdditionalProperties")]
    [InlineData("SetConfiguration")]
    public async Task MappedDependencyIsNotFlattenedToRootConfiguration(string mapping)
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-graph");
        var dependency = fixture.Write("Dependency/Dependency.csproj", Project());
        var root = fixture.Write("Root/Root.csproj", Project($"<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" {mapping}=\"Configuration=Debug\" /></ItemGroup>"));
        var result = await Discover(root);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Equal(2, result.BuildGraph.Count);
        var first = result.BuildGraph[0];
        var last = result.BuildGraph[1];
        Assert.Equal(dependency, first.ProjectPath);
        Assert.Equal("Debug", first.Configuration);
        Assert.Equal("Debug", first.GlobalProperties["Configuration"]);
        Assert.Equal(root, last.ProjectPath);
        Assert.Equal("Release", last.Configuration);
        Assert.Equal(new[] { first.Id }, last.Dependencies);
        Assert.Empty(first.Dependencies);
        Assert.All(result.BuildGraph, node => Assert.Equal("net10.0", node.TargetFramework));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "Root", "obj")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "Dependency", "obj")));
    }

    [Fact]
    public async Task SharedDependencyDeduplicatesOnlyIdenticalMappedGlobalProperties()
    {
        using var fixture = TestDirectory.Create("crap4csharp-graph-dedup");
        var dependency = fixture.Write("Dependency/Dependency.csproj", Project());
        fixture.Write("Left/Left.csproj", Project("<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" AdditionalProperties=\"Flavor=left\" /></ItemGroup>"));
        fixture.Write("Right/Right.csproj", Project("<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" AdditionalProperties=\"Flavor=right\" /></ItemGroup>"));
        var root = fixture.Write("Root/Root.csproj", Project("<ItemGroup><ProjectReference Include=\"../Left/Left.csproj\" /><ProjectReference Include=\"../Right/Right.csproj\" /></ItemGroup>"));
        var result = await Discover(root);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Equal(5, result.BuildGraph.Count);
        var dependencies = result.BuildGraph.Where(node => node.ProjectPath == dependency).ToArray();
        Assert.Equal(2, dependencies.Length);
        Assert.Equal(new[] { "left", "right" }, dependencies.Select(node => node.GlobalProperties["Flavor"]).Order());
        Assert.NotEqual(dependencies[0].Id, dependencies[1].Id);
        var seen = new HashSet<string>();
        foreach (var node in result.BuildGraph)
        {
            Assert.All(node.Dependencies, id => Assert.Contains(id, seen));
            Assert.True(seen.Add(node.Id));
        }
    }

    [Fact]
    public async Task MultiFrameworkDependencyNegotiationIsExplicitRatherThanFlattened()
    {
        using var fixture = TestDirectory.Create("crap4csharp-graph-frameworks");
        var dependency = fixture.Write("Dependency/Dependency.csproj", Project().Replace(
            "<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>netstandard2.1;net10.0</TargetFrameworks>"));
        var root = fixture.Write("Root/Root.csproj", Project("<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" /></ItemGroup>"));
        var result = await Discover(root);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.All(result.BuildGraph, node => Assert.False(string.IsNullOrWhiteSpace(node.TargetFramework)));
        var rootNode = Assert.Single(result.BuildGraph, node => node.ProjectPath == root);
        Assert.NotEmpty(rootNode.Dependencies);
        Assert.All(rootNode.Dependencies, id => Assert.Contains(result.BuildGraph,
            node => node.Id == id && node.ProjectPath == dependency && node.TargetFramework == "net10.0"));
    }

    [Fact]
    public async Task IdenticalSharedDependencyIsScheduledOnce()
    {
        using var fixture = TestDirectory.Create("crap4csharp-graph-identical");
        var dependency = fixture.Write("Dependency/Dependency.csproj", Project());
        fixture.Write("Left/Left.csproj", Project("<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" /></ItemGroup>"));
        fixture.Write("Right/Right.csproj", Project("<ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" /></ItemGroup>"));
        var root = fixture.Write("Root/Root.csproj", Project("<ItemGroup><ProjectReference Include=\"../Left/Left.csproj\" /><ProjectReference Include=\"../Right/Right.csproj\" /></ItemGroup>"));
        var result = await Discover(root);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Equal(4, result.BuildGraph.Count);
        Assert.Single(result.BuildGraph, node => node.ProjectPath == dependency);
    }

    private static Task<ProjectContextLoadResult> Discover(string root) => ProjectContextLoader.DiscoverGraphAsync(
        new(root, "Release", "AnyCPU", [], true, false, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);

    private static string Project(string extra = "") => "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + extra +
        "<Target Name=\"RefuseTargets\" BeforeTargets=\"Restore;Build;Compile;CompileDesignTime\"><Error Text=\"Graph discovery must not execute targets\" /></Target></Project>";
}
