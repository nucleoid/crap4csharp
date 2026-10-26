using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class LogicalContextIdentityTests
{
    [Fact]
    public async Task RealGraphRelocationChangesPhysicalIdentityButPreservesPublicSelector()
    {
        using var first = TestDirectory.Create("crap-context-first");
        using var second = TestDirectory.Create("crap-context-second");
        const string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
        var left = await Discover(first.Write("src/Example/Example.csproj", project));
        var right = await Discover(second.Write("src/Example/Example.csproj", project));
        Assert.True(left.Success, left.FailureReason);
        Assert.True(right.Success, right.FailureReason);
        var a = Assert.Single(left.BuildGraph); var b = Assert.Single(right.BuildGraph);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(LogicalContextIdentity.Create(first.Path, a), LogicalContextIdentity.Create(second.Path, b));
        foreach (var state in new[] { "completed", "failed", "interrupted" })
        {
            // Result projection DATA only; real graph discovery above supplies
            // the roles. These rows do not assert a native test invocation.
            IReadOnlyList<TestExecutionOutcomeResult> Project(string root, ProjectBuildGraphNode node)
                => FreshTestOutcomeProjection.Create(root, [node],
                    [new(node.Id, node.ProjectPath, node.Configuration, node.Platform, node.TargetFramework, "VSTest", "18.0.1", "6.0.4")],
                    [new(node.Id, state, null, null, null)]);
            var leftOutcome = Assert.Single(Project(first.Path, a));
            var rightOutcome = Assert.Single(Project(second.Path, b));
            Assert.Equal(leftOutcome.ContextId, rightOutcome.ContextId);
            Assert.Equal(LogicalContextIdentity.Create(first.Path, a), leftOutcome.ContextId);
            Assert.NotEqual(a.Id, leftOutcome.ContextId);
            Assert.Equal(state, leftOutcome.Status); Assert.Null(leftOutcome.ExitCode);
            Assert.Equal(a.ProjectPath, leftOutcome.Project);
        }
        // Custom mapped paths are not guessed to be interchangeable just because
        // each happens to start with its checkout root.
        var leftGlobals = a.GlobalProperties.ToDictionary(p => p.Key, p => p.Value);
        var rightGlobals = b.GlobalProperties.ToDictionary(p => p.Key, p => p.Value);
        leftGlobals["CustomOutputRoot"] = first.Path;
        rightGlobals["CustomOutputRoot"] = second.Path;
        Assert.NotEqual(LogicalContextIdentity.Create(first.Path, a with { GlobalProperties = leftGlobals }),
            LogicalContextIdentity.Create(second.Path, b with { GlobalProperties = rightGlobals }));
    }

    [Fact]
    public void MixedCaseRepositoryRoleHasTheSamePinnedSelectorOnEveryOperatingSystem()
    {
        using var root = TestDirectory.Create("crap-context-mixed-case");
        var node = Node(root.Path, "src/MixedCase/Example.csproj", new Dictionary<string, string> { ["Flavor"] = "Exact" });
        // Hash of the complete v1 selector with the exact mixed-case role and
        // mapped value. This expectation is shared by Windows and Linux CI.
        Assert.Equal("a83d1e8658cd9bb065e47e8546d2e657fb8d3942b5fd8b99eac68b2d50ef40c7",
            LogicalContextIdentity.Create(root.Path, node));
        Assert.NotEqual(LogicalContextIdentity.Create(root.Path, node), LogicalContextIdentity.Create(root.Path,
            node with { ProjectPath = Path.Combine(root.Path, "src", "MIXEDCASE", "Example.csproj") }));
    }

    [Fact]
    public void FullProjectRoleAndEveryMappedGlobalDistinguishSelectors()
    {
        using var root = TestDirectory.Create("crap-context-roles");
        var node = Node(root.Path, "src/Example/Example.csproj", new Dictionary<string, string>
            { ["Configuration"] = "Release", ["Flavor"] = "left", ["CustomPath"] = "/exact/value" });
        var expected = LogicalContextIdentity.Create(root.Path, node);
        Assert.NotEqual(expected, LogicalContextIdentity.Create(root.Path, node with
            { ProjectPath = Path.Combine(root.Path, "other", "Example", "Example.csproj") }));
        foreach (var key in node.GlobalProperties.Keys)
        {
            var changed = node.GlobalProperties.ToDictionary(p => p.Key, p => p.Value);
            changed[key] += "-different";
            Assert.NotEqual(expected, LogicalContextIdentity.Create(root.Path, node with { GlobalProperties = changed }));
        }
        Assert.NotEqual(expected, LogicalContextIdentity.Create(root.Path, node with { TargetFramework = "netstandard2.1" }));
        Assert.NotEqual(expected, LogicalContextIdentity.Create(root.Path, node with { Platform = "x64" }));
        Assert.NotEqual(expected, LogicalContextIdentity.Create(root.Path, node with { Configuration = "Debug" }));
        var reordered = node.GlobalProperties.Reverse().ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value);
        Assert.Equal(expected, LogicalContextIdentity.Create(root.Path, node with { GlobalProperties = reordered }));
    }

    [Fact]
    public void ExternalRolesAndAmbiguousMappedNamesAreRefused()
    {
        using var root = TestDirectory.Create("crap-context-bound-root");
        using var foreign = TestDirectory.Create("crap-context-outside");
        Assert.Throws<ArgumentException>(() => LogicalContextIdentity.Create(root.Path,
            Node(foreign.Path, "Example.csproj", new Dictionary<string, string>())));
        Assert.Throws<ArgumentException>(() => LogicalContextIdentity.Create(root.Path,
            Node(root.Path, "Example.csproj", new Dictionary<string, string> { ["Flavor"] = "a", ["flavor"] = "b" })));
    }

    private static ProjectBuildGraphNode Node(string root, string role, IReadOnlyDictionary<string, string> globals)
        => new("physical-id", Path.GetFullPath(role, root), "Release", "AnyCPU", "net10.0", globals, []);

    private static Task<ProjectContextLoadResult> Discover(string project) => ProjectContextLoader.DiscoverGraphAsync(
        new(project, "Release", "AnyCPU", [], true, false, TimeSpan.FromMinutes(2)), TestContext.Current.CancellationToken);
}
