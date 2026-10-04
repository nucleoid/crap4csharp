namespace Crap4CSharp.Core;

public sealed record PolicyObservation(string Kind, string EntityKey, string Rule, string Ruleset, string ContextId,
    string Path, string BodyChecksum, int Complexity, double? Coverage, double? Crap, string? CoverageReason,
    bool Selected, bool IdentityAmbiguous);

public sealed record PolicyExemption(string EntityKey, string Rule, string ContextId, string CoverageReason,
    string Reason);

public sealed record PolicyFinding(string Code, string EntityKey, string ContextId, string Path,
    string Decision, IReadOnlyList<string> Reasons);

public sealed record PolicyEvaluationResult(int ExitCode, string Decision, IReadOnlyList<PolicyFinding> Findings,
    IReadOnlyList<string> OperationalReasons, IReadOnlyList<string> ExemptedEntityKeys);

public static class PolicyEvaluator
{
    public static PolicyEvaluationResult Evaluate(RepositoryPolicy policy, BaselineDocument? baseline,
        IReadOnlyList<PolicyObservation> observations, IReadOnlyList<PolicyExemption> exemptions,
        string? expectedPolicyHash = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(exemptions);
        var operational = new List<string>();
        if (policy.Mode == RepositoryPolicyMode.Incremental && baseline is null)
            operational.Add("baseline.required");
        if (baseline is not null)
        {
            try { BaselineDocument.Validate(baseline, expectedPolicyHash ?? baseline.PolicyHash, policy.Ruleset); }
            catch (BaselineException exception) { operational.Add(exception.Code); }
        }
        var duplicate = observations.GroupBy(item => (item.EntityKey, item.Rule, item.ContextId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) operational.Add("policy.duplicateObservation");
        if (exemptions.GroupBy(item => (item.EntityKey, item.Rule, item.ContextId, item.CoverageReason))
            .Any(group => group.Count() > 1))
            operational.Add("exemption.duplicate");

        var findings = new List<PolicyFinding>();
        var exempted = new List<string>();
        var applicable = policy.Mode == RepositoryPolicyMode.Strict ? observations : observations.Where(item => item.Selected);
        var baselineByKey = (baseline?.Entries ?? []).GroupBy(item => (item.EntityKey, item.Rule))
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var item in applicable.OrderBy(item => item.ContextId, StringComparer.Ordinal)
            .ThenBy(item => item.EntityKey, StringComparer.Ordinal).ThenBy(item => item.Rule, StringComparer.Ordinal))
        {
            if (item.Ruleset != policy.Ruleset || item.IdentityAmbiguous)
            {
                operational.Add(item.IdentityAmbiguous ? "baseline.identityAmbiguous" : "policy.rulesetMismatch");
                continue;
            }
            if (item.Coverage is null || item.Crap is null || !double.IsFinite(item.Crap.Value) ||
                !double.IsFinite(item.Coverage.Value))
            {
                var exemption = exemptions.FirstOrDefault(value => value.EntityKey == item.EntityKey &&
                    value.Rule == item.Rule && value.ContextId == item.ContextId && value.CoverageReason == item.CoverageReason &&
                    !string.IsNullOrWhiteSpace(value.Reason) && IsNarrowUnsupportedReason(value.CoverageReason));
                if (exemption is not null) exempted.Add(item.EntityKey);
                else operational.Add(item.CoverageReason ?? "coverage.unknown");
                continue;
            }
            if (item.Crap <= policy.Threshold) continue;
            if (policy.Mode == RepositoryPolicyMode.Strict || !baselineByKey.TryGetValue((item.EntityKey, item.Rule), out var allowance))
            {
                findings.Add(Finding("crap.thresholdExceeded", item, "fail"));
                continue;
            }
            if (allowance.Ruleset != item.Ruleset) { operational.Add("baseline.rulesetMismatch"); continue; }
            if (item.Complexity > allowance.Complexity)
                findings.Add(Finding("baseline.complexityWorsened", item, "fail"));
            if (item.Coverage < allowance.Coverage)
                findings.Add(Finding("baseline.coverageWorsened", item, "fail"));
            if (item.Crap > allowance.Crap)
                findings.Add(Finding("baseline.crapWorsened", item, "fail"));
            if (item.BodyChecksum != allowance.BodyChecksum)
                findings.Add(Finding("baseline.bodyFingerprintChanged", item, "observation"));
            if (item.Path != allowance.Path)
                findings.Add(Finding("baseline.pathChanged", item, "observation"));
        }

        if (baseline is not null)
        {
            var current = observations.Select(item => (item.EntityKey, item.Rule)).ToHashSet();
            findings.AddRange(baseline.Entries.Where(entry => !current.Contains((entry.EntityKey, entry.Rule)))
                .Select(entry => new PolicyFinding("baseline.staleEntry", entry.EntityKey, "", entry.Path,
                    "ignored", ["baseline.deletedEntityIgnored"])));
        }
        var orderedFindings = findings.OrderBy(item => item.ContextId, StringComparer.Ordinal)
            .ThenBy(item => item.EntityKey, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray();
        var orderedOperational = operational.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var violations = orderedFindings.Any(item => item.Decision == "fail");
        var exit = orderedOperational.Length > 0 ? 1 : violations ? 2 : 0;
        return new PolicyEvaluationResult(exit, exit == 1 ? "unknown" : exit == 2 ? "fail" : "pass",
            orderedFindings, orderedOperational, exempted.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static PolicyFinding Finding(string code, PolicyObservation item, string decision) =>
        new(code, item.EntityKey, item.ContextId, item.Path, decision, [code]);

    private static bool IsNarrowUnsupportedReason(string? reason) => reason is
        CoverageReasonCodes.UnsupportedGeneratedMapping or CoverageReasonCodes.UnsupportedCallable or
        CoverageReasonCodes.AmbiguousCallableOwnership;
}
