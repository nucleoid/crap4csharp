using Crap4CSharp.Core;

// Raw outcome terminal only. No accepted analysis, manifest or reusable success is
// inferred from exit zero, filenames, prior completed contexts or a failing TRX.
internal static class FreshFailureResult
{
    internal static ResultDocument Create(FreshExecutionEvidence evidence, DateTimeOffset started, TimeSpan duration)
    {
        var tests = evidence.Tests;
        if (tests.Failure is null && tests.Count == evidence.Plan.Tests.Count && tests.All(t => t.Process.ExitCode == 0))
            throw new InvalidOperationException("Successful execution requires the final analysis/publication path.");
        var cancelled = tests.Failure is OperationCanceledException;
        var timedOut = tests.Failure is ProcessTimeoutException;
        var reason = cancelled ? "run.cancelled" : timedOut ? "run.timedOut" : tests.Failure is ProjectContextException context ? context.Reason :
            tests.Failure is PolicyException policy ? policy.Code : tests.Any(t => t.Process.ExitCode != 0) ? "tests.failed" : "tests.incomplete";
        var outcomes = tests.Outcomes.Select(o => new TestExecutionOutcomeResult(o.GraphId,
            evidence.Plan.Tests.Single(t => t.GraphId == o.GraphId).ProjectPath, o.State, o.Process?.ExitCode, o.ResultsDirectory, o.LogDirectory)).ToArray();
        var artifacts = tests.Outcomes.SelectMany(o => new[]
        {
            o.ResultsDirectory is null ? null : new RunArtifact("partial-test-results", o.ResultsDirectory, "retained", false, false),
            o.LogDirectory is null ? null : new RunArtifact("private-diagnostic-logs", o.LogDirectory, "retained", false, false)
        }).Where(a => a is not null).Select(a => a!).ToArray();
        var evaluation = new EvaluationSection("check", new PolicyOptions(evidence.Preflight.Policy.Threshold, false),
            new EvaluationScope(evidence.Preflight.Root, []), [],
            [new("policyTrust", "pass", evidence.Preflight.Trusted.Trust == "base-trusted" ? "policy.baseTrusted" : "policy.localUnreviewed", true),
                new("testExecution", cancelled ? "cancelled" : tests.Any(t => t.Process.ExitCode != 0) ? "fail" : "operationalError", reason, true),
                new("provenance", "operationalError", "provenance.incompleteExecution", true),
                new("coverage", "operationalError", "coverage.unacceptedPartialEvidence", true),
                new("crap", "operationalError", "crap.analysisIncomplete", true)], [], [],
            new CoverageSummary(0,0,0,new Dictionary<string,int>()), [], new EvaluationDecision(false, "unknown", reason))
            { Callables = [], Families = [], Provenance = new("invalid", "none", false, false, [reason]),
                PolicyTrust = new(evidence.Preflight.Trusted.Trust, evidence.Preflight.Trusted.Revision,
                    evidence.Preflight.Trusted.Policy.PolicyPath, evidence.Preflight.Trusted.Policy.Hash,
                    TrustedPolicyLoader.BoundCompatibilityHash(evidence.Preflight.Trusted.Policy, evidence.Preflight.Trusted.ExemptionBytes)) };
        var run = new RunSection(Guid.NewGuid().ToString("D"), started, started + duration, duration.TotalMilliseconds,
            [], artifacts.Select(a => a.Path).ToArray(), artifacts, new(cancelled,timedOut, cancelled || timedOut ? reason : null),
            cancelled ? "cancelled" : "operationalError", 1) { TestOutcomes = outcomes };
        return new(ResultContract.SchemaVersion, typeof(FreshFailureResult).Assembly.GetName().Version!.ToString(),
            ComplexityRules.CallablesV1, evaluation, run);
    }
}
