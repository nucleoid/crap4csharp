using Crap4CSharp.Core;

// Git selection/revision state, not a universal file-system or malicious-actor attestation.
// Authored input bytes and actual compiler documents remain the retained compiler proof's responsibility.
internal static class FreshGitProof
{
    internal static async Task ValidateAsync(FreshCheckPreflight preflight, CancellationToken token,
        IReadOnlyList<string>? provenOutputDirectories = null)
    {
        var before = preflight.GitSnapshot ?? throw Refuse();
        var after = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base,
            preflight.Request.BaseRef, preflight.Request.Head), preflight.Root, preflight.Request.Timeout, token);
        if (Identity(before, provenOutputDirectories) != Identity(after, provenOutputDirectories)) throw Refuse();
        if (preflight.Scope is { Mode: ChangeScopeMode.Worktree } selected)
        {
            var current = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Worktree),
                preflight.Root, preflight.Request.Timeout, token);
            if (Identity(selected, provenOutputDirectories) != Identity(current, provenOutputDirectories)) throw Refuse();
        }
    }

    internal static string Identity(CapturedChangeScope scope, IReadOnlyList<string>? provenOutputDirectories = null)
    {
        bool ExpectedOutput(string? path)
        {
            if (path is null) return false;
            var full = Path.GetFullPath(path, scope.RepositoryRoot);
            return (provenOutputDirectories ?? []).Any(directory => Path.IsPathFullyQualified(directory) &&
                full.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        string? FilteredPath(string diagnostic)
        {
            foreach (var prefix in new[] { "scope.unclassifiedChangedInput:", "scope.excludedChangedSource:", "scope.excludedTestSource:" })
                if (diagnostic.StartsWith(prefix, StringComparison.Ordinal)) return diagnostic[prefix.Length..];
            return null;
        }
        var diagnostics = scope.Diagnostics.Where(d => d != "scope.contextIncomplete" && !ExpectedOutput(FilteredPath(d))).ToArray();
        var files = scope.Files.Where(f => !ExpectedOutput(f.OldPath) && !ExpectedOutput(f.NewPath)).ToArray();
        var incomplete = files.Any(f => GitScopeResolver.ContextInput(f.OldPath) || GitScopeResolver.ContextInput(f.NewPath)) ||
            diagnostics.Select(FilteredPath).Where(p => p is not null).Any(p => GitScopeResolver.BuildRelevantFilteredInput(p!));
        var revision = scope.Revision;
        var values = new List<string>
        {
            CanonicalIdentity.Tuple("revision", scope.Mode.ToString(), scope.SourceState.ToString(), scope.RepositoryRoot,
                revision.ResolvedBase, revision.ResolvedHead, revision.CurrentHead, revision.MergeBase,
                revision.IndexIdentity, (incomplete ? ScopeCompleteness.ContextIncomplete : ScopeCompleteness.Complete).ToString())
        };
        values.AddRange(diagnostics.Select(d => CanonicalIdentity.Tuple("diagnostic", d)));
        foreach (var file in files)
        {
            values.Add(CanonicalIdentity.Tuple("changed-file", file.OldPath, file.NewPath, file.Kind.ToString(),
                file.OldIdentity, file.NewIdentity, file.OldObjectIdentity, file.NewObjectIdentity));
            values.AddRange(file.AddedRanges.Select(r => CanonicalIdentity.Tuple("added", file.NewPath,
                r.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture), r.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            values.AddRange(file.DeletedRanges.Select(r => CanonicalIdentity.Tuple("deleted", file.OldPath,
                r.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture), r.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        return CanonicalIdentity.Set("fresh-git-selection-state-v1", values);
    }

    private static PolicyException Refuse() => new("provenance.gitStateChanged",
        "Git HEAD/index/base/selection state changed after fresh preflight; execution evidence cannot be published.");
}
