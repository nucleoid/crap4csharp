using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class GitScopeTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "crap4csharp-scope-tests", Guid.NewGuid().ToString("N"));

    public GitScopeTests() => Directory.CreateDirectory(temporary);
    public void Dispose() => Directory.Delete(temporary, recursive: true);

    [Fact]
    public async Task BaseHeadCapturesCommittedBytesAndIgnoresDirtyWorktree()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int Before() => 1; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        var @base = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        Write("Code.cs", "class C { int Committed() => 2; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "head");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        Write("Code.cs", "class C { int Dirty() => 3; }");

        var scope = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, @base, head, ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(@base, scope.Revision.MergeBase);
        Assert.Equal(head, scope.Revision.ResolvedHead);
        var file = Assert.Single(scope.Files);
        Assert.Contains("Committed", file.NewSource!.Text);
        Assert.DoesNotContain("Dirty", file.NewSource.Text);
    }

    [Fact]
    public async Task StagedAndWorktreeCaptureDifferentExactBytes()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int Base() => 0; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        Write("Code.cs", "class C { int Staged() => 1; }");
        await Git("add", "--", "Code.cs");
        Write("Code.cs", "class C { int Worktree() => 2; }");

        var staged = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Staged), temporary, TimeSpan.FromSeconds(10), CancellationToken.None);
        var worktree = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Worktree), temporary, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains("Staged", Assert.Single(staged.Files).NewSource!.Text);
        Assert.Contains("Worktree", Assert.Single(worktree.Files).NewSource!.Text);
    }

    [Fact]
    public void FutureCheckContractRejectsUnsupportedSnapshotBeforeConsumerLoad()
    {
        var loaded = false;

        var result = ScopeExecutionContract.ValidateBeforeConsumerLoad(ChangeScopeMode.Staged, ScopeSourceState.Worktree,
            () => loaded = true);

        Assert.Equal("scope.executionSnapshotUnsupported", result);
        Assert.False(loaded);
    }

    [Fact]
    public async Task EmptyScoringScopeStillRunsRequiredFutureChecks()
    {
        var calls = 0;

        var checks = await ScopeExecutionContract.RunRequiredChecksBeforeScoringAsync(0, () =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<CheckResult>>(
                [new CheckResult("testExecution", "operationalError", "tests.failed", true)]);
        });

        Assert.Equal(1, calls);
        Assert.Equal("tests.failed", Assert.Single(checks).Reason);
    }

    [Fact]
    public async Task WorktreeIncludesUntrackedUnicodeAndNewlinePathWithExactBytes()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("seed.txt", "seed");
        await Git("add", "--", "seed.txt");
        await Git("commit", "--quiet", "-m", "base");
        const string path = "unicodé name\npart.cs";
        Write(path, "class Exact { int M() => 1; }\n");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.Equal(path, file.NewPath);
        Assert.Equal("class Exact { int M() => 1; }\n", file.NewSource!.Text);
        Assert.StartsWith("sha256:", file.NewIdentity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBaseFailsOperationallyInsteadOfReturningEmptyScope()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 1; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, "missing-ref", "HEAD", ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Contains("scope.invalidRef", error.Message);
    }

    [Fact]
    public async Task PureRenameRetainsComparableContentAndDistinctBlobIdentity()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Old.cs", "class C { int Same() => 1; }\n");
        await Git("add", "--", "Old.cs");
        await Git("commit", "--quiet", "-m", "base");
        File.Move(Path.Combine(temporary, "Old.cs"), Path.Combine(temporary, "New.cs"));

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.Equal(ScopeChangeKind.Renamed, file.Kind);
        Assert.Equal(file.OldIdentity, file.NewIdentity);
        Assert.NotNull(file.OldObjectIdentity);
        var oldMethods = new SourceAnalyzer().AnalyzeCaptured(file.OldPath!, file.OldSource!.Bytes);
        var newMethods = new SourceAnalyzer().AnalyzeCaptured(file.NewPath!, file.NewSource!.Bytes);
        Assert.Empty(ChangedMethodSelector.Select(file, oldMethods, newMethods, ScopeGranularity.Method).Methods);
    }

    private async Task<ProcessResult> Git(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, temporary, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(temporary, relative), text);
}
