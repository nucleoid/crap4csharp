using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerReferenceGraphTests
{
    [Theory]
    [InlineData("cold")]
    [InlineData("metadata")]
    [InlineData("selection-drift")]
    [InlineData("foreign-project")]
    [InlineData("unknown-output")]
    [InlineData("undeclared-owner")]
    [InlineData("unauthorized-metadata")]
    public void ReferencesFollowObservedOutputsRatherThanAllScheduledFrameworks(string variant)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-edges");
        var library = fixture.Write("Library.csproj", "<Project />");
        var tests = fixture.Write("Tests.csproj", "<Project />");
        ProjectGraphContextBinding Binding(string id, string path, string framework)
        {
            var context = ProjectAnalysisContext.Create(path, Path.GetFileNameWithoutExtension(path), framework,
                "Debug", "AnyCPU", LanguageVersion.CSharp14, [], SourceCodeKind.Regular, [], [], new(false, false),
                new("sdk", "msbuild", "roslyn")) with
            {
                ResolvedProjectPath = path,
                EvaluatedTargetPath = Path.Combine(fixture.Path, id, "bin", "assembly.dll"),
                EvaluatedIntermediateDirectory = Path.Combine(fixture.Path, id, "obj"),
                EvaluatedReferenceAssemblyPath = Path.Combine(fixture.Path, id, "obj", "ref", "assembly.dll")
            };
            return new(id, path, context, path == tests);
        }
        var nine = Binding("nine", library, "net9.0");
        var ten = Binding("ten", library, "net10.0");
        var consumer = Binding("tests", tests, "net10.0");
        consumer = consumer with { Context = consumer.Context with
            { ProjectReferenceOutputs = [new(library, ten.Context.EvaluatedTargetPath!)] } };
        var bindings = new[] { nine, ten, consumer };
        var nodes = bindings.Select(b => new ProjectBuildGraphNode(b.GraphId, b.ProjectPath, "Debug", "AnyCPU",
            b.Context.TargetFramework, new Dictionary<string, string>(), b.IsTestProject ? ["nine", "ten"] : [])).ToArray();
        ProjectContextLoadResult Graph(ProjectGraphContextBinding changed) => new(true, [nine.Context, ten.Context, changed.Context], [])
            { BuildGraph = nodes, GraphContexts = [nine, ten, changed] };
        var original = Graph(consumer);
        if (variant == "metadata") consumer = consumer with { Context = consumer.Context with
            { ProjectReferenceOutputs = [], ReferencePaths = [ten.Context.EvaluatedReferenceAssemblyPath!] } };
        if (variant == "selection-drift") consumer = consumer with { Context = consumer.Context with
            { ProjectReferenceOutputs = [new(library, nine.Context.EvaluatedTargetPath!)] } };
        if (variant == "foreign-project") consumer = consumer with { Context = consumer.Context with
            { ProjectReferenceOutputs = [new(tests, ten.Context.EvaluatedTargetPath!)] } };
        if (variant == "unknown-output") consumer = consumer with { Context = consumer.Context with
            { ProjectReferenceOutputs = [new(library, Path.Combine(fixture.Path, "other.dll"))] } };
        if (variant == "unauthorized-metadata") consumer = consumer with { Context = consumer.Context with
            { ProjectReferenceOutputs = [], ReferencePaths = [ten.Context.EvaluatedReferenceAssemblyPath!] } };
        var current = Graph(consumer);
        if (variant is "undeclared-owner" or "unauthorized-metadata") current = current with
            { BuildGraph = [nodes[0], nodes[1], nodes[2] with { Dependencies = ["nine"] }] };
        var token = TestContext.Current.CancellationToken;
        if (variant is "cold" or "metadata")
        {
            var edges = CompilerReferenceGraph.Observe(current, token);
            Assert.Equal(new[] { "ten" }, edges["tests"]);
            Assert.Equal(new[] { "ten", "tests" }, CompilerReferenceGraph.Closure(edges, "tests").Order(StringComparer.Ordinal));
            CompilerReferenceGraph.ValidateSame(original, current, token);
        }
        else if (variant == "selection-drift") Assert.Equal("compiler.referenceGraphUnproven",
            Assert.Throws<ProjectContextException>(() => CompilerReferenceGraph.ValidateSame(original, current, token)).Reason);
        else Assert.Equal(variant == "unauthorized-metadata" ? "compiler.referencesUnproven" : "compiler.referenceGraphUnproven",
            Assert.Throws<ProjectContextException>(() => CompilerReferenceGraph.Observe(current, token)).Reason);
    }
}
