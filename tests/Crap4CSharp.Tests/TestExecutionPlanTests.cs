using System.Reflection;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestExecutionPlanTests
{
    // Structural protocol controls, not actual compiler or test-execution observations.
    [Theory]
    [InlineData("discovery-failed")]
    [InlineData("workspace-failed")]
    [InlineData("empty")]
    [InlineData("duplicate-discovery-node")]
    [InlineData("missing-discovery-node")]
    [InlineData("extra-discovery-node")]
    [InlineData("project")]
    [InlineData("relative-project")]
    [InlineData("configuration")]
    [InlineData("platform")]
    [InlineData("framework")]
    [InlineData("globals")]
    [InlineData("missing-global")]
    [InlineData("dependencies")]
    [InlineData("duplicate-dependency")]
    [InlineData("missing-binding")]
    [InlineData("missing-context")]
    [InlineData("missing-runner")]
    [InlineData("duplicate-runner")]
    [InlineData("extra-runner")]
    [InlineData("runner-project")]
    [InlineData("runner-configuration")]
    [InlineData("runner-platform")]
    [InlineData("runner-framework")]
    [InlineData("runner-kind")]
    [InlineData("runner-sdk")]
    [InlineData("runner-collector")]
    [InlineData("no-tests")]
    [InlineData("output-collision")]
    public void WholePlanRefusesIncompleteOrMismatchedInputs(string defect)
    {
        using var directory = TestDirectory.Create("crap4csharp-test-plan-refusal");
        var loaded = Valid(directory.Path);
        var discovery = Discovery(loaded);
        var n = discovery.BuildGraph.ToArray();
        var r = discovery.TestRunners[0];
        discovery = defect switch
        {
            "discovery-failed" => discovery with { Success = false },
            "empty" => discovery with { BuildGraph = [] },
            "duplicate-discovery-node" => discovery with { BuildGraph = [n[0], n[0], n[1]] },
            "missing-discovery-node" => discovery with { BuildGraph = [n[1]] },
            "extra-discovery-node" => discovery with { BuildGraph = [.. n, n[0] with { Id = "extra" }] },
            "project" => discovery with { BuildGraph = [n[0] with { ProjectPath = n[1].ProjectPath }, n[1]] },
            "relative-project" => discovery with { BuildGraph = [n[0] with { ProjectPath = "Dep.csproj" }, n[1]] },
            "configuration" => discovery with { BuildGraph = [n[0] with { Configuration = "Other" }, n[1]] },
            "platform" => discovery with { BuildGraph = [n[0] with { Platform = "x64" }, n[1]] },
            "framework" => discovery with { BuildGraph = [n[0] with { TargetFramework = "net9.0" }, n[1]] },
            "globals" => discovery with { BuildGraph = [n[0] with { GlobalProperties = new Dictionary<string,string> { ["Flavor"] = "right" } }, n[1]] },
            "missing-global" => discovery with { BuildGraph = [n[0] with { GlobalProperties = new Dictionary<string,string>() }, n[1]] },
            "dependencies" => discovery with { BuildGraph = [n[0], n[1] with { Dependencies = [] }] },
            "duplicate-dependency" => discovery with { BuildGraph = [n[0], n[1] with { Dependencies = ["dep", "dep"] }] },
            "missing-runner" => discovery with { TestRunners = [] },
            "duplicate-runner" => discovery with { TestRunners = [r, r] },
            "extra-runner" => discovery with { TestRunners = [r, r with { GraphId = "root" }] },
            "runner-project" => discovery with { TestRunners = [r with { ProjectPath = n[1].ProjectPath }] },
            "runner-configuration" => discovery with { TestRunners = [r with { Configuration = "Release" }] },
            "runner-platform" => discovery with { TestRunners = [r with { Platform = "x64" }] },
            "runner-framework" => discovery with { TestRunners = [r with { TargetFramework = "net9.0" }] },
            "runner-kind" => discovery with { TestRunners = [r with { Runner = "Microsoft.Testing.Platform" }] },
            "runner-sdk" => discovery with { TestRunners = [r with { TestSdkVersion = "17.0.0" }] },
            "runner-collector" => discovery with { TestRunners = [r with { CollectorVersion = "1.0.0" }] },
            _ => discovery
        };
        loaded = defect switch
        {
            "workspace-failed" => loaded with { Success = false },
            "missing-binding" => loaded with { GraphContexts = [loaded.GraphContexts[1]] },
            "missing-context" => loaded with { Contexts = [loaded.Contexts[1]] },
            "no-tests" => loaded with { GraphContexts = loaded.GraphContexts.Select(b => b with { IsTestProject = false }).ToArray() },
            "output-collision" => loaded with { GraphContexts = [loaded.GraphContexts[0], loaded.GraphContexts[1] with
                { Context = loaded.Contexts[1] with { EvaluatedTargetPath = loaded.Contexts[0].EvaluatedTargetPath } }] },
            _ => loaded
        };
        if (defect == "no-tests") discovery = discovery with { TestRunners = [] };
        var error = Assert.Throws<ProjectContextException>(() => Freeze(discovery, loaded));
        Assert.StartsWith(defect is "missing-binding" or "missing-context" or "workspace-failed" or "output-collision" ? "compiler.graph" : "test.plan", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FrozenPlanCannotBeChangedThroughOriginalOrReturnedCollections()
    {
        using var directory = TestDirectory.Create("crap4csharp-test-plan-freeze");
        var loaded = Valid(directory.Path);
        var discovery = Discovery(loaded);
        // Case-insensitive MSBuild property names and metadata order do not change the selected graph.
        discovery = discovery with { BuildGraph = discovery.BuildGraph.Reverse().Select(n => n with
            { GlobalProperties = new Dictionary<string,string> { ["fLaVoR"] = "left" } }).ToArray() };
        var plan = Freeze(discovery, loaded);
        var frozen = Graph(plan);
        var tests = Tests(plan);
        ((ProjectBuildGraphNode[])loaded.BuildGraph)[0] = loaded.BuildGraph[0] with { Id = "corrupted" };
        ((string[])loaded.BuildGraph[1].Dependencies)[0] = "corrupted";
        ((Dictionary<string,string>)loaded.BuildGraph[1].GlobalProperties)["Flavor"] = "corrupted";
        ((string[])loaded.Contexts[0].PreprocessorSymbols)[0] = "CORRUPTED";
        ((ProjectPhaseInput[])loaded.Contexts[0].PhaseInputs)[0] = new("corrupted", "source", false);
        ((ProjectTestRunnerMetadata[])discovery.TestRunners)[0] = discovery.TestRunners[0] with { Runner = "corrupted" };
        Assert.Equal(new[] { "dep", "root" }, frozen.BuildGraph.Select(n => n.Id));
        Assert.Equal("dep", Assert.Single(frozen.BuildGraph[1].Dependencies));
        Assert.Equal("left", frozen.BuildGraph[1].GlobalProperties["Flavor"]);
        Assert.Equal("LEFT", Assert.Single(frozen.Contexts[0].PreprocessorSymbols));
        Assert.Equal("project", Assert.Single(frozen.Contexts[0].PhaseInputs).Role);
        Assert.Equal("VSTest", Assert.Single(tests).Runner);
        Immutable(frozen.BuildGraph); Immutable(frozen.GraphContexts); Immutable(frozen.Contexts);
        Immutable(frozen.BuildGraph[1].Dependencies); Immutable(frozen.Contexts[0].PreprocessorSymbols);
        Immutable(frozen.Contexts[0].PhaseInputs); Immutable(frozen.Contexts[0].Sources);
        Immutable(frozen.Contexts[0].CompilerDocuments); Immutable(frozen.Contexts[0].ReferencePaths);
        Immutable(frozen.Contexts[0].Imports); Immutable(frozen.Contexts[0].Exclusions);
        Immutable(frozen.Contexts[0].ProtectedPaths); Immutable(frozen.Contexts[0].GeneratedTransitionPaths);
        Immutable(tests);
        var dictionary = Assert.IsAssignableFrom<IDictionary<string,string>>(frozen.BuildGraph[1].GlobalProperties);
        Assert.Throws<NotSupportedException>(() => dictionary["Flavor"] = "changed");
    }

    [Fact]
    public async Task RealMappedRequiredTestSurvivesIncludeTestsFalseWithoutBuildOrTest()
    {
        using var directory = TestDirectory.Create("crap4csharp-mapped-test-plan");
        const string packages = "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.0.1\" /><PackageReference Include=\"coverlet.collector\" Version=\"6.0.4\" /></ItemGroup>";
        var dependency = directory.Write("Dep/Dep.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><DefineConstants Condition=\"'$(Flavor)' == 'left'\">$(DefineConstants);LEFT</DefineConstants></PropertyGroup>" + packages + "</Project>");
        directory.Write("Dep/Code.cs", "public class Dependency {} ");
        var project = directory.Write("Root/Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Dep/Dep.csproj\" AdditionalProperties=\"Configuration=Debug;Flavor=left\" /></ItemGroup></Project>");
        directory.Write("Root/Code.cs", "public class Root {} ");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var discovery = await ProjectContextLoader.DiscoverTestRunnersAsync(request, TestContext.Current.CancellationToken);
        Assert.True(discovery.Success, discovery.FailureReason + string.Join(" | ", discovery.Diagnostics));
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + string.Join(" | ", loaded.Diagnostics));
        var before = Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var plan = Freeze(discovery, loaded);
        var test = Assert.Single(Tests(plan));
        Assert.Equal(dependency, test.ProjectPath);
        Assert.Equal("Debug", test.Configuration);
        var frozen = Graph(plan);
        var node = Assert.Single(frozen.BuildGraph, n => n.Id == test.GraphId);
        Assert.Equal("left", node.GlobalProperties["Flavor"]);
        Assert.Equal("Release", Assert.Single(frozen.BuildGraph, n => n.ProjectPath == project).Configuration);
        Assert.Contains("LEFT", Assert.Single(frozen.GraphContexts, b => b.GraphId == test.GraphId).Context.PreprocessorSymbols);
        Assert.False(Assert.Single(frozen.GraphContexts, b => b.GraphId == test.GraphId).Context.ExclusionPolicy.IncludeTests);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, value) in before) { Assert.Equal(value.Bytes, File.ReadAllBytes(path)); Assert.Equal(value.Mtime, File.GetLastWriteTimeUtc(path)); }
        Assert.Empty(Directory.GetFiles(directory.Path, "*.dll", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.pdb", SearchOption.AllDirectories));
    }

    [Fact]
    public void EveryRequiredTestIsScheduledOnceInDependencyOrder()
    {
        using var directory = TestDirectory.Create("crap4csharp-two-test-plan");
        var loaded = Valid(directory.Path);
        loaded = loaded with { GraphContexts = loaded.GraphContexts.Select(b => b with { IsTestProject = true }).ToArray() };
        var discovery = Discovery(loaded);
        discovery = discovery with { TestRunners = new[] { discovery.TestRunners[0] with
            { GraphId = "root", ProjectPath = loaded.BuildGraph[1].ProjectPath, Configuration = "Release" }, discovery.TestRunners[0] } };
        var plan = Freeze(discovery, loaded);
        Assert.Equal(new[] { "dep", "root" }, Tests(plan).Select(t => t.GraphId));
        Assert.Equal(2, Tests(plan).Select(t => t.GraphId).Distinct().Count());
        Assert.Equal(new[] { "Debug", "Release" }, Tests(plan).Select(t => t.Configuration));
    }

    [Fact]
    public void PrecancelledPlanCannotPerformValidationOrLaunchAnything()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        // Invalid/empty inputs intentionally would produce a different exception if validation ran.
        Assert.Throws<OperationCanceledException>(() => Freeze(new(false, [], []), new(false, [], []), cancellation.Token));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("root-selector")]
    [InlineData("workspace")]
    [InlineData("globals")]
    [InlineData("symbols")]
    [InlineData("generated")]
    [InlineData("include-tests")]
    [InlineData("required-test")]
    [InlineData("bytes")]
    [InlineData("oversized")]
    [InlineData("cancelled")]
    public void CompletedTestPlanRecipeIndependentlyBindsRequiredScheduleWithoutPublishingSecrets(string change)
    {
        using var directory = TestDirectory.Create("crap4csharp-completed-plan");
        var loaded = Valid(directory.Path);
        const string secret = "private-global-canary-do-not-export";
        loaded = loaded with { BuildGraph = loaded.BuildGraph.Select(n => n with
            { GlobalProperties = new Dictionary<string,string> { ["Flavor"] = secret } }).ToArray() };
        var plan = Freeze(Discovery(loaded), loaded);
        var selector = Path.Combine(directory.Path, "Root.csproj");
        var bytes = Assert.IsType<byte[]>(Recipe("Capture", plan, directory.Path, selector, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain(directory.Path, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var current = loaded;
        if (change == "globals") current = current with { BuildGraph = current.BuildGraph.Select(n => n with
            { GlobalProperties = new Dictionary<string,string> { ["Flavor"] = "changed" } }).ToArray() };
        if (change is "symbols" or "generated" or "include-tests")
        {
            var contexts = current.Contexts.Select(c => change switch
            {
                "symbols" => c with { PreprocessorSymbols = ["RIGHT"] },
                "generated" => c with { ExclusionPolicy = new(false, true) },
                _ => c with { ExclusionPolicy = new(true, false) }
            }).ToArray();
            current = current with { Contexts = contexts, GraphContexts = current.GraphContexts.Select((b,i) => b with { Context = contexts[i] }).ToArray() };
        }
        if (change == "required-test") current = current with { GraphContexts = current.GraphContexts.Select(b => b with { IsTestProject = true }).ToArray() };
        var discovery = Discovery(current);
        if (change == "required-test") discovery = discovery with { TestRunners = [discovery.TestRunners[0], discovery.TestRunners[0] with
            { GraphId = "root", ProjectPath = selector, Configuration = "Release" }] };
        var newPlan = Freeze(discovery, current);
        if (change == "root-selector") selector = Path.Combine(directory.Path, "Dep.csproj");
        var root = change == "workspace" ? Path.GetDirectoryName(directory.Path)! : directory.Path;
        if (change == "bytes") bytes[0] ^= 1;
        if (change == "oversized") bytes = new byte[256 * 1024 + 1];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (change == "cancelled") cancellation.Cancel();
        if (change == "same") Recipe("Validate", bytes, newPlan, root, selector, cancellation.Token);
        else if (change == "cancelled") Assert.Throws<OperationCanceledException>(() => Recipe("Validate", bytes, newPlan, root, selector, cancellation.Token));
        else Assert.Equal("provenance.completedTestPlanMismatch", Assert.Throws<PolicyException>(() => Recipe("Validate", bytes, newPlan, root, selector, cancellation.Token)).Code);
    }

    private static object? Recipe(string name, params object[] args)
    {
        var type = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.CompletedTestPlanRecipe");
        Assert.True(type is not null, "Missing distinct completed-test required-plan recipe; prior compiler evidence cannot substitute for it.");
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static object Freeze(ProjectContextLoadResult discovery, ProjectContextLoadResult loaded, CancellationToken? token = null)
    {
        var type = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.CheckExecutionPlan");
        Assert.True(type is not null, "Missing independent metadata/Workspace immutable test-plan entrypoint.");
        var method = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        try { return method.Invoke(null, [discovery, loaded, token ?? TestContext.Current.CancellationToken])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static ProjectContextLoadResult Graph(object plan) => (ProjectContextLoadResult)plan.GetType().GetProperty("PreparedGraph")!.GetValue(plan)!;
    private static IReadOnlyList<ProjectTestRunnerMetadata> Tests(object plan) => (IReadOnlyList<ProjectTestRunnerMetadata>)plan.GetType().GetProperty("Tests")!.GetValue(plan)!;
    private static void Immutable<T>(IReadOnlyList<T> values)
    {
        Assert.False(values is T[]);
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(default!));
        if (list.Count > 0) Assert.Throws<NotSupportedException>(() => list[0] = default!);
    }
    private static ProjectContextLoadResult Discovery(ProjectContextLoadResult loaded) => new(true, [], [])
    {
        BuildGraph = loaded.BuildGraph.Select(n => n with { Dependencies = n.Dependencies.ToArray(), GlobalProperties = new Dictionary<string,string>(n.GlobalProperties) }).ToArray(),
        TestRunners = new[] { new ProjectTestRunnerMetadata("dep", loaded.BuildGraph[0].ProjectPath, "Debug", "AnyCPU", "net10.0", "VSTest", "18.0.1", "6.0.4") }
    };
    private static ProjectContextLoadResult Valid(string root)
    {
        var contexts = new[] { Context(Path.Combine(root, "Dep.csproj"), "Debug"), Context(Path.Combine(root, "Root.csproj"), "Release") };
        var nodes = contexts.Select((c,i) => new ProjectBuildGraphNode(i == 0 ? "dep" : "root", c.ProjectPath, c.Configuration,
            c.Platform, c.TargetFramework, new Dictionary<string,string> { ["Flavor"] = "left" }, i == 0 ? Array.Empty<string>() : new[] { "dep" })).ToArray();
        return new(true, contexts, []) { BuildGraph = nodes, GraphContexts = nodes.Select((n,i) => new ProjectGraphContextBinding(n.Id, n.ProjectPath, contexts[i], i == 0)).ToArray() };
    }
    private static ProjectAnalysisContext Context(string project, string configuration) => ProjectAnalysisContext.Create(project,
        Path.GetFileNameWithoutExtension(project), "net10.0", configuration, "AnyCPU", LanguageVersion.Latest, ["LEFT"],
        SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
    {
        ResolvedProjectPath = project,
        EvaluatedTargetPath = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project), "bin", "app.dll"),
        EvaluatedIntermediateDirectory = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project), "obj"),
        EvaluatedReferenceAssemblyPath = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project), "obj", "ref", "app.dll"),
        PhaseInputs = new[] { new ProjectPhaseInput(project, "project", false) }
    };
}
