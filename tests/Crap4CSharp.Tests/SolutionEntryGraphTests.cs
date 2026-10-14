using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class SolutionEntryGraphTests
{
    [Theory]
    [InlineData("TargetFramework")]
    [InlineData("TargetFrameworks")]
    public async Task SolutionRootAndReferenceSelectOneCompilerOwner(string frameworkProperty)
    {
        using var fixture = TestDirectory.Create("crap4csharp-solution-entry-graph");
        fixture.Write("global.json", """{"sdk":{"version":"10.0.103","rollForward":"disable"}}""");
        fixture.Write("Library/Library.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{frameworkProperty}>net10.0</{frameworkProperty}></PropertyGroup></Project>");
        fixture.Write("Library/Code.cs", "public class Library { public int Value => 42; }");
        fixture.Write("Root/Root.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{frameworkProperty}>net10.0</{frameworkProperty}></PropertyGroup><ItemGroup><ProjectReference Include=\"../Library/Library.csproj\" /></ItemGroup></Project>");
        fixture.Write("Root/Code.cs", "public class Root { public int Value => new Library().Value; }");
        var solution = fixture.Write("Consumer.slnx", "<Solution><Project Path=\"Library/Library.csproj\" /><Project Path=\"Root/Root.csproj\" /></Solution>");
        var token = TestContext.Current.CancellationToken;
        var request = new ProjectContextLoadRequest(solution, "Debug", null, [], false, false, TimeSpan.FromMinutes(3))
            { NativeSdkIdentityProbe = true };
        var restored = await ProjectBuildPreparation.RestoreAsync(request, token);
        Assert.True(restored.ExitCode == 0, restored.StandardOutput + restored.StandardError);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(loaded.Success, string.Join(" | ", loaded.Diagnostics));
        var graph = CompilerGraphExecution.ValidateAndFreeze(loaded);
        Assert.Equal(2, graph.Count);
        var library = Assert.Single(graph, n => Path.GetFileName(n.ProjectPath) == "Library.csproj");
        var root = Assert.Single(graph, n => Path.GetFileName(n.ProjectPath) == "Root.csproj");
        Assert.Equal(library.Id, Assert.Single(root.Dependencies));
        Assert.Equal(2, loaded.Contexts.Select(c => c.ContextId).Distinct().Count());
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
    }
}
