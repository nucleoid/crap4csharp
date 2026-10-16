using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerEvidenceTests
{
    [Fact]
    public async Task WarmMappedBuildObservesActualTargetAddedSourcesWithoutReferenceRebuild()
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-evidence");
        var dep = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="CountBuild" BeforeTargets="CoreCompile"><WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency {}");
        var added = fixture.Write("Shared/Added.cs", "public class Added {}");
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>
            <DefineConstants>$(DefineConstants);FLAVOR_$(Flavor)</DefineConstants></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj" AdditionalProperties="Configuration=Debug" /></ItemGroup>
            <Target Name="AddSource" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="../Shared/Added.cs" Link="obj/Added.cs" /></ItemGroup>
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        var code = fixture.Write("Root/Code.cs", "#if FLAVOR_LEFT\npublic class Root {}\n#else\n#error Mapped Flavor was lost\n#endif");
        var request = new ProjectContextLoadRequest(root, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var graph = await ProjectContextLoader.DiscoverGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(graph.Success, string.Join(" | ", graph.Diagnostics));
        var original = new[] { dep, root, added, code }.ToDictionary(p => p, p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p)));
        foreach (var pass in new[] { 1, 2 })
        foreach (var node in graph.BuildGraph)
        {
            var globals = new Dictionary<string, string>(node.GlobalProperties) { ["Flavor"] = "LEFT" };
            var directory = Path.Combine(fixture.Path, "private", pass.ToString(), node.Id);
            Directory.CreateDirectory(directory);
            var observed = await CompilerEvidence.BuildAsync(node with { GlobalProperties = globals }, directory,
                TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            Assert.Equal(node.ProjectPath, observed.ProjectPath);
            var sources = Assert.Single(observed.Parameters, p => p.Name == "Sources");
            Assert.Contains(sources.Items, p => Path.GetFullPath(p, Path.GetDirectoryName(node.ProjectPath)!) == (node.ProjectPath == root ? added : Path.Combine(fixture.Path, "Dependency", "Code.cs")));
            Assert.Single(observed.Parameters, p => p.Name == "OutputAssembly");
            Assert.Single(observed.Parameters, p => p.Name == "References");
            Assert.NotNull(observed.Assembly);
            Assert.NotNull(observed.Pdb);
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Assembly.Path)), observed.Assembly.Sha256);
            Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(observed.Pdb.Path)), observed.Pdb.Sha256);
            Assert.Contains(node.ProjectPath == dep ? "Debug" : "Release", observed.Assembly.Path, StringComparison.Ordinal);
            var mismatch = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(
                Path.Combine(directory, "compiler.binlog"), node.ProjectPath == root ? dep : root, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
            Assert.Equal("compiler.associationInvalid", mismatch.Reason);
            var binlog = Path.Combine(directory, "compiler.binlog");
            var fullBytes = File.ReadAllBytes(binlog);
            File.WriteAllBytes(binlog, fullBytes[..80]);
            var truncated = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(binlog,
                node.ProjectPath, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
            Assert.Equal("compiler.binlogInvalid", truncated.Reason);
            File.WriteAllBytes(binlog, fullBytes);
            Assert.Equal(pass, File.ReadAllLines(Path.Combine(Path.GetDirectoryName(node.ProjectPath)!, "count.txt")).Length);
        }
        foreach (var (path, saved) in original)
        {
            Assert.Equal(saved.Item1, File.ReadAllBytes(path));
            Assert.Equal(saved.Item2, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualReferenceOutputParameterAndParentHashAreCaptured(bool captureHash)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-output-capture");
        var project = fixture.Write("App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        fixture.Write("Code.cs", "public class App { public int Value() => 42; }");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var graph = await ProjectContextLoader.DiscoverGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(graph.Success, string.Join(" | ", graph.Diagnostics));
        var directory = Path.Combine(fixture.Path, "private");
        Directory.CreateDirectory(directory);
        var observation = await CompilerEvidence.BuildAsync(Assert.Single(graph.BuildGraph), directory,
            TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        if (!captureHash)
        {
            var parameter = Assert.Single(observation.Parameters, p => p.Name == "OutputRefAssembly");
            var path = Path.GetFullPath(Assert.Single(parameter.Items), fixture.Path);
            Assert.True(File.Exists(path));
            Assert.Contains("refint", path, StringComparison.Ordinal);
            return;
        }
        // Reflection keeps this missing-API regression executable before the implementation.
        var property = typeof(CompilerObservation).GetProperty("ReferenceAssembly",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(property);
        var captured = Assert.IsType<CompilerOutput>(property.GetValue(observation));
        Assert.Equal(ProjectAnalysisContext.ContentHash(File.ReadAllBytes(captured.Path)), captured.Sha256);
        Assert.Equal(new FileInfo(captured.Path).Length, captured.Length);
        Assert.NotEqual(observation.Assembly!.Sha256, captured.Sha256);
    }

    [Fact]
    public async Task OversizedBinlogRefusesBeforeWorkerAndPreCancellationDoesNotLaunch()
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-limit");
        var project = fixture.Write("App.csproj", "<Project />");
        var log = Path.Combine(fixture.Path, "large.binlog");
        using (var stream = File.Create(log)) stream.SetLength(CompilerEvidence.MaximumBinlogBytes + 1);
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(log, project,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("compiler.binlogLimit", error.Reason);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompilerEvidence.ReadAsync(log, project,
            TimeSpan.FromSeconds(30), cancelled.Token));
    }

    [Fact]
    public async Task TruncatedBinlogIsTypedRefusalNotUnhandledWorkerCrash()
    {
        using var fixture = TestDirectory.Create("crap4csharp-compiler-truncated");
        var project = fixture.Write("App.csproj", "<Project />");
        var log = fixture.Write("truncated.binlog", "not a binary log");
        var error = await Assert.ThrowsAsync<ProjectContextException>(() => CompilerEvidence.ReadAsync(log, project,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal("compiler.binlogInvalid", error.Reason);
    }
}
