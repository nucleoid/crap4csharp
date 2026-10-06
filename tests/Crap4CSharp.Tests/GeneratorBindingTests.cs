using System.Text.Json;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class GeneratorBindingTests
{
    [Theory]
    [InlineData(false, "emit")]
    [InlineData(true, "emit")]
    [InlineData(false, "throw")]
    [InlineData(true, "throw")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    public async Task ActualGeneratorIsInspectedIndependentlyOfPublicInclusion(bool includeGenerated, string behavior)
    {
        using var fixture = TestDirectory.Create("crap4csharp-generator-binding");
        var action = behavior switch
        {
            "emit" => "context.AddSource(\"Hidden.cs\", \"public class Hidden { public int Risk(int x) => x > 0 ? 1 : 2; }\");",
            "throw" => "throw new System.InvalidOperationException(\"fixture generator failed\");",
            _ => ""
        };
        var generatorSource = "using Microsoft.CodeAnalysis; [Generator] public sealed class FixtureGenerator : ISourceGenerator { public void Initialize(GeneratorInitializationContext context) {} public void Execute(GeneratorExecutionContext context) { " + action + " } }";
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
        if (behavior == "throw")
        {
            Assert.False(loaded.Success);
            Assert.Equal("context.generatedSourceUnavailable", loaded.FailureReason);
        }
        else
        {
            Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
            var context = Assert.Single(loaded.GraphContexts).Context;
            // Proposed private protocol marker: public exclusion cannot hide binding obligations.
            Assert.Equal(behavior == "emit" ? 1 : 0, context.GeneratorOnlyTreeCount);
            Assert.Equal(behavior == "emit" && includeGenerated,
                context.Sources.Any(s => s.IsGenerated && s.LogicalPath.EndsWith("Hidden.cs", StringComparison.Ordinal)));
            var copy = JsonSerializer.Deserialize<ProjectAnalysisContext>(JsonSerializer.Serialize(context, ProjectContextLoader.JsonOptions), ProjectContextLoader.JsonOptions)!;
            Assert.Equal(context.GeneratorOnlyTreeCount, copy.GeneratorOnlyTreeCount);
            Assert.DoesNotContain("generatorOnlyTreeCount", JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);
            if (behavior == "emit")
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
                // Real successful Csc produced a PE/PDB, yet generator source binding
                // remains unsupported even when public generated analysis is disabled.
                var exception = Assert.Throws<ProjectContextException>(() => CompilerOutputBinding.Validate(finalContext,
                    observed, TestContext.Current.CancellationToken));
                Assert.Equal("compiler.generatorBindingUnsupported", exception.Reason);
            }
        }
        foreach (var (path, saved) in authored)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }
}
