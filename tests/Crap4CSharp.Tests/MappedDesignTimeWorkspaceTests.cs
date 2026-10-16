using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class MappedDesignTimeWorkspaceTests
{
    [Theory]
    [InlineData("SkipCompilerExecution=false")]
    [InlineData("BuildProjectReferences=true")]
    [InlineData("DesignTimeBuild=false")]
    public async Task UnsafeMappedGlobalsRefuseBeforeAnyDesignTimeTarget(string unsafeProperty)
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-unsafe-globals");
        var project = Prepare(fixture, "Configuration=Debug;" + unsafeProperty);
        var request = Request(project);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.False(loaded.Success);
        Assert.Equal("context.mappedGlobalsUnsupported", loaded.FailureReason);
        Assert.Empty(Directory.GetFiles(fixture.Path, "design-count.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.pdb", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyTargetMutationToOtherGraphNodeRefusesBeforeItsTargetsRun(bool addSource)
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-graph-mutation");
        var project = Prepare(fixture, "Configuration=Debug", poisonRoot: true, addSource: addSource);
        var request = Request(project);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.False(loaded.Success);
        Assert.Equal("context.inputsMutated", loaded.FailureReason);
        Assert.Contains("Poisoned", File.ReadAllText(Path.Combine(fixture.Path, addSource ? "Root/Added.cs" : "Root/Code.cs")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "Root/design-count.txt")));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.pdb", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MappedAliasLoadsColdAndKeepsActualCompilerReferenceProperties()
    {
        using var fixture = TestDirectory.Create("crap4csharp-mapped-graph-alias");
        var project = Prepare(fixture, "Configuration=Debug", alias: true);
        var request = Request(project);
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, token)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        var root = loaded.GraphContexts.Single(binding => binding.ProjectPath == project);
        Assert.Contains("Debug", Assert.Single(root.Context.ProjectReferenceOutputs).OutputPath, StringComparison.Ordinal);
        var phased = await CompilerGraphExecution.RunPhasedAsync(request, loaded, (node, cancellation) =>
        {
            var directory = Path.Combine(fixture.Path, "private", node.Id);
            Directory.CreateDirectory(directory);
            return CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(2), cancellation);
        }, token);
        var reference = Assert.Single(phased.Observations.Single(observation => observation.ProjectPath == project).Parameters,
            parameter => parameter.Name == "References");
        Assert.Contains(reference.ReferenceProperties, properties => properties.Aliases == "Mapped");
        Assert.Equal(2, phased.Observations.Count);
    }

    private static ProjectContextLoadRequest Request(string project) =>
        new(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));

    private static string Prepare(TestDirectory fixture, string mapping, bool poisonRoot = false, bool alias = false, bool addSource = false)
    {
        const string counter = "<Target Name=\"DesignCount\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' == 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/design-count.txt\" Lines=\"design\" Overwrite=\"false\" /></Target>";
        var poison = poisonRoot ? "<Target Name=\"PoisonOtherNode\" BeforeTargets=\"CoreCompile\" Condition=\"'$(DesignTimeBuild)' == 'true'\"><WriteLinesToFile File=\"$(MSBuildProjectDirectory)/../Root/" + (addSource ? "Added.cs" : "Code.cs") + "\" Lines=\"public class Poisoned {}\" Overwrite=\"true\" /></Target>" : "";
        fixture.Write("Dependency/Dependency.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + counter + poison + "</Project>");
        fixture.Write("Dependency/Code.cs", "public class Dependency { public const int Value = 31; }");
        fixture.Write("Root/Code.cs", alias ? "extern alias Mapped; public class Root { public const int Value = Mapped::Dependency.Value; }" : "public class Root { public const int Value = Dependency.Value; }");
        return fixture.Write("Root/Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Dependency/Dependency.csproj\" AdditionalProperties=\"" + mapping + "\"" + (alias ? " Aliases=\"Mapped\"" : "") + " /></ItemGroup>" + counter + "</Project>");
    }
}
