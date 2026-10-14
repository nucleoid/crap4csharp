using Crap4CSharp.Core;

internal sealed record FreshCheckRequest(string? Project, string PolicyPath, string? BaseRef,
    string? Scope, string? Head, string SourceState, string Granularity,
    string? Output, string? ArtifactOutput, bool NoRestore, bool NoBuild, string? BuildArtifacts,
    PolicyOverrides Overrides, TimeSpan Timeout)
{
    // Parent-only post-build acquisition control; no CLI selector. Public
    // orchestration enables it explicitly; direct ordinary controls default off.
    internal bool WarmPostBuildProbe { get; init; }
    internal bool NativeSdkIdentityProbe { get; init; }
}
internal sealed record FreshCheckPreflight(string Root, FreshCheckRequest Request,
    TrustedPolicyResolution Trusted, RepositoryPolicy Policy, CapturedChangeScope? Scope)
{
    internal CapturedChangeScope? GitSnapshot { get; init; }
    internal IReadOnlyDictionary<string, string>? LocalSnapshot { get; init; }
}

internal static class FreshCheckPreflightLoader
{
    internal static async Task<FreshCheckPreflight> LoadAsync(FreshCheckRequest request, string workingDirectory,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Scope == "staged" || request.SourceState == "head")
            throw new PolicyException("scope.executionSnapshotUnsupported", "Fresh check executes only current worktree bytes; staged/head snapshot execution is not supported.");
        if (request.Scope is not (null or "all" or "worktree" or "base") || request.SourceState != "worktree" ||
            request.Granularity is not ("method" or "file")) throw new ArgumentException("Unsupported fresh scope/source-state/granularity selector.");
        if (request.NoBuild != (request.BuildArtifacts is not null))
            throw new ArgumentException("Fresh --no-build requires exactly one explicit --build-artifacts manifest; prior build evidence never comes from warm bin/obj files.");
        if (request.BaseRef is null)
        {
            if (request.Scope != "all" || request.Head is not null)
                throw new PolicyException("scope.gitEvidenceRequired", "Local fresh check requires explicit all scope and no Git head selector.");
            var localRoot = Path.GetFullPath(workingDirectory);
            var local = TrustedPolicyLoader.LoadLocal(localRoot, request.PolicyPath);
            var validationLocal = PolicyOverrideValidator.Validate(local.Policy.Policy, request.Overrides with { Scope = "all" });
            if (!validationLocal.Allowed)
                throw new PolicyException(validationLocal.Reasons[0], "CLI override cannot weaken local policy: " + string.Join(", ", validationLocal.Reasons));
            var localPolicy = local.Policy.Policy with
            {
                Threshold = request.Overrides.Threshold ?? local.Policy.Policy.Threshold,
                Configuration = request.Overrides.Configuration ?? local.Policy.Policy.Configuration,
                TargetFrameworks = request.Overrides.Frameworks?.ToArray() ?? local.Policy.Policy.TargetFrameworks.ToArray(), Scope = "all"
            };
            ValidateDestinations(request, localRoot, local.ContentHashes.Keys.Concat(localPolicy.ProductionProjects)
                .Concat(localPolicy.TestProjects).Concat(request.Project is null ? [] : [request.Project]));
            return new(localRoot, request, local, localPolicy, null)
                { LocalSnapshot = FreshLocalProof.Capture(localRoot, token) };
        }
        var gitRoot = await ProcessRunner.RunAsync("git", ["--no-optional-locks", "rev-parse", "--show-toplevel"],
            Path.GetFullPath(workingDirectory), request.Timeout, token);
        if (gitRoot.ExitCode != 0 || string.IsNullOrWhiteSpace(gitRoot.StandardOutput))
            throw new PolicyException("scope.notGitRepository", "Base-trusted fresh check requires a Git repository.");
        var root = Path.GetFullPath(gitRoot.StandardOutput.TrimEnd('\r', '\n'));
        var trusted = TrustedPolicyLoader.LoadFromBase(request.BaseRef, request.PolicyPath,
            repositoryRoot: root, timeout: request.Timeout, cancellationToken: token);
        var effectiveScope = request.Scope ?? "base"; // Explicit --base supplies the default scope selector.
        var overrides = request.Overrides with { Scope = effectiveScope };
        var validation = PolicyOverrideValidator.Validate(trusted.Policy.Policy, overrides);
        if (!validation.Allowed) throw new PolicyException(validation.Reasons[0], "CLI override cannot weaken trusted policy: " + string.Join(", ", validation.Reasons));
        var policy = trusted.Policy.Policy with
        {
            Threshold = overrides.Threshold ?? trusted.Policy.Policy.Threshold,
            Configuration = overrides.Configuration ?? trusted.Policy.Policy.Configuration,
            TargetFrameworks = overrides.Frameworks?.ToArray() ?? trusted.Policy.Policy.TargetFrameworks.ToArray(),
            Scope = effectiveScope
        };
        if (request.Head is not null && policy.Scope != "base") throw new ArgumentException("--head requires base scope.");
        var protectedPaths = trusted.ContentHashes.Keys.Concat(policy.ProductionProjects).Concat(policy.TestProjects)
            .Concat(request.Project is null ? [] : new[] { request.Project }).Select(p => Path.GetFullPath(p, root)).ToArray();
        ValidateDestinations(request, root, protectedPaths);
        CapturedChangeScope? scope = policy.Scope == "all" ? null : await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(policy.Scope == "base" ? ChangeScopeMode.Base : ChangeScopeMode.Worktree,
                policy.Scope == "base" ? request.BaseRef : null, request.Head), root, request.Timeout, token);
        if (scope is not null && policy.Scope == "base" && scope.Revision.MergeBase != trusted.Revision)
            throw new PolicyException("provenance.scopeRevisionMismatch", "Scope and trusted policy did not resolve the same immutable base.");
        var gitSnapshot = scope is { Mode: ChangeScopeMode.Base } ? scope : await GitScopeResolver.CaptureAsync(
            new GitScopeRequest(ChangeScopeMode.Base, request.BaseRef), root, request.Timeout, token);
        if (gitSnapshot.Revision.MergeBase != trusted.Revision)
            throw new PolicyException("provenance.scopeRevisionMismatch", "Git identity and trusted policy did not resolve the same immutable base.");
        token.ThrowIfCancellationRequested();
        return new(root, request, trusted, policy, scope) { GitSnapshot = gitSnapshot };
    }

    // Baseline trust remains local. The optional immutable base selects only
    // separately observed Git identity, never reviewed policy or saved state.
    internal static async Task<FreshCheckPreflight> LoadLocalIdentityAsync(FreshCheckRequest request,
        string workingDirectory, string? identityBase, CancellationToken token)
    {
        if (request.BaseRef is not null || request.Scope != "all")
            throw new PolicyException("baseline.fullScopeRequired", "Local-trust identity observation requires explicit all scope.");
        var local = await LoadAsync(request, workingDirectory, token);
        if (identityBase is null) return local;
        var git = await GitScopeResolver.CaptureAsync(new GitScopeRequest(ChangeScopeMode.Base, identityBase),
            local.Root, request.Timeout, token);
        if (!PathIdentityPolicy.Current.Comparer.Equals(Path.GetFullPath(git.RepositoryRoot), local.Root))
            throw new PolicyException("provenance.completedPreflightUnsupported", "Current Git root differs from selected local root.");
        token.ThrowIfCancellationRequested();
        return local with { Request = request with { BaseRef = identityBase }, GitSnapshot = git, LocalSnapshot = null };
    }

    internal static void ValidateDestinations(FreshCheckRequest request, string root, IEnumerable<string> protectedPaths)
    {
        var paths = PathIdentityPolicy.Current.Comparer;
        var inputs = protectedPaths.Select(p => Path.GetFullPath(p, root)).ToArray();
        var output = request.Output is null ? null : Path.GetFullPath(request.Output, root);
        var artifact = request.ArtifactOutput is null ? null : Path.GetFullPath(request.ArtifactOutput, root);
        bool Contains(string parent, string child) => paths.Equals(parent, child) || child.StartsWith(
            Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        foreach (var destination in new[] { output, artifact }.Where(p => p is not null).Select(p => p!))
        {
            for (var component = destination; component is not null; component = Path.GetDirectoryName(component))
                if ((File.Exists(component) || Directory.Exists(component)) &&
                    (File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new PolicyException("output.aliasesPolicyInput", "Output destinations must not traverse reparse points.");
            if (inputs.Any(input => paths.Equals(input, destination) || artifact == destination && Contains(destination, input)))
                throw new PolicyException("output.aliasesPolicyInput", "Output aliases a trusted policy overlay or selected consumer input.");
        }
        if (output is not null) OutputDestinationSafety.RejectExistingConsumerFile(output, root);
        if (artifact is not null && (File.Exists(artifact) || Directory.Exists(artifact)))
            throw new PolicyException("output.aliasesPolicyInput", "Artifact output must name a new invocation-owned directory.");
        if (output is not null && artifact is not null && (Contains(artifact, output) || Contains(output, artifact)))
            throw new PolicyException("output.aliasesPolicyInput", "Result and bundle destinations must not overlap.");
    }
}
