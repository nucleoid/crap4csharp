using System.Text.Json;
using System.Reflection;
using Crap4CSharp.Core;
using Xunit;
namespace Crap4CSharp.Tests;

public sealed class CompilerBoundExecutionTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("copiedPe")]
    [InlineData("reference")]
    [InlineData("source")]
    [InlineData("actualReferences")]
    [InlineData("unauthorizedPrepared")]
    [InlineData("preparedBytes")]
    public async Task ActualMappedGraphRequiresMandatoryBindingAndRejectsLaterOutputDrift(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-destination");
        var dependency = fixture.Write("Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <Target Name="Count" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        fixture.Write("Dependency/Code.cs", "public class Dependency {}");
        var root = fixture.Write("Root/Root.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><ProjectReference Include="../Dependency/Dependency.csproj" AdditionalProperties="Configuration=Debug" /></ItemGroup>
            <Target Name="Count" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true'">
            <WriteLinesToFile File="$(MSBuildProjectDirectory)/count.txt" Lines="build" Overwrite="false" /></Target></Project>
            """);
        fixture.Write("Root/Code.cs", "public class Root : Dependency {}");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(root, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        Assert.Empty(Directory.GetFiles(fixture.Path, "count.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.dll", SearchOption.AllDirectories));
        var dep = Assert.Single(loaded.GraphContexts, b => b.ProjectPath == dependency).Context;
        Assert.Equal(Path.Combine(fixture.Path, "Dependency", "obj", "Debug", "net10.0", "ref", "Dependency.dll"), dep.EvaluatedReferenceAssemblyPath);
        var rootContext = Assert.Single(loaded.GraphContexts, b => b.ProjectPath == root).Context;
        Assert.Equal(Path.Combine(fixture.Path, "Root", "obj", "Release", "net10.0", "ref", "Root.dll"), rootContext.EvaluatedReferenceAssemblyPath);
        foreach (var context in loaded.Contexts)
        {
            var copy = JsonSerializer.Deserialize<ProjectAnalysisContext>(JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!;
            Assert.Equal(context.EvaluatedReferenceAssemblyPath, copy.EvaluatedReferenceAssemblyPath);
            Assert.DoesNotContain("evaluatedReferenceAssemblyPath", JsonSerializer.Serialize(context), StringComparison.OrdinalIgnoreCase);
            Assert.False(context.Capabilities.CompiledInputBindingComplete);
        }
        var method = typeof(CompilerGraphExecution).GetMethod("RunBoundAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        if (mutation == "unauthorizedPrepared")
        {
            var binding = Assert.Single(loaded.GraphContexts, b => b.ProjectPath == dependency);
            var changed = binding with { Context = binding.Context with
            { ReferencePaths = binding.Context.ReferencePaths.Concat([rootContext.EvaluatedReferenceAssemblyPath!]).ToArray() } };
            var contexts = loaded.GraphContexts.Select(b => b.GraphId == changed.GraphId ? changed : b).ToArray();
            loaded = loaded with { GraphContexts = contexts, Contexts = contexts.Select(b => b.Context).ToArray() };
        }
        // Fixture-owned extra prepared reference: byte drift fails before a consumer,
        // without modifying SDK/package inputs shared with other tests.
        string? extra = null;
        if (mutation == "preparedBytes")
        {
            extra = fixture.Write("reference.bin", "original");
            var binding = Assert.Single(loaded.GraphContexts, b => b.ProjectPath == root);
            var changed = binding with { Context = binding.Context with
            { ReferencePaths = binding.Context.ReferencePaths.Concat([extra]).ToArray() } };
            var contexts = loaded.GraphContexts.Select(b => b.GraphId == changed.GraphId ? changed : b).ToArray();
            loaded = loaded with { GraphContexts = contexts, Contexts = contexts.Select(b => b.Context).ToArray() };
        }
        var launched = new List<string>();
        Func<ProjectBuildGraphNode, CancellationToken, Task<CompilerObservation>> build = async (node, token) =>
        {
            launched.Add(node.ProjectPath);
            var directory = Path.Combine(fixture.Path, "private", node.Id);
            Directory.CreateDirectory(directory);
            var observed = await CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(2), token);
            if (mutation == "preparedBytes" && node.ProjectPath == dependency)
            {
                var time = File.GetLastWriteTimeUtc(extra!);
                File.WriteAllText(extra!, "modified"); File.SetLastWriteTimeUtc(extra!, time);
            }
            if (node.ProjectPath == root && mutation is "copiedPe" or "reference" or "source")
            {
                var path = mutation == "copiedPe" ? dep.EvaluatedTargetPath! : mutation == "reference"
                    ? dep.EvaluatedReferenceAssemblyPath! : Path.Combine(fixture.Path, "Dependency", "Code.cs");
                var time = File.GetLastWriteTimeUtc(path);
                var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
                File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, time);
            }
            // Explicit protocol corruption of an ACTUAL observation, not a fake Csc run.
            if (mutation == "actualReferences" && node.ProjectPath == root)
                observed = observed with { Parameters = observed.Parameters.Select(p => p.Name == "References"
                    ? p with { Items = p.Items.Where(i => Path.GetFullPath(i, Path.GetDirectoryName(root)!) != dep.EvaluatedReferenceAssemblyPath).ToArray() } : p).ToArray() };
            return observed;
        };
        var operation = (Task<CompilerGraphPhaseResult>)method.Invoke(null, [request, loaded, build, TestContext.Current.CancellationToken, null])!;
        if (mutation != "none")
        {
            var failure = await Assert.ThrowsAsync<ProjectContextException>(() => operation);
            Assert.Equal(mutation switch
            {
                "copiedPe" => "compiler.outputsUnproven",
                "reference" => "compiler.referencesUnproven",
                "source" => "compiler.inputsMutated",
                _ => "compiler.referencesUnproven"
            }, failure.Reason);
        }
        else
        {
            var result = await operation;
            Assert.Equal(2, result.Observations.Count);
            Assert.DoesNotContain(result.FinalGraph.Contexts, c => c.Capabilities.CompiledInputBindingComplete);
        }
        Assert.Equal(mutation == "unauthorizedPrepared" ? [] : mutation == "preparedBytes" ? [dependency] : new[] { dependency, root }, launched);
        if (mutation is "unauthorizedPrepared" or "preparedBytes") return;
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Dependency", "count.txt")));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Root", "count.txt")));
        foreach (var (path, saved) in authored)
        {
            if (mutation == "source" && path == Path.Combine(fixture.Path, "Dependency", "Code.cs")) continue;
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

}
