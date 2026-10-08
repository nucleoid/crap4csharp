using System.Diagnostics;
using Crap4CSharp.Core;

internal static class CurrentEvidenceAdapter
{
    public const string SupportedRecipeProvider = "sdk-project-context-output-v1";

    internal static void ValidateSupportedRecipes(RunManifest manifest)
    {
        var provider = manifest.Contexts.FirstOrDefault()?.CurrentRevalidation?.Provider;
        if (provider is not (SupportedRecipeProvider or CompletedRevalidationDeclaration.Provider) ||
            manifest.Contexts.Count == 0 || manifest.Contexts.Any(context =>
            !context.ReuseRecipeComplete || context.CurrentRevalidation is null ||
            context.CurrentRevalidation.Provider != provider))
            throw new PolicyException("provenance.revalidationRecipeUnsupported",
                "Current evidence reuse requires a complete supported current-revalidation recipe for every context. Capture new evidence; historical bundles remain available for offline analyze.");
    }

    public static CurrentEvidence Capture(RunManifest manifest, string workspaceRoot,
        Func<string, IReadOnlyList<string>, string>? gitExecutor = null, TimeSpan? gitTimeout = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var revision = manifest.Revision.Kind switch
        {
            "git" => ObserveGit(root, manifest.Contexts.SelectMany(context => context.Inputs
                .Where(input => !input.Generated && input.Role != "reference")
                .Select(input => CapturedEvaluationInputs.DeclaredRepositorySourcePath(context, input)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                gitExecutor ?? ((path, arguments) => Git(path, arguments, gitTimeout ?? TimeSpan.FromSeconds(10)))),
            "none" => new ObservedRevision("none",
                CanonicalIdentity.Set("local-workspace-v1", [root]), null, null),
            _ => throw new InvalidDataException($"Unsupported revision kind: {manifest.Revision.Kind}")
        };
        var inputs = new List<CurrentInputEvidence>();
        foreach (var value in manifest.Contexts.SelectMany(context => context.Inputs
                     .Where(input => !input.Generated && input.Role != "reference")
                     .Select(input => (Input: input, RepositoryPath:
                         CapturedEvaluationInputs.DeclaredRepositorySourcePath(context, input))))
                 .GroupBy(value => value.Input.Role + "\n" + value.RepositoryPath, StringComparer.Ordinal)
                 .Select(group => group.First()).OrderBy(value => value.Input.LogicalPath, StringComparer.Ordinal))
        {
            var input = value.Input;
            var logical = CanonicalIdentity.NormalizeLogicalPath(input.LogicalPath);
            var path = ResolveRegularFile(root, value.RepositoryPath);
            if (path is null)
            {
                inputs.Add(new(input.Role, input.LogicalPath, -1, "missing") { RepositoryPath = input.RepositoryPath });
                continue;
            }
            var bytes = ArtifactBundle.ReadBounded(path, ArtifactBundle.MaxArtifactBytes, null, logical);
            inputs.Add(new(input.Role, input.LogicalPath, bytes.Length, CanonicalIdentity.Sha256(bytes))
                { RepositoryPath = input.RepositoryPath });
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
        ValidateSupportedRecipes(manifest);
        // A manifest-only call must never interpret complete-provider hash-only
        // metadata as the legacy recipe. Its actual bundle and current preflight
        // are mandatory inputs to the independent complete proof.
        if (manifest.Contexts.Any(context => context.CurrentRevalidation!.Provider != SupportedRecipeProvider))
            throw new PolicyException("provenance.revalidationRecipeUnsupported",
                "Completed evidence revalidation requires the actual bundle and independently acquired current preflight.");
        var basic = Capture(manifest, workspaceRoot, gitTimeout: timeout);
        var root = Path.GetFullPath(workspaceRoot);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var verifiedInputs = new Dictionary<string, CurrentInputEvidence>(StringComparer.Ordinal);
        var protectedPaths = new HashSet<string>(PathIdentityPolicy.Current.Comparer);
        foreach (var context in manifest.Contexts)
        foreach (var input in context.Inputs.Where(input => !input.Generated))
        {
            var repositoryPath = CapturedEvaluationInputs.DeclaredRepositorySourcePath(context, input);
            var path = ResolveRegularFile(root, NormalizeRecipePath(repositoryPath));
            if (path is not null) protectedPaths.Add(path);
        }
        foreach (var expected in manifest.Contexts.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            // Shared preflight has validated every context before any Git/project work.
            var recipe = expected.CurrentRevalidation!;
            if (!string.Equals(NormalizeRecipePath(recipe.Project), NormalizeRecipePath(expected.Project),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Current revalidation recipe project differs from its declared production project.");
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
            foreach (var path in current.ProtectedPaths) protectedPaths.Add(path);
            foreach (var source in current.Sources.Where(source => source.ResolvedPath is not null))
                protectedPaths.Add(source.ResolvedPath!);
            foreach (var source in current.Sources.Where(source => !source.IsGenerated))
            {
                var declaration = expected.Inputs.Single(input => input.Role == "source" && !input.Generated &&
                    input.LogicalPath == source.LogicalPath);
                var physical = source.ResolvedPath ?? throw new InvalidDataException(
                    "Current authored source has no resolved physical repository path.");
                var repositoryPath = Path.GetRelativePath(root, physical).Replace('\\', '/');
                if (repositoryPath == ".." || repositoryPath.StartsWith("../", StringComparison.Ordinal) ||
                    Path.IsPathRooted(repositoryPath) || declaration.RepositoryPath != repositoryPath)
                    throw new InvalidDataException("Captured source repository path differs from its current physical repository path.");
                _ = ResolveRegularFile(root, NormalizeRecipePath(repositoryPath)) ??
                    throw new InvalidDataException("Current authored source repository path is missing.");
            }
            foreach (var source in current.Sources.Where(source => !source.IsGenerated))
            {
                var declaration = expected.Inputs.Single(input => input.Role == "source" && !input.Generated &&
                    input.LogicalPath == source.LogicalPath);
                verifiedInputs[declaration.Role + "\n" + declaration.RepositoryPath] =
                    new CurrentInputEvidence(declaration.Role, declaration.LogicalPath,
                        declaration.Length, source.ContentIdentity) { RepositoryPath = declaration.RepositoryPath };
            }
            var expectedReferences = expected.Inputs.Where(input => input.Role == "reference" && !input.Generated)
                .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray();
            if (expectedReferences.Length > 0)
                throw new InvalidDataException("This current-revalidation provider cannot independently bind captured metadata references to the loader's current resolved reference closure.");
            var parse = expected.ParseOptions ?? throw new InvalidDataException("Captured context has no parse options.");
            if (!Microsoft.CodeAnalysis.CSharp.LanguageVersionFacts.TryParse(parse.LanguageVersion,
                    out var capturedLanguageVersion) || capturedLanguageVersion != current.LanguageVersion ||
                parse.SourceKind != current.SourceKind.ToString() ||
                !parse.PreprocessorSymbols.Order(StringComparer.Ordinal).SequenceEqual(current.PreprocessorSymbols.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Current project parse context differs from captured tested context.");

            var assemblyPath = ResolveRegularFile(root, NormalizeRecipePath(recipe.AssemblyPath))
                ?? throw new InvalidDataException("Current assembly output is missing.");
            var pdbPath = ResolveRegularFile(root, NormalizeRecipePath(recipe.PdbPath))
                ?? throw new InvalidDataException("Current PDB output is missing.");
            protectedPaths.Add(assemblyPath);
            protectedPaths.Add(pdbPath);
            var assembly = ArtifactBundle.ReadBounded(assemblyPath, ArtifactBundle.MaxArtifactBytes, null, recipe.AssemblyPath);
            var pdb = ArtifactBundle.ReadBounded(pdbPath, ArtifactBundle.MaxArtifactBytes, null, recipe.PdbPath);
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
            verifiedInputs[input.Role + "\n" + (input.RepositoryPath ?? input.LogicalPath)] = input;
        return basic with { Inputs = verifiedInputs.Values.OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray(),
            ContextHashes = hashes, MembershipRecipeRevalidated = true,
            ProtectedPaths = protectedPaths.Order(StringComparer.Ordinal).ToArray() };
    }

    // Shared bundle-aware entry point. Provider declarations only select a gate;
    // they cannot manufacture its successful current/compiler/test observation.
    internal static Task<CurrentEvidence> CaptureSupportedAsync(ArtifactBundle bundle,
        FreshCheckPreflight preflight, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateSupportedRecipes(bundle.Manifest);
        return bundle.Manifest.Contexts[0].CurrentRevalidation!.Provider == CompletedRevalidationDeclaration.Provider
            ? CaptureCompletedAsync(bundle, preflight, token)
            : CaptureSupportedAsync(bundle.Manifest, preflight.Root, preflight.Request.Timeout, token);
    }

    // A private observation does not advertise a reusable recipe. The complete
    // validator independently recomputes every declaration before projection.
    internal static async Task<CurrentEvidence> CaptureCompletedAsync(ArtifactBundle bundle,
        FreshCheckPreflight preflight, CancellationToken token)
    {
        var compiler = await SavedCompletedProof.ValidateAsync(bundle, preflight, token);
        var paths = new HashSet<string>(PathIdentityPolicy.Current.Comparer);
        foreach (var binding in compiler.FinalGraph.GraphContexts)
        {
            token.ThrowIfCancellationRequested();
            var context = binding.Context;
            paths.Add(binding.ProjectPath);
            foreach (var path in context.ProtectedPaths.Concat(context.PhaseInputs.Select(input => input.Path))
                .Concat(context.CompilerDocuments.Select(document => document.ResolvedPath!))
                .Concat(new[] { context.EvaluatedTargetPath!, Path.ChangeExtension(context.EvaluatedTargetPath!, ".pdb"),
                    context.EvaluatedReferenceAssemblyPath! }).Where(path => !string.IsNullOrEmpty(path)))
                paths.Add(Path.GetFullPath(path));
        }
        foreach (var observation in compiler.Observations)
        {
            paths.Add(observation.Assembly!.Path);
            paths.Add(observation.Pdb!.Path);
            foreach (var path in observation.Parameters.Single(parameter => parameter.Name == "References").Items)
                paths.Add(Path.GetFullPath(path));
        }
        var inputs = bundle.Manifest.Contexts.SelectMany(context => context.Inputs).Where(input => !input.Generated)
            .GroupBy(input => input.Role + "\n" + (input.RepositoryPath ?? input.LogicalPath), StringComparer.Ordinal)
            .Select(group => group.First()).Select(input => new CurrentInputEvidence(input.Role, input.LogicalPath,
                input.Length, input.Sha256) { RepositoryPath = input.RepositoryPath }).ToArray();
        var hashes = bundle.Manifest.Contexts.ToDictionary(context => context.Id,
            context => ManifestIdentity.ContextHash(context, ManifestIdentity.SourceSetHash(context),
                ManifestIdentity.InputClosureHash(context)), StringComparer.Ordinal);
        // Independently observed current preflight may include proven build outputs
        // absent from the captured ORIGINAL pre-build selection. Normalize only the
        // regated evaluated output footprint, preserving HEAD/base/index and all
        // outside-output changes. Never copy or rewrite the captured revision hash.
        var bundleFiles = SavedBundleFootprint.Validate(bundle, preflight.Root, paths.ToArray(), token);
        var state = preflight.GitSnapshot is { } git
            ? FreshGitProof.Identity(git, SavedCompletedProof.OutputFootprint(compiler)) :
            SavedLocalMembership.Observe(bundle, preflight, SavedCompletedProof.OutputFootprint(compiler), token, bundleFiles);
        compiler.RetainedProof!.ValidateCurrent(token);
        _ = SavedBundleFootprint.Validate(bundle, preflight.Root, paths.ToArray(), token);
        token.ThrowIfCancellationRequested();
        return new(CanonicalIdentity.Tuple("fresh-repository", preflight.Root),
            CanonicalIdentity.Tuple("fresh-workspace", preflight.Root), preflight.GitSnapshot?.Revision.CurrentHead,
            inputs, hashes, true, state) { ProtectedPaths = paths.Order(StringComparer.Ordinal).ToArray() };
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
        if (Line(Run("rev-parse", "--show-prefix")).Length != 0)
            throw new InvalidDataException("Current workspace must equal the Git repository root.");
        var commonDirectory = Path.GetFullPath(Line(Run("rev-parse", "--git-common-dir")), root);
        var worktreeDirectory = Path.GetFullPath(Line(Run("rev-parse", "--git-dir")), root);
        var head = Line(Run("rev-parse", "HEAD"));
        // Bounded literal pathspec batches preserve declared-input-only observation without
        // Windows' command-line ceiling. Disable rename pairing so batch boundaries cannot
        // change the representation of a staged rename between declared inputs.
        string ObservePaths(params string[] command)
        {
            var result = new System.Text.StringBuilder();
            var batch = new List<string>();
            var size = 0;
            void Flush()
            {
                if (batch.Count == 0) return;
                result.Append(git(root, ["--literal-pathspecs", .. command, "--", .. batch]));
                batch.Clear();
                size = 0;
            }
            foreach (var path in inputPaths)
            {
                var cost = path.Length * 2 + 3; // conservative Windows quoting allowance
                if (cost > 8000) throw new InvalidDataException("Declared Git input path exceeds the command budget.");
                if (size + cost > 8000) Flush();
                batch.Add(path);
                size += cost;
            }
            Flush();
            return result.ToString();
        }
        var status = ObservePaths("status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames");
        var submodules = Run("submodule", "status", "--recursive");
        var staged = ObservePaths("diff", "--cached", "--binary", "--full-index", "--no-renames");
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
