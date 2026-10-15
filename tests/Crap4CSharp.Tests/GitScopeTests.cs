using Crap4CSharp.Core;
using Xunit;

namespace Crap4CSharp.Tests;

public sealed class GitScopeTests : IDisposable
{
    private readonly TestDirectory fixture = TestDirectory.Create("crap4csharp-scope-tests");
    private string temporary => fixture.Path;

    public void Dispose() => fixture.Dispose();

    [Fact]
    public async Task ConvertedCheckoutDoesNotWidenScopeAndStillDetectsSameTimestampConfigurationEdits()
    {
        var token = TestContext.Current.CancellationToken;
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        await Git("config", "core.autocrlf", "true");
        Write("Code.cs", "class C {\n int M() => 42;\n}\n");
        Write("Project.csproj", "<Project>\n<PropertyGroup><Value>42</Value></PropertyGroup>\n</Project>\n");
        await Git("add", "."); await Git("commit", "--quiet", "-m", "base");
        File.Delete(Path.Combine(temporary, "Code.cs"));
        File.Delete(Path.Combine(temporary, "Project.csproj"));
        await Git("checkout-index", "--all", "--force");
        var index = File.ReadAllBytes(Path.Combine(temporary, ".git", "index"));
        var request = new GitScopeRequest(ChangeScopeMode.Worktree);
        var clean = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(30), token);
        Assert.Equal(ScopeCompleteness.Complete, clean.Completeness);
        Assert.All(clean.Files, file => { Assert.Empty(file.AddedRanges); Assert.Empty(file.DeletedRanges); });
        var project = Path.Combine(temporary, "Project.csproj");
        var timestamp = File.GetLastWriteTimeUtc(project);
        File.WriteAllText(project, File.ReadAllText(project).Replace("42", "43", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(project, timestamp);
        var changed = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(30), token);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, changed.Completeness);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(temporary, ".git", "index")));
        await Git("update-index", "--skip-worktree", "Project.csproj");
        var hidden = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(request, temporary,
            TimeSpan.FromSeconds(30), token));
        Assert.Equal("scope.hiddenIndexEntry", hidden.Reason);
    }

    [Fact]
    public async Task LargerOpaqueQueryPreservesOriginalProtocolAllowanceAndCompleteGroups()
    {
        var paths = Enumerable.Range(0, 48).Select(i => "input" + i).ToArray();
        var calls = new List<string[]>();
        var consumed = new List<string>();
        await GitScopeResolver.ReadOpaqueTreeBatchesAsync(paths, (batch, token) =>
        {
            token.ThrowIfCancellationRequested(); calls.Add(batch.ToArray());
            if (batch.Count > 16) throw new ScopeException("scope.gitProtocolLimit", "bounded combined response");
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', batch)));
        }, bytes => consumed.AddRange(System.Text.Encoding.UTF8.GetString(bytes).Split('\n')),
            TestContext.Current.CancellationToken);
        Assert.Equal(paths, calls[0]);
        Assert.Equal(4, calls.Count);
        Assert.Equal(paths, calls.Skip(1).SelectMany(c => c));
        Assert.Equal(paths, consumed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpaqueQueryCancellationAndOtherFailuresNeverRetryOrConsume(bool cancellation)
    {
        var calls = 0; var consumed = false;
        Task Run() => GitScopeResolver.ReadOpaqueTreeBatchesAsync(Enumerable.Range(0, 48).Select(i => "input" + i).ToArray(),
            (_, _) =>
            {
                calls++;
                if (cancellation) throw new OperationCanceledException();
                throw new ScopeException("scope.gitFailed", "actual Git failure");
            }, _ => consumed = true, TestContext.Current.CancellationToken);
        if (cancellation) await Assert.ThrowsAsync<OperationCanceledException>(Run);
        else Assert.Equal("scope.gitFailed", (await Assert.ThrowsAsync<ScopeException>(Run)).Reason);
        Assert.Equal(1, calls); Assert.False(consumed);
    }

    [Fact]
    public async Task CancelledFallbackResponseCannotBecomeAcceptedOpaqueEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0; var consumed = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => GitScopeResolver.ReadOpaqueTreeBatchesAsync(
            Enumerable.Range(0, 48).Select(i => "input" + i).ToArray(), (batch, _) =>
            {
                calls++;
                if (batch.Count > 16) throw new ScopeException("scope.gitProtocolLimit", "combined response");
                cancellation.Cancel();
                return Task.FromResult(Array.Empty<byte>());
            }, _ => consumed = true, cancellation.Token));
        Assert.Equal(2, calls); Assert.False(consumed);
    }

    [Fact]
    public async Task ManyBundlePathsCannotHideADeletedBaselineGitlink()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Readme.txt", "base");
        await Git("add", ".");
        await Git("commit", "--quiet", "-m", "base");
        var head = (await Git("rev-parse", "HEAD")).StandardOutput.Trim();
        await Git("update-index", "--add", "--cacheinfo", "160000", head, "nested");
        await Git("commit", "--quiet", "-m", "baseline gitlink");
        await Git("update-index", "--force-remove", "nested");
        for (var i = 0; i < 192; i++) Write($"bundle/inputs/{i:x64}.bin", "owned fixture");
        var index = File.ReadAllBytes(Path.Combine(temporary, ".git", "index"));
        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, "HEAD"),
            temporary, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(scope.Files);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, scope.Completeness);
        Assert.Contains("scope.changedDirectoryOrGitlink:nested", scope.Diagnostics);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(temporary, ".git", "index")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedStatlessIndexRejectsLengthAndPreservedTimestampByteChanges(bool changeLength)
    {
        await Git("init", "--quiet");
        Write("Code.cs", "class C {}\n");
        await Git("add", "--", "Code.cs");
        var opening = await Git("ls-files", "--stage", "-z", "--");
        using var owned = await GitScopeResolver.GitScratchIndex.CreateAsync(temporary,
            System.Text.Encoding.UTF8.GetBytes(opening.StandardOutput), TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        owned.Validate(TestContext.Current.CancellationToken);
        var bytes = File.ReadAllBytes(owned.IndexPath);
        var timestamp = File.GetLastWriteTimeUtc(owned.IndexPath);
        if (changeLength) bytes = [.. bytes, 0];
        else bytes[^1] ^= 1;
        File.WriteAllBytes(owned.IndexPath, bytes);
        File.SetLastWriteTimeUtc(owned.IndexPath, timestamp);
        var error = Assert.Throws<ScopeException>(() => owned.Validate(TestContext.Current.CancellationToken));
        Assert.Equal("scope.changedDuringCapture", error.Reason);
    }

    [Fact]
    public async Task OwnedStatlessIndexStaysStatlessAcrossIndependentDiffsAndHonorsCancellation()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 1; }\n");
        await Git("add", ".");
        await Git("commit", "--quiet", "-m", "base");
        var consumerIndex = File.ReadAllBytes(Path.Combine(temporary, ".git", "index"));
        var opening = await Git("ls-files", "--stage", "-z", "--");
        string ownedPath;
        using (var owned = await GitScopeResolver.GitScratchIndex.CreateAsync(temporary,
            System.Text.Encoding.UTF8.GetBytes(opening.StandardOutput), TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken))
        {
            ownedPath = owned.IndexPath;
            for (var observation = 0; observation < 2; observation++)
            {
                var result = await ProcessRunner.RunWithEnvironmentAsync("git",
                    ["--no-optional-locks", "diff-index", "--name-status", "-z", "HEAD", "--"], temporary,
                    TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken, owned.Environment);
                Assert.Equal(0, result.ExitCode);
                owned.Validate(TestContext.Current.CancellationToken);
                var source = Path.Combine(temporary, "Code.cs");
                var time = File.GetLastWriteTimeUtc(source);
                Write("Code.cs", "class C { int M() => 2; }\n");
                File.SetLastWriteTimeUtc(source, time);
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => owned.Validate(cancelled.Token));
        }
        Assert.False(File.Exists(ownedPath));
        Assert.Equal(consumerIndex, File.ReadAllBytes(Path.Combine(temporary, ".git", "index")));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task LargeStatlessScopeRetainsRawSourceAndFilteredInputIdentity(int fileCount)
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        await Git("config", "core.autocrlf", "false");
        for (var index = 0; index < fileCount; index++)
            Write($"Source/File{index:D5}.cs", $"class C{index} {{ int M() => 42; }}\n");
        for (var index = 0; index < 64; index++) Write($"Config/Input{index:D3}.props", "<Project />\n");
        await GitWithTimeout(TimeSpan.FromSeconds(180), "add", ".");
        await GitWithTimeout(TimeSpan.FromSeconds(180), "commit", "--quiet", "-m", "base");
        var indexPath = Path.Combine(temporary, ".git", "index");
        var originalIndex = File.ReadAllBytes(indexPath);
        var request = new GitScopeRequest(ChangeScopeMode.Base, "HEAD");
        var token = TestContext.Current.CancellationToken;
        var clean = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(180), token);
        Assert.Empty(clean.Files);
        Assert.Equal(ScopeCompleteness.Complete, clean.Completeness);

        var sourcePath = "Source/File00000.cs";
        var physical = Path.Combine(temporary, sourcePath);
        var timestamp = File.GetLastWriteTimeUtc(physical);
        Write(sourcePath, "class C0 { int M() => 43; }\n");
        File.SetLastWriteTimeUtc(physical, timestamp);
        var changed = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(180), token);
        var file = Assert.Single(changed.Files);
        Assert.Equal(sourcePath, file.NewPath);
        Assert.Equal("class C0 { int M() => 42; }\n", file.OldSource!.Text);
        Assert.Equal("class C0 { int M() => 43; }\n", file.NewSource!.Text);
        Assert.NotEqual(file.OldIdentity, file.NewIdentity);
        Assert.Equal(ScopeCompleteness.Complete, changed.Completeness);

        var props = Path.Combine(temporary, "Config/Input000.props");
        var propsTimestamp = File.GetLastWriteTimeUtc(props);
        Write("Config/Input000.props", "<Project/> \n");
        File.SetLastWriteTimeUtc(props, propsTimestamp);
        var filtered = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(180), token);
        Assert.Single(filtered.Files);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, filtered.Completeness);
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public async Task BatchFramingDoesNotLowerTheExistingPerBlobProtocolAllowance()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        await Git("config", "core.autocrlf", "false");
        Write("Large A.cs", "//" + new string('x', 9 * 1024 * 1024));
        Write("Large B.cs", "//" + new string('y', 9 * 1024 * 1024));
        await Git("add", ".");
        await Git("commit", "--quiet", "-m", "base");
        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree), temporary,
            TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);
        Assert.Empty(scope.Files);
        Assert.Equal(ScopeCompleteness.Complete, scope.Completeness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedFileReadsKeepRecordOrderWhenLaterUntrackedInputFailsFirst(bool firstLineMap)
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("A.cs", "class A { }");
        await Git("add", "--", "A.cs");
        await Git("commit", "--quiet", "-m", "base");
        var indexPath = Path.Combine(temporary, ".git", "index");
        var originalIndex = File.ReadAllBytes(indexPath);
        Write("A.cs", firstLineMap ? "// separator\u2028class A { }" : "class A { }\0");
        Write("Z.cs", firstLineMap ? "class Z { }\0" : "// separator\u2028class Z { }");
        var token = TestContext.Current.CancellationToken;
        if (firstLineMap)
        {
            var error = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
                new GitScopeRequest(ChangeScopeMode.Worktree), temporary, TimeSpan.FromSeconds(30), token));
            Assert.Equal("scope.lineMapIncompatible", error.Reason);
            Assert.Contains("A.cs", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => GitScopeResolver.CaptureAsync(
                new GitScopeRequest(ChangeScopeMode.Worktree), temporary, TimeSpan.FromSeconds(30), token));
            Assert.Contains("A.cs", error.Message);
        }
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentOpeningFailuresKeepOriginalRefusalPrecedence(bool unborn)
    {
        await Git("init", "--quiet");
        if (!unborn)
        {
            await Git("config", "user.email", "scope@example.invalid");
            await Git("config", "user.name", "Scope Test");
            Write("Code.cs", "class C { int M() => 1; }");
            await Git("add", "Code.cs");
            await Git("commit", "--quiet", "-m", "base");
            await Git("update-index", "--skip-worktree", "Code.cs");
        }
        // The missing base fails concurrently with an earlier HEAD/hidden-index
        // refusal. Read scheduling must not change the diagnostic or index.
        var indexPath = Path.Combine(temporary, ".git", "index");
        var originalIndex = File.Exists(indexPath) ? File.ReadAllBytes(indexPath) : null;
        var error = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, "missing-base"), temporary,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(unborn ? "scope.unbornHead" : "scope.hiddenIndexEntry", error.Reason);
        if (originalIndex is null) Assert.False(File.Exists(indexPath));
        else Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrDeletedGitlinkWidensScopeWithoutHashingItsDirectory(bool deleted)
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 1; }");
        await Git("add", "Code.cs");
        var module = Path.Combine(temporary, "Module");
        Directory.CreateDirectory(module);
        async Task<string> ModuleGit(params string[] arguments)
        {
            var result = await ProcessRunner.RunAsync("git", arguments, module, TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, result.StandardError);
            return result.StandardOutput.Trim();
        }
        await ModuleGit("init", "--quiet");
        await ModuleGit("config", "user.email", "module@example.invalid");
        await ModuleGit("config", "user.name", "Module Test");
        Write("Module/Code.cs", "class Dependency { int M() => 1; }");
        await ModuleGit("add", "Code.cs");
        await ModuleGit("commit", "--quiet", "-m", "module base");
        var moduleHead = await ModuleGit("rev-parse", "HEAD");
        await Git("update-index", "--add", "--cacheinfo", "160000," + moduleHead + ",Module");
        await Git("commit", "--quiet", "-m", "base with gitlink");
        if (deleted) await Git("rm", "--cached", "Module");
        else
        {
            Write("Module/Code.cs", "class Dependency { int M() => 2; }");
            await ModuleGit("add", "Code.cs");
            await ModuleGit("commit", "--quiet", "-m", "module change");
        }
        var index = Path.Combine(temporary, ".git", "index");
        var before = File.ReadAllBytes(index);
        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, "HEAD"),
            temporary, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(scope.Files);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, scope.Completeness);
        Assert.Contains("scope.changedDirectoryOrGitlink:Module", scope.Diagnostics);
        Assert.Equal(before, File.ReadAllBytes(index));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatlessWorktreeComparisonDistinguishesCleanInputsFromPreservedTimestampEdits(bool baseScope)
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("Code.cs", "class C { int M() => 42; }");
        Write("App.csproj", "<Project />");
        Write("policy.json", "{}");
        Write("Tests/Tests.cs", "class Tests {}");
        await Git("add", ".");
        await Git("commit", "--quiet", "-m", "base");
        var indexPath = Path.Combine(temporary, ".git", "index");
        var originalIndex = File.ReadAllBytes(indexPath);
        var request = new GitScopeRequest(baseScope ? ChangeScopeMode.Base : ChangeScopeMode.Worktree,
            baseScope ? "HEAD" : null);
        var clean = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Empty(clean.Files);
        Assert.Equal(ScopeCompleteness.Complete, clean.Completeness);
        Assert.DoesNotContain(clean.Diagnostics, d => d.StartsWith("scope.unclassifiedChangedInput:", StringComparison.Ordinal));
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));

        var source = Path.Combine(temporary, "Code.cs");
        var timestamp = File.GetLastWriteTimeUtc(source);
        File.WriteAllText(source, "class C { int M() => 43; }");
        File.SetLastWriteTimeUtc(source, timestamp);
        var changed = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal("Code.cs", Assert.Single(changed.Files).NewPath);
        Assert.Equal(ScopeCompleteness.Complete, changed.Completeness);
        Assert.DoesNotContain(changed.Diagnostics, d => d.StartsWith("scope.unclassifiedChangedInput:", StringComparison.Ordinal));
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));

        var project = Path.Combine(temporary, "App.csproj");
        var projectTimestamp = File.GetLastWriteTimeUtc(project);
        var projectLength = new FileInfo(project).Length;
        File.WriteAllText(project, "<Project/> ");
        Assert.Equal(projectLength, new FileInfo(project).Length);
        File.SetLastWriteTimeUtc(project, projectTimestamp);
        var contextChanged = await GitScopeResolver.CaptureAsync(request, temporary, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        Assert.Equal("Code.cs", Assert.Single(contextChanged.Files).NewPath);
        Assert.Equal(ScopeCompleteness.ContextIncomplete, contextChanged.Completeness);
        Assert.Equal(new[] { "scope.unclassifiedChangedInput:App.csproj" },
            contextChanged.Diagnostics.Where(d => d.StartsWith("scope.unclassifiedChangedInput:", StringComparison.Ordinal)));
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public async Task StagedGitBlobBeyondProtocolLimitRefusesRatherThanRetainingUnboundedResponse()
    {
        await Git("init", "--quiet");
        Write("Oversized.cs", "//" + new string('x', 16777216));
        await Git("add", "--", "Oversized.cs");
        var failure = await Assert.ThrowsAsync<ScopeException>(() => GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Staged), temporary, TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken));
        Assert.Equal("scope.gitProtocolLimit", failure.Reason);
        Assert.Contains("16777216", failure.Message);
    }

    [Fact]
    public async Task UnbornStagedGitScopePreservesExactUtf8BytesAndUnusualPath()
    {
        await Git("init", "--quiet");
        string name = OperatingSystem.IsWindows() ? "unicode space é.cs" : "unicode space é\nfile.cs";
        const string source = "// café 😀\nclass C { string M() => \"é\"; }\n";
        Write(name, source);
        await Git("add", "--", name);
        var scope = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Staged),
            temporary, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var file = Assert.Single(scope.Files);
        Assert.Equal(name, file.NewPath);
        Assert.Equal(source, file.NewSource!.Text);
    }

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
    public async Task WorktreeIncludesUntrackedUnicodeAndSpacePathWithExactBytesOnAllPlatforms()
    {
        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("seed.txt", "seed");
        await Git("add", "--", "seed.txt");
        await Git("commit", "--quiet", "-m", "base");
        const string path = "unicodé name part.cs";
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
    public async Task WorktreeIncludesUntrackedNewlinePathWithExactBytesWhenFilesystemSupportsIt()
    {
        // Windows forbids control characters in file names, so this Git -z parsing case is Unix-only.
        if (OperatingSystem.IsWindows())
            return;

        await Git("init", "--quiet");
        await Git("config", "user.email", "scope@example.invalid");
        await Git("config", "user.name", "Scope Test");
        Write("seed.txt", "seed");
        await Git("add", "--", "seed.txt");
        await Git("commit", "--quiet", "-m", "base");
        const string path = "name\npart.cs";
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
        fixture.DeleteFile(Path.Combine(".git", "objects", head[..2], head[2..]));

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

    [Fact]
    public void OwnedFixtureCleanupDeletesReadOnlyLooseObjectsAndSentinels()
    {
        var owned = TestDirectory.Create("crap4csharp-cleanup-contract");
        var looseObject = owned.Write(Path.Combine(".git", "objects", "aa", "object"), "object");
        var sentinel = owned.Write("readonly.txt", "sentinel");
        File.SetAttributes(looseObject, File.GetAttributes(looseObject) | FileAttributes.ReadOnly);
        File.SetAttributes(sentinel, File.GetAttributes(sentinel) | FileAttributes.ReadOnly);
        var root = owned.Path;

        owned.Dispose();

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void OwnedFixtureCleanupDoesNotFollowLinksOutsideRoot()
    {
        if (OperatingSystem.IsWindows()) return;

        using var outside = TestDirectory.Create("crap4csharp-cleanup-outside");
        var target = outside.Write("target.txt", "outside");
        var targetAttributes = File.GetAttributes(target);
        var owned = TestDirectory.Create("crap4csharp-cleanup-contract");
        File.CreateSymbolicLink(Path.Combine(owned.Path, "outside-link"), target);
        var root = owned.Path;

        owned.Dispose();

        Assert.False(Directory.Exists(root));
        Assert.Equal("outside", File.ReadAllText(target));
        Assert.Equal(targetAttributes, File.GetAttributes(target));
    }

    private async Task<ProcessResult> Git(params string[] arguments)
        => await GitWithTimeout(TimeSpan.FromSeconds(10), arguments);

    private async Task<ProcessResult> GitWithTimeout(TimeSpan timeout, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, temporary, timeout, TestContext.Current.CancellationToken);
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
