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

    private async Task<ProcessResult> Git(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, temporary, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    private void Write(string relative, string text) => File.WriteAllText(Path.Combine(temporary, relative), text);
}
