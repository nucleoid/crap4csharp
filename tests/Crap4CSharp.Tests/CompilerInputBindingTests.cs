using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerInputBindingTests
{
    [Theory]
    [InlineData("missing-source")]
    [InlineData("extra-source")]
    [InlineData("duplicate-source")]
    [InlineData("missing-parameter")]
    [InlineData("duplicate-parameter")]
    [InlineData("unknown-parameter")]
    [InlineData("unresolved-document")]
    [InlineData("duplicate-document")]
    [InlineData("changed-bytes")]
    [InlineData("missing-file")]
    [InlineData("missing-additional")]
    [InlineData("extra-config")]
    [InlineData("missing-analyzer")]
    [InlineData("duplicate-optional")]
    [InlineData("wrong-project")]
    public void MissingIndependentBindingRefuses(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-input-bind");
        var (context, observed) = Valid(fixture);
        var parameters = observed.Parameters.ToArray();
        switch (defect)
        {
            case "missing-source": parameters[0] = parameters[0] with { Items = [] }; break;
            case "extra-source": parameters[0] = parameters[0] with { Items = [.. parameters[0].Items, fixture.Write("Other.cs", "public class Other {}") ] }; break;
            case "duplicate-source": parameters[0] = parameters[0] with { Items = [.. parameters[0].Items, "./Code.cs"] }; break;
            case "missing-parameter": parameters = parameters[1..]; break;
            case "duplicate-parameter": parameters = [.. parameters, parameters[0]]; break;
            case "unknown-parameter": parameters = [.. parameters, new("ForgedInputs", [])]; break;
            case "unresolved-document": context = context with { CompilerDocuments = [context.CompilerDocuments[0] with { ResolvedPath = null }] }; break;
            case "duplicate-document": context = context with { CompilerDocuments = [.. context.CompilerDocuments, context.CompilerDocuments[0]] }; break;
            case "changed-bytes": File.WriteAllText(context.CompilerDocuments[0].ResolvedPath!, "public class Changed {}"); break;
            case "missing-file": File.Delete(context.CompilerDocuments[0].ResolvedPath!); break;
            case "missing-additional": parameters = parameters.Where(p => p.Name != "AdditionalFiles").ToArray(); break;
            case "extra-config": parameters = [.. parameters, new("AnalyzerConfigFiles", [fixture.Write("extra.editorconfig", "root=true")])]; break;
            case "missing-analyzer": parameters = parameters.Where(p => p.Name != "Analyzers").ToArray(); break;
            case "duplicate-optional": parameters[2] = parameters[2] with { Items = [.. parameters[2].Items, parameters[2].Items[0]] }; break;
            case "wrong-project": observed = observed with { ProjectPath = fixture.Write("Shadow.csproj", "<Project />") }; break;
        }
        observed = observed with { Parameters = parameters };
        var error = Assert.Throws<ProjectContextException>(() => CompilerInputBinding.Validate(context, observed, TestContext.Current.CancellationToken));
        Assert.Equal("compiler.inputsUnproven", error.Reason);
    }

    [Fact]
    public void ExactInventoryAllowsAbsentConfigProbesAndDoesNotAssertCompiledCapability()
    {
        using var fixture = TestDirectory.Create("crap4csharp-input-positive");
        var (context, observed) = Valid(fixture);
        context = context with { PhaseInputs = [.. context.PhaseInputs, new(Path.Combine(fixture.Path, ".globalconfig"), "analyzerConfigProbe", false)] };
        CompilerInputBinding.Validate(context, observed, TestContext.Current.CancellationToken);
        Assert.False(context.Capabilities.CompiledInputBindingComplete);
        Assert.False(context.Capabilities.ReuseRecipeComplete);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("build-only-source")]
    [InlineData("build-only-additional")]
    public async Task ActualSdkCompilerInputsBindIncludingExcludedSourcesAndOptionalFiles(string variant)
    {
        using var fixture = TestDirectory.Create("crap4csharp-input-actual");
        var condition = variant == "build-only-source" ? " Condition=\"'$(DesignTimeBuild)' != 'true'\"" : "";
        var extraAdditional = variant == "build-only-additional" ? "<AdditionalFiles Include=\"../Shared/Extra.txt\" Condition=\"'$(DesignTimeBuild)' != 'true'\" />" : "";
        var project = fixture.Write("App/App.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><AdditionalFiles Include="Rules.txt" /></ItemGroup>
            <Target Name="AddSource" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="../Shared/Added.cs" Link="obj/Added.cs"{{condition}} />{{extraAdditional}}</ItemGroup></Target></Project>
            """);
        fixture.Write("App/Code.cs", "public class Code { public int M() => 1; }");
        fixture.Write("App/Rules.txt", "rules");
        fixture.Write("Shared/Extra.txt", "extra rules");
        fixture.Write("Shared/Added.cs", "public class Added {}"); // No sequence points; Sources remains mandatory.
        var original = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (File.ReadAllBytes(p), File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason);
        var directory = Path.Combine(fixture.Path, "private"); Directory.CreateDirectory(directory);
        var phase = await CompilerGraphExecution.RunPhasedAsync(request, loaded,
            (node, token) => CompilerEvidence.BuildAsync(node, directory, TimeSpan.FromMinutes(2), token), TestContext.Current.CancellationToken);
        var context = Assert.Single(phase.FinalGraph.GraphContexts).Context;
        var observed = Assert.Single(phase.Observations);
        Assert.Contains(context.CompilerDocuments, d => d.IsGenerated);
        Assert.Contains(observed.Parameters, p => p.Name == "AdditionalFiles" && p.Items.Count > 0);
        if (variant == "ordinary")
        {
            CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken);
            Assert.Equal(File.ReadAllBytes(observed.Assembly!.Path), File.ReadAllBytes(context.EvaluatedTargetPath!));
            Assert.Equal(File.ReadAllBytes(observed.Pdb!.Path), File.ReadAllBytes(Path.ChangeExtension(context.EvaluatedTargetPath!, ".pdb")));
            Assert.Contains(context.CompilerDocuments, d => d.ResolvedPath!.EndsWith("Added.cs", StringComparison.Ordinal) && !d.IsGenerated);
            // Actual SDK Csc observation plus final Workspace; no-sequence-point Added.cs
            // is still required by the complete Sources gate. Target-copy mutation is real.
            var target = context.EvaluatedTargetPath!;
            var saved = File.ReadAllBytes(target);
            File.AppendAllText(target, "copy changed");
            Assert.Equal("compiler.outputsUnproven", Assert.Throws<ProjectContextException>(() =>
                CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken)).Reason);
            File.WriteAllBytes(target, saved);
            CompilerOutputBinding.Validate(context, observed, TestContext.Current.CancellationToken);
        }
        else
        {
            var error = Assert.Throws<ProjectContextException>(() => CompilerInputBinding.Validate(context, observed, TestContext.Current.CancellationToken));
            Assert.Equal("compiler.inputsUnproven", error.Reason);
            Assert.Contains(observed.Parameters, p => p.Name == (variant == "build-only-source" ? "Sources" : "AdditionalFiles") &&
                p.Items.Any(i => i.EndsWith(variant == "build-only-source" ? "Added.cs" : "Extra.txt", StringComparison.Ordinal)));
        }
        foreach (var (path, saved) in original)
        {
            Assert.Equal(saved.Item1, File.ReadAllBytes(path));
            Assert.Equal(saved.Item2, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData("oversized-source")]
    [InlineData("oversized-optional")]
    [InlineData("present-probe")]
    [InlineData("symlink-source")]
    public void BoundReadsRefuseUnsupportedInputs(string defect)
    {
        using var fixture = TestDirectory.Create("crap4csharp-input-limits");
        var (context, observed) = Valid(fixture);
        if (defect.StartsWith("oversized", StringComparison.Ordinal))
        {
            var path = defect == "oversized-source" ? context.CompilerDocuments[0].ResolvedPath! : Path.Combine(fixture.Path, "Rules.txt");
            using (var stream = File.Create(path)) stream.SetLength(CompilerPhaseSnapshot.MaximumFileBytes + 1);
        }
        else if (defect == "present-probe")
            context = context with { PhaseInputs = [.. context.PhaseInputs, new(fixture.Write(".globalconfig", "root=true"), "analyzerConfigProbe", false)] };
        else
        {
            // Windows symlink creation requires additional user privileges; runtime proof is Linux.
            if (OperatingSystem.IsWindows()) return;
            var path = context.CompilerDocuments[0].ResolvedPath!;
            var target = fixture.Write("Original.cs", File.ReadAllText(path));
            File.Delete(path);
            File.CreateSymbolicLink(path, target);
        }
        var error = Assert.Throws<ProjectContextException>(() => CompilerInputBinding.Validate(context, observed, TestContext.Current.CancellationToken));
        Assert.Equal(defect.StartsWith("oversized", StringComparison.Ordinal) ? "compiler.inputLimit" : "compiler.inputsUnproven", error.Reason);
    }

    [Fact]
    public void PreCancellationPrecedesInputFileReads()
    {
        using var fixture = TestDirectory.Create("crap4csharp-input-cancel");
        var (context, observed) = Valid(fixture);
        File.Delete(context.CompilerDocuments[0].ResolvedPath!);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CompilerInputBinding.Validate(context, observed, cancelled.Token));
    }

    private static (ProjectAnalysisContext, CompilerObservation) Valid(TestDirectory fixture)
    {
        var project = fixture.Write("App.csproj", "<Project />");
        var source = fixture.Write("Code.cs", "public class Code {}");
        var additional = fixture.Write("Rules.txt", "rules");
        var analyzer = fixture.Write("Analyzer.dll", "synthetic path/byte inventory control, not an actual analyzer");
        var context = ProjectAnalysisContext.Create("App.csproj", "App", "net10.0", "Release", "AnyCPU", LanguageVersion.CSharp14,
            [], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
        {
            ResolvedProjectPath = project,
            CompilerDocuments = [new("Code.cs", "Code.cs", ProjectAnalysisContext.ContentHash(File.ReadAllBytes(source)), false, false) { ResolvedPath = source }],
            PhaseInputs = [new(project, "project", false), new(source, "source", false), new(additional, "additionalFile", false), new(analyzer, "analyzer", false)]
        };
        return (context, new(project, [new("Sources", ["Code.cs"]), new("OutputAssembly", ["obj/App.dll"]),
            new("AdditionalFiles", ["Rules.txt"]), new("Analyzers", ["Analyzer.dll"]), new("References", [])]));
    }
}
