using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Crap4CSharp.Core;

internal static partial class GitScopeResolver
{
    private static readonly string[] SafeGitPrefix =
        ["--no-optional-locks", "-c", "core.quotepath=false", "-c", "diff.external=", "-c", "diff.renameLimit=0",
         "-c", "diff.algorithm=myers", "-c", "diff.indentHeuristic=false"];

    public static async Task<CapturedChangeScope> CaptureAsync(GitScopeRequest request, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (request.Mode is ChangeScopeMode.All)
            throw new ArgumentException("All scope does not use Git capture.");
        if (request.Mode is not ChangeScopeMode.Base && (request.BaseRef is not null || request.HeadRef is not null || request.SourceState == ScopeSourceState.Head))
            throw new ArgumentException("Base/head/source-state selectors are valid only for base scope.");
        if (request.Mode == ChangeScopeMode.Base && string.IsNullOrWhiteSpace(request.BaseRef))
            throw new ArgumentException("Base scope requires an explicit base ref.");

        var rootResult = await Git(workingDirectory, timeout, cancellationToken, ["rev-parse", "--show-toplevel"], allowFailure: true);
        if (rootResult.ExitCode != 0) throw new ScopeException("scope.notGitRepository", "the working directory is not inside a Git repository.");
        var root = Text(rootResult).TrimEnd('\r', '\n');
        if (root.Length == 0) throw new ScopeException("scope.notGitRepository", "git returned an empty repository root.");

        var capturesWorktree = request.Mode == ChangeScopeMode.Worktree ||
            request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Worktree;
        var currentHead = await ResolveCurrentHeadOrUnborn(root, timeout, cancellationToken);
        if (request.Mode == ChangeScopeMode.Base && currentHead is null)
            throw new ScopeException("scope.unbornHead", "base scope requires an existing HEAD commit.");
        var indexBefore = await IndexIdentity(root, timeout, cancellationToken);
        await EnsureNoConflicts(root, timeout, cancellationToken);
        if (capturesWorktree)
            await EnsureNoHiddenIndexEntries(root, timeout, cancellationToken);

        string? resolvedBase = null;
        string? resolvedHead = currentHead;
        string? mergeBase = null;
        if (request.Mode == ChangeScopeMode.Base)
        {
            resolvedBase = await ResolveCommit(root, request.BaseRef!, timeout, cancellationToken);
            resolvedHead = await ResolveCommit(root, request.HeadRef ?? "HEAD", timeout, cancellationToken);
            var mergeResult = await Git(root, timeout, cancellationToken,
                ["merge-base", "--all", resolvedBase, resolvedHead], allowFailure: true);
            var mergeBases = mergeResult.ExitCode == 0 ? NullOrLineValues(mergeResult) : [];
            if (mergeBases.Count == 0)
                throw new ScopeException("scope.noMergeBase", "no merge base is available; history may be shallow or unrelated.");
            if (mergeBases.Count != 1)
                throw new ScopeException("scope.mergeBaseAmbiguous", $"expected one merge base but found {mergeBases.Count}.");
            mergeBase = mergeBases[0];
            if (request.SourceState == ScopeSourceState.Worktree && resolvedHead != currentHead)
                throw new ScopeException("scope.nonCurrentHeadWorktree", "worktree source state requires head to resolve to current HEAD.");
        }

        var emptyTree = currentHead is null ? Text(await Git(root, timeout, cancellationToken,
            ["hash-object", "-t", "tree", "--stdin"], [])).Trim() : null;
        var baseline = request.Mode == ChangeScopeMode.Base ? mergeBase! : currentHead ?? emptyTree!;
        var records = await ReadChanges(request, root, baseline, resolvedHead, timeout, cancellationToken);
        var files = new List<ChangedFile>();
        var verificationFiles = new List<ChangedFile>();
        foreach (var record in records.Where(record => Eligible(record.OldPath) || Eligible(record.NewPath)))
        {
            var captured = await CaptureFile(request, root, baseline, resolvedHead, record, timeout, cancellationToken);
            verificationFiles.Add(captured);
            var file = NormalizeEligibility(captured);
            if (file.Kind == ScopeChangeKind.Modified && file.OldIdentity == file.NewIdentity) continue;
            files.Add(file);
        }
        files = CoalesceExactWorktreeRenames(files);

        if (capturesWorktree)
        {
            var recordsAfter = await ReadChanges(request, root, baseline, resolvedHead, timeout, cancellationToken);
            if (!records.SequenceEqual(recordsAfter))
                throw new ScopeException("scope.changedDuringCapture", "the set of worktree changes changed during capture.");
        }
        var currentHeadAfter = await ResolveCurrentHeadOrUnborn(root, timeout, cancellationToken);
        var indexAfter = await IndexIdentity(root, timeout, cancellationToken);
        if (currentHead != currentHeadAfter || indexBefore != indexAfter)
            throw new ScopeException("scope.changedDuringCapture", "HEAD or index changed during capture.");
        if (capturesWorktree)
            VerifyWorktreeBytes(root, verificationFiles);

        var diagnostics = new List<string>
        {
            "git.diff.noExternal=true", "git.diff.noTextconv=true", "git.diff.text=true", "git.diff.renames=50%",
            "git.diff.copies=50%", "git.diff.algorithm=myers", "git.diff.indentHeuristic=false"
        };
        var filteredPaths = records.SelectMany(record => new[] { record.OldPath, record.NewPath })
            .Where(path => path is not null && !Eligible(path)).Select(path => path!)
            .Distinct(PathComparer()).Order(PathComparer()).ToArray();
        var contextIncomplete = records.Any(record => ContextInput(record.OldPath) || ContextInput(record.NewPath)) ||
            filteredPaths.Any(BuildRelevantFilteredInput);
        foreach (var path in filteredPaths)
        {
            diagnostics.Add(path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? IsExcludedTestSource(path)
                    ? $"scope.excludedTestSource:{path}"
                    : $"scope.excludedChangedSource:{path}"
                : $"scope.unclassifiedChangedInput:{path}");
        }
        if (contextIncomplete)
            diagnostics.Add("scope.contextIncomplete");
        diagnostics.AddRange(files.Where(file => file.OldSource is not null && file.NewSource is not null &&
                file.OldIdentity != file.NewIdentity && file.AddedRanges.Count == 0 && file.DeletedRanges.Count == 0 &&
                NormalizeForDiff(file.OldSource.Text) == NormalizeForDiff(file.NewSource.Text))
            .Select(file => $"scope.lineEndingOnly:{file.NewPath ?? file.OldPath}"));
        diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToList();
        return new CapturedChangeScope(request.Mode, request.SourceState, root,
            new ScopeRevision(request.BaseRef, request.HeadRef ?? (request.Mode == ChangeScopeMode.Base ? "HEAD" : null),
                resolvedBase, resolvedHead, currentHead, mergeBase, indexBefore),
            files.OrderBy(file => file.NewPath ?? file.OldPath, PathComparer()).ToArray(), diagnostics,
            contextIncomplete ? ScopeCompleteness.ContextIncomplete : ScopeCompleteness.Complete);
    }

