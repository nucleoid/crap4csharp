using System.Diagnostics;
using Crap4CSharp.Core;

internal static class CurrentEvidenceAdapter
{
    public const string SupportedRecipeProvider = "sdk-project-context-output-v1";

    public static CurrentEvidence Capture(RunManifest manifest, string workspaceRoot,
        Func<string, IReadOnlyList<string>, string>? gitExecutor = null, TimeSpan? gitTimeout = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var revision = manifest.Revision.Kind switch
        {
            "git" => ObserveGit(root, manifest.Contexts.SelectMany(context => context.Inputs)
                .Where(input => !input.Generated && input.Role != "reference").Select(input => input.LogicalPath)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                gitExecutor ?? ((path, arguments) => Git(path, arguments, gitTimeout ?? TimeSpan.FromSeconds(10)))),
            "none" => new ObservedRevision("none",
                CanonicalIdentity.Set("local-workspace-v1", [root]), null, null),
            _ => throw new InvalidDataException($"Unsupported revision kind: {manifest.Revision.Kind}")
        };
        var inputs = new List<CurrentInputEvidence>();
        foreach (var input in manifest.Contexts.SelectMany(context => context.Inputs)
            .Where(input => !input.Generated && input.Role != "reference")
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

    public static async Task<CurrentEvidence> CaptureSupportedAsync(RunManifest manifest, string workspaceRoot,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var basic = Capture(manifest, workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var verifiedInputs = new Dictionary<string, CurrentInputEvidence>(StringComparer.Ordinal);
        foreach (var expected in manifest.Contexts.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var recipe = expected.CurrentRevalidation;
            if (!expected.ReuseRecipeComplete || recipe is null || recipe.Provider != SupportedRecipeProvider)
                return basic;
            var project = ResolveRegularFile(root, NormalizeRecipePath(recipe.Project))
                ?? throw new InvalidDataException($"Current revalidation project is missing: {recipe.Project}");
            var loaded = await ProjectContextLoader.LoadAsync(new ProjectContextLoadRequest(project,
                expected.Configuration, expected.Platform, [expected.TargetFramework], true, true, timeout), cancellationToken);
            if (!loaded.Success) throw new InvalidDataException(loaded.FailureReason ?? "Current project context revalidation failed.");
            var candidates = loaded.Contexts.Where(context =>
                string.Equals(context.TargetFramework, expected.TargetFramework, StringComparison.Ordinal) &&
                string.Equals(context.Configuration, expected.Configuration, StringComparison.Ordinal) &&
                string.Equals(context.Platform, expected.Platform, StringComparison.Ordinal)).ToArray();
            if (candidates.Length != 1) throw new InvalidDataException("Current project context did not resolve uniquely.");
            var current = candidates[0];
            if (current.ContextId != expected.Id)
                throw new InvalidDataException("Current evaluated project context identity differs from the captured tested context.");
            var expectedSources = expected.Inputs.Where(input => input.Role == "source")
                .Select(input => (input.LogicalPath, input.Sha256, input.Generated)).OrderBy(item => item.LogicalPath, StringComparer.Ordinal).ToArray();
            var currentSources = current.Sources.Select(source => (source.LogicalPath,
                    source.ContentIdentity, source.IsGenerated)).OrderBy(item => item.LogicalPath, StringComparer.Ordinal).ToArray();
            if (!expectedSources.SequenceEqual(currentSources))
                throw new InvalidDataException("Current project source membership or content differs from captured tested inputs.");
            foreach (var source in current.Sources.Where(source => !source.IsGenerated))
            {
                var declaration = expected.Inputs.Single(input => input.Role == "source" && !input.Generated &&
                    input.LogicalPath == source.LogicalPath);
                verifiedInputs[declaration.Role + "\n" + declaration.LogicalPath] =
                    new CurrentInputEvidence(declaration.Role, declaration.LogicalPath,
                        declaration.Length, source.ContentIdentity);
            }
            var expectedReferences = expected.Inputs.Where(input => input.Role == "reference" && !input.Generated)
                .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray();
            var referenceMappings = recipe.References.OrderBy(item => item.LogicalPath, StringComparer.Ordinal).ToArray();
            if (referenceMappings.Select(item => item.LogicalPath).Distinct(StringComparer.Ordinal).Count() != referenceMappings.Length ||
                !expectedReferences.Select(item => item.LogicalPath)
                    .SequenceEqual(referenceMappings.Select(item => item.LogicalPath), StringComparer.Ordinal))
                throw new InvalidDataException("Current reference revalidation mappings are incomplete or ambiguous.");
            foreach (var reference in expectedReferences)
            {
                var mapping = referenceMappings.Single(item => item.LogicalPath == reference.LogicalPath);
                var referencePath = ResolveRegularFile(root, NormalizeRecipePath(mapping.WorkspacePath))
                    ?? throw new InvalidDataException($"Current reference is missing: {mapping.WorkspacePath}");
                var bytes = ArtifactBundle.ReadBounded(referencePath, ArtifactBundle.MaxArtifactBytes, null,
                    mapping.WorkspacePath);
                verifiedInputs[reference.Role + "\n" + reference.LogicalPath] =
                    new CurrentInputEvidence(reference.Role, reference.LogicalPath, bytes.Length,
                        CanonicalIdentity.Sha256(bytes));
            }
            var parse = expected.ParseOptions ?? throw new InvalidDataException("Captured context has no parse options.");
            if (!Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(parse.LanguageVersion,
                    out var capturedLanguageVersion) || capturedLanguageVersion != current.LanguageVersion ||
                parse.SourceKind != current.SourceKind.ToString() ||
                !parse.PreprocessorSymbols.Order(StringComparer.Ordinal).SequenceEqual(current.PreprocessorSymbols.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Current project parse context differs from captured tested context.");

            var assembly = ArtifactBundle.ReadBounded(ResolveRegularFile(root, NormalizeRecipePath(recipe.AssemblyPath))
                ?? throw new InvalidDataException("Current assembly output is missing."), ArtifactBundle.MaxArtifactBytes, null, recipe.AssemblyPath);
            var pdb = ArtifactBundle.ReadBounded(ResolveRegularFile(root, NormalizeRecipePath(recipe.PdbPath))
                ?? throw new InvalidDataException("Current PDB output is missing."), ArtifactBundle.MaxArtifactBytes, null, recipe.PdbPath);
            var build = manifest.Builds.SingleOrDefault(item => item.ContextId == expected.Id)
                ?? throw new InvalidDataException("Captured context does not have exactly one build binding.");
            var inspected = ArtifactEvidenceInspector.InspectBuild(System.Collections.Immutable.ImmutableArray.Create(assembly),
                System.Collections.Immutable.ImmutableArray.Create(pdb));
            if (CanonicalIdentity.Sha256(assembly) != build.AssemblySha256 ||
                CanonicalIdentity.Sha256(pdb) != build.PdbSha256 || inspected.ModuleIdentity != build.ModuleIdentity ||
                inspected.Mvid != build.Mvid || inspected.DebugIdentity != build.DebugIdentity)
                throw new InvalidDataException("Current compiled output differs from captured tested build evidence.");
            hashes.Add(expected.Id, expected.ContextHash);
        }
        foreach (var input in basic.Inputs.Where(input => input.Role is not ("source" or "reference")))
            verifiedInputs[input.Role + "\n" + input.LogicalPath] = input;
        return basic with { Inputs = verifiedInputs.Values.OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray(),
            ContextHashes = hashes, MembershipRecipeRevalidated = true };
    }

    private static string NormalizeRecipePath(string value)
    {
        if (Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Current revalidation paths must be bounded repository-relative paths.");
        try { return CanonicalIdentity.NormalizeLogicalPath(value); }
        catch (ArgumentException exception) { throw new InvalidDataException("Current revalidation path is invalid.", exception); }
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
