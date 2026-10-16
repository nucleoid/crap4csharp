using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CurrentRequiredPolicyTargetsTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("metadata")]
    [InlineData("missing-production")]
    [InlineData("missing-test")]
    [InlineData("configuration")]
    [InlineData("framework")]
    [InlineData("unlisted-test")]
    [InlineData("listed-nontest")]
    public void CurrentRequiredGraphAndClassificationMustMatchPolicy(string mutation)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "crap-policy-targets"));
        var policy = new RepositoryPolicy(RepositoryPolicy.Version, RepositoryPolicyMode.Strict,
            ["Library.csproj"], ["Tests.csproj", "Independent.csproj"], "Debug", ["net10.0"], "all", 30,
            MissingCoveragePolicy.Fail, ["tests", "coverage", "crap"], [], "crap-v1", null, [], new());
        var projects = new[] { "Library.csproj", "Tests.csproj", "Independent.csproj" };
        var contexts = projects.Select(p => ProjectAnalysisContext.Create(Path.Combine(root, p), p, "net10.0",
            "Debug", "AnyCPU", LanguageVersion.Preview, [], SourceCodeKind.Regular, [], [], new(false, false),
            new("10", "10", "5"))).ToArray();
        var graph = new ProjectContextLoadResult(true, [], [])
        {
            BuildGraph = contexts.Select((c, i) => new ProjectBuildGraphNode(i.ToString(), c.ProjectPath,
                c.Configuration, c.Platform, c.TargetFramework, new Dictionary<string, string>(), [])).ToArray(),
            GraphContexts = contexts.Select((c, i) => new ProjectGraphContextBinding(i.ToString(), c.ProjectPath, c, i != 0)).ToArray()
        };
        switch (mutation)
        {
            case "metadata": graph = graph with { GraphContexts = [] }; break;
            case "missing-production": graph = graph with { BuildGraph = graph.BuildGraph.Skip(1).ToArray() }; break;
            case "missing-test": graph = graph with { BuildGraph = graph.BuildGraph.Take(2).ToArray() }; break;
            case "configuration": graph = graph with { BuildGraph = graph.BuildGraph.Select(n => n with { Configuration = "Release" }).ToArray() }; break;
            case "framework": graph = graph with { BuildGraph = graph.BuildGraph.Select(n => n with { TargetFramework = "net9.0" }).ToArray() }; break;
            case "unlisted-test": policy = policy with { TestProjects = ["Tests.csproj"] }; break;
            case "listed-nontest": graph = graph with { GraphContexts = graph.GraphContexts.Select(b => b with { IsTestProject = false }).ToArray() }; break;
        }
        var preflight = new FreshCheckPreflight(root, null!, null!, policy, null);
        if (mutation is "none" or "metadata") FreshCheckExecution.RequirePolicyTargets(preflight, graph);
        else Assert.Equal(mutation is "unlisted-test" or "listed-nontest" ? "policy.testContextMismatch" : "policy.requiredContextMissing",
            Assert.Throws<PolicyException>(() => FreshCheckExecution.RequirePolicyTargets(preflight, graph)).Code);
    }
}