    private static async Task<IReadOnlyList<PathChange>> ReadChanges(GitScopeRequest request, string root, string baseline,
        string? resolvedHead, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var capturesWorktree = request.Mode == ChangeScopeMode.Worktree ||
            request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Worktree;
        var arguments = new List<string> { capturesWorktree ? "diff-index" : "diff", "--name-status", "-z", "--no-ext-diff", "--no-textconv", "--find-renames=50%", "--find-copies=50%" };
        if (request.Mode == ChangeScopeMode.Staged) arguments.Add("--cached");
        arguments.Add(baseline);
        if (request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Head) arguments.Add(resolvedHead!);
        arguments.Add("--");
        var changes = ParseNameStatus((await Git(root, timeout, cancellationToken, arguments)).Output);

        if (request.Mode == ChangeScopeMode.Worktree || request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Worktree)
        {
            var untracked = SplitNull((await Git(root, timeout, cancellationToken,
                ["ls-files", "--others", "--exclude-standard", "-z", "--"])).Output);
            changes.AddRange(untracked.Select(path => new PathChange(ScopeChangeKind.Added, null, path)));
        }
        return changes;
    }

    private static async Task<ChangedFile> CaptureFile(GitScopeRequest request, string root, string baseline,
        string? resolvedHead, PathChange record, TimeSpan timeout, CancellationToken cancellationToken)
    {
        CapturedSource? oldSource = null;
        CapturedSource? newSource = null;
        string? oldIdentity = null;
        string? newIdentity = null;
        string? oldObjectIdentity = null;
        string? newObjectIdentity = null;
        if (record.OldPath is not null && record.Kind != ScopeChangeKind.Added)
        {
            var bytes = (await Git(root, timeout, cancellationToken, ["show", $"{baseline}:{record.OldPath}"])).Output;
            oldSource = Capture(record.OldPath, bytes);
            EnsureCompatibleLineMap(oldSource);
            oldIdentity = oldSource.ContentIdentity;
            oldObjectIdentity = await BlobIdentity(root, $"{baseline}:{record.OldPath}", timeout, cancellationToken);
        }
        if (record.NewPath is not null && record.Kind != ScopeChangeKind.Deleted)
        {
            byte[] bytes;
            if (request.Mode == ChangeScopeMode.Staged)
            {
                bytes = (await Git(root, timeout, cancellationToken, ["show", $":{record.NewPath}"])).Output;
                newObjectIdentity = await BlobIdentity(root, $":{record.NewPath}", timeout, cancellationToken);
            }
            else if (request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Head)
            {
                bytes = (await Git(root, timeout, cancellationToken, ["show", $"{resolvedHead}:{record.NewPath}"])).Output;
                newObjectIdentity = await BlobIdentity(root, $"{resolvedHead}:{record.NewPath}", timeout, cancellationToken);
            }
            else
            {
                bytes = await File.ReadAllBytesAsync(Path.Combine(root, record.NewPath), cancellationToken);
            }
            newSource = Capture(record.NewPath, bytes);
            EnsureCompatibleLineMap(newSource);
            newIdentity = newSource.ContentIdentity;
        }

        var (added, deleted) = record.Kind is ScopeChangeKind.Added or ScopeChangeKind.Copied
            ? (newSource is null ? [] : new[] { WholeFile(newSource.Text) }, Array.Empty<LineRange>())
            : record.Kind == ScopeChangeKind.Deleted
                ? (Array.Empty<LineRange>(), oldSource is null ? [] : new[] { WholeFile(oldSource.Text) })
                : oldIdentity == newIdentity
                    ? (Array.Empty<LineRange>(), Array.Empty<LineRange>())
                : await ReadHunks(root, record, oldSource!, newSource!, timeout, cancellationToken);
        return new ChangedFile(record.OldPath, record.NewPath, record.Kind, oldIdentity, newIdentity,
            oldSource, newSource, added, deleted, oldObjectIdentity, newObjectIdentity);
    }

