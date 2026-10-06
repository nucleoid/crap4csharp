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
        ["FLAVOR_LEFT"], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn"));
}
