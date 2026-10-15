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
        // All opening reads depend only on the resolved root. They retain
        // independent observations; await every owned cleanup before applying
        // the same HEAD/unborn -> index/conflicts/hidden -> base/head precedence.
        var currentHeadRead = ResolveCurrentHeadOrUnborn(root, timeout, cancellationToken);
        var indexRead = IndexIdentity(root, timeout, cancellationToken);
        var conflictsRead = EnsureNoConflicts(root, timeout, cancellationToken);
        var hiddenRead = capturesWorktree ? EnsureNoHiddenIndexEntries(root, timeout, cancellationToken) : Task.CompletedTask;
        var baseRead = request.Mode == ChangeScopeMode.Base
            ? ResolveCommit(root, request.BaseRef!, timeout, cancellationToken) : Task.FromResult(string.Empty);
        var selectedHeadRead = request.Mode == ChangeScopeMode.Base
            ? ResolveCommit(root, request.HeadRef ?? "HEAD", timeout, cancellationToken) : Task.FromResult(string.Empty);
        try { await Task.WhenAll(currentHeadRead, indexRead, conflictsRead, hiddenRead, baseRead, selectedHeadRead); }
        catch
        {
            // Each settled task is reawaited in original refusal order below.
            // This catch never accepts a failed read or leaves a child running.
        }
        var currentHead = await currentHeadRead;
        if (request.Mode == ChangeScopeMode.Base && currentHead is null)
            throw new ScopeException("scope.unbornHead", "base scope requires an existing HEAD commit.");
        await SettleReads(indexRead, conflictsRead, hiddenRead);
        await SettleReads(baseRead, selectedHeadRead);
        var openingIndex = await indexRead;
        var indexBefore = openingIndex.Identity;

        string? resolvedBase = null;
        string? resolvedHead = currentHead;
        string? mergeBase = null;
        if (request.Mode == ChangeScopeMode.Base)
        {
            resolvedBase = await baseRead;
            resolvedHead = await selectedHeadRead;
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
        using var scratchIndex = capturesWorktree
            ? await GitScratchIndex.CreateAsync(root, openingIndex.Bytes, timeout, cancellationToken) : null;
        var records = await ReadChanges(request, root, baseline, resolvedHead, timeout, cancellationToken, scratchIndex);
        var files = new List<ChangedFile>();
        var verificationFiles = new List<ChangedFile>();
        var blobInventory = capturesWorktree && records.Any(record => record.OldPath is not null && Eligible(record.OldPath))
            ? await ReadBlobInventory(root, baseline, timeout, cancellationToken) : null;
        // Independent file reads use bounded waves. Interpret them in the original
        // record order, and settle every owned read on refusal. HEAD/index and
        // closing worktree membership/byte checks remain independent and fresh.
        foreach (var batch in records.Where(record => Eligible(record.OldPath) || Eligible(record.NewPath)).Chunk(64))
        {
            var blobs = blobInventory is null ? null : await ReadBlobBatch(root, batch, blobInventory, timeout, cancellationToken);
            foreach (var wave in batch.Chunk(2))
            {
                var reads = wave.Select(record => CaptureFile(request, root, baseline, resolvedHead, record, timeout, cancellationToken, blobs)).ToArray();
                try
                {
                    foreach (var read in reads)
                    {
                        var captured = await read;
                        verificationFiles.Add(captured);
                        var file = NormalizeEligibility(captured);
                        if (file is null) continue;
                        if (file.Kind == ScopeChangeKind.Modified && file.OldIdentity == file.NewIdentity) continue;
                        files.Add(file);
                    }
                }
                catch
                {
                    try { await Task.WhenAll(reads); }
                    catch { /* Preserve original record-order refusal after cleanup. */ }
                    throw;
                }
            }
        }
        files = CoalesceExactWorktreeRenames(files);

        if (capturesWorktree)
        {
            var recordsAfter = await ReadChanges(request, root, baseline, resolvedHead, timeout, cancellationToken, scratchIndex);
            if (!records.SequenceEqual(recordsAfter))
                throw new ScopeException("scope.changedDuringCapture", "the set of worktree changes changed during capture.");
        }
        var closingHeadRead = ResolveCurrentHeadOrUnborn(root, timeout, cancellationToken);
        var closingIndexRead = IndexIdentity(root, timeout, cancellationToken);
        await SettleReads(closingHeadRead, closingIndexRead);
        var currentHeadAfter = await closingHeadRead;
        var indexAfter = (await closingIndexRead).Identity;
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
        var opaqueDirectories = await ChangedOpaqueDirectories(records, root, baseline, openingIndex.Bytes,
            timeout, cancellationToken);
        var contextIncomplete = records.Any(record => ContextInput(record.OldPath) || ContextInput(record.NewPath)) ||
            filteredPaths.Any(BuildRelevantFilteredInput) || opaqueDirectories.Count > 0;
        diagnostics.AddRange(opaqueDirectories.Select(path => "scope.changedDirectoryOrGitlink:" + path));
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

    // A gitlink is not a blob. Changed nested repositories may contain compiler
    // inputs, so retain their uncertainty and widen rather than guess no impact.
    private static async Task<IReadOnlyList<string>> ChangedOpaqueDirectories(IReadOnlyList<PathChange> records,
        string root, string baseline, byte[] openingIndex, TimeSpan timeout, CancellationToken token)
    {
        var paths = records.SelectMany(record => new[] { record.OldPath, record.NewPath })
            .Where(path => path is not null && !Eligible(path)).Select(path => path!)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (paths.Count == 0) return [];
        var opaque = paths.Where(path => Directory.Exists(Path.Combine(root, path)))
            .ToHashSet(StringComparer.Ordinal);
        void AddGitlinks(byte[] bytes)
        {
            foreach (var entry in SplitNull(bytes))
            {
                token.ThrowIfCancellationRequested();
                if (!entry.StartsWith("160000 ", StringComparison.Ordinal)) continue;
                var separator = entry.IndexOf('\t');
                if (separator < 0) throw new ScopeException("scope.malformedDiff", "Gitlink identity has no path.");
                var path = entry[(separator + 1)..];
                if (paths.Contains(path)) opaque.Add(path);
            }
        }
        AddGitlinks(openingIndex);
        await ReadOpaqueTreeBatchesAsync(paths.ToArray(), async (batch, ct) =>
            (await Git(root, timeout, ct, new[] { "ls-tree", "-r", "-z", "--full-tree", baseline, "--" }
                .Concat(batch.Select(path => ":(literal)" + path)).ToArray())).Output, AddGitlinks, token);
        return opaque.Order(StringComparer.Ordinal).ToArray();
    }

    // Group whole original sixteen-path queries. A larger response may exceed
    // the existing per-command protocol allowance: retry its exact original
    // groups, never truncate, lower the allowance, or reuse a closing observation.
    internal static async Task ReadOpaqueTreeBatchesAsync(IReadOnlyList<string> paths,
        Func<IReadOnlyList<string>, CancellationToken, Task<byte[]>> read,
        Action<byte[]> consume, CancellationToken token)
    {
        var batch = new List<string>(128);
        long characters = 1024; // Reserve executable and fixed argument overhead.
        foreach (var original in paths.Chunk(16))
        {
            token.ThrowIfCancellationRequested();
            var added = original.Sum(path => 2L * (path.Length + ":(literal)".Length) + 3);
            if (batch.Count > 0 && (batch.Count + original.Length > 128 || characters + added > 24000))
            {
                await ReadBatch(); batch.Clear(); characters = 1024;
            }
            batch.AddRange(original); characters += added;
        }
        if (batch.Count > 0) await ReadBatch();

        async Task ReadBatch()
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = await read(batch.ToArray(), token); }
            catch (ScopeException error) when (error.Reason == "scope.gitProtocolLimit" && batch.Count > 16)
            {
                foreach (var original in batch.Chunk(16))
                {
                    token.ThrowIfCancellationRequested();
                    var part = await read(original, token);
                    token.ThrowIfCancellationRequested();
                    consume(part);
                }
                token.ThrowIfCancellationRequested();
                return;
            }
            token.ThrowIfCancellationRequested();
            consume(bytes);
        }
    }

    private static async Task SettleReads(params Task[] reads)
    {
        try { await Task.WhenAll(reads); }
        catch
        {
            // When several reads fail, retain their declaration-order failure
            // precedence after all processes have completed their owned cleanup.
            foreach (var read in reads) await read;
            throw;
        }
    }

    private static async Task<IReadOnlyList<PathChange>> ReadChanges(GitScopeRequest request, string root, string baseline,
        string? resolvedHead, TimeSpan timeout, CancellationToken cancellationToken, GitScratchIndex? scratchIndex)
    {
        var capturesWorktree = request.Mode == ChangeScopeMode.Worktree ||
            request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Worktree;
        var arguments = new List<string> { capturesWorktree ? "diff-index" : "diff", "--name-status", "-z", "--no-ext-diff", "--no-textconv", "--find-renames=50%", "--find-copies=50%" };
        if (request.Mode == ChangeScopeMode.Staged) arguments.Add("--cached");
        arguments.Add(baseline);
        if (request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Head) arguments.Add(resolvedHead!);
        arguments.Add("--");
        List<PathChange> changes;
        if (capturesWorktree)
        {
            // Reconstruct the same semantic index without its stat cache. Git must
            // inspect bytes even for same-size edits with restored timestamps.
            // The consumer's index, flags and staged identities remain untouched.
            if (scratchIndex is null) throw new InvalidOperationException("Worktree capture requires its owned statless index.");
            scratchIndex.Validate(cancellationToken);
            {
                changes = ParseNameStatus((await Git(root, timeout, cancellationToken, arguments,
                    environment: scratchIndex.Environment)).Output);
                scratchIndex.Validate(cancellationToken);
                // A statless index conservatively reports unchanged files as
                // modified. Eligible sources are already compared using raw
                // captured bytes below. Apply the same distinction to filtered
                // inputs before deciding that project/configuration changes
                // widen the scope. --no-filters preserves physical newline and
                // filter differences that ordinary git diff can hide. Configuration-only
                // CRLF separators are equivalent for scope selection; physical
                // compiler inputs still retain their exact byte identities.
                var unchanged = new HashSet<string>(StringComparer.Ordinal);
                var candidates = changes.Where(change => change.Kind == ScopeChangeKind.Modified &&
                    !Eligible(change.NewPath) && change.OldPath == change.NewPath && change.NewPath is not null &&
                    (File.GetAttributes(Path.Combine(root, change.NewPath)) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
                    .Select(change => change.NewPath!).Distinct(StringComparer.Ordinal).ToArray();
                var inventory = candidates.Length == 0 ? null : await ReadBlobInventory(root, baseline, timeout, cancellationToken);
                foreach (var path in candidates)
                {
                    // Non-blob transitions remain uncertain/changed. For ordinary
                    // blobs retain raw --no-filters semantics without one child
                    // pair per 16 paths. Closing capture independently repeats it.
                    if (inventory!.TryGetValue(path, out var oldBlob) &&
                        (await RawWorktreeBlobIdentity(Path.Combine(root, path), oldBlob.Identity.Length, cancellationToken) == oldBlob.Identity ||
                         (ContextInput(path) || Path.GetExtension(path).ToLowerInvariant() is ".resx" or ".rsp") &&
                         await ConfigurationLineEndingIdentity(Path.Combine(root, path), oldBlob.Identity.Length, cancellationToken) == oldBlob.Identity))
                        unchanged.Add(path);
                }
                changes = changes.Where(change => change.NewPath is null || !unchanged.Contains(change.NewPath)).ToList();
            }
        }
        else changes = ParseNameStatus((await Git(root, timeout, cancellationToken, arguments)).Output);

        if (request.Mode == ChangeScopeMode.Worktree || request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Worktree)
        {
            var untracked = SplitNull((await Git(root, timeout, cancellationToken,
                ["ls-files", "--others", "--exclude-standard", "-z", "--"])).Output);
            changes.AddRange(untracked.Select(path => new PathChange(ScopeChangeKind.Added, null, path)));
        }
        return changes;
    }

    // Only the immutable opening semantic index is reused. Every diff, raw
    // worktree read, HEAD/index observation and closing byte proof remains fresh.
    // Byte checks ensure a command cannot introduce a stat cache between reads.
    internal sealed class GitScratchIndex : IDisposable
    {
        private readonly DirectoryInfo directory;
        private readonly byte[] identity;
        private readonly long length;
        internal string IndexPath { get; }
        internal IReadOnlyDictionary<string, string> Environment { get; }

        private GitScratchIndex(DirectoryInfo directory, byte[] identity, long length)
        {
            this.directory = directory; this.identity = identity; this.length = length;
            IndexPath = Path.Combine(directory.FullName, "index");
            Environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = IndexPath };
        }

        internal static async Task<GitScratchIndex> CreateAsync(string root, byte[] openingIndex,
            TimeSpan timeout, CancellationToken token)
        {
            var owned = Directory.CreateTempSubdirectory("crap4csharp-git-index-");
            try
            {
                var path = Path.Combine(owned.FullName, "index");
                await Git(root, timeout, token, ["update-index", "-z", "--index-info"], openingIndex,
                    environment: new Dictionary<string, string> { ["GIT_INDEX_FILE"] = path });
                var length = new FileInfo(path).Length;
                return new(owned, ReadIdentity(path, length, token), length);
            }
            catch { Remove(owned); throw; }
        }

        internal void Validate(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0 ||
                !ReadIdentity(IndexPath, length, token).AsSpan().SequenceEqual(identity))
                throw new ScopeException("scope.changedDuringCapture", "The invocation-owned statless index changed during capture.");
        }

        private static byte[] ReadIdentity(string path, long expectedLength, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new ScopeException("scope.changedDuringCapture", "The invocation-owned index is not a regular file.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (stream.Length != expectedLength) throw Changed();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                total += count;
                if (total > expectedLength) throw Changed();
                hash.AppendData(buffer.AsSpan(0, count));
            }
            if (total != expectedLength || stream.Length != expectedLength) throw Changed();
            token.ThrowIfCancellationRequested();
            return hash.GetHashAndReset();
            static ScopeException Changed() => new("scope.changedDuringCapture", "The invocation-owned statless index length changed during capture.");
        }

        public void Dispose() => Remove(directory);
        private static void Remove(DirectoryInfo owned)
        {
            try { owned.Delete(recursive: true); }
            catch (IOException) { /* Preserve only this invocation's scratch on failed cleanup. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<ChangedFile> CaptureFile(GitScopeRequest request, string root, string baseline,
        string? resolvedHead, PathChange record, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, GitBlobBytes>? blobs = null)
    {
        CapturedSource? oldSource = null;
        CapturedSource? newSource = null;
        string? oldIdentity = null;
        string? newIdentity = null;
        string? oldObjectIdentity = null;
        string? newObjectIdentity = null;
        if (record.OldPath is not null && record.Kind != ScopeChangeKind.Added)
        {
            if (blobs is not null && blobs.TryGetValue(record.OldPath, out var blob))
            {
                oldSource = Capture(record.OldPath, blob.Bytes);
                EnsureCompatibleLineMap(oldSource);
                oldObjectIdentity = blob.Identity;
            }
            else
                (oldSource, oldObjectIdentity) = await CaptureBlob(root, $"{baseline}:{record.OldPath}", record.OldPath, timeout, cancellationToken);
            oldIdentity = oldSource.ContentIdentity;
        }
        if (record.NewPath is not null && record.Kind != ScopeChangeKind.Deleted)
        {
            if (request.Mode == ChangeScopeMode.Staged)
            {
                (newSource, newObjectIdentity) = await CaptureBlob(root, $":{record.NewPath}", record.NewPath, timeout, cancellationToken);
            }
            else if (request.Mode == ChangeScopeMode.Base && request.SourceState == ScopeSourceState.Head)
            {
                (newSource, newObjectIdentity) = await CaptureBlob(root, $"{resolvedHead}:{record.NewPath}", record.NewPath, timeout, cancellationToken);
            }
            else
            {
                newSource = Capture(record.NewPath, await File.ReadAllBytesAsync(Path.Combine(root, record.NewPath), cancellationToken));
                EnsureCompatibleLineMap(newSource);
            }
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

    private static ChangedFile? NormalizeEligibility(ChangedFile file)
    {
        var oldEligible = Eligible(file.OldPath);
        var newEligible = Eligible(file.NewPath);
        if (!oldEligible && newEligible && file.NewSource is not null)
            return new ChangedFile(null, file.NewPath, ScopeChangeKind.Added, null, file.NewIdentity,
                null, file.NewSource, [WholeFile(file.NewSource.Text)], [], null, file.NewObjectIdentity);
        if (oldEligible && !newEligible && file.Kind == ScopeChangeKind.Copied)
            return null;
        if (oldEligible && !newEligible && file.Kind == ScopeChangeKind.Renamed && file.OldSource is not null)
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
            if ((entry[0] == 'S' || char.IsLower(entry[0])) && (Eligible(path) || BuildRelevantFilteredInput(path)))
                throw new ScopeException("scope.hiddenIndexEntry", $"{path} is marked skip-worktree or assume-unchanged.");
        }
    }

    private static async Task<GitIndexObservation> IndexIdentity(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bytes = (await Git(root, timeout, cancellationToken, ["ls-files", "--stage", "-z", "--"])).Output;
        return new("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes);
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

    private static async Task<(CapturedSource Source, string ObjectIdentity)> CaptureBlob(string root, string spec,
        string path, TimeSpan timeout, CancellationToken token)
    {
        // Independent read-only observations, no cache or removed closing proof.
        // Keep content/line-map failure precedence and settle both processes.
        var content = Git(root, timeout, token, ["show", spec]);
        var identity = BlobIdentity(root, spec, timeout, token);
        try
        {
            var source = Capture(path, (await content).Output);
            EnsureCompatibleLineMap(source);
            return (source, await identity);
        }
        catch
        {
            try { await Task.WhenAll(content, identity); }
            catch { /* Preserve the original content/identity failure after cleanup. */ }
            throw;
        }
    }

    private static async Task<string> BlobIdentity(string root, string spec, TimeSpan timeout, CancellationToken cancellationToken) =>
        Text(await Git(root, timeout, cancellationToken, ["rev-parse", "--verify", "--end-of-options", spec])).Trim();

    private static bool Eligible(string? path) => path is not null && SourceDiscovery.IsSource(path) &&
        !SourceDiscovery.IsExcludedByDirectory(path, ".");

    internal static bool ContextInput(string? path) => path is not null &&
        (Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" ||
         Path.GetFileName(path).Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals("nuget.config", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(path).Equals(".globalconfig", StringComparison.OrdinalIgnoreCase));

    internal static bool BuildRelevantFilteredInput(string path) =>
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
        IReadOnlyList<string> arguments, byte[]? input = null, bool allowFailure = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessProtocolResult captured;
        try
        {
            var childEnvironment = new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "0" };
            if (environment is not null)
                foreach (var pair in environment) childEnvironment.Add(pair.Key, pair.Value);
            captured = await ProcessRunner.RunProtocolAsync("git", SafeGitPrefix.Concat(arguments),
                workingDirectory, timeout, cancellationToken, maximumBytesPerStream: 16777216,
                standardInput: input is null ? null : new ReadOnlyMemory<byte>(input),
                environment: childEnvironment);
        }
        catch (InvalidDataException exception)
        {
            throw new ScopeException("scope.gitProtocolLimit", "Git response exceeded the complete bounded protocol limit: " +
                exception.Message, exception);
        }
        // Preserve raw stdout bytes (NUL-delimited paths/blobs); decode only diagnostics.
        string error = Encoding.UTF8.GetString(captured.StandardError);
        var result = new GitResult(captured.ExitCode, captured.StandardOutput, error);
        if (!allowFailure && result.ExitCode != 0)
            throw new ScopeException("scope.gitFailed", $"git {arguments[0]} failed: {error.Trim()}");
        return result;
    }

    private sealed record GitResult(int ExitCode, byte[] Output, string Error);
    private sealed record GitIndexObservation(string Identity, byte[] Bytes);
    private sealed record PathChange(ScopeChangeKind Kind, string? OldPath, string? NewPath);

    [GeneratedRegex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex HunkRegex();
}
