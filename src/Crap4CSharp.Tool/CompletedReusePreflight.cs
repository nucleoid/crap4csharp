using Crap4CSharp.Core;

// Current policy/root/selection observation only; caller still owes the complete
// adapter, original VerifyCurrent and a new acquisition at the final boundary.
internal static class CompletedReusePreflight
{
    internal static async Task<FreshCheckPreflight> LoadAsync(ArtifactBundle bundle, string workingDirectory,
        string policyPath, string? explicitBase, bool baselineLocal, string? output,
        TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(workingDirectory);
        CurrentEvidenceAdapter.ValidateSupportedRecipes(bundle.Manifest);
        if (bundle.Manifest.Contexts.Any(c => c.CurrentRevalidation!.Provider != CompletedRevalidationDeclaration.Provider))
            throw Refuse();
        var plan = NeutralBytes(bundle, CompletedTestPlanRecipe.Kind, token);
        var scope = NeutralBytes(bundle, "scope", token);
        var selectedRoot = CompletedTestPlanRecipe.ResolveSelectedRoot(plan, root, token);
        var selectors = CompletedReuseSelectors.Read(scope, plan, token);
        var captured = CapturedEvaluationInputs.Read(bundle);
        if (baselineLocal && selectors.Scope != "all")
            throw new PolicyException("baseline.fullScopeRequired", "Baseline generation requires captured all scope.");
        string? identityBase;
        if (bundle.Manifest.Revision.Kind == "git")
        {
            identityBase = bundle.Manifest.Revision.Base;
            if (!Commit(identityBase) || !Commit(bundle.Manifest.Revision.Head) || !Commit(bundle.Manifest.Revision.ScopeHead)) throw Refuse();
            if (!baselineLocal && explicitBase is null) throw Refuse();
        }
        else if (bundle.Manifest.Revision.Kind == "none" && explicitBase is null && selectors.Scope == "all")
            identityBase = null;
        else throw Refuse();
        var localTrust = baselineLocal || bundle.Manifest.Revision.Kind == "none";
        var request = new FreshCheckRequest(selectedRoot, policyPath, baselineLocal ? null : explicitBase,
            selectors.Scope, null, "worktree", selectors.Granularity, baselineLocal ? null : output, null, true, false, null,
            new PolicyOverrides(Threshold: captured.Policy.Threshold,
                IncludeTests: selectors.IncludeTests ? true : null,
                IncludeGenerated: selectors.IncludeGenerated ? true : null), timeout);
        var preflight = localTrust
            ? await FreshCheckPreflightLoader.LoadLocalIdentityAsync(request, root, identityBase, token)
            : await FreshCheckPreflightLoader.LoadAsync(request, root, token);
        if (!PathIdentityPolicy.Current.Comparer.Equals(preflight.Root, root)) throw Refuse();
        if (localTrust)
        {
            if (preflight.Trusted.Trust != "local-unreviewed" || preflight.Trusted.Revision != "none" ||
                preflight.Policy.Mode != RepositoryPolicyMode.Strict || preflight.Policy.Scope != "all") throw Refuse();
        }
        CompletedTrustedPolicy.Validate(bundle.Manifest.Artifacts, bundle.Bytes,
            preflight.Trusted.Policy.CanonicalBytes, preflight.Trusted.ExemptionBytes, token);
        token.ThrowIfCancellationRequested();
        return preflight;
    }

    internal static void ValidateScope(ArtifactBundle bundle, FreshCheckPreflight current)
    {
        var available = bundle.Manifest.Contexts.SelectMany(c => c.Inputs
            .Where(i => i.Role == "source" && (!i.Generated || c.IncludeGenerated))
            .Select(i => CapturedEvaluationInputs.DeclaredRepositorySourcePath(c, i)))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var scope = current.Scope;
        var expected = scope is null || scope.Completeness == ScopeCompleteness.ContextIncomplete ? available :
            scope.Files.Select(f => f.NewPath).Where(p => p is not null && available.Contains(p, StringComparer.Ordinal))
                .Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        PolicyCheckCommand.ValidateScopeSourceCase(available, (scope?.Files ?? []).Where(f => f.NewPath is not null)
            .Select(f => f.NewPath!));
        if (!CapturedEvaluationInputs.Read(bundle).Scope.Sources.Order(StringComparer.Ordinal)
            .SequenceEqual(expected, StringComparer.Ordinal))
            throw new PolicyException("policy.scopeMismatch", "Captured scope differs from independently current selection.");
    }

    private static byte[] NeutralBytes(ArtifactBundle bundle, string kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var matches = bundle.Manifest.Artifacts.Where(a => a.Kind == kind).ToArray();
        if (matches.Length != 1) throw Refuse();
        var artifact = matches[0];
        if (artifact.Format != "json" || artifact.ContextId is not null || artifact.BuildId is not null ||
            artifact.ExecutionId is not null || artifact.CoordinateKind is not null ||
            !bundle.Bytes.TryGetValue(artifact.Locator, out var bytes) || bytes.IsDefault ||
            bytes.Length != artifact.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != artifact.Sha256) throw Refuse();
        return bytes.ToArray();
    }
    private static bool Commit(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static PolicyException Refuse() => new("provenance.completedPreflightUnsupported",
        "Completed reuse cannot independently reconstruct a supported current root, policy and selection.");
}