    private static async Task<(IReadOnlyList<LineRange> Added, IReadOnlyList<LineRange> Deleted)> ReadHunks(
        string root, PathChange record, CapturedSource oldSource, CapturedSource newSource,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (NormalizeForDiff(oldSource.Text) == NormalizeForDiff(newSource.Text))
            return ([], []);

        var temporary = Directory.CreateTempSubdirectory("crap4csharp-scope-");
        string patch;
        try
        {
            var oldPath = Path.Combine(temporary.FullName, "old.cs");
            var newPath = Path.Combine(temporary.FullName, "new.cs");
            await File.WriteAllTextAsync(oldPath, NormalizeForDiff(oldSource.Text), new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(newPath, NormalizeForDiff(newSource.Text), new UTF8Encoding(false), cancellationToken);
            var diff = await Git(root, timeout, cancellationToken,
                ["diff", "--no-index", "--unified=0", "--no-color", "--no-ext-diff", "--no-textconv", "--text", "--", oldPath, newPath],
                allowFailure: true);
            if (diff.ExitCode is not (0 or 1))
                throw new ScopeException("scope.gitFailed", $"git diff --no-index failed: {diff.Error.Trim()}");
            patch = Text(diff);
        }
        finally
        {
            try { temporary.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var added = new List<LineRange>();
        var deleted = new List<LineRange>();
        foreach (Match match in HunkRegex().Matches(patch))
        {
            AddRange(deleted, match.Groups[1].Value, match.Groups[2].Value);
            AddRange(added, match.Groups[3].Value, match.Groups[4].Value);
        }
        if (added.Count == 0 && deleted.Count == 0 && record.Kind is ScopeChangeKind.Modified or ScopeChangeKind.Renamed)
            throw new ScopeException("scope.missingHunks", $"content changed for {record.NewPath ?? record.OldPath} but Git produced no text hunks.");
        return (added, deleted);
    }

    private static void AddRange(List<LineRange> ranges, string startText, string countText)
    {
        var start = int.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
        var count = countText.Length == 0 ? 1 : int.Parse(countText, System.Globalization.CultureInfo.InvariantCulture);
        if (count > 0) ranges.Add(new LineRange(start, start + count - 1));
    }

    private static LineRange WholeFile(string text)
    {
        var lines = text.Length == 0 ? 0 : 1 + text.Count(character => character == '\n');
        return new LineRange(1, Math.Max(1, lines));
    }

    private static CapturedSource Capture(string path, byte[] bytes)
    {
        if (bytes.AsSpan().Contains((byte)0))
            throw new InvalidDataException($"scope.binarySourceUnsupported: {path} contains NUL bytes.");
        try { return CapturedSource.Create(path, bytes); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"scope.binarySourceUnsupported: {path} is not valid UTF-8.", exception);
        }
    }

    private static void EnsureCompatibleLineMap(CapturedSource source)
    {
        var text = source.Text;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n') ||
                character is '\u0085' or '\u2028' or '\u2029')
                throw new ScopeException("scope.lineMapIncompatible",
                    $"{source.LogicalPath} contains a line separator that Git and Roslyn number differently.");
        }
    }

