using System.Text.Json;
using Crap4CSharp.Core;

internal static class PolicyCheckCommand
{
    public static async Task<ResultDocument> RunAsync(string manifestPath, string policyPath, string baseRef,
        string workingDirectory, string? outputPath, TimeSpan timeout, DateTimeOffset startedAt, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workingDirectory);
        var bundle = ArtifactBundle.Load(manifestPath, root);
        if (outputPath is not null) bundle.RejectOutputAlias(outputPath, root);
        var trusted = TrustedPolicyLoader.LoadFromBase(baseRef, policyPath, repositoryRoot: root);
        var policy = trusted.Policy.Policy;
        var compatibilityHash = TrustedPolicyLoader.BoundCompatibilityHash(trusted.Policy, trusted.ExemptionBytes);
        ValidatePolicyCoverage(bundle.Manifest, policy);
        ValidateGeneratedInventory(bundle.Manifest);
        var structural = ProvenanceVerifier.VerifyCaptureCancellable(bundle.Manifest, bundle.Bytes,
            policy.Ruleset, cancellationToken);
        if (structural.Status == ProvenanceStatus.Invalid)
            throw new InvalidDataException(string.Join(", ", structural.Reasons));
        var current = await CurrentEvidenceAdapter.CaptureSupportedAsync(bundle.Manifest, root, timeout,
            cancellationToken);
        if (outputPath is not null) RejectOutputAlias(outputPath, root, trusted, current);
        var provenance = ProvenanceVerifier.VerifyCurrent(bundle.Manifest, bundle.Bytes, current, true,
            policy.Ruleset);
        if (provenance.Status != ProvenanceStatus.Verified)
            throw new InvalidDataException(string.Join(", ", provenance.Reasons));

        var captured = CapturedEvaluationInputs.Read(bundle);
        var replay = AnalyzeCommand.Replay(bundle, root, startedAt, duration, cancellationToken, allSources: true);
        var scope = await ResolveScopeAsync(policy, baseRef, root, timeout, cancellationToken);
        var allSources = bundle.Manifest.Contexts.SelectMany(context => context.Inputs)
            .Where(input => input.Role == "source" && !input.Generated).Select(input => input.LogicalPath)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var expectedScopedSources = policy.Scope == "all" || scope!.Value.Widened ? allSources : scope.Value.Files
            .Select(file => file.NewPath).Where(path => path is not null).Select(path => path!)
            .Concat(scope.Value.ProductionExcludedPaths).Where(path => allSources.Contains(path, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!captured.Scope.Sources.Order(StringComparer.Ordinal).SequenceEqual(expectedScopedSources, StringComparer.Ordinal))
            throw new PolicyException("policy.scopeMismatch", "Captured scope differs from the independently observed trusted-policy scope.");

        bool Selected(CallableResult callable)
            => IsSelected(policy.Scope, scope?.Widened ?? false, scope?.Files ?? [], callable,
                scope?.ProductionExcludedPaths ?? []);

        var callableSelections = (replay.Evaluation.Callables ?? []).ToDictionary(item => item.ObservationId,
            Selected, StringComparer.Ordinal);
        var observations = PolicyObservations(replay, policy, callableSelections).ToArray();
        var exemptionResolution = ParseExemptions(trusted.ExemptionBytes, replay);
        var exemptions = exemptionResolution.Active;
        var evaluated = PolicyEvaluator.Evaluate(policy, trusted.Baseline, observations, exemptions,
            compatibilityHash);
        var policyFindings = evaluated.Findings.Select(finding => ToFinding(finding, replay, policy.Threshold)).ToArray();
        var checks = new List<CheckResult>
        {
            new("policyTrust", "pass", "policy.baseTrusted", true),
            new("provenance", "pass", "provenance.currentWorkspaceMatch", true),
            new("testExecution", "pass", "tests.capturedSuccessful", policy.RequiredChecks.Contains("tests")),
            new("coverage", evaluated.OperationalReasons.Count == 0 ? "pass" : "operationalError",
                evaluated.OperationalReasons.FirstOrDefault() ?? "coverage.complete", policy.RequiredChecks.Contains("coverage")),
            new("crap", evaluated.Findings.Any(item => item.Decision == "fail") ? "fail" : "pass",
                evaluated.Findings.Any(item => item.Decision == "fail") ? "crap.thresholdExceeded" : "crap.withinThreshold",
                policy.RequiredChecks.Contains("crap"))
        };
        var decision = new EvaluationDecision(evaluated.ExitCode != 1, evaluated.Decision,
            evaluated.ExitCode == 1 ? evaluated.OperationalReasons.FirstOrDefault() ?? "policy.unknown" :
            evaluated.ExitCode == 2 ? "crap.thresholdExceeded" : "crap.withinThreshold");
        var differences = TrustedPolicyLoader.CompareProposed(root, trusted)
            .Select(item => new PolicyDifferenceResult(item.Path, item.Status, item.TrustedHash, item.ProposedHash)).ToArray();
        var evaluation = replay.Evaluation with
        {
            InvocationMode = "check",
            Policy = new PolicyOptions(policy.Threshold, false),
            Checks = checks,
            Findings = policyFindings,
            Decision = decision,
            Provenance = new EvaluationProvenance("verified", provenance.Basis, true, provenance.Reusable,
                provenance.Reasons),
            PolicyTrust = new PolicyTrustResult(trusted.Trust, trusted.Revision, trusted.Policy.PolicyPath,
                trusted.Policy.Hash, compatibilityHash),
            PolicyDifferences = differences,
            CallableExemptions = exemptionResolution.Matches,
            ExemptionErrors = []
        };
        return replay with
        {
            Evaluation = evaluation,
            Run = replay.Run with { Status = evaluated.ExitCode == 1 ? "operationalError" : "completed",
                ExitCode = evaluated.ExitCode,
                Artifacts = replay.Run.Artifacts.Select(item => item.Kind == "manifest"
                    ? item with { Reusable = provenance.Reusable } : item).ToArray() }
        };
    }

