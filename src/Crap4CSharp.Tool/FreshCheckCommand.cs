using System.Diagnostics;
using Crap4CSharp.Core;

internal static class FreshCheckCommand
{
    internal static async Task<ResultDocument> RunAsync(FreshCheckRequest request, string root, DateTimeOffset started,
        CancellationToken cancellation, Action<FreshFailureOutputProof>? retainFailureOutputProof = null,
        Action<string>? retainPrivateDirectory = null, Action<Func<Task>>? retainTerminalProof = null,
        Action<Func<Exception, ResultDocument>>? retainTerminalFailure = null, CompletedProofTiming? timing = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(request.Timeout);
        using var totalTiming = timing?.Start("public.innerTotal");
        var elapsed = Stopwatch.StartNew();
        FreshExecutionEvidence? retainedEvidence = null;
        try
        {
            return await RunCoreAsync(request, root, started, elapsed, cancellation, deadline, retainFailureOutputProof, retainPrivateDirectory, retainTerminalProof, evidence =>
                {
                    retainedEvidence = evidence;
                    retainTerminalFailure?.Invoke(error => FreshFailureResult.Refuse(evidence, error, started, elapsed.Elapsed));
                }, timing);
        }
        catch (Exception error) when (retainedEvidence is not null && App.IsHandled(error))
        {
            return FreshFailureResult.Refuse(retainedEvidence, error, started, elapsed.Elapsed,
                deadlineExpired: !cancellation.IsCancellationRequested && deadline.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new ProcessTimeoutException("Fresh check", request.Timeout);
        }
    }

    private static async Task<ResultDocument> RunCoreAsync(FreshCheckRequest request, string root,
        DateTimeOffset started, Stopwatch elapsed, CancellationToken cancellation, CancellationTokenSource deadline, Action<FreshFailureOutputProof>? retainFailureOutputProof, Action<string>? retainPrivateDirectory, Action<Func<Task>>? retainTerminalProof, Action<FreshExecutionEvidence> retainEvidence, CompletedProofTiming? timing)
    {
        var token = deadline.Token;
        FreshCheckPreflight preflight;
        using (timing?.Start("public.preflight"))
            preflight = await FreshCheckPreflightLoader.LoadAsync(request, root, token);
        // Trusted generated eligibility is retained in hash-bound capture/replay
        // metadata without relabeling physical generated inputs as authored.
        FreshExecutionEvidence evidence;
        using (timing?.Start("public.execution"))
            evidence = await FreshCheckExecution.RunAsync(preflight, token, retainFailureOutputProof, retainPrivateDirectory);
        retainEvidence(evidence);
        if (evidence.Tests.Failure is not null || evidence.Tests.Count != evidence.Plan.Tests.Count || evidence.Tests.Any(t => t.Process.ExitCode != 0))
        {
            var failure = FreshFailureResult.Create(evidence, started, elapsed.Elapsed,
                deadlineExpired: !cancellation.IsCancellationRequested && deadline.IsCancellationRequested);
            // Interrupted proof cannot support partial analysis. Keep the actual
            // completed/failed/not-run outcomes instead of throwing them away.
            if (token.IsCancellationRequested) return failure;
            return await FreshPartialAnalysis.AttachAsync(evidence, failure, token);
        }
        FreshCapturedBundle captured;
        using (timing?.Start("public.capture"))
            captured = await FreshManifestCapture.CaptureAsync(evidence, token, timing);
        // Local current reconstruction is explicitly strict/all; other supported
        // fresh local modes retain captured-only semantics rather than inventing
        // a Git-reviewed or unsupported reusable recipe.
        var promote = preflight.GitSnapshot is not null ||
            preflight.Policy.Mode == RepositoryPolicyMode.Strict && preflight.Policy.Scope == "all";
        if (promote)
            using (timing?.Start("public.promotion"))
                captured = await FreshCompletedCapture.PromoteAsync(evidence, captured, token, timing);
        string privateManifest;
        using (timing?.Start("public.privatePublish"))
            privateManifest = await FreshManifestCapture.PublishAsync(evidence, captured,
                Path.Combine(evidence.PrivateDirectory, "evaluation-bundle"), token, timing, "public.privatePublish");
        var bundle = ArtifactBundle.Load(privateManifest, evidence.PrivateDirectory);
        ResultDocument replay;
        using (timing?.Start("public.replay"))
            replay = AnalyzeCommand.Replay(bundle, preflight.Root, started, elapsed.Elapsed, token,
                allSources: true, thresholdOverride: preflight.Policy.Threshold);
        FreshPolicyDecision policy;
        using (timing?.Start("public.policy"))
            policy = FreshPolicyEvaluation.Evaluate(preflight, bundle.Manifest, replay);
        using (timing?.Start("public.preExportRegate"))
            await FreshManifestCapture.RegateAsync(evidence, token, timing, "public.preExportRegate");
        var manifest = privateManifest;
        if (request.ArtifactOutput is not null && policy.Evaluation.ExitCode != 1)
            using (timing?.Start("public.exportPublish"))
                manifest = await FreshManifestCapture.PublishAsync(evidence, captured, request.ArtifactOutput, token, timing, "public.exportPublish");
        // Publication already guarded the new bundle before its atomic move. An
        // invocation-owned published directory is now expected to exist; retain the
        // final result-file guards without reclassifying our own bundle as ambient.
        FreshCheckExecution.ProtectPreparedDestinations(preflight with
            { Request = request with { ArtifactOutput = null } }, evidence.Compiler.FinalGraph);
        var complete = policy.Evaluation.ExitCode != 1;
        ProvenanceResult? completedProof = null;
        if (promote)
        {
            // Bind the actual final exported footprint, not the private prospective
            // footprint. No original fresh membership waiver after export.
            var finalBundle = ArtifactBundle.Load(manifest, preflight.Root);
            using (timing?.Start("public.finalProof"))
                completedProof = await FreshCompletedCapture.ValidateAsync(evidence, finalBundle, token, timing: timing);
            retainTerminalProof?.Invoke(async () =>
            {
                // The inner deadline source is disposed on return. Recreate only
                // its REMAINING absolute budget at the outer publication boundary.
                cancellation.ThrowIfCancellationRequested();
                var remaining = request.Timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new ProcessTimeoutException("Fresh check", request.Timeout);
                using var finalDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                finalDeadline.CancelAfter(remaining);
                try
                {
                    using (timing?.Start("public.outerProof"))
                        await FreshCompletedCapture.ValidateAsync(evidence, finalBundle, finalDeadline.Token, timing: timing);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && finalDeadline.IsCancellationRequested)
                { throw new ProcessTimeoutException("Fresh check", request.Timeout); }
            });
        }
        var semanticEmpty = bundle.Manifest.Contexts.Count > 0 && bundle.Manifest.Contexts.All(c => c.CoverageApplicability == "semantic-empty");
        // Applicability follows the effective policy observations, not raw Git
        // file count. Strict mode still evaluates all callables; incremental
        // widening and selected family guards remain applicable.
        var crapNotApplicable = complete && !policy.HasApplicableCrap;
        var crapReason = semanticEmpty ? "crap.noApplicableSourceInventory" : crapNotApplicable
            ? preflight.Policy.Mode == RepositoryPolicyMode.Incremental ? "crap.noApplicableChangedCallables" : "crap.noApplicablePolicyCallables"
            : policy.Evaluation.ExitCode == 2 ? "crap.thresholdExceeded" : complete ? "crap.withinThreshold" : "crap.analysisIncomplete";
        var checks = new CheckResult[]
        {
            new("policyTrust", "pass", preflight.Trusted.Trust == "base-trusted" ? "policy.baseTrusted" : "policy.localUnreviewed", true),
            new("provenance", "pass", preflight.Request.NoBuild ? "provenance.priorCompilerFreshTestPostflightBound" : "provenance.freshCompilerTestPostflightBound", true),
            new("testExecution", "pass", "tests.actualRequiredContextsPassed", true),
            new("coverage", semanticEmpty ? "notApplicable" : policy.Evaluation.OperationalReasons.Count == 0 ? "pass" : "operationalError",
                semanticEmpty ? "coverage.noApplicableSourceInventory" : policy.Evaluation.OperationalReasons.FirstOrDefault() ?? "coverage.complete", preflight.Policy.RequiredChecks.Contains("coverage")),
            new("crap", crapNotApplicable ? "notApplicable" : policy.Evaluation.ExitCode == 2 ? "fail" : complete ? "pass" : "operationalError",
                crapReason,
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
                Provenance = new("verified", preflight.Request.NoBuild ? "priorCompilerFreshTestPostflightBinding" : "freshCompilerTestPostflightBinding", true, completedProof?.Reusable ?? false, completedProof?.Reasons ?? [ProvenanceReasonCodes.ReuseRecipeIncomplete]),
                PolicyTrust = new(preflight.Trusted.Trust, preflight.Trusted.Revision, preflight.Trusted.Policy.PolicyPath,
                    preflight.Trusted.Policy.Hash, policy.CompatibilityHash), PolicyDifferences = differences,
                CallableExemptions = policy.Exemptions.Matches, ExemptionErrors = []
            },
            Run = replay.Run with
            {
                Status = complete ? "completed" : "operationalError", ExitCode = policy.Evaluation.ExitCode,
                Artifacts = [new("manifest", manifest, "retained", complete, completedProof?.Reusable ?? false)],
                TestOutcomes = evidence.Tests.Outcomes.Select(o => new TestExecutionOutcomeResult(o.GraphId,
                    evidence.Plan.Tests.Single(t => t.GraphId == o.GraphId).ProjectPath, o.State, o.Process?.ExitCode,
                    o.ResultsDirectory, o.LogDirectory)).ToArray()
            }
        };
    }
}
