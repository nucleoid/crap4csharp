using System.Diagnostics;
using Crap4CSharp.Core;

internal static class CurrentEvidenceAdapter
{
    public static CurrentEvidence Capture(RunManifest manifest, string workspaceRoot,
        Func<string, IReadOnlyList<string>, string>? gitExecutor = null, TimeSpan? gitTimeout = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var revision = manifest.Revision.Kind switch
        {
            "git" => ObserveGit(root, manifest.Contexts.SelectMany(context => context.Inputs)
                .Where(input => !input.Generated).Select(input => input.LogicalPath)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                gitExecutor ?? ((path, arguments) => Git(path, arguments, gitTimeout ?? TimeSpan.FromSeconds(10)))),
            "none" => new ObservedRevision("none",
                CanonicalIdentity.Set("local-workspace-v1", [root]), null, null),
            _ => throw new InvalidDataException($"Unsupported revision kind: {manifest.Revision.Kind}")
        };
        var inputs = new List<CurrentInputEvidence>();
        foreach (var input in manifest.Contexts.SelectMany(context => context.Inputs).Where(input => !input.Generated)
            .GroupBy(input => input.Role + "\n" + input.LogicalPath, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(input => input.LogicalPath, StringComparer.Ordinal))
        {
            var logical = CanonicalIdentity.NormalizeLogicalPath(input.LogicalPath);
            var path = ResolveRegularFile(root, logical);
            if (path is null)
            {
                inputs.Add(new(input.Role, input.LogicalPath, -1, "missing"));
                continue;
            }
            var bytes = ArtifactBundle.ReadBounded(path, ArtifactBundle.MaxArtifactBytes, null, logical);
            inputs.Add(new(input.Role, input.LogicalPath, bytes.Length, CanonicalIdentity.Sha256(bytes)));
        }

        // v1 manifests do not yet carry a complete executable membership recipe. Re-reading the saved list
        // proves byte equality only; it cannot prove that no new glob/import/input appeared. Keep context hashes
        // absent so VerifyCurrent returns contextNotRevalidated instead of manufacturing a fresh verification.
        return new CurrentEvidence(revision.RepositoryIdentity, revision.WorkspaceIdentity, revision.Head,
            inputs, new Dictionary<string, string>(StringComparer.Ordinal), false, revision.StateHash);
    }

    private static ObservedRevision ObserveGit(string root, IReadOnlyList<string> inputPaths,
        Func<string, IReadOnlyList<string>, string> git)
    {
        string Run(params string[] arguments) => git(root, arguments);
        static string Line(string value) => value.TrimEnd('\r', '\n');
        var repositoryRoot = Line(Run("rev-parse", "--show-toplevel"));
        var commonDirectory = Path.GetFullPath(Line(Run("rev-parse", "--git-common-dir")), root);
        var worktreeDirectory = Path.GetFullPath(Line(Run("rev-parse", "--git-dir")), root);
        var head = Line(Run("rev-parse", "HEAD"));
        var status = git(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", .. inputPaths]);
        var submodules = Run("submodule", "status", "--recursive");
        var staged = git(root, ["diff", "--cached", "--binary", "--full-index", "--", .. inputPaths]);
        var stateHash = CanonicalIdentity.Set("git-workspace-state-v1",
            [CanonicalIdentity.Tuple("status", status),
             CanonicalIdentity.Tuple("submodules", submodules),
             CanonicalIdentity.Tuple("staged", staged)]);
        return new ObservedRevision(
            CanonicalIdentity.Set("git-repository-v1", [repositoryRoot, commonDirectory]),
            CanonicalIdentity.Set("git-worktree-v1", [repositoryRoot, worktreeDirectory]), head, stateHash);
    }

    private static string Git(string root, IReadOnlyList<string> arguments, TimeSpan timeoutValue)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start git.");
        using var timeout = new CancellationTokenSource(timeoutValue);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Git identity observation timed out.", exception);
        }
        var output = outputTask.Result;
        var error = errorTask.Result;
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Git identity observation failed: {error.Trim()}");
        return output;
    }

    private static string? ResolveRegularFile(string root, string logical)
    {
        var current = root;
        foreach (var part in logical.Split('/'))
        {
            current = Path.Combine(current, part);
            var relative = Path.GetRelativePath(root, current);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Workspace input escapes the declared root.");
            if (!File.Exists(current) && !Directory.Exists(current)) return null;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Workspace input contains a symbolic link: {logical}");
        }
        if (Directory.Exists(current)) throw new InvalidDataException($"Workspace input is not a regular file: {logical}");
        return current;
    }

    private sealed record ObservedRevision(string RepositoryIdentity, string WorkspaceIdentity, string? Head,
        string? StateHash);
}