    private static async Task<ResolvedPolicyScope?> ResolveScopeAsync(RepositoryPolicy policy, string baseRef,
        string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (policy.Scope == "all") return null;
        var request = policy.Scope == "base" ? new GitScopeRequest(ChangeScopeMode.Base, baseRef) :
            new GitScopeRequest(ChangeScopeMode.Worktree);
        var result = await GitScopeResolver.CaptureAsync(request, root, timeout, cancellationToken);
        var excluded = result.Diagnostics.Where(item => item.StartsWith("scope.excludedTestSource:", StringComparison.Ordinal) ||
                item.StartsWith("scope.excludedChangedSource:", StringComparison.Ordinal))
            .Select(item => item[(item.IndexOf(':') + 1)..]).Distinct(StringComparer.Ordinal).ToArray();
        return new ResolvedPolicyScope(result.Files, result.Completeness != ScopeCompleteness.Complete, excluded);
    }

    private static IEnumerable<PolicyObservation> PolicyObservations(ResultDocument result, RepositoryPolicy policy,
        IReadOnlyDictionary<string, bool> selected)
    {
        var callables = result.Evaluation.Callables ?? [];
        foreach (var callable in callables.Where(item => item.Applicability == "applicable" &&
                     !Excluded(policy, item.Path)))
        {
            var ambiguous = callable.CoverageReason == CoverageReasonCodes.AmbiguousCallableOwnership;
            yield return new PolicyObservation(callable.Kind is "Lambda" or "AnonymousMethod" or "lambda" or "anonymous-method"
                    ? "anonymous" : "method", ambiguous ? callable.ObservationId : callable.CallableId,
                "crap.thresholdExceeded", callable.Ruleset, callable.ContextId, callable.Path, callable.BodyChecksum,
                callable.Complexity ?? 0, callable.Coverage, callable.Crap, callable.CoverageReason,
                selected.GetValueOrDefault(callable.ObservationId), ambiguous);
        }
        foreach (var family in result.Evaluation.Families ?? [])
        {
            var roots = callables.Where(item => item.CallableId == family.RootCallableId).ToArray();
            if (roots.Length != 1) throw new InvalidDataException("A callable family did not resolve uniquely.");
            var root = roots[0];
            if (Excluded(policy, root.Path)) continue;
            var familySelected = callables.Where(item => item.FamilyIds.Contains(family.FamilyId, StringComparer.Ordinal))
                .Any(item => selected.GetValueOrDefault(item.ObservationId));
            yield return new PolicyObservation("family", family.FamilyId, CallableFamilyEvaluator.Rule,
                result.ComplexityRulesetVersion, root.ContextId, root.Path, root.BodyChecksum, family.Complexity,
                family.Coverage, family.Crap, family.IncompleteReasons.FirstOrDefault(), familySelected, false)
                { RelatedEntityKeys = family.IncompleteCallableIds };
        }
    }