    private static string NormalizeForDiff(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    internal static void VerifyWorktreeBytes(string root, IEnumerable<ChangedFile> files)
    {
        foreach (var file in files)
        {
            if (file.NewPath is null || file.NewSource is null) continue;
            var path = Path.Combine(root, file.NewPath);
            if (!File.Exists(path) || Capture(file.NewPath, File.ReadAllBytes(path)).ContentIdentity != file.NewSource.ContentIdentity)
                throw new ScopeException("scope.changedDuringCapture", $"{file.NewPath} changed during capture.");
        }
        foreach (var file in files.Where(file => file.Kind == ScopeChangeKind.Deleted && file.OldPath is not null))
        {
            if (File.Exists(Path.Combine(root, file.OldPath!)))
                throw new ScopeException("scope.changedDuringCapture", $"deleted path {file.OldPath} reappeared during capture.");
        }
    }

    private static List<ChangedFile> CoalesceExactWorktreeRenames(List<ChangedFile> files)
    {
        var consumed = new HashSet<ChangedFile>();
        var result = new List<ChangedFile>();
        foreach (var deleted in files.Where(file => file.Kind == ScopeChangeKind.Deleted))
        {
            var additions = files.Where(file => file.Kind == ScopeChangeKind.Added &&
                file.NewIdentity == deleted.OldIdentity).ToArray();
            var deletions = files.Where(file => file.Kind == ScopeChangeKind.Deleted &&
                file.OldIdentity == deleted.OldIdentity).ToArray();
            if (additions.Length != 1 || deletions.Length != 1) continue;
            var added = additions[0];
            consumed.Add(deleted);
            consumed.Add(added);
            result.Add(new ChangedFile(deleted.OldPath, added.NewPath, ScopeChangeKind.Renamed,
                deleted.OldIdentity, added.NewIdentity, deleted.OldSource, added.NewSource,
                [], [], deleted.OldObjectIdentity, added.NewObjectIdentity));
        }
        result.AddRange(files.Where(file => !consumed.Contains(file)));
        return result;
    }

    private static ChangedFile NormalizeEligibility(ChangedFile file)
    {
        var oldEligible = Eligible(file.OldPath);
        var newEligible = Eligible(file.NewPath);
        if (!oldEligible && newEligible && file.NewSource is not null)
            return new ChangedFile(null, file.NewPath, ScopeChangeKind.Added, null, file.NewIdentity,
                null, file.NewSource, [WholeFile(file.NewSource.Text)], [], null, file.NewObjectIdentity);
        if (oldEligible && !newEligible && file.OldSource is not null)
            return new ChangedFile(file.OldPath, null, ScopeChangeKind.Deleted, file.OldIdentity, null,
                file.OldSource, null, [], [WholeFile(file.OldSource.Text)], file.OldObjectIdentity, null);
        return file;
    }

    private static async Task EnsureNoConflicts(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var unmerged = await Git(root, timeout, cancellationToken, ["ls-files", "--unmerged", "-z", "--"]);
        if (unmerged.Output.Length > 0) throw new ScopeException("scope.conflictedIndex", "unmerged index entries are unsupported.");
    }

    private static async Task EnsureNoHiddenIndexEntries(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var entries = SplitNull((await Git(root, timeout, cancellationToken, ["ls-files", "-v", "-z", "--"])).Output);
        foreach (var entry in entries)
        {
            if (entry.Length < 3 || entry[1] != ' ') continue;
            var path = entry[2..];
            if ((entry[0] == 'S' || char.IsLower(entry[0])) && (Eligible(path) || ContextInput(path)))
                throw new ScopeException("scope.hiddenIndexEntry", $"{path} is marked skip-worktree or assume-unchanged.");
        }
    }

    private static async Task<string> IndexIdentity(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bytes = (await Git(root, timeout, cancellationToken, ["ls-files", "--stage", "-z", "--"])).Output;
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<string?> TryResolveCommit(string root, string reference, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await Git(root, timeout, cancellationToken,
            ["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"], allowFailure: true);
        return result.ExitCode == 0 ? Text(result).Trim() : null;
    }

    private static async Task<string?> ResolveCurrentHeadOrUnborn(string root, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var resolved = await Git(root, timeout, cancellationToken,
            ["rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"], allowFailure: true);
        if (resolved.ExitCode == 0) return Text(resolved).Trim();

        var symbolic = await Git(root, timeout, cancellationToken,
            ["symbolic-ref", "-q", "HEAD"], allowFailure: true);
        if (symbolic.ExitCode == 0)
        {
            var branch = Text(symbolic).Trim();
            var exists = await Git(root, timeout, cancellationToken,
                ["show-ref", "--verify", "--quiet", branch], allowFailure: true);
            if (exists.ExitCode == 1) return null;
        }

        throw new ScopeException("scope.gitFailed", $"git rev-parse HEAD failed: {resolved.Error.Trim()}");
    }

    private static async Task<string> ResolveCommit(string root, string reference, TimeSpan timeout, CancellationToken cancellationToken) =>
        await TryResolveCommit(root, reference, timeout, cancellationToken)
        ?? throw new ScopeException("scope.invalidRef", $"'{reference}' does not resolve to a commit.");

    private static async Task<string> BlobIdentity(string root, string spec, TimeSpan timeout, CancellationToken cancellationToken) =>
        Text(await Git(root, timeout, cancellationToken, ["rev-parse", "--verify", "--end-of-options", spec])).Trim();

    private static bool Eligible(string? path) => path is not null && SourceDiscovery.IsSource(path) &&
        !SourceDiscovery.IsExcludedByDirectory(path, ".");

    private static bool ContextInput(string? path) => path is not null &&
        (Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" ||
         Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals("nuget.config", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals(".globalconfig", StringComparison.OrdinalIgnoreCase));

    private static bool BuildRelevantFilteredInput(string path) =>
        ContextInput(path) ||
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsExcludedTestSource(path) ||
        Path.GetExtension(path).ToLowerInvariant() is ".rsp" or ".resx";

    private static bool IsExcludedTestSource(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        SourceDiscovery.IsExcludedByDirectory(path, ".") &&
        path.Replace('\\', '/').Split('/').SkipLast(1).Any(segment =>
            segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));

    private static List<PathChange> ParseNameStatus(byte[] bytes)
    {
        var fields = SplitNull(bytes);
        var changes = new List<PathChange>();
        for (var index = 0; index < fields.Count;)
        {
            var statusField = fields[index++];
            string status;
            string? firstPath = null;
            var tab = statusField.IndexOf('\t');
            if (tab >= 0) { status = statusField[..tab]; firstPath = statusField[(tab + 1)..]; }
            else status = statusField;
            if (firstPath is null && index < fields.Count) firstPath = fields[index++];
            if (firstPath is null || status.Length == 0) throw new InvalidDataException("scope.malformedDiff: missing status path.");
            var code = status[0];
            if (code is 'R' or 'C')
            {
                if (index >= fields.Count) throw new InvalidDataException("scope.malformedDiff: rename/copy is missing destination path.");
                changes.Add(new PathChange(code == 'R' ? ScopeChangeKind.Renamed : ScopeChangeKind.Copied, firstPath, fields[index++]));
            }
            else changes.Add(code switch
            {
                'A' => new PathChange(ScopeChangeKind.Added, null, firstPath),
                'D' => new PathChange(ScopeChangeKind.Deleted, firstPath, null),
                'M' or 'T' => new PathChange(ScopeChangeKind.Modified, firstPath, firstPath),
                _ => throw new InvalidDataException($"scope.malformedDiff: unsupported status '{status}'.")
            });
        }
        return changes;
    }

    private static List<string> SplitNull(byte[] bytes)
    {
        var values = new List<string>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != 0) continue;
            values.Add(Encoding.UTF8.GetString(bytes, start, index - start));
            start = index + 1;
        }
        if (start != bytes.Length) throw new InvalidDataException("scope.malformedDiff: missing NUL terminator.");
        return values;
    }

    private static IReadOnlyList<string> NullOrLineValues(GitResult result) => Text(result)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string Text(GitResult result) => new UTF8Encoding(false, true).GetString(result.Output);

    private static async Task<GitResult> Git(string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyList<string> arguments, byte[]? input = null, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in SafeGitPrefix.Concat(arguments)) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        if (input is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
            process.StandardInput.Close();
        }
        await using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try { await process.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(outputTask, errorTask);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new ProcessTimeoutException("git", timeout);
        }
        await outputTask;
        var error = await errorTask;
        var result = new GitResult(process.ExitCode, output.ToArray(), error);
        if (!allowFailure && result.ExitCode != 0)
            throw new ScopeException("scope.gitFailed", $"git {arguments[0]} failed: {error.Trim()}");
        return result;
    }

    private sealed record GitResult(int ExitCode, byte[] Output, string Error);
    private sealed record PathChange(ScopeChangeKind Kind, string? OldPath, string? NewPath);

    [GeneratedRegex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HunkRegex();
}
