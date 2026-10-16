using Crap4CSharp.Core;

internal sealed record FreshPolicyDecision(PolicyEvaluationResult Evaluation, IReadOnlyList<PolicyObservation> Observations,
    IReadOnlyList<FindingResult> Findings, ParsedPolicyExemptions Exemptions, string CompatibilityHash, bool HasApplicableCrap);

// Pure evaluation only: does not attest execution, publish artifacts or turn captured
// replay into current/verified provenance. The orchestration boundary owns those gates.
internal static class FreshPolicyEvaluation
{
    internal static FreshPolicyDecision Evaluate(FreshCheckPreflight preflight, RunManifest manifest, ResultDocument replay)
    {
        var policy = preflight.Policy;
        if (policy.Ruleset != ComplexityRules.CallablesV1 || replay.ComplexityRulesetVersion != policy.Ruleset ||
            replay.Evaluation.Callables is null)
            throw new PolicyException("policy.rulesetMismatch", "Fresh callable evaluation requires the actual selected callable ruleset.");
        var scope = preflight.Scope;
        if (policy.Scope != "all" && scope is null)
            throw new PolicyException("policy.scopeMismatch", "Fresh selected scope must be independently captured before execution.");
        if (scope is not null)
            PolicyCheckCommand.ValidateScopeRevision(policy.Scope, preflight.Trusted.Revision, manifest.Revision, scope.Revision);
        var excluded = (scope?.Diagnostics ?? []).Where(d => d.StartsWith("scope.excludedTestSource:", StringComparison.Ordinal) ||
            d.StartsWith("scope.excludedChangedSource:", StringComparison.Ordinal)).Select(d => d[(d.IndexOf(':') + 1)..]).ToArray();
        var widened = scope is { Completeness: not ScopeCompleteness.Complete };
        string RepositoryPath(CallableResult callable)
        {
            var context = manifest.Contexts.Single(c => c.Id == callable.ContextId);
            var input = context.Inputs.Single(i => i.Role == "source" && i.LogicalPath == callable.Path);
            return CapturedEvaluationInputs.DeclaredRepositorySourcePath(context, input);
        }
        bool Selected(CallableResult callable)
        {
            var path = RepositoryPath(callable);
            PolicyCheckCommand.ValidateScopeSourceCase([path], (scope?.Files ?? []).Where(f => f.NewPath is not null)
                .Select(f => f.NewPath!).Concat(excluded));
            if (preflight.Request.Granularity == "file")
                return policy.Scope == "all" || widened || excluded.Contains(path, StringComparer.Ordinal) ||
                    (scope?.Files ?? []).Any(f => f.NewPath == path);
            if (preflight.Request.Granularity != "method") throw new ArgumentException("Unsupported fresh granularity.");
            return PolicyCheckCommand.IsSelected(policy.Scope, widened, scope?.Files ?? [], callable, path, excluded);
        }
        var selections = replay.Evaluation.Callables.ToDictionary(c => c.ObservationId, Selected, StringComparer.Ordinal);
        var observations = PolicyCheckCommand.PolicyObservations(replay, manifest, policy, selections).ToArray();
        var exemptions = PolicyCheckCommand.ParseExemptions(preflight.Trusted.ExemptionBytes, replay);
        // Compatibility stays bound to the original reviewed policy/waivers; a
        // permitted tighter threshold changes gates, not approved debt identity.
        var compatibility = TrustedPolicyLoader.BoundCompatibilityHash(preflight.Trusted.Policy, preflight.Trusted.ExemptionBytes);
        var evaluated = PolicyEvaluator.Evaluate(policy, preflight.Trusted.Baseline, observations, exemptions.Active, compatibility);
        return new(evaluated, observations, evaluated.Findings.Select(f => PolicyCheckCommand.ToFinding(f, replay, policy.Threshold)).ToArray(),
            exemptions, compatibility, observations.Any(o => policy.Mode == RepositoryPolicyMode.Strict || o.Selected));
    }
}