    private static bool Excluded(RepositoryPolicy policy, string path) => policy.Exclusions.Any(exclusion =>
        path == exclusion || path.StartsWith(exclusion.TrimEnd('/') + "/", StringComparison.Ordinal));

    internal static ParsedPolicyExemptions ParseExemptions(IReadOnlyDictionary<string, byte[]> files,
        ResultDocument result)
    {
        var callables = result.Evaluation.Callables ?? [];
        var active = new List<PolicyExemption>();
        var reported = new List<CallableExemptionMatch>();
        foreach (var file in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(file.Value, new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            var root = document.RootElement;
            RequireKeys(root, ["version", "entries"]);
            if (root.GetProperty("version").GetString() != CallableExemptions.Version ||
                root.GetProperty("entries").ValueKind != JsonValueKind.Array)
                throw new PolicyException("exemption.malformed", $"Trusted exemption file is malformed: {file.Key}");
            foreach (var entry in root.GetProperty("entries").EnumerateArray())
            {
                RequireKeys(entry, ["ruleset", "contextId", "targetFramework", "callableId", "bodyChecksum",
                    "reasonCode", "justification", "reviewReference", "familyIds"]);
                string Text(string name) => entry.GetProperty(name).ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(entry.GetProperty(name).GetString()) ? entry.GetProperty(name).GetString()! :
                    throw new PolicyException("exemption.malformed", $"Trusted exemption field '{name}' is invalid.");
                var callableId = Text("callableId");
                var contextId = Text("contextId");
                var reason = Text("reasonCode");
                var targetFramework = Text("targetFramework");
                var context = result.Evaluation.Contexts.SingleOrDefault(item => item.Id == contextId);
                var familyElement = entry.GetProperty("familyIds");
                if (familyElement.ValueKind != JsonValueKind.Array ||
                    familyElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(item.GetString())))
                    throw new PolicyException("exemption.malformed", "Trusted exemption familyIds are invalid.");
                var familyIds = familyElement.EnumerateArray().Select(item => item.GetString()!)
                    .Order(StringComparer.Ordinal).ToArray();
                if (familyIds.Distinct(StringComparer.Ordinal).Count() != familyIds.Length)
                    throw new PolicyException("exemption.duplicateFamily", "Trusted exemption repeats a family identity.");
                var bodyChecksum = Text("bodyChecksum");
                var ruleset = Text("ruleset");
                var matches = callables.Where(item => item.CallableId == callableId && item.ContextId == contextId &&
                    item.BodyChecksum == bodyChecksum && item.Ruleset == ruleset &&
                    item.CoverageReason == reason).ToArray();
                if (reason is not (CoverageReasonCodes.UnsupportedGeneratedMapping or
                        CoverageReasonCodes.UnsupportedCallable or CoverageReasonCodes.AmbiguousCallableOwnership))
                    throw new PolicyException("exemption.reasonNotUnsupported", "Trusted exemption reason is not narrowly unsupported.");
                if (matches.Length > 1)
                    throw new PolicyException("exemption.multiplyMatched", "Trusted exemption matches multiple current callables.");
                if (matches.Length == 1 && !matches[0].FamilyIds.Order(StringComparer.Ordinal)
                        .SequenceEqual(familyIds, StringComparer.Ordinal))
                    throw new PolicyException("exemption.familyAcknowledgementMismatch",
                        "Trusted exemption must acknowledge exactly the affected callable families.");
                var justification = Text("justification");
                var reviewReference = Text("reviewReference");
                if (new[] { callableId, contextId, reason, targetFramework, justification, reviewReference }
                    .Any(value => value.Contains('*', StringComparison.Ordinal)))
                    throw new PolicyException("exemption.wildcardRejected", "Trusted exemptions cannot contain wildcards.");
                if (matches.Length == 0 || context?.TargetFramework != targetFramework)
                {
                    reported.Add(new CallableExemptionMatch(callableId, "stale-ignored", reason, justification,
                        reviewReference, familyIds, true));
                    continue;
                }
                var entityKey = reason == CoverageReasonCodes.AmbiguousCallableOwnership
                    ? matches[0].ObservationId : callableId;
                active.Add(new PolicyExemption(entityKey, "crap.thresholdExceeded", contextId, reason, justification));
                reported.Add(new CallableExemptionMatch(entityKey, "exempted-unsupported", reason, justification,
                    reviewReference, familyIds, true));
            }
        }
        if (active.GroupBy(item => (item.EntityKey, item.Rule, item.ContextId, item.CoverageReason))
            .Any(group => group.Count() > 1))
            throw new PolicyException("exemption.duplicate", "Trusted exemption entries contain duplicate identities.");
        return new ParsedPolicyExemptions(active, reported);
    }

