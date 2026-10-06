using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerGraphExecutionTests
{
    [Theory]
    [InlineData("failed-loader")]
    [InlineData("empty")]
    [InlineData("duplicate-node")]
    [InlineData("missing-binding")]
    [InlineData("duplicate-binding")]
    [InlineData("extra-binding")]
    [InlineData("extra-context")]
    [InlineData("wrong-project")]
    [InlineData("missing-physical-project")]
    [InlineData("wrong-physical-project")]
    [InlineData("wrong-configuration")]
    [InlineData("wrong-framework")]
    [InlineData("wrong-platform")]
    [InlineData("missing-context")]
    [InlineData("unknown-dependency")]
    [InlineData("self-dependency")]
    [InlineData("reversed")]
    [InlineData("cycle")]
    [InlineData("same-project-contexts")]
    public async Task WholeGraphRefusesBeforeAnyBuild(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-execution-preflight");
        var loaded = Valid(fixture.Path);
        var nodes = loaded.BuildGraph.ToArray();
        var bindings = loaded.GraphContexts.ToArray();
        loaded = defect switch
        {
            "failed-loader" => loaded with { Success = false },
            "empty" => loaded with { BuildGraph = [], GraphContexts = [], Contexts = [] },
            "duplicate-node" => loaded with { BuildGraph = [nodes[0], nodes[0], nodes[1]] },
            "missing-binding" => loaded with { GraphContexts = [bindings[0]] },
            "duplicate-binding" => loaded with { GraphContexts = [bindings[0], bindings[1], bindings[1]] },
            "extra-binding" => loaded with { GraphContexts = [.. bindings, bindings[1] with { GraphId = "extra" }] },
            "extra-context" => loaded with { Contexts = [.. loaded.Contexts, Context(Path.Combine(fixture.Path, "Extra.csproj"))] },
            "wrong-project" => loaded with { GraphContexts = [bindings[0], bindings[1] with { ProjectPath = nodes[0].ProjectPath }] },
            "missing-physical-project" => loaded with { GraphContexts = [bindings[0], bindings[1] with { Context = bindings[1].Context with { ResolvedProjectPath = null } }] },
            "wrong-physical-project" => loaded with { GraphContexts = [bindings[0], bindings[1] with { Context = bindings[1].Context with { ResolvedProjectPath = nodes[0].ProjectPath } }] },
            "wrong-configuration" => loaded with { GraphContexts = [bindings[0], bindings[1] with { Context = bindings[1].Context with { Configuration = "Debug" } }] },
            "wrong-framework" => loaded with { GraphContexts = [bindings[0], bindings[1] with { Context = bindings[1].Context with { TargetFramework = "net9.0" } }] },
            "wrong-platform" => loaded with { GraphContexts = [bindings[0], bindings[1] with { Context = bindings[1].Context with { Platform = "x64" } }] },
            "missing-context" => loaded with { Contexts = [loaded.Contexts[0]] },
            "unknown-dependency" => loaded with { BuildGraph = [nodes[0], nodes[1] with { Dependencies = ["missing"] }] },
            "self-dependency" => loaded with { BuildGraph = [nodes[0], nodes[1] with { Dependencies = [nodes[1].Id] }] },
            "reversed" => loaded with { BuildGraph = [nodes[1], nodes[0]] },
            "cycle" => loaded with { BuildGraph = [nodes[0] with { Dependencies = [nodes[1].Id] }, nodes[1]] },
            "same-project-contexts" => loaded with
            {
                BuildGraph = [nodes[0], nodes[1] with { ProjectPath = nodes[0].ProjectPath }],
                GraphContexts = [bindings[0], bindings[1] with { ProjectPath = nodes[0].ProjectPath, Context = bindings[0].Context }],
                Contexts = [bindings[0].Context]
            },
            _ => throw new InvalidOperationException()
        };
        var launches = new List<string>();
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerGraphExecution.RunAsync(loaded,
            (node, _) => { launches.Add(node.Id); return Task.FromResult(new CompilerObservation(node.ProjectPath, [])); },
            TestContext.Current.CancellationToken));
        Assert.StartsWith("compiler.graph", error.Reason, StringComparison.Ordinal);
        Assert.Empty(launches); // A valid first node must not execute before an invalid later node is examined.
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("missing-intermediate")]
    [InlineData("relative-target")]
    [InlineData("shared-target-directory")]
    [InlineData("shared-intermediate")]
    [InlineData("cross-role-overlap")]
    [InlineData("nested-output")]
    public async Task EvaluatedOutputRefusalPrecedesEveryBuild(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-output-preflight");
        var loaded = Valid(fixture.Path);
        var dependency = loaded.Contexts[0];
        var root = loaded.Contexts[1];
        root = defect switch
        {
            "missing-target" => root with { EvaluatedTargetPath = null },
            "missing-intermediate" => root with { EvaluatedIntermediateDirectory = null },
            "relative-target" => root with { EvaluatedTargetPath = "bin/Root.dll" },
            "shared-target-directory" => root with { EvaluatedTargetPath = Path.Combine(Path.GetDirectoryName(dependency.EvaluatedTargetPath!)!, "Different.dll") },
            "shared-intermediate" => root with { EvaluatedIntermediateDirectory = dependency.EvaluatedIntermediateDirectory },
            "cross-role-overlap" => root with { EvaluatedIntermediateDirectory = Path.GetDirectoryName(dependency.EvaluatedTargetPath!) },
            "nested-output" => root with { EvaluatedIntermediateDirectory = Path.Combine(dependency.EvaluatedIntermediateDirectory!, "nested") },
            _ => throw new InvalidOperationException()
        };
        loaded = loaded with { Contexts = [dependency, root], GraphContexts = [loaded.GraphContexts[0], loaded.GraphContexts[1] with { Context = root }] };
        var launches = 0;
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerGraphExecution.RunAsync(loaded,
            (node, _) => { launches++; return Task.FromResult(new CompilerObservation(node.ProjectPath, [])); },
            TestContext.Current.CancellationToken));
        Assert.Equal("compiler.graphOutputUnproven", error.Reason);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task ValidGraphBuildsEachMappedContextOnceInDependencyOrder()
    {
        using var fixture = TestDirectory.Create("crap4csharp-execution-order");
        var loaded = Valid(fixture.Path);
        var launches = new List<string>();
        var observations = await CompilerGraphExecution.RunAsync(loaded, (node, token) =>
        {
            Assert.Equal(TestContext.Current.CancellationToken, token);
            launches.Add(node.Id);
            Assert.Equal("LEFT", node.GlobalProperties["Flavor"]);
            return Task.FromResult(new CompilerObservation(node.ProjectPath, []));
        }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "dependency", "root" }, launches);
        Assert.Equal(loaded.BuildGraph.Select(n => n.ProjectPath), observations.Select(o => o.ProjectPath));
    }

    [Fact]
    public async Task BuildFailureStopsDependentsAndOriginalFailureIsPreserved()
    {
        using var fixture = TestDirectory.Create("crap4csharp-execution-failure");
        var launches = new List<string>();
        var expected = new IOException("actual build failure");
        var actual = await Assert.ThrowsAsync<IOException>(() => CompilerGraphExecution.RunAsync(Valid(fixture.Path),
            (node, _) => { launches.Add(node.Id); throw expected; }, TestContext.Current.CancellationToken));
        Assert.Same(expected, actual);
        Assert.Equal(new[] { "dependency" }, launches);
    }

    [Fact]
    public async Task ActualMappedGraphBuildsDependencyAndRootOnceWithoutImplicitReferenceRebuild()
    {
        using var fixture = TestDirectory.Create("crap4csharp-actual-graph-execution");
        fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="CountBuild" BeforeTargets="CoreCompile"><WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" Condition="'$(DesignTimeBuild)' != 'true'" /></Target></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency {}");
        var added = fixture.Write("Shared/Added.cs", "public class Added {}");
        var project = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj" AdditionalProperties="Configuration=Debug" /></ItemGroup>
            <Target Name="AddSource" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="../Shared/Added.cs" Link="obj/Added.cs" /></ItemGroup>
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" Condition="'$(DesignTimeBuild)' != 'true'" /></Target></Project>
            """);
        fixture.Write("Root/Code.cs", "public class Root {}");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        Assert.Empty(Directory.GetFiles(fixture.Path, "count.txt", SearchOption.AllDirectories));
        var results = await CompilerGraphExecution.RunAsync(loaded, (node, token) =>
        {
            var directory = Path.Combine(fixture.Path, "private", node.Id);
            Directory.CreateDirectory(directory);
            return CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(2), token);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);
        Assert.Contains("Dependency", results[0].ProjectPath, StringComparison.Ordinal);
        Assert.Equal(project, results[1].ProjectPath);
        Assert.Contains("Debug", results[0].Assembly!.Path, StringComparison.Ordinal);
        Assert.Contains("Release", results[1].Assembly!.Path, StringComparison.Ordinal);
        Assert.Contains(Assert.Single(results[1].Parameters, p => p.Name == "Sources").Items,
            s => Path.GetFullPath(s, Path.GetDirectoryName(project)!) == added);
        foreach (var observed in results)
        {
            Assert.Single(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(observed.ProjectPath)!, "count.txt")));
            Assert.Equal(observed.Assembly!.Sha256, ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Assembly.Path)));
            Assert.Equal(observed.Pdb!.Sha256, ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Pdb.Path)));
        }
        foreach (var (path, original) in authored)
        {
            Assert.Equal(original.Bytes, File.ReadAllBytes(path));
            Assert.Equal(original.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task ActualDistinctProjectsSharingEvaluatedOutputDirectoryRefuseBeforeBuild()
    {
        using var fixture = TestDirectory.Create("crap4csharp-actual-output-collision");
        fixture.Write("Directory.Build.props", """
            <Project><PropertyGroup><OutputPath>$(MSBuildThisFileDirectory)shared-output/</OutputPath></PropertyGroup></Project>
            """);
        fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency {}");
        var project = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj" /></ItemGroup></Project>
            """);
        fixture.Write("Root/Code.cs", "public class Root {}");
        var originals = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        Assert.Equal(2, loaded.Contexts.Count);
        Assert.All(loaded.Contexts, c => Assert.NotNull(c.EvaluatedIntermediateDirectory));
        Assert.Equal(Path.GetDirectoryName(loaded.Contexts[0].EvaluatedTargetPath), Path.GetDirectoryName(loaded.Contexts[1].EvaluatedTargetPath));
        Assert.NotEqual(loaded.Contexts[0].EvaluatedTargetPath, loaded.Contexts[1].EvaluatedTargetPath);
        var launches = 0;
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerGraphExecution.RunAsync(loaded,
            (node, _) => { launches++; return Task.FromResult(new CompilerObservation(node.ProjectPath, [])); },
            TestContext.Current.CancellationToken));
        Assert.Equal("compiler.graphOutputUnproven", error.Reason);
        Assert.Equal(0, launches);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.pdb", SearchOption.AllDirectories));
        foreach (var (path, original) in originals)
        {
            Assert.Equal(original.Bytes, File.ReadAllBytes(path));
            Assert.Equal(original.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void PhysicalProjectPathIsPrivateTransportOnly()
    {
        var context = Context(Path.GetFullPath("private-project.csproj"));
        Assert.DoesNotContain("resolvedProjectPath", System.Text.Json.JsonSerializer.Serialize(context,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), StringComparison.Ordinal);
        var transported = System.Text.Json.JsonSerializer.Deserialize<ProjectAnalysisContext>(
            System.Text.Json.JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions);
        Assert.Equal(context.ResolvedProjectPath, transported!.ResolvedProjectPath);
        Assert.Equal(context.EvaluatedTargetPath, transported.EvaluatedTargetPath);
        Assert.Equal(context.EvaluatedIntermediateDirectory, transported.EvaluatedIntermediateDirectory);
        var publicJson = System.Text.Json.JsonSerializer.Serialize(context,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("evaluatedTargetPath", publicJson, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluatedIntermediateDirectory", publicJson, StringComparison.Ordinal);
    }

    private static ProjectContextLoadResult Valid(string root)
    {
        var contexts = new[] { Context(Path.Combine(root, "Dependency.csproj")), Context(Path.Combine(root, "Root.csproj")) };
        var nodes = contexts.Select((c, i) => new ProjectBuildGraphNode(i == 0 ? "dependency" : "root", c.ProjectPath,
            c.Configuration, c.Platform, c.TargetFramework, new Dictionary<string, string> { ["Flavor"] = "LEFT" },
            i == 0 ? [] : ["dependency"])).ToArray();
        return new(true, contexts, [])
        {
            BuildGraph = nodes,
            GraphContexts = nodes.Select((n, i) => new ProjectGraphContextBinding(n.Id, n.ProjectPath, contexts[i], i == 1)).ToArray()
        };
    }

    private static ProjectAnalysisContext Context(string project) => ProjectAnalysisContext.Create(project,
        Path.GetFileNameWithoutExtension(project), "net10.0", "Release", "AnyCPU", LanguageVersion.Latest,
        ["FLAVOR_LEFT"], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn"))
        with { ResolvedProjectPath = project,
            EvaluatedTargetPath = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project), "bin", "Release", "net10.0", "app.dll"),
            EvaluatedIntermediateDirectory = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project), "obj", "Release", "net10.0") };
}
