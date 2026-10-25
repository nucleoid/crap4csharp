using System.Text.Json;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class GeneratorBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningOnlyNonGeneratorAnalyzerDoesNotInvalidateOrdinaryOrGraphLoading(bool includeGenerated)
    {
        using var fixture = TestDirectory.Create("crap4csharp-analyzer-warning");
        const string source = """
            using System.Collections.Immutable;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.Diagnostics;
            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public sealed class BrokenAnalyzer : DiagnosticAnalyzer
            {
                public BrokenAnalyzer() => throw new System.InvalidOperationException("warning-only analyzer constructor");
                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
                public override void Initialize(AnalysisContext context) {}
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(ISourceGenerator).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("BrokenAnalyzer", [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzer = Path.Combine(fixture.Path, "BrokenAnalyzer.dll");
        using (var stream = File.Create(analyzer))
        {
            var emitted = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics));
        }
        var project = fixture.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Analyzer Include=\"../BrokenAnalyzer.dll\" /></ItemGroup></Project>");
        fixture.Write("App/Code.cs", "public class Code { public int Value() => 1; }");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, includeGenerated, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var build = await ProcessRunner.RunAsync("dotnet", ["build", project, "-c", "Release", "--no-restore", "-m:1"],
            fixture.Path, request.Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, build.ExitCode);
        Assert.Contains("CS8032", build.StandardOutput + build.StandardError);
        var ordinary = await ProjectContextLoader.LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.True(ordinary.Success, ordinary.FailureReason + ": " + string.Join(" | ", ordinary.Diagnostics));
        var graph = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(graph.Success, graph.FailureReason + ": " + string.Join(" | ", graph.Diagnostics));
        Assert.Equal(0, Assert.Single(graph.GraphContexts).Context.GeneratorOnlyTreeCount);
        var revalidated = await ProjectContextLoader.LoadAsync(request, TestContext.Current.CancellationToken);
        Assert.True(revalidated.Success, revalidated.FailureReason + ": " + string.Join(" | ", revalidated.Diagnostics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedPotentialGeneratorRemainsRefused(bool includeGenerated)
    {
        using var fixture = TestDirectory.Create("crap4csharp-unresolved-generator");
        var project = fixture.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Analyzer Include=\"../Missing.dll\" /></ItemGroup></Project>");
        fixture.Write("App/Code.cs", "public class Code {}");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, includeGenerated, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.False(loaded.Success);
        Assert.Equal("context.generatedSourceUnavailable", loaded.FailureReason);
    }

    [Theory]
    [InlineData(false, "emit")]
    [InlineData(true, "emit")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [InlineData(false, "constructor")]
    [InlineData(true, "constructor")]
    [InlineData(false, "declaration")]
    [InlineData(true, "declaration")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    public async Task ActualGeneratorIsInspectedIndependentlyOfPublicInclusion(bool includeGenerated, string behavior)
    {
        var fixture = TestDirectory.Create("crap4csharp-generator-binding"); // Retain actual failed compiler evidence.
        var action = behavior switch
        {
            "emit" => "context.AddSource(\"Hidden.cs\", \"public class Hidden { public int Risk(int x) => x > 0 ? 1 : 2; }\");",
            "declaration" => "context.AddSource(\"Hidden.cs\", \"public class Hidden {}\");",
            "throw" => "throw new System.InvalidOperationException(\"fixture generator failed\");",
            _ => ""
        };
        var generatorSource = "using Microsoft.CodeAnalysis; [Generator] public sealed class FixtureGenerator : ISourceGenerator { public void Initialize(GeneratorInitializationContext context) {} public void Execute(GeneratorExecutionContext context) { " + action + " } }";
        if (behavior == "constructor") generatorSource = generatorSource.Replace(
            "public void Initialize", "public FixtureGenerator() => throw new System.InvalidOperationException(\"generator constructor failed\"); public void Initialize", StringComparison.Ordinal);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(ISourceGenerator).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("FixtureGenerator",
            [CSharpSyntaxTree.ParseText(generatorSource, cancellationToken: TestContext.Current.CancellationToken)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzer = Path.Combine(fixture.Path, "FixtureGenerator.dll");
        using (var stream = File.Create(analyzer))
        {
            var emitted = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(emitted.Success, string.Join(" | ", emitted.Diagnostics));
        }
        var project = fixture.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Analyzer Include=\"../FixtureGenerator.dll\" /></ItemGroup></Project>");
        fixture.Write("App/Code.cs", "public class Code {}");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, includeGenerated, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        if (behavior is "throw" or "constructor")
        {
            Assert.False(loaded.Success);
            Assert.Equal("context.generatedSourceUnavailable", loaded.FailureReason);
        }
        else
        {
            Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
            var context = Assert.Single(loaded.GraphContexts).Context;
            // Proposed private protocol marker: public exclusion cannot hide binding obligations.
            Assert.Equal(behavior is "emit" or "declaration" ? 1 : 0, context.GeneratorOnlyTreeCount);
            Assert.Equal((behavior is "emit" or "declaration") && includeGenerated,
                context.Sources.Any(s => context.GeneratedCompilerDocuments.Any(d => d.LogicalPath == s.LogicalPath)));
            var copy = JsonSerializer.Deserialize<ProjectAnalysisContext>(JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!;
            Assert.Equal(context.GeneratorOnlyTreeCount, copy.GeneratorOnlyTreeCount);
            Assert.Equal(context.GeneratedCompilerDocuments.ToArray(), copy.GeneratedCompilerDocuments.ToArray());
            Assert.DoesNotContain("generatedCompilerDocuments", JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("generatorOnlyTreeCount", JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
            if (behavior is "emit" or "declaration")
            {
                var privateDirectory = Path.Combine(fixture.Path, "private");
                Directory.CreateDirectory(privateDirectory);
                var observed = await CompilerEvidence.BuildAsync(Assert.Single(loaded.BuildGraph), privateDirectory,
                    request.Timeout, TestContext.Current.CancellationToken);
                Assert.NotNull(observed.Assembly);
                var final = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
                Assert.True(final.Success, final.FailureReason + ": " + string.Join(" | ", final.Diagnostics));
                var finalContext = Assert.Single(final.GraphContexts).Context;
                Assert.Equal(1, finalContext.GeneratorOnlyTreeCount);
                if (behavior == "declaration")
                {
                    // A declaration-only generated tree lacks actual PDB document
                    // evidence and remains outside the supported binding profile.
                    Assert.Throws<ProjectContextException>(() => CompilerOutputBinding.Validate(finalContext,
                        observed, TestContext.Current.CancellationToken));
                }
                else
                {
                    InspectRetainedGeneratorOutputs(fixture, finalContext, observed);
                    var output = CompilerOutputBinding.ValidateAndInspect(finalContext, observed, TestContext.Current.CancellationToken);
                    var binding = CompilerDocumentBinding.Create(finalContext, observed, output.Build, TestContext.Current.CancellationToken);
                    var generated = Assert.Single(finalContext.GeneratedCompilerDocuments);
                    var generatedSource = Assert.Single(CompilerSourceSet.Create(finalContext, TestContext.Current.CancellationToken).GeneratedDocuments);
                    Assert.Contains(generated.Path, binding.PhysicalPathsWithPdb);
                    Assert.False(File.Exists(generated.Path)); // It is an identity, not an opened/emitted file.
                    Assert.Contains(output.Build.Methods, method => method.SequencePoints.Any(point =>
                        binding.EligiblePdbNames([generatedSource]).Contains(point.Document)));
                    Assert.False(finalContext.Capabilities.CompiledInputBindingComplete);
                    foreach (var defect in new[] { "bytes", "name", "duplicate", "missing", "ordinary-collision", "module" })
                    {
                        var changed = generated;
                        var contextWithDefect = finalContext;
                        if (defect == "bytes")
                        {
                            var bytes = Convert.FromBase64String(generated.BytesBase64).Concat(new byte[] { 32 }).ToArray();
                            var hash = generated.ChecksumAlgorithm == "sha256" ? System.Security.Cryptography.SHA256.HashData(bytes) :
                                System.Security.Cryptography.SHA1.HashData(bytes);
                            changed = generated with { BytesBase64 = Convert.ToBase64String(bytes), Checksum = Convert.ToHexStringLower(hash) };
                        }
                        if (defect == "name") changed = generated with { Path = Path.Combine(Path.GetDirectoryName(generated.Path)!, "Foreign.g.cs") };
                        if (defect == "ordinary-collision") changed = generated with { Path = finalContext.CompilerDocuments[0].ResolvedPath! };
                        contextWithDefect = contextWithDefect with { GeneratedCompilerDocuments = [changed] };
                        if (defect == "duplicate") contextWithDefect = contextWithDefect with
                        { GeneratorOnlyTreeCount = 2, GeneratedCompilerDocuments = [changed, changed] };
                        if (defect == "missing") contextWithDefect = contextWithDefect with { GeneratedCompilerDocuments = [] };
                        if (defect == "module") contextWithDefect = contextWithDefect with { AssemblyName = "ForeignModule" };
                        Assert.Throws<ProjectContextException>(() => CompilerOutputBinding.Validate(contextWithDefect,
                            observed, TestContext.Current.CancellationToken));
                    }
                }
            }
        }
        foreach (var (path, saved) in authored)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }
    [Fact]
    public async Task ActualSdkRegexGeneratorBindsEveryCurrentGeneratedDocument()
    {
        var fixture = TestDirectory.Create("crap4csharp-sdk-regex-binding"); // Retain actual failed compiler evidence.
        var project = fixture.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PathMap>$(MSBuildProjectDirectory)=/_/</PathMap></PropertyGroup></Project>");
        fixture.Write("App/Code.cs", "using System.Text.RegularExpressions; public static partial class Code { [GeneratedRegex(\"^a+$\")] private static partial Regex Pattern(); public static bool Matches(string value) => Pattern().IsMatch(value); }");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        var privateDirectory = Path.Combine(fixture.Path, "private"); Directory.CreateDirectory(privateDirectory);
        var observation = await CompilerEvidence.BuildAsync(Assert.Single(loaded.BuildGraph), privateDirectory,
            request.Timeout, TestContext.Current.CancellationToken);
        var final = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(final.Success, final.FailureReason + ": " + string.Join(" | ", final.Diagnostics));
        var context = Assert.Single(final.GraphContexts).Context;
        Assert.True(context.GeneratorOnlyTreeCount > 0);
        Assert.Equal(context.GeneratorOnlyTreeCount, context.GeneratedCompilerDocuments.Count);
        InspectRetainedGeneratorOutputs(fixture, context, observation);
        var output = CompilerOutputBinding.ValidateAndInspect(context, observation, TestContext.Current.CancellationToken);
        var sourceSet = CompilerSourceSet.Create(context, TestContext.Current.CancellationToken);
        var binding = CompilerDocumentBinding.Create(context, observation, output.Build, TestContext.Current.CancellationToken);
        Assert.All(sourceSet.GeneratedDocuments, source => Assert.Contains(source.ResolvedPath!, binding.PhysicalPathsWithPdb));
        Assert.All(output.Build.Documents.Keys, name => Assert.StartsWith("/_/", name, StringComparison.Ordinal));
        Assert.Contains(output.Build.Methods, method => method.MethodName == "Matches" && method.SequencePoints.Count > 0);
        Assert.DoesNotContain(context.Sources, source => sourceSet.IsGeneratorTree(source));
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
    }

    private static void InspectRetainedGeneratorOutputs(TestDirectory fixture, ProjectAnalysisContext context, CompilerObservation observation)
    {
        File.WriteAllText(Path.Combine(fixture.Path, "retained-context.json"), JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions));
        File.WriteAllText(Path.Combine(fixture.Path, "retained-observation.json"), JsonSerializer.Serialize(observation, ProjectContextLoader.JsonOptions));
        try
        {
            _ = ArtifactEvidenceInspector.InspectBuild([.. File.ReadAllBytes(observation.Assembly!.Path)],
                [.. File.ReadAllBytes(observation.Pdb!.Path)]);
        }
        catch (InvalidDataException error)
        {
            Assert.Fail("Actual generator PE/PDB inspection failed; retained fixture: " + fixture.Path + ": " + error.Message);
        }
    }

}
