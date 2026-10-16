using Crap4CSharp.Core;

// Raw outcome terminal only. No accepted analysis, manifest or reusable success is
// inferred from exit zero, filenames, prior completed contexts or a failing TRX.
internal static class FreshFailureResult
{
    internal static ResultDocument Refuse(FreshExecutionEvidence evidence, Exception error,
        DateTimeOffset started, TimeSpan duration, bool deadlineExpired = false)
    {
        // Preserve raw child results and ordering; invalidate only their aggregate
        // acceptance after capture, promotion or terminal-publication refusal.
        var refused = new TestExecutionRun(evidence.Tests.ToArray(), evidence.Tests.Outcomes, error);
        return Create(evidence with { Tests = refused, Coverage = null }, started, duration, deadlineExpired);
    }

    internal static ResultDocument Create(FreshExecutionEvidence evidence, DateTimeOffset started, TimeSpan duration, bool deadlineExpired = false)
    {
        var tests = evidence.Tests;
        if (tests.Failure is null && tests.Count == evidence.Plan.Tests.Count && tests.All(t => t.Process.ExitCode == 0))
            throw new InvalidOperationException("Successful execution requires the final analysis/publication path.");
        var cancelled = tests.Failure is OperationCanceledException && !deadlineExpired;
        var timedOut = tests.Failure is ProcessTimeoutException || deadlineExpired;
        var failed = tests.Any(t => t.Process.ExitCode != 0);
        var interruptionReason = cancelled ? "run.cancelled" : timedOut ? "run.timedOut" : null;
        // A later interrupted context cannot erase an earlier completed nonzero
        // test process. Keep its raw failure primary without reading new TRX or
        // coverage evidence after cancellation; retain interruption separately.
        var reason = failed ? "tests.failed" :
            cancelled ? "run.cancelled" : timedOut ? "run.timedOut" : tests.Failure is ProjectContextException context ? context.Reason :
            tests.Failure is PolicyException policy ? policy.Code : tests.Failure is ScopeException scope ? scope.Reason :
            tests.Count == evidence.Plan.Tests.Count && tests.All(t => t.Process.ExitCode == 0)
                ? "provenance.postTestEvidenceUnproven" : "tests.incomplete";
        var outcomes = tests.Outcomes.Select(o => new TestExecutionOutcomeResult(o.GraphId,
            evidence.Plan.Tests.Single(t => t.GraphId == o.GraphId).ProjectPath, o.State, o.Process?.ExitCode, o.ResultsDirectory, o.LogDirectory)).ToArray();
        var artifacts = tests.Outcomes.SelectMany(o => new[]
        {
            o.ResultsDirectory is null ? null : new RunArtifact("partial-test-results", o.ResultsDirectory, "retained", false, false),
            o.LogDirectory is null ? null : new RunArtifact("private-diagnostic-logs", o.LogDirectory, "retained", false, false)
        }).Where(a => a is not null).Select(a => a!).ToArray();
        var evaluation = new EvaluationSection("check", new PolicyOptions(evidence.Preflight.Policy.Threshold, false),
            new EvaluationScope(".", []), [],
            [new("policyTrust", "pass", evidence.Preflight.Trusted.Trust == "base-trusted" ? "policy.baseTrusted" : "policy.localUnreviewed", true),
                new("testExecution", failed ? "fail" : cancelled ? "cancelled" : "operationalError", reason, true),
                new("provenance", "operationalError", "provenance.incompleteExecution", true),
                new("coverage", "operationalError", "coverage.unacceptedPartialEvidence", true),
                new("crap", "operationalError", "crap.analysisIncomplete", true)], [], [],
            new CoverageSummary(0,0,0,new Dictionary<string,int>()), [], new EvaluationDecision(false, "unknown", reason))
            { Callables = [], Families = [], Provenance = new("invalid", "none", false, false, [reason]),
                PolicyTrust = new(evidence.Preflight.Trusted.Trust, evidence.Preflight.Trusted.Revision,
                    evidence.Preflight.Trusted.Policy.PolicyPath, evidence.Preflight.Trusted.Policy.Hash,
                    TrustedPolicyLoader.BoundCompatibilityHash(evidence.Preflight.Trusted.Policy, evidence.Preflight.Trusted.ExemptionBytes)) };
        if (failed && tests.Failure is not null)
        {
            var secondary = interruptionReason ?? (tests.Failure is ProjectContextException contextError ? contextError.Reason :
                tests.Failure is PolicyException policyError ? policyError.Code : tests.Failure is ScopeException scopeError ? scopeError.Reason :
                "provenance.postTestEvidenceUnproven");
            evaluation = evaluation with { Checks = evaluation.Checks.Concat(
                [new CheckResult("postTestEvidence", "operationalError", secondary, true)]).ToArray() };
        }
        var run = new RunSection(Guid.NewGuid().ToString("D"), started, started + duration, duration.TotalMilliseconds,
            [], artifacts.Select(a => a.Path).ToArray(), artifacts, new(cancelled,timedOut, interruptionReason),
            cancelled ? "cancelled" : "operationalError", 1) { TestOutcomes = outcomes };
        return new(ResultContract.SchemaVersion, App.ToolVersion,
            ComplexityRules.CallablesV1, evaluation, run);
    }
}
