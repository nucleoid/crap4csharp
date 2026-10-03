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
        Assert.Equal(ProjectAnalysisContext.ContentHash(System.Text.Encoding.UTF8.GetBytes(file.NewSource.Text)),
            file.NewIdentity);
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

        var error = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
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

    [Fact]
    public async Task GitAttributesCannotHideChangedTextHunks()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write(".gitattributes", "*.cs -diff\n");
        Write("Code.cs", "class C {\n int Before() => 1;\n}\n");
        await Git("add", "--", ".gitattributes", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        Write("Code.cs", "class C {\n int After() => 2;\n}\n");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.NotEmpty(file.AddedRanges);
        Assert.NotEmpty(file.DeletedRanges);
    }

    [Fact]
    public async Task TestSourcesAreExcludedAndProjectChangesMakeCompletenessTyped()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("src/Code.cs", "class C { int M() => 1; }");
        Write("tests/TestCode.cs", "class T { int Test() => 1; }");
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await Git("add", "--", ".");
        await Git("commit", "--quiet", "-m", "base");
        Write("src/Code.cs", "class C { int M() => 2; }");
        Write("tests/TestCode.cs", "class T { int Test() => 2; }");
        Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("src/Code.cs", Assert.Single(scope.Files).NewPath);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, scope.Completeness);
        Assert.Contains("scope.contextIncomplete", scope.Diagnostics);
    }

    [Fact]
    public async Task DocumentationAndTestChangesDoNotMakeProductionScopeIncomplete()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("src/Code.cs", "class C { int M() => 1; }");
        Write("tests/TestCode.cs", "class T { int Test() => 1; }");
        Write("README.md", "before\n");
        await Git("add", "--", ".");
        await Git("commit", "--quiet", "-m", "base");
        Write("src/Code.cs", "class C { int M() => 2; }");
        Write("tests/TestCode.cs", "class T { int Test() => 2; }");
        Write("README.md", "after\n");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(ScopeCompleteness.Complete, scope.Completeness);
        Assert.Contains("scope.excludedTestSource:tests/TestCode.cs", scope.Diagnostics);
        Assert.Contains("scope.unclassifiedChangedInput:README.md", scope.Diagnostics);
        Assert.DoesNotContain("scope.contextIncomplete", scope.Diagnostics);
    }

    [Fact]
    public async Task RenameFromExcludedTestDirectoryBecomesProductionAddition()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("tests/Helper.cs", "class Helper { int M() => 1; }\n");
        await Git("add", "--", ".");
        await Git("commit", "--quiet", "-m", "base");
        var @base = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        Directory.CreateDirectory(Path.Combine(temporary, "src"));
        File.Move(Path.Combine(temporary, "tests", "Helper.cs"), Path.Combine(temporary, "src", "Helper.cs"));
        await Git("add", "--", ".");
        await Git("commit", "--quiet", "-m", "move to production");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();

        var scope = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, @base, head, ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.Equal(ScopeChangeKind.Added, file.Kind);
        Assert.Null(file.OldPath);
        var methods = new SourceAnalyzer().AnalyzeCaptured(file.NewPath!, file.NewSource!.Bytes);
        Assert.Equal("M", Assert.Single(ChangedMethodSelector.Select(file, [], methods, ScopeGranularity.Method).Methods).MethodName);
    }

    [Fact]
    public async Task CopyFromModifiedProductionFileIntoTestsDoesNotDeleteProductionSource()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("src/A.cs", "class A { int M() => 1; }\n");
        await Git("add", "--", ".");
        await Git("commit", "--quiet", "-m", "base");
        var @base = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        Write("src/A.cs", "class A { int M() => 2; }\n");
        Write("tests/ACopy.cs", "class A { int M() => 1; }\n");
        await Git("add", "--", ".");
        await Git("commit", "--quiet", "-m", "modify and copy");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        var nameStatus = (await Git("diff", "--name-status", "--find-copies=50%", @base, head, "--")).StandardOutput;
        Assert.Contains("C100\tsrc/A.cs\ttests/ACopy.cs", nameStatus);

        var scope = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, @base, head, ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.Equal(ScopeChangeKind.Modified, file.Kind);
        Assert.Equal("src/A.cs", file.NewPath);
        Assert.DoesNotContain(scope.Files, item => item.Kind == ScopeChangeKind.Deleted);
    }

    [Fact]
    public async Task CommittedHeadCaptureAllowsSkipWorktreeEntries()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int Before() => 1; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        var @base = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        Write("Code.cs", "class C { int After() => 2; }");
        await Git("commit", "--quiet", "-am", "head");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        await Git("update-index", "--skip-worktree", "Code.cs");

        var scope = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, @base, head, ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains("After", Assert.Single(scope.Files).NewSource!.Text);
    }

    [Fact]
    public async Task MissingHeadObjectIsNotMisreportedAsUnborn()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 1; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        File.Delete(Path.Combine(temporary, ".git", "objects", head[..2], head[2..]));

        var error = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, "HEAD", "HEAD", ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Equal("scope.gitFailed", error.Reason);
    }

    [Fact]
    public void CapturedAndProjectContextContentIdentitiesUseTheSameFormat()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("class C { }");

        var captured = CapturedSource.Create("Code.cs", bytes);

        Assert.Equal(ProjectAnalysisContext.ContentHash(bytes), captured.ContentIdentity);
    }

    [Fact]
    public void StatOnlyCandidateIsStillReverifiedAfterInitialIdentityMatch()
    {
        Write("Code.cs", "class C { int M() => 1; }");
        var source = CapturedSource.Create("Code.cs", File.ReadAllBytes(Path.Combine(temporary, "Code.cs")));
        var candidate = new ChangedFile("Code.cs", "Code.cs", ScopeChangeKind.Modified,
            source.ContentIdentity, source.ContentIdentity, source, source, [], []);
        Write("Code.cs", "class C { int M() => 2; }");

        var error = Assert.Throws<ScopeException>(() =>
            GitScopeResolver.VerifyWorktreeBytes(temporary, [candidate]));

        Assert.Equal("scope.changedDuringCapture", error.Reason);
    }

    [Fact]
    public async Task UnbornWorktreeAndStagedScopesTreatSourcesAsAdditions()
    {
        await Git("init", "--quiet");
        Write("Staged.cs", "class Staged { int M() => 1; }");
        await Git("add", "--", "Staged.cs");
        Write("Untracked.cs", "class Untracked { int M() => 1; }");

        var staged = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Staged), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        var worktree = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal("Staged.cs", Assert.Single(staged.Files).NewPath);
        Assert.Equal(2, worktree.Files.Count);
        Assert.All(worktree.Files, file => Assert.Equal(ScopeChangeKind.Added, file.Kind));
    }

    [Fact]
    public async Task WorktreeUsesFinalNetContentWhileStagedKeepsIndexChange()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        const string original = "class C { int Base() => 0; }";
        Write("Code.cs", original);
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        Write("Code.cs", "class C { int Staged() => 1; }");
        await Git("add", "--", "Code.cs");
        Write("Code.cs", original);

        var staged = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Staged), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        var worktree = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains("Staged", Assert.Single(staged.Files).NewSource!.Text);
        Assert.Empty(worktree.Files);
        Assert.Equal(original, File.ReadAllText(Path.Combine(temporary, "Code.cs")));
        Assert.Contains("MM Code.cs", (await Git("status", "--short")).StandardOutput);
    }

    [Fact]
    public async Task DivergedBaseUsesRecordedMergeBaseAgainstSelectedHead()
    {
        await Git("init", "--quiet", "--initial-branch=main");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int Initial() => 0; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "initial");
        var initial = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        await Git("branch", "side");
        Write("Code.cs", "class C { int Main() => 1; }");
        await Git("commit", "--quiet", "-am", "main");
        var main = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        await Git("switch", "--quiet", "side");
        Write("Side.cs", "class Side { int M() => 1; }");
        await Git("add", "--", "Side.cs");
        await Git("commit", "--quiet", "-m", "side");
        var side = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();

        var scope = await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, side, main, ScopeSourceState.Head), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(side, scope.Revision.ResolvedBase);
        Assert.Equal(main, scope.Revision.ResolvedHead);
        Assert.Equal(initial, scope.Revision.MergeBase);
        Assert.Contains("Main", Assert.Single(scope.Files).NewSource!.Text);
    }

    [Fact]
    public async Task DetachedHeadWorksAndConflictedIndexFailsClosed()
    {
        await Git("init", "--quiet", "--initial-branch=main");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 0; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        await Git("checkout", "--quiet", "--detach", head);
        Write("Code.cs", "class C { int M() => 1; }");
        var detached = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(head, detached.Revision.CurrentHead);

        await Git("checkout", "--quiet", "-B", "main", head);
        await Git("checkout", "--quiet", "-b", "other");
        Write("Code.cs", "class C { int Other() => 2; }");
        await Git("commit", "--quiet", "-am", "other");
        await Git("checkout", "--quiet", "main");
        Write("Code.cs", "class C { int Main() => 3; }");
        await Git("commit", "--quiet", "-am", "main");
        var merge = await ProcessRunner.RunAsync("git", ["merge", "other"], temporary, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.NotEqual(0, merge.ExitCode);

        var error = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Worktree), temporary, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal("scope.conflictedIndex", error.Reason);
    }

    [Fact]
    public async Task UntrackedContextAndExcludedSourceMakeScopeIncomplete()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("seed.txt", "seed");
        await Git("add", "--", "seed.txt");
        await Git("commit", "--quiet", "-m", "base");
        Write("Directory.Build.props", "<Project />");
        Write("Generated.g.cs", "class Generated { int M() => 1; }");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Empty(scope.Files);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, scope.Completeness);
        Assert.Contains(scope.Diagnostics, item => item == "scope.contextIncomplete");
        Assert.Contains(scope.Diagnostics, item => item == "scope.excludedChangedSource:Generated.g.cs");
    }

    [Fact]
    public async Task CrLfIsNormalizedOnlyForHunksAndExactBytesRemainCaptured()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C {\n int Changed() => 1;\n int Unrelated() => 2;\n}\n");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        File.WriteAllText(Path.Combine(temporary, "Code.cs"),
            "class C {\r\n int Changed() => 3;\r\n int Unrelated() => 2;\r\n}\r\n");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.Contains("\r\n", file.NewSource!.Text);
        var oldMethods = new SourceAnalyzer().AnalyzeCaptured(file.OldPath!, file.OldSource!.Bytes);
        var newMethods = new SourceAnalyzer().AnalyzeCaptured(file.NewPath!, file.NewSource.Bytes);
        Assert.Equal("Changed", Assert.Single(ChangedMethodSelector.Select(file, oldMethods, newMethods, ScopeGranularity.Method).Methods).MethodName);
    }

    [Fact]
    public async Task LineEndingOnlyChangeHasNoExecutableRangesAndIsDiagnosed()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C {\n int M() => 1;\n}\n");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        File.WriteAllText(Path.Combine(temporary, "Code.cs"), "class C {\r\n int M() => 1;\r\n}\r\n");

        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        var file = Assert.Single(scope.Files);
        Assert.Empty(file.AddedRanges);
        Assert.Empty(file.DeletedRanges);
        Assert.Contains("scope.lineEndingOnly:Code.cs", scope.Diagnostics);
        Assert.Equal(ScopeCompleteness.Complete, scope.Completeness);
    }

    [Fact]
    public async Task WorktreeCaptureDoesNotRewriteConsumerIndex()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 1; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        var sourcePath = Path.Combine(temporary, "Code.cs");
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(5));
        var indexPath = Path.Combine(temporary, ".git", "index");
        var indexBytes = await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken);
        var indexWriteTime = File.GetLastWriteTimeUtc(indexPath);

        await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(indexBytes, await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken));
        Assert.Equal(indexWriteTime, File.GetLastWriteTimeUtc(indexPath));
    }

    [Fact]
    public async Task IncompatibleLineSeparatorAndHiddenIndexEntryFailClosed()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 1; }");
        await Git("add", "--", "Code.cs");
        await Git("commit", "--quiet", "-m", "base");
        Write("Code.cs", "// separator\u2028class C { int M() => 2; }");
        var lineError = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Worktree), temporary, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal("scope.lineMapIncompatible", lineError.Reason);

        Write("Code.cs", "class C { int M() => 1; }");
        await Git("update-index", "--skip-worktree", "Code.cs");
        var hiddenError = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Worktree), temporary, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal("scope.hiddenIndexEntry", hiddenError.Reason);
    }

    private async Task<ProcessResult> Git(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, temporary, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result;
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(temporary, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
