using System.Diagnostics;
using Crap4CSharp.Core;

internal static class FreshCheckCommand
{
    internal static async Task<ResultDocument> RunAsync(FreshCheckRequest request, string root, DateTimeOffset started,
        CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(request.Timeout);
        var token = deadline.Token; var elapsed = Stopwatch.StartNew();
        var preflight = await FreshCheckPreflightLoader.LoadAsync(request, root, token);
        // Trusted generated eligibility is retained in hash-bound capture/replay
        // metadata without relabeling physical generated inputs as authored.
        var evidence = await FreshCheckExecution.RunAsync(preflight, token);
        if (evidence.Tests.Failure is not null || evidence.Tests.Count != evidence.Plan.Tests.Count || evidence.Tests.Any(t => t.Process.ExitCode != 0))
            return FreshFailureResult.Create(evidence, started, elapsed.Elapsed);
        var captured = await FreshManifestCapture.CaptureAsync(evidence, token);
        var privateManifest = await FreshManifestCapture.PublishAsync(evidence, captured,
            Path.Combine(evidence.PrivateDirectory, "evaluation-bundle"), token);
        var bundle = ArtifactBundle.Load(privateManifest, evidence.PrivateDirectory);
        var replay = AnalyzeCommand.Replay(bundle, preflight.Root, started, elapsed.Elapsed, token,
            allSources: true, thresholdOverride: preflight.Policy.Threshold);
        var policy = FreshPolicyEvaluation.Evaluate(preflight, bundle.Manifest, replay);
        await FreshManifestCapture.RegateAsync(evidence, token);
        var manifest = privateManifest;
        if (request.ArtifactOutput is not null && policy.Evaluation.ExitCode != 1)
            manifest = await FreshManifestCapture.PublishAsync(evidence, captured, request.ArtifactOutput, token);
        // Publication already guarded the new bundle before its atomic move. An
        // invocation-owned published directory is now expected to exist; retain the
        // final result-file guards without reclassifying our own bundle as ambient.
        FreshCheckExecution.ProtectPreparedDestinations(preflight with
            { Request = request with { ArtifactOutput = null } }, evidence.Compiler.FinalGraph);
        var complete = policy.Evaluation.ExitCode != 1;
        var semanticEmpty = bundle.Manifest.Contexts.Count > 0 && bundle.Manifest.Contexts.All(c => c.CoverageApplicability == "semantic-empty");
        var checks = new CheckResult[]
        {
            new("policyTrust", "pass", preflight.Trusted.Trust == "base-trusted" ? "policy.baseTrusted" : "policy.localUnreviewed", true),
            new("provenance", "pass", "provenance.freshCompilerTestPostflightBound", true),
            new("testExecution", "pass", "tests.actualRequiredContextsPassed", true),
            new("coverage", semanticEmpty ? "notApplicable" : policy.Evaluation.OperationalReasons.Count == 0 ? "pass" : "operationalError",
                semanticEmpty ? "coverage.noApplicableSourceInventory" : policy.Evaluation.OperationalReasons.FirstOrDefault() ?? "coverage.complete", preflight.Policy.RequiredChecks.Contains("coverage")),
            new("crap", semanticEmpty ? "notApplicable" : policy.Evaluation.ExitCode == 2 ? "fail" : complete ? "pass" : "operationalError",
                semanticEmpty ? "crap.noApplicableSourceInventory" : policy.Evaluation.ExitCode == 2 ? "crap.thresholdExceeded" : complete ? "crap.withinThreshold" : "crap.analysisIncomplete",
                preflight.Policy.RequiredChecks.Contains("crap"))
        };
        var differences = TrustedPolicyLoader.CompareProposed(preflight.Root, preflight.Trusted)
            .Select(d => new PolicyDifferenceResult(d.Path,d.Status,d.TrustedHash,d.ProposedHash)).ToArray();
        return replay with
        {
            Evaluation = replay.Evaluation with
            {
                InvocationMode = "check", Policy = new(preflight.Policy.Threshold, false), Checks = checks, Findings = policy.Findings,
                Decision = new(complete, policy.Evaluation.Decision, policy.Evaluation.OperationalReasons.FirstOrDefault() ??
                    (policy.Evaluation.ExitCode == 2 ? "crap.thresholdExceeded" : "crap.withinThreshold")),
                Provenance = new("verified", "freshCompilerTestPostflightBinding", true, false, [ProvenanceReasonCodes.ReuseRecipeIncomplete]),
                PolicyTrust = new(preflight.Trusted.Trust, preflight.Trusted.Revision, preflight.Trusted.Policy.PolicyPath,
                    preflight.Trusted.Policy.Hash, policy.CompatibilityHash), PolicyDifferences = differences,
                CallableExemptions = policy.Exemptions.Matches, ExemptionErrors = []
            },
            Run = replay.Run with
            {
                Status = complete ? "completed" : "operationalError", ExitCode = policy.Evaluation.ExitCode,
                Artifacts = [new("manifest", manifest, "retained", complete, false)],
                TestOutcomes = evidence.Tests.Outcomes.Select(o => new TestExecutionOutcomeResult(o.GraphId,
                    evidence.Plan.Tests.Single(t => t.GraphId == o.GraphId).ProjectPath, o.State, o.Process?.ExitCode,
                    o.ResultsDirectory, o.LogDirectory)).ToArray()
            }
        };
    }
}
