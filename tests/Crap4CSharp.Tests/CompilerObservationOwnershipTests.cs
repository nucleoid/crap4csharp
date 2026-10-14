using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerObservationOwnershipTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("wrong-project")]
    [InlineData("unowned")]
    public void SharedProjectPathRequiresExactCompleteContextOwnership(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-observation-owners");
        var project = fixture.Write("App.csproj", "<Project />");
        ProjectGraphContextBinding Binding(string id, string framework)
        {
            var context = ProjectAnalysisContext.Create("App.csproj", "App", framework, "Debug", "AnyCPU",
                LanguageVersion.CSharp14, [], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn"))
                with { ResolvedProjectPath = project };
            return new(id, project, context, false);
        }
        var bindings = new[] { Binding("nine", "net9.0"), Binding("ten", "net10.0") };
        var observations = new[] { new CompilerObservation(project, []) { GraphId = "nine" },
            new CompilerObservation(project, []) { GraphId = "ten" } };
        var graph = new ProjectContextLoadResult(true, bindings.Select(b => b.Context).ToArray(), []) { GraphContexts = bindings };
        var result = new CompilerGraphPhaseResult(graph, observations);
        result = mutation switch
        {
            "missing" => result with { Observations = [observations[0]] },
            "duplicate" => result with { Observations = [observations[0], observations[0]] },
            "foreign" => result with { Observations = [observations[0], observations[1] with { GraphId = "other" }] },
            "wrong-project" => result with { Observations = [observations[0], observations[1] with
                { ProjectPath = Path.Combine(fixture.Path, "Other.csproj") }] },
            "unowned" => result with { Observations = [observations[0], observations[1] with { GraphId = null }] },
            _ => result
        };
        if (mutation == "none")
        {
            var mapped = result.ByGraphId();
            Assert.Equal(2, mapped.Count);
            Assert.Same(observations[0], mapped["nine"]);
            Assert.Same(observations[1], mapped["ten"]);
        }
        else Assert.Equal("compiler.contextMismatch", Assert.Throws<ProjectContextException>(() => result.ByGraphId()).Reason);
    }
}
