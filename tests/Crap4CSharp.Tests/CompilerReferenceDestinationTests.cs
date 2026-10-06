using System.Text.Json;
using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerReferenceDestinationTests
{
    [Fact]
    public async Task MappedReferenceDestinationSurvivesPrivateTransportAndMatchesActualConsumerCsc()
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
        // Independent prepared metadata references are frozen BEFORE any build. The mapped
        // dependency reference PE is added ONLY after its once build and BEFORE the consumer.
        var prepared = loaded.GraphContexts.ToDictionary(b => b.GraphId,
            b => CompilerReferenceSnapshot.Capture(b.Context.ReferencePaths, TestContext.Current.CancellationToken));
        CompilerObservation? consumer = null;
        foreach (var node in loaded.BuildGraph)
        {
            var directory = Path.Combine(fixture.Path, "private", node.Id);
            Directory.CreateDirectory(directory);
            var closure = node.ProjectPath == root
                ? prepared[node.Id].Extend([dep.EvaluatedReferenceAssemblyPath!], TestContext.Current.CancellationToken)
                : prepared[node.Id];
            var observation = await CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            if (node.ProjectPath == dependency)
                prepared[node.Id].Validate(observation, TestContext.Current.CancellationToken);
            if (node.ProjectPath == root)
            {
                closure.Validate(observation, TestContext.Current.CancellationToken);
                var referencePath = dep.EvaluatedReferenceAssemblyPath!;
                var savedReference = File.ReadAllBytes(referencePath);
                var savedTime = File.GetLastWriteTimeUtc(referencePath);
                var mutatedReference = savedReference.ToArray(); mutatedReference[^1] ^= 1;
                File.WriteAllBytes(referencePath, mutatedReference);
                File.SetLastWriteTimeUtc(referencePath, savedTime);
                var drift = Assert.Throws<ProjectContextException>(() => closure.VerifyUnchanged(TestContext.Current.CancellationToken));
                Assert.Equal("compiler.referencesUnproven", drift.Reason);
                File.WriteAllBytes(referencePath, savedReference); File.SetLastWriteTimeUtc(referencePath, savedTime);
                closure.VerifyUnchanged(TestContext.Current.CancellationToken);
                consumer = observation;
            }
        }
        Assert.NotNull(consumer);
        Assert.Contains(Assert.Single(consumer.Parameters, p => p.Name == "References").Items,
            p => Path.GetFullPath(p, Path.GetDirectoryName(root)!) == dep.EvaluatedReferenceAssemblyPath);
        Assert.True(File.Exists(dep.EvaluatedReferenceAssemblyPath));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Dependency", "count.txt")));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Path, "Root", "count.txt")));
        foreach (var (path, saved) in authored)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void PrivateReferenceDestinationDoesNotChangePublicIdentityOrCapabilities()
    {
        var context = ProjectAnalysisContext.Create("App.csproj", "App", "net10.0", "Release", "AnyCPU",
            Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp14, [], Microsoft.CodeAnalysis.SourceCodeKind.Regular,
            [], [], new(false, false), new("sdk", "msbuild", "roslyn"));
        var changed = context with { EvaluatedReferenceAssemblyPath = Path.Combine(Path.GetTempPath(), "private-ref.dll") };
        Assert.Equal(JsonSerializer.Serialize(context), JsonSerializer.Serialize(changed));
        Assert.Equal(context.ContextId, changed.ContextId);
        Assert.Equal(context.Capabilities, changed.Capabilities);
    }
}
