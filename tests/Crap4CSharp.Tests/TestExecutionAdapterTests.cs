using System.Reflection;
using System.Xml.Linq;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class TestExecutionAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualMappedTestsRunOnceWithoutAnyImplicitBuildOrRestore(bool passes)
    {
        using var fixture = TestDirectory.Create("crap4csharp-actual-once-tests");
        var dependency = fixture.Write("Dep/Dep.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="Count" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        fixture.Write("Dep/Code.cs", "public class Dependency { public static int Value => 42; }");
        var project = fixture.Write("Tests/Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>
            <DefineConstants Condition="'$(Flavor)' == 'left'">$(DefineConstants);LEFT</DefineConstants></PropertyGroup>
            <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" PrivateAssets="all" />
            <PackageReference Include="xunit.v3" Version="3.2.2" PrivateAssets="all" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <PackageReference Include="coverlet.collector" Version="6.0.4" PrivateAssets="all" />
            <ProjectReference Include="../Dep/Dep.csproj" AdditionalProperties="Configuration=Debug" /></ItemGroup>
            <Target Name="Count" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" /></Target>
            <Target Name="ValidateMappedTestLaunch" BeforeTargets="VSTest">
            <Error Condition="'$(Flavor)' != 'left' Or '$(Configuration)' != 'Release' Or '$(TargetFramework)' != 'net10.0'" Text="Mapped test selectors lost" />
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/launch-count.txt" Lines="test" Overwrite="false" /></Target>
            </Project>
            """);
        var source = fixture.Write("Tests/Tests.cs", """
            using Xunit;
            public class RuntimeAssertions {
            [Fact] public void Once() {
            #if !LEFT
            #error Mapped Flavor global property was lost
            #endif
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory,"test-count.txt"), "test\n");
            Assert.Equal(EXPECTED, Dependency.Value);
            } }
            """.Replace("EXPECTED", passes ? "42" : "43", StringComparison.Ordinal));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        // LoadGraph has no extra root-global argument: use an entry root with the mapped test dependency.
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Tests/Tests.csproj" AdditionalProperties="Configuration=Release;Flavor=left" /></ItemGroup></Project>
            """);
        fixture.Write("Root/Code.cs", "public class Root {}");
        request = request with { Target = root, Configuration = "Debug" };
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var discovery = await ProjectContextLoader.DiscoverTestRunnersAsync(request, TestContext.Current.CancellationToken);
        Assert.True(discovery.Success, discovery.FailureReason + string.Join(" | ", discovery.Diagnostics));
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + string.Join(" | ", loaded.Diagnostics));
        var plan = CheckExecutionPlan.Create(discovery, loaded, TestContext.Current.CancellationToken);
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, token) =>
        {
            var owned = System.IO.Path.Combine(fixture.Path, "build", node.Id);
            Directory.CreateDirectory(owned);
            try { return await CompilerEvidence.BuildAsync(node, owned, TimeSpan.FromMinutes(2), token); }
            catch (ProjectContextException failure) { throw new InvalidOperationException(failure.Message + "\n" + File.ReadAllText(System.IO.Path.Combine(owned, "stdout.log")) + "\n" + File.ReadAllText(System.IO.Path.Combine(owned, "stderr.log")), failure); }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(3, bound.Observations.Count);
        var authored = new[] { project, dependency, root, source }.ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Time: File.GetLastWriteTimeUtc(p)));
        var outputs = bound.FinalGraph.Contexts.SelectMany(c => new[] { c.EvaluatedTargetPath!, System.IO.Path.ChangeExtension(c.EvaluatedTargetPath!, ".pdb") })
            .ToDictionary(p => p, p => File.ReadAllBytes(p));
        var assetsPath = System.IO.Path.Combine(fixture.Path, "Tests/obj/project.assets.json");
        var restores = (Bytes: File.ReadAllBytes(assetsPath), Time: File.GetLastWriteTimeUtc(assetsPath));
        var method = typeof(ProjectContextLoader).Assembly.GetType("Crap4CSharp.Core.TestExecutionAdapter")?.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var ownedResults = System.IO.Path.Combine(fixture.Path, "test-run");
        Directory.CreateDirectory(ownedResults);
        var operation = (Task)method.Invoke(null, [plan, ownedResults, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken])!;
        await operation;
        var results = Assert.IsAssignableFrom<System.Collections.IEnumerable>(operation.GetType().GetProperty("Result")!.GetValue(operation));
        var observation = Assert.Single(results.Cast<object>());
        var process = (LoggedProcessResult)observation.GetType().GetProperty("Process")!.GetValue(observation)!;
        Assert.Equal(passes ? 0 : 1, process.ExitCode);
        var resultsDirectory = (string)observation.GetType().GetProperty("ResultsDirectory")!.GetValue(observation)!;
        var trx = XDocument.Load(Assert.Single(Directory.GetFiles(resultsDirectory, "*.trx")));
        var counters = Assert.Single(trx.Descendants(), e => e.Name.LocalName == "Counters");
        Assert.Equal("1", counters.Attribute("total")!.Value);
        Assert.Equal(passes ? "1" : "0", counters.Attribute("passed")!.Value);
        Assert.Equal(passes ? "0" : "1", counters.Attribute("failed")!.Value);
        Assert.Equal(passes ? "Passed" : "Failed", Assert.Single(trx.Descendants(), e => e.Name.LocalName == "UnitTestResult").Attribute("outcome")!.Value);
        Assert.NotEmpty(Directory.GetFiles(resultsDirectory, "coverage.opencover.xml", SearchOption.AllDirectories));
        Assert.NotEmpty(File.ReadAllBytes(process.StandardOutput.Path));
        Assert.True(File.Exists(process.StandardError.Path));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/bin/Release/net10.0/test-count.txt")));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/build-count.txt")));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Tests/launch-count.txt")));
        Assert.Single(File.ReadAllLines(System.IO.Path.Combine(fixture.Path, "Dep/build-count.txt")));
        Assert.Equal(restores.Bytes, File.ReadAllBytes(assetsPath));
        Assert.Equal(restores.Time, File.GetLastWriteTimeUtc(assetsPath));
        foreach (var (path, saved) in authored) { Assert.Equal(saved.Bytes, File.ReadAllBytes(path)); Assert.Equal(saved.Time, File.GetLastWriteTimeUtc(path)); }
        foreach (var (path, bytes) in outputs) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void ActualTestArgumentsPreserveMappedSelectorsAndEscapeMSBuildDelimiters()
    {
        var properties = new Dictionary<string,string> { ["Flavor"] = "left;other=value,%$@'()*?", ["Platform"] = "AnyCPU" };
        var node = new ProjectBuildGraphNode("test", Path.GetFullPath("Tests.csproj"), "Release", "AnyCPU", "net10.0", properties, []);
        var arguments = TestExecutionAdapter.CreateArguments(node);
        Assert.Equal(new[] { "test", node.ProjectPath, "--no-build", "--no-restore", "-m:1", "-nr:false" }, arguments.Take(6));
        Assert.Contains("-p:Flavor=left%3Bother=value%2C%25%24%40%27%28%29%2A%3F", arguments);
        Assert.Contains("-p:Configuration=Release", arguments);
        Assert.Contains("-p:TargetFramework=net10.0", arguments);
        Assert.Contains("-p:BuildProjectReferences=false", arguments);
        Assert.True(Assert.IsAssignableFrom<IList<string>>(arguments).IsReadOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-name")]
    [InlineData("bad;Another")]
    [InlineData("bad=name")]
    public void InvalidPropertyNamesRefuseBeforeTransport(string name)
    {
        var error = Assert.Throws<ProjectContextException>(() => MappedGlobalArguments.Create(new Dictionary<string,string> { [name] = "x" }));
        Assert.Equal("compiler.globalPropertyInvalid", error.Reason);
    }

    [Fact]
    public void DuplicateCaseInsensitivePropertiesAndNulRefuseBeforeTransport()
    {
        Assert.Throws<ProjectContextException>(() => MappedGlobalArguments.Create(new Dictionary<string,string>
            { ["Flavor"] = "left", ["flavor"] = "right" }));
        Assert.Throws<ProjectContextException>(() => MappedGlobalArguments.Create(new Dictionary<string,string>
            { ["Flavor"] = "left\0right" }));
    }

    [Fact]
    public async Task PrecancelledAdapterCannotValidateOrCreateResults()
    {
        using var fixture = TestDirectory.Create("crap4csharp-test-adapter-cancel");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => TestExecutionAdapter.RunAsync(null!, fixture.Path, TimeSpan.FromSeconds(10), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Path));
    }

    // Structural prelaunch controls; no fake Csc observation or successful SDK/test claim.
    [Theory]
    [InlineData("missing")]
    [InlineData("nonempty")]
    [InlineData("intermediate")]
    [InlineData("target")]
    [InlineData("input")]
    public async Task UnsafeResultsRootRefusesBeforeAnySdkOrTestLaunch(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-test-adapter-destination");
        var directory = Path.Combine(fixture.Path, defect is "intermediate" ? "obj" : defect is "target" ? "bin" : "results");
        if (defect != "missing") Directory.CreateDirectory(directory);
        if (defect == "nonempty") File.WriteAllText(Path.Combine(directory, "owned-by-user.txt"), "preserve");
        var project = Path.Combine(fixture.Path, "Project.csproj");
        var context = ProjectAnalysisContext.Create(project, "Project", "net10.0", "Release", "AnyCPU",
            Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, [], Microsoft.CodeAnalysis.SourceCodeKind.Regular,
            [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
        {
            ResolvedProjectPath = project,
            EvaluatedTargetPath = Path.Combine(fixture.Path, "bin", "Project.dll"),
            EvaluatedIntermediateDirectory = Path.Combine(fixture.Path, "obj"),
            EvaluatedReferenceAssemblyPath = Path.Combine(fixture.Path, "obj", "ref", "Project.dll"),
            PhaseInputs = new[] { new ProjectPhaseInput(project, "project", false),
                new ProjectPhaseInput(defect == "input" ? Path.Combine(directory, "immutable.cs") : Path.Combine(fixture.Path, "Code.cs"), "source", false) }
        };
        var node = new ProjectBuildGraphNode("test", project, "Release", "AnyCPU", "net10.0", new Dictionary<string,string>(), []);
        var runner = new ProjectTestRunnerMetadata("test", project, "Release", "AnyCPU", "net10.0", "VSTest", "18.0.1", "6.0.4");
        var graph = new ProjectContextLoadResult(true, new[] { context }, [])
        { BuildGraph = new[] { node }, GraphContexts = new[] { new ProjectGraphContextBinding("test", project, context, true) }, TestRunners = new[] { runner } };
        var plan = CheckExecutionPlan.Create(graph, graph, TestContext.Current.CancellationToken);
        var before = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Time: File.GetLastWriteTimeUtc(p)));
        var membership = Directory.GetFileSystemEntries(fixture.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => TestExecutionAdapter.RunAsync(plan, directory,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("test.planInvalid", error.Reason);
        Assert.Equal(membership, Directory.GetFileSystemEntries(fixture.Path, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, saved) in before) { Assert.Equal(saved.Bytes, File.ReadAllBytes(path)); Assert.Equal(saved.Time, File.GetLastWriteTimeUtc(path)); }
    }

}
