using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class CompilerReferenceSnapshotTests
{
    // These controls deliberately use arbitrary regular-file bytes/fake reference observations;
    // they prove snapshot integrity, not PE validity, graph resolution or actual compilation.
    [Theory]
    [InlineData("bytes")]
    [InlineData("mtime")]
    [InlineData("deleted")]
    [InlineData("unexpected")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("duplicateParameter")]
    public void ChangedOrUnboundReferenceClosureRefuses(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-snapshot");
        var reference = fixture.Write("Reference.dll", "reference");
        var project = fixture.Write("App.csproj", "<Project />");
        var snapshot = CompilerReferenceSnapshot.Capture([reference], TestContext.Current.CancellationToken);
        var time = File.GetLastWriteTimeUtc(reference);
        IReadOnlyList<string> items = [reference];
        if (mutation == "bytes") { File.WriteAllText(reference, "changed!!"); File.SetLastWriteTimeUtc(reference, time); }
        if (mutation == "mtime") File.SetLastWriteTimeUtc(reference, time.AddMinutes(1));
        if (mutation == "deleted") File.Delete(reference);
        if (mutation == "unexpected") items = [fixture.Write("Other.dll", "other")];
        if (mutation == "missing") items = [];
        if (mutation == "duplicate") items = [reference, reference];
        var parameters = mutation == "duplicateParameter"
            ? new CompilerParameter[] { new("References", items), new("References", items) }
            : [new CompilerParameter("References", items)];
        var error = Assert.Throws<ProjectContextException>(() => snapshot.Validate(new(project, parameters), TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("relative")]
    [InlineData("oversize")]
    public void CaptureRefusesUnresolvedOrUnboundedPreparedPaths(string mutation)
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-capture");
        var reference = fixture.Write("Reference.dll", "reference");
        IReadOnlyList<string> paths = [reference];
        if (mutation == "missing") File.Delete(reference);
        if (mutation == "duplicate") paths = [reference, reference];
        if (mutation == "relative") paths = ["Reference.dll"];
        if (mutation == "oversize") { using var stream = File.OpenWrite(reference); stream.SetLength(CompilerPhaseSnapshot.MaximumFileBytes + 1); }
        var error = Assert.Throws<ProjectContextException>(() => CompilerReferenceSnapshot.Capture(paths, TestContext.Current.CancellationToken));
        Assert.Equal(mutation == "oversize" ? "compiler.inputLimit" : "compiler.referencesUnproven", error.Reason);
    }

    [Fact]
    public void StagedExtensionCannotRebaselineMutatedPreparedBytes()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-stage");
        var prepared = fixture.Write("Prepared.dll", "prepared");
        var snapshot = CompilerReferenceSnapshot.Capture([prepared], TestContext.Current.CancellationToken);
        var dependency = fixture.Write("Dependency.dll", "dependency");
        File.WriteAllText(prepared, "mutation");
        var error = Assert.Throws<ProjectContextException>(() => snapshot.Extend([dependency], TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }

    [Fact]
    public void StagedExtensionPreservesBothClosuresAndOriginalMembership()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-stage-positive");
        var prepared = fixture.Write("Prepared.dll", "prepared");
        var project = fixture.Write("App.csproj", "<Project />");
        var before = CompilerReferenceSnapshot.Capture([prepared], TestContext.Current.CancellationToken);
        var dependency = fixture.Write("Dependency.dll", "dependency");
        var after = before.Extend([dependency], TestContext.Current.CancellationToken);
        before.Validate(new(project, [new("References", [prepared])]), TestContext.Current.CancellationToken);
        after.Validate(new(project, [new("References", [prepared, dependency])]), TestContext.Current.CancellationToken);
        File.WriteAllText(dependency, "mutated");
        var error = Assert.Throws<ProjectContextException>(() => after.VerifyUnchanged(TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
        before.VerifyUnchanged(TestContext.Current.CancellationToken); // Extension did not mutate the old inventory.
    }

    [Fact]
    public void PreCancellationRefusesBeforeReferenceReads()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CompilerReferenceSnapshot.Capture(["missing"], cancelled.Token));
    }

    [Fact]
    public void SharedStagedPathKeepsOriginalIdentityAndDuplicateSuppliedPathsRefuse()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-overlap");
        var path = fixture.Write("Reference.dll", "reference");
        var snapshot = CompilerReferenceSnapshot.Capture([path], TestContext.Current.CancellationToken);
        snapshot.Extend([path], TestContext.Current.CancellationToken).VerifyUnchanged(TestContext.Current.CancellationToken);
        var error = Assert.Throws<ProjectContextException>(() => snapshot.Extend([path, path], TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
    }

    [Fact]
    public void SymlinkReferenceRefusesWithoutFollowingItsTarget()
    {
        if (OperatingSystem.IsWindows()) return; // Windows reparse execution remains hosted-CI owned.
        using var fixture = TestDirectory.Create("crap4csharp-reference-symlink");
        var target = fixture.Write("Target.dll", "reference");
        var link = Path.Combine(fixture.Path, "Link.dll");
        File.CreateSymbolicLink(link, target);
        var error = Assert.Throws<ProjectContextException>(() => CompilerReferenceSnapshot.Capture([link], TestContext.Current.CancellationToken));
        Assert.Equal("compiler.referencesUnproven", error.Reason);
        Assert.Equal("reference", File.ReadAllText(target));
    }

    [Fact]
    public void EmptyClosureControl()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-empty");
        var project = fixture.Write("App.csproj", "<Project />");
        var snapshot = CompilerReferenceSnapshot.Capture([], TestContext.Current.CancellationToken);
        snapshot.Validate(new(project, []), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PreparedSdkReferencesBindIndependentActualCscWithoutConsumerMutation()
    {
        using var fixture = TestDirectory.Create("crap4csharp-reference-actual");
        var project = fixture.Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
            """);
        fixture.Write("App/Code.cs", "public class Code { public string M() => System.String.Empty; }");
        var authored = Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories).ToDictionary(p => p,
            p => (Bytes: File.ReadAllBytes(p), Mtime: File.GetLastWriteTimeUtc(p)));
        var request = new ProjectContextLoadRequest(project, "Release", "AnyCPU", [], false, false, TimeSpan.FromMinutes(2));
        Assert.Equal(0, (await ProjectBuildPreparation.RestoreAsync(request, TestContext.Current.CancellationToken)).ExitCode);
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, loaded.FailureReason + ": " + string.Join(" | ", loaded.Diagnostics));
        var context = Assert.Single(loaded.GraphContexts).Context;
        Assert.NotEmpty(context.ReferencePaths);
        var snapshot = CompilerReferenceSnapshot.Capture(context.ReferencePaths, TestContext.Current.CancellationToken);
        var directory = Path.Combine(fixture.Path, "private"); Directory.CreateDirectory(directory);
        var observation = await CompilerEvidence.BuildAsync(Assert.Single(loaded.BuildGraph), directory, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        snapshot.Validate(observation, TestContext.Current.CancellationToken);
        snapshot.VerifyUnchanged(TestContext.Current.CancellationToken);
        foreach (var (path, saved) in authored)
        {
            Assert.Equal(saved.Bytes, File.ReadAllBytes(path));
            Assert.Equal(saved.Mtime, File.GetLastWriteTimeUtc(path));
        }
    }
}
