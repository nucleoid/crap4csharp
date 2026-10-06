using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProjectGraphContextTests
{
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
