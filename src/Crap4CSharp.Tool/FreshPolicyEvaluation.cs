using Crap4CSharp.Core;

internal sealed record FreshPolicyDecision(PolicyEvaluationResult Evaluation, IReadOnlyList<PolicyObservation> Observations,
    IReadOnlyList<FindingResult> Findings, ParsedPolicyExemptions Exemptions, string CompatibilityHash);

// Pure evaluation only: does not attest execution, publish artifacts or turn captured
// replay into current/verified provenance. The orchestration boundary owns those gates.
internal static class FreshPolicyEvaluation
{
    internal static FreshPolicyDecision Evaluate(FreshCheckPreflight preflight, RunManifest manifest, ResultDocument replay)
        => throw new NotImplementedException("Trusted fresh policy composition is not implemented.");
}
