using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerPhaseSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedSynchronousReadsRejectLengthOrTimestampChange(bool timestampOnly)
    {
        using var fixture = TestDirectory.Create("crap4csharp-shared-read-stat");
        var path = fixture.Write("input", "same");
        var token = TestContext.Current.CancellationToken;
        var loaded = Loaded(path, "source");
        var snapshot = CompilerPhaseSnapshot.Capture(loaded, token);
        var shared = new CompilerFileReadScope(token);
        snapshot.Validate(loaded, token, shared);
        if (timestampOnly) File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        else File.WriteAllText(path, "longer input");
        Assert.Equal("compiler.retainedProofInvalid",
            Assert.Throws<ProjectContextException>(() => snapshot.Validate(loaded, token, shared)).Reason);
        shared.Abandon();
    }

    [Theory]
    [InlineData("source")]
    [InlineData("project")]
    [InlineData("import")]
    [InlineData("additionalFile")]
    [InlineData("analyzerConfig")]
    [InlineData("analyzer")]
    [InlineData("restoreAssets")]
    [InlineData("sdkConfiguration")]
    public void EveryImmutableRoleRejectsByteChangeEvenWithRestoredMtime(string role)
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-snapshot");
        var path = fixture.Write("input", "before");
        var loaded = Loaded(path, role);
        var snapshot = CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken);
        var time = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "after!");
        File.SetLastWriteTimeUtc(path, time);
        Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => snapshot.Validate(loaded, TestContext.Current.CancellationToken)).Reason);
    }

    [Theory]
    [InlineData("mtime")]
    [InlineData("deleted")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("reclassified")]
    public void TimestampDeletionAndMembershipChangesRefuse(string change)
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-snapshot");
        var path = fixture.Write("input", "same");
        var loaded = Loaded(path, "source");
        var snapshot = CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken);
        var context = loaded.GraphContexts[0].Context;
        if (change == "mtime") File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        if (change == "deleted") File.Delete(path);
        if (change == "added") context = context with { PhaseInputs = context.PhaseInputs.Append(new(fixture.Write("added", "new"), "source", false)).ToArray() };
        if (change == "removed") context = context with { PhaseInputs = [] };
        if (change == "reclassified") context = context with { PhaseInputs = [new(path, "source", true)] };
        var final = loaded with { GraphContexts = [loaded.GraphContexts[0] with { Context = context }], Contexts = [context] };
        Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => snapshot.Validate(final, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void UnchangedClosurePassesAndOnlyClassifiedGeneratedTransitionsAreDeferred()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-snapshot");
        var loaded = Loaded(fixture.Write("input", "same"), "source");
        var generated = fixture.Write("obj/generated.cs", "old");
        var context = loaded.GraphContexts[0].Context with { PhaseInputs = loaded.GraphContexts[0].Context.PhaseInputs.Append(new(generated, "source", true)).ToArray() };
        loaded = loaded with { GraphContexts = [loaded.GraphContexts[0] with { Context = context }], Contexts = [context] };
        var snapshot = CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken);
        File.WriteAllText(generated, "new");
        snapshot.Validate(loaded, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void WholeMappedClosureIsFrozenNotJustFirstContext()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-snapshot");
        var first = Loaded(fixture.Write("first", "same"), "source");
        var second = Loaded(fixture.Write("second", "same"), "additionalFile");
        var bindings = new[] { first.GraphContexts[0], second.GraphContexts[0] with { GraphId = "second" } };
        var loaded = first with { GraphContexts = bindings, Contexts = bindings.Select(b => b.Context).ToArray() };
        var snapshot = CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken);
        File.WriteAllText(second.GraphContexts[0].Context.PhaseInputs[0].Path, "changed");
        Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => snapshot.Validate(loaded, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public async Task RealBuildAuthoredTargetMutationIsRejectedWhileOrdinaryGeneratedOutputsPass()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-real");
        var project = fixture.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            <ItemGroup><AdditionalFiles Include="Rules.txt" /></ItemGroup>
            <Target Name="Mutate" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true' And Exists('mutate')">
            <WriteLinesToFile File="Rules.txt" Lines="changed" Overwrite="true" /></Target></Project>
            """);
        fixture.Write("Code.cs", "public class Code {}");
        fixture.Write("Rules.txt", "before");
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        for (var pass = 0; pass < 2; pass++)
        {
            var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
            Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
            Assert.All(loaded.GraphContexts.SelectMany(b => b.Context.PhaseInputs).Where(p => !p.ExpectedGenerated && p.Role != "analyzerConfigProbe"),
                p => Assert.True(File.Exists(p.Path), p.Role + ": " + p.Path));
            var snapshot = CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken);
            if (pass == 1) fixture.Write("mutate", "yes");
            var owned = Path.Combine(fixture.Path, "private" + pass);
            Directory.CreateDirectory(owned);
            await CompilerEvidence.BuildAsync(Assert.Single(loaded.BuildGraph), owned, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
            var final = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
            Assert.True(final.Success, final.FailureReason + ": " + string.Join(" | ", final.Diagnostics));
            if (pass == 0) snapshot.Validate(final, TestContext.Current.CancellationToken);
            else Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => snapshot.Validate(final, TestContext.Current.CancellationToken)).Reason);
        }
    }

    [Fact]
    public void AbsentAnalyzerConfigProbeIsBoundAndCreationWithoutRecaptureRefuses()
    {
        using var fixture = TestDirectory.Create("crap4csharp-config-probe");
        var path = Path.Combine(fixture.Path, ".globalconfig");
        var loaded = Loaded(path, "analyzerConfigProbe");
        var snapshot = CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken);
        snapshot.Validate(loaded, TestContext.Current.CancellationToken);
        File.WriteAllText(path, "is_global = true");
        Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => snapshot.Validate(loaded, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void OversizedAndPrecancelledCaptureRefuse()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-limits");
        var path = fixture.Write("large", "");
        using (var stream = File.OpenWrite(path)) stream.SetLength(CompilerPhaseSnapshot.MaximumFileBytes + 1);
        Assert.Equal("compiler.inputLimit", Assert.Throws<ProjectContextException>(() => CompilerPhaseSnapshot.Capture(Loaded(path, "source"), TestContext.Current.CancellationToken)).Reason);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompilerPhaseSnapshot.Capture(Loaded(path, "source"), cancelled.Token));
    }

    [Fact]
    public void MissingProbeCannotAuthorizeMissingRequiredSourceAtSamePath()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-probe-alias");
        var path = Path.Combine(fixture.Path, "missing");
        var loaded = Loaded(path, "analyzerConfigProbe");
        var context = loaded.GraphContexts[0].Context with { PhaseInputs = [new(path, "analyzerConfigProbe", false), new(path, "source", false)] };
        loaded = loaded with { GraphContexts = [loaded.GraphContexts[0] with { Context = context }], Contexts = [context] };
        Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void ContradictoryGeneratedClassificationRefuses()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-generated-conflict");
        var path = fixture.Write("input", "same");
        var loaded = Loaded(path, "source");
        var context = loaded.GraphContexts[0].Context with { PhaseInputs = [new(path, "source", false), new(path, "source", true)] };
        loaded = loaded with { GraphContexts = [loaded.GraphContexts[0] with { Context = context }], Contexts = [context] };
        Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => CompilerPhaseSnapshot.Capture(loaded, TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public void MissingProbeCannotTraverseSymlinkAncestor()
    {
        using var fixture = TestDirectory.Create("crap4csharp-phase-probe-link");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "actual"));
        var alias = Path.Combine(fixture.Path, "link");
        ReparseTestDirectory.Link(alias, Path.Combine(fixture.Path, "actual"));
        try
        {
            var path = Path.Combine(alias, "missing.globalconfig");
            Assert.Equal("compiler.inputsMutated", Assert.Throws<ProjectContextException>(() => CompilerPhaseSnapshot.Capture(Loaded(path, "analyzerConfigProbe"), TestContext.Current.CancellationToken)).Reason);
        }
        finally { Directory.Delete(alias); }
    }

    private static ProjectContextLoadResult Loaded(string path, string role)
    {
        var context = ProjectAnalysisContext.Create("App.csproj", "App", "net10.0", "Release", "AnyCPU", LanguageVersion.CSharp14,
            [], SourceCodeKind.Regular, [], [], new(false, false), new("sdk", "msbuild", "roslyn")) with
        { PhaseInputs = [new(path, role, false)] };
        return new(true, [context], []) { GraphContexts = [new("graph", Path.Combine(Path.GetDirectoryName(path)!, "App.csproj"), context, false)] };
    }
}
