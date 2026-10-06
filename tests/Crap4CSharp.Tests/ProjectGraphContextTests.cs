using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProjectGraphContextTests
{
    [Fact]
    public async Task SameProjectCustomGlobalsRemainDistinctWorkspaceBindings()
    {
        using var fixture = TestDirectory.Create("crap4csharp-graph-flavors");
        var dependency = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <DefineConstants>$(DefineConstants);FLAVOR_$(Flavor)</DefineConstants>
            </PropertyGroup></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency { public int M() => 1; }");
        foreach (var flavor in new[] { "LEFT", "RIGHT" })
        {
            fixture.Write($"{flavor}/{flavor}.csproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj" AdditionalProperties="Flavor={{flavor}}" /></ItemGroup></Project>
                """);
        }
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../LEFT/LEFT.csproj" /><ProjectReference Include="../RIGHT/RIGHT.csproj" /></ItemGroup></Project>
            """);
        var request = new ProjectContextLoadRequest(root, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var result = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Equal(5, result.GraphContexts.Count);
        var launches = 0;
        var preflight = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerGraphExecution.RunAsync(result,
            (node, _) => { launches++; return Task.FromResult(new CompilerObservation(node.ProjectPath, [])); },
            TestContext.Current.CancellationToken));
        Assert.Equal("compiler.graphOutputUnproven", preflight.Reason);
        Assert.Equal(0, launches);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.pdb", SearchOption.AllDirectories));
        var bindings = result.GraphContexts.Where(b => b.ProjectPath == dependency).ToArray();
        Assert.Equal(2, bindings.Length);
        Assert.NotEqual(bindings[0].GraphId, bindings[1].GraphId);
        Assert.NotEqual(bindings[0].Context.ContextId, bindings[1].Context.ContextId);
        foreach (var binding in bindings)
        {
            var node = Assert.Single(result.BuildGraph, n => n.Id == binding.GraphId);
            Assert.Contains("FLAVOR_" + node.GlobalProperties["Flavor"], binding.Context.PreprocessorSymbols);
            Assert.DoesNotContain("FLAVOR_" + (node.GlobalProperties["Flavor"] == "LEFT" ? "RIGHT" : "LEFT"), binding.Context.PreprocessorSymbols);
        }
    }

    [Fact]
    public async Task GraphWorkspaceLoadingNeverImplicitlyRestoresColdAssets()
    {
        using var fixture = TestDirectory.Create("crap4csharp-graph-cold");
        var root = fixture.Write("Cold.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var result = await ProjectContextLoader.LoadGraphAsync(new(root, "Release", null, [], false, false,
            TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("context.assetsUnavailable", result.FailureReason);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "obj")));
    }

    [Theory]
    [InlineData("AdditionalProperties")]
    [InlineData("SetConfiguration")]
    public async Task RequiredGraphContextsKeepMappedConfigurationAndCustomSymbols(string mapping)
    {
        using var fixture = TestDirectory.Create("crap4csharp-graph-context");
        var dep = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <DefineConstants Condition="'$(Flavor)' == 'left'">$(DefineConstants);LEFT_FLAVOR</DefineConstants>
            </PropertyGroup></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency { public int M() => 1; }");
        var root = fixture.Write("Root/Root.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup>
            <ProjectReference Include="../Dependency/Dependency.csproj" {{mapping}}="Configuration=Debug" AdditionalProperties="Flavor=left{{(mapping == "AdditionalProperties" ? ";Configuration=Debug" : "")}}" />
            </ItemGroup></Project>
            """.Replace("AdditionalProperties=\"Configuration=Debug\" AdditionalProperties=", "AdditionalProperties="));
        fixture.Write("Root/Code.cs", "public class Root { public int M() => 1; }");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(root, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        var restored = await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(0, restored.ExitCode);
        var result = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.FailureReason + ": " + string.Join(" | ", result.Diagnostics));
        Assert.Equal(2, result.GraphContexts.Count);
        var dependency = Assert.Single(result.GraphContexts, b => b.ProjectPath == dep);
        Assert.Equal("Debug", dependency.Context.Configuration);
        Assert.Contains("DEBUG", dependency.Context.PreprocessorSymbols);
        Assert.Contains("LEFT_FLAVOR", dependency.Context.PreprocessorSymbols);
        var test = Assert.Single(result.GraphContexts, b => b.ProjectPath == root);
        Assert.True(test.IsTestProject); // include-tests=false never removes required execution contexts.
        Assert.Equal("Release", test.Context.Configuration);
        Assert.DoesNotContain("DEBUG", test.Context.PreprocessorSymbols);
        Assert.Equal(result.BuildGraph.Select(n => n.Id), result.GraphContexts.Select(b => b.GraphId));
        foreach (var (path, value) in authored)
        {
            Assert.Equal(value.Bytes, File.ReadAllBytes(path));
            Assert.Equal(value.Mtime, File.GetLastWriteTimeUtc(path));
        }
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.pdb", SearchOption.AllDirectories));
    }
}
