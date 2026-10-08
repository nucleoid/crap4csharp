using Crap4CSharp.Core;

// Complete saved evidence is independently reacquired from current inputs. The
// legacy manifest-only route has separate, deliberately unchanged semantics.
internal static class CompletedPolicyCheck
{
    internal static async Task<ResultDocument> RunAsync(ArtifactBundle bundle, string policyPath, string baseRef,
        string root, string? outputPath, TimeSpan timeout, DateTimeOffset startedAt, TimeSpan duration,
        CancellationToken token, Action<Func<Task>>? retainTerminalProof = null)
    {
        async Task<(FreshCheckPreflight Preflight, ProvenanceResult Proof)> ObserveAsync()
        {
            var preflight = await CompletedReusePreflight.LoadAsync(bundle, root, policyPath, baseRef,
                false, outputPath, timeout, token);
            CompletedReusePreflight.ValidateScope(bundle, preflight);
            var current = await CurrentEvidenceAdapter.CaptureSupportedAsync(bundle, preflight, token);
            var proof = ProvenanceVerifier.VerifyCurrent(bundle.Manifest, bundle.Bytes, current, true,
                preflight.Policy.Ruleset);
            if (proof.Status != ProvenanceStatus.Verified)
                throw new InvalidDataException(string.Join(", ", proof.Reasons));
            if (outputPath is not null)
            {
                bundle.RejectOutputAlias(outputPath, root);
                PolicyCheckCommand.RejectOutputAlias(outputPath, root, preflight.Trusted, current);
                OutputDestinationSafety.RejectExistingConsumerFile(outputPath, root);
            }
            return (preflight, proof);
        }

        var observed = await ObserveAsync();
        var preflight = observed.Preflight;
        var policy = preflight.Policy;
        var replay = AnalyzeCommand.Replay(bundle, root, startedAt, duration, token,
            allSources: true, thresholdOverride: policy.Threshold);
        // Reuse the independently current file/method/family/generated scope
        // semantics instead of inferring required tests from production executions.
        var decision = FreshPolicyEvaluation.Evaluate(preflight, bundle.Manifest, replay);
        var complete = decision.Evaluation.ExitCode != 1;
        var semanticEmpty = bundle.Manifest.Contexts.Count > 0 &&
            bundle.Manifest.Contexts.All(c => c.CoverageApplicability == "semantic-empty");
        var crapNotApplicable = complete && !decision.HasApplicableCrap;
        var crapReason = semanticEmpty ? "crap.noApplicableSourceInventory" : crapNotApplicable
            ? policy.Mode == RepositoryPolicyMode.Incremental ? "crap.noApplicableChangedCallables" : "crap.noApplicablePolicyCallables"
            : decision.Evaluation.ExitCode == 2 ? "crap.thresholdExceeded" : complete ? "crap.withinThreshold" : "crap.analysisIncomplete";
        var checks = new CheckResult[]
        {
            new("policyTrust", "pass", "policy.baseTrusted", true),
            new("provenance", "pass", "provenance.currentWorkspaceMatch", true),
            new("testExecution", "pass", "tests.capturedSuccessful", policy.RequiredChecks.Contains("tests")),
            new("coverage", semanticEmpty ? "notApplicable" : decision.Evaluation.OperationalReasons.Count == 0 ? "pass" : "operationalError",
                semanticEmpty ? "coverage.noApplicableSourceInventory" : decision.Evaluation.OperationalReasons.FirstOrDefault() ?? "coverage.complete",
                policy.RequiredChecks.Contains("coverage")),
            new("crap", crapNotApplicable ? "notApplicable" : decision.Evaluation.ExitCode == 2 ? "fail" : complete ? "pass" : "operationalError",
                crapReason, policy.RequiredChecks.Contains("crap"))
        };
        var differences = TrustedPolicyLoader.CompareProposed(root, preflight.Trusted)
            .Select(d => new PolicyDifferenceResult(d.Path, d.Status, d.TrustedHash, d.ProposedHash)).ToArray();
        var result = replay with
        {
            Evaluation = replay.Evaluation with
            {
                InvocationMode = "check", Policy = new(policy.Threshold, false), Checks = checks,
                Findings = decision.Findings,
                Decision = new(complete, decision.Evaluation.Decision, decision.Evaluation.OperationalReasons.FirstOrDefault() ??
                    (decision.Evaluation.ExitCode == 2 ? "crap.thresholdExceeded" : "crap.withinThreshold")),
                Provenance = new("verified", observed.Proof.Basis, true, observed.Proof.Reusable, observed.Proof.Reasons),
                PolicyTrust = new(preflight.Trusted.Trust, preflight.Trusted.Revision, preflight.Trusted.Policy.PolicyPath,
                    preflight.Trusted.Policy.Hash, decision.CompatibilityHash),
                PolicyDifferences = differences, CallableExemptions = decision.Exemptions.Matches, ExemptionErrors = []
            },
            Run = replay.Run with
            {
                Status = complete ? "completed" : "operationalError", ExitCode = decision.Evaluation.ExitCode,
                Artifacts = replay.Run.Artifacts.Select(a => a.Kind == "manifest"
                    ? a with { Reusable = observed.Proof.Reusable } : a).ToArray()
            }
        };
        // A new acquisition after pure evaluation binds all required contexts,
        // current trust bytes, and ORIGINAL captured revision before acceptance.
        await ObserveAsync();
        token.ThrowIfCancellationRequested();
        // Retain this invocation's ORIGINAL bundle and independently current
        // acquisition recipe. The outer writer invokes it after serialization,
        // immediately before publishing; never bootstrap from warm outputs or
        // replace the original captured revision with a new baseline.
        retainTerminalProof?.Invoke(async () =>
        {
            await ObserveAsync();
            token.ThrowIfCancellationRequested();
        });
        return result;
    }
}