    private static void RequireKeys(JsonElement value, IReadOnlyCollection<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(item => item.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal) == false)
            throw new PolicyException("exemption.malformed", "Trusted exemption contains missing, duplicate, or unknown keys.");
    }

    private static FindingResult ToFinding(PolicyFinding finding, ResultDocument result, double threshold)
    {
        var callable = (result.Evaluation.Callables ?? []).FirstOrDefault(item => item.CallableId == finding.EntityKey);
        var span = callable is null ? new SourceSpan(1, 1) : new SourceSpan(callable.Span.StartLine, callable.Span.EndLine);
        return new FindingResult(FindingIdentity.Create(finding.ContextId, finding.Path, finding.EntityKey, span, finding.Code),
            finding.EntityKey, finding.Code, finding.Decision == "fail" ? "error" : "info", "policy",
            finding.ContextId, finding.Path, finding.EntityKey, callable?.SemanticSignature, span,
            callable?.Complexity ?? 0, callable?.Coverage, callable?.Crap, callable?.CoverageReason, threshold,
            "gt", finding.Decision, finding.Reasons);
    }

    private static void ValidatePolicyCoverage(RunManifest manifest, RepositoryPolicy policy)
    {
        var contexts = manifest.Contexts.Select(item => (CanonicalIdentity.NormalizeLogicalPath(item.Project),
            item.TargetFramework, item.Configuration)).OrderBy(item => item.Item1, StringComparer.Ordinal)
            .ThenBy(item => item.TargetFramework, StringComparer.Ordinal).ToArray();
        var expected = policy.ProductionProjects.SelectMany(project => policy.TargetFrameworks.Select(framework =>
            (project, framework, policy.Configuration))).OrderBy(item => item.project, StringComparer.Ordinal)
            .ThenBy(item => item.framework, StringComparer.Ordinal).ToArray();
        if (!contexts.SequenceEqual(expected))
            throw new PolicyException("policy.productionContextMismatch", "Captured production contexts differ from trusted policy.");
        var tests = manifest.Executions.Select(item => item.TestProject).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(CanonicalIdentity.NormalizeLogicalPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!tests.SequenceEqual(policy.TestProjects.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new PolicyException("policy.testTargetMismatch", "Captured test targets differ from trusted policy.");
    }

    internal static bool IsSelected(string scope, bool widened, IReadOnlyList<ChangedFile> files,
        CallableResult callable, IReadOnlyList<string>? productionExcludedPaths = null) => scope == "all" || widened ||
        (productionExcludedPaths ?? []).Contains(callable.Path, StringComparer.Ordinal) ||
        files.Where(file => file.NewPath == callable.Path)
        .Any(file => file.Kind is ScopeChangeKind.Added or ScopeChangeKind.Copied ||
            file.DeletedRanges.Count > 0 ||
            file.AddedRanges.Any(range => range.Intersects(callable.Span.StartLine, callable.Span.EndLine)));

    internal static void ValidateGeneratedInventory(RunManifest manifest)
    {
        var branchClassifiedGenerated = manifest.Contexts.SelectMany(context => context.Inputs)
            .Where(input => input.Role == "source" && input.Generated)
            .Where(input => !input.LogicalPath.Split('/').Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .Select(input => input.LogicalPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (branchClassifiedGenerated.Length > 0)
            throw new PolicyException("policy.branchGeneratedSource",
                "A production source was classified as generated outside an evaluated obj output: " +
                string.Join(", ", branchClassifiedGenerated));
    }

    internal static void RejectOutputAlias(string outputPath, string root, TrustedPolicyResolution trusted,
        CurrentEvidence current)
    {
        var candidate = Path.GetFullPath(outputPath, root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var overlays = trusted.ContentHashes.Keys.Select(path => Path.GetFullPath(path, root));
        if (overlays.Concat(current.ProtectedPaths).Any(path => string.Equals(Path.GetFullPath(path), candidate, comparison)))
            throw new PolicyException("output.aliasesPolicyInput",
                "Output path aliases a source or trusted policy overlay.");
    }

    private readonly record struct ResolvedPolicyScope(IReadOnlyList<ChangedFile> Files, bool Widened,
        IReadOnlyList<string> ProductionExcludedPaths);
}

internal sealed record ParsedPolicyExemptions(IReadOnlyList<PolicyExemption> Active,
    IReadOnlyList<CallableExemptionMatch> Matches);
