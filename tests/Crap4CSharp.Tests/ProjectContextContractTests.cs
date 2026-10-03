using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class ProjectContextContractTests
{
    [Fact]
    public void ContextIdentityIsDeterministicAndSensitiveToCompilerContext()
    {
        var source = new ProjectSourceIdentity("src/A.cs", "A.cs", "sha-a", false, false);
        var first = ProjectAnalysisContext.Create("p", "App", "net10.0", "Debug", "AnyCPU",
            LanguageVersion.CSharp14, ["DEBUG", "NET10_0"], SourceCodeKind.Regular,
            ["Directory.Build.props"], [source], new ProjectExclusionPolicy(false, false),
            new ProjectAdapterIdentity("10.0.103", "18.0.3", "5.0.0"));
        var same = ProjectAnalysisContext.Create("p", "App", "net10.0", "Debug", "AnyCPU",
            LanguageVersion.CSharp14, ["NET10_0", "DEBUG"], SourceCodeKind.Regular,
            ["Directory.Build.props"], [source], new ProjectExclusionPolicy(false, false),
            new ProjectAdapterIdentity("10.0.103", "18.0.3", "5.0.0"));
        var changed = ProjectAnalysisContext.Create("p", "App", "netstandard2.1", "Debug", "AnyCPU",
            LanguageVersion.CSharp14, ["DEBUG", "NETSTANDARD2_1"], SourceCodeKind.Regular,
            ["Directory.Build.props"], [source], new ProjectExclusionPolicy(false, false),
            new ProjectAdapterIdentity("10.0.103", "18.0.3", "5.0.0"));

        Assert.Equal(first.ContextId, same.ContextId);
        Assert.Equal(first.SourceSetIdentity, same.SourceSetIdentity);
        Assert.NotEqual(first.ContextId, changed.ContextId);
    }

    [Fact]
    public void AnalyzerUsesCapturedParseOptionsAndTreePath()
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp14, preprocessorSymbols: ["FEATURE"]);
        var methods = new SourceAnalyzer().AnalyzeText("#if FEATURE\nclass C { int Enabled() => 1; }\n#else\nclass C { int Disabled() => 2; }\n#endif", "/logical/Linked.cs", options);
        var method = Assert.Single(methods);
        Assert.Equal("Enabled", method.MethodName);
        Assert.Equal("/logical/Linked.cs", method.File);
    }

    [Fact]
    public void CoverageNeverCrossesProjectContext()
    {
        var method = new SourceMethod("A.cs", "C", "M", "C.M()", "M()", 1, 1, 1);
        var contexts = new[]
        {
            new ContextualSourceMethod("net10", method),
            new ContextualSourceMethod("netstandard", method)
        };
        var report = new ContextualCoverageReport("net10", []);
        var matches = CoverageMatcher.ApplyDetailed(contexts, [report]);
        Assert.Equal(CoverageReasonCodes.Unavailable, Assert.Single(matches, x => x.ContextId == "netstandard").CoverageReason);
    }

    [Fact]
    public void CompiledInputBindingFailsClosedOnContextOrSourceDrift()
    {
        var source = new ProjectSourceIdentity("A.cs", "A.cs", "sha-a", false, false);
        var context = ProjectAnalysisContext.Create("App.csproj", "App", "net10.0", "Debug", "AnyCPU",
            LanguageVersion.CSharp14, [], SourceCodeKind.Regular, [], [source],
            new ProjectExclusionPolicy(false, false), new ProjectAdapterIdentity("10.0.103", "18.0.11", "5.0.0"));
        var evidence = new CompiledInputEvidence(context.ContextId, "missing.dll", null,
            [new CompiledInputIdentity("A.cs", "different", false)], true, null);

        var capability = CompiledInputCapture.Validate(context, evidence);

        Assert.False(capability.CompiledInputBindingComplete);
        Assert.Equal("context.compiledInputDrift", capability.Limitation);
    }
}
