using System.Diagnostics;
using Crap4CSharp.Core;

internal static class FreshCheckCommand
{
    internal static async Task<FreshCheckPreparation> RunAsync(FreshCheckRequest request, string root, DateTimeOffset started,
        CancellationToken cancellation, Action<FreshFailureOutputProof>? retainFailureOutputProof = null,
        Action<string>? retainPrivateDirectory = null, Action<Func<Task>>? retainTerminalProof = null,
        Action<Func<Exception, ResultDocument>>? retainTerminalFailure = null, CompletedProofTiming? timing = null,
        Action<string>? onStagingCreated = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(request.Timeout);
        using var totalTiming = timing?.Start("public.innerTotal");
        var elapsed = Stopwatch.StartNew();
        FreshExecutionEvidence? retainedEvidence = null;
        string? retainedArtifactDirectory = null;
        ResultDocument RetainArtifactOutput(ResultDocument refusal)
        {
            if (retainedArtifactDirectory is null) return refusal;
            return refusal with { Run = refusal.Run with
            {
                Artifacts = refusal.Run.Artifacts.Concat(new[] { new RunArtifact("retained-artifact-output",
                    retainedArtifactDirectory, "retained", false, false) }).ToArray(),
                DiagnosticLocations = refusal.Run.DiagnosticLocations.Concat(new[] { retainedArtifactDirectory }).ToArray()
            } };
        }
        try
        {
            return await RunCoreAsync(request, root, started, elapsed, cancellation, deadline, retainFailureOutputProof, retainPrivateDirectory, retainTerminalProof, evidence =>
                {
                    retainedEvidence = evidence;
                    retainTerminalFailure?.Invoke(error => RetainArtifactOutput(FreshFailureResult.Refuse(evidence, error, started, elapsed.Elapsed)));
                }, path => retainedArtifactDirectory = path, timing, onStagingCreated);
        }
        catch (Exception error) when (retainedEvidence is not null && App.IsHandled(error))
        {
            FreshPrivateFailureEvidence.TryRetain(retainedEvidence.PrivateDirectory, error, deadline.Token);
            return FreshCheckPreparation.Immediate(RetainArtifactOutput(FreshFailureResult.Refuse(retainedEvidence, error, started, elapsed.Elapsed,
                deadlineExpired: !cancellation.IsCancellationRequested && deadline.IsCancellationRequested)));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new ProcessTimeoutException("Fresh check", request.Timeout);
        }
    }

    private static async Task<FreshCheckPreparation> RunCoreAsync(FreshCheckRequest request, string root,
        DateTimeOffset started, Stopwatch elapsed, CancellationToken cancellation, CancellationTokenSource deadline, Action<FreshFailureOutputProof>? retainFailureOutputProof, Action<string>? retainPrivateDirectory, Action<Func<Task>>? retainTerminalProof, Action<FreshExecutionEvidence> retainEvidence, Action<string> retainArtifactDirectory, CompletedProofTiming? timing,
        Action<string>? onStagingCreated)
    {
        void RetainCreatedStaging(string path)
        {
            retainArtifactDirectory(path);
            // This observer belongs only to initial staging, before writer proof.
            // CommitExport updates its retained locator without invoking it again.
            onStagingCreated?.Invoke(path);
        }
        var token = deadline.Token;
        FreshCheckPreflight preflight;
        using (timing?.Start("public.preflight"))
            preflight = await FreshCheckPreflightLoader.LoadAsync(request, root, token);
        // Trusted generated eligibility is retained in hash-bound capture/replay
        // metadata without relabeling physical generated inputs as authored.
        FreshExecutionEvidence evidence;
        using (timing?.Start("public.execution"))
            evidence = await FreshCheckExecution.RunAsync(preflight, token, retainFailureOutputProof, retainPrivateDirectory, timing);
        retainEvidence(evidence);
        if (evidence.Tests.Failure is not null || evidence.Tests.Count != evidence.Plan.Tests.Count || evidence.Tests.Any(t => t.Process.ExitCode != 0))
        {
            FreshPrivateFailureEvidence.TryRetain(evidence.PrivateDirectory, evidence.Tests.Failure, token);
            var failure = FreshFailureResult.Create(evidence, started, elapsed.Elapsed,
                deadlineExpired: !cancellation.IsCancellationRequested && deadline.IsCancellationRequested);
            // Interrupted proof cannot support partial analysis. Keep the actual
            // completed/failed/not-run outcomes instead of throwing them away.
            if (token.IsCancellationRequested) return FreshCheckPreparation.Immediate(failure);
            return FreshCheckPreparation.Immediate(await FreshPartialAnalysis.AttachAsync(evidence, failure, token));
        }
        // Local current reconstruction is explicitly strict/all; other supported
        // fresh local modes retain captured-only semantics rather than inventing
        // a Git-reviewed or unsupported reusable recipe.
        var promote = preflight.GitSnapshot is not null ||
            preflight.Policy.Mode == RepositoryPolicyMode.Strict && preflight.Policy.Scope == "all";
        if (promote && retainTerminalProof is null)
            throw new InvalidOperationException("Fresh completed results require the terminal publication boundary.");
        FreshCapturedBundle captured;
        using (timing?.Start("public.capture"))
            captured = promote
                ? await FreshManifestCapture.PrepareCompletedAsync(evidence, token, timing)
                : await FreshManifestCapture.CaptureAsync(evidence, token, timing);
        if (promote)
            using (timing?.Start("public.promotion"))
                captured = FreshCompletedCapture.PrepareForValidation(evidence, captured, token);
        ResultDocument replay;
        using (timing?.Start("public.replay"))
            replay = AnalyzeCommand.ReplayCaptured(captured.Manifest, captured.Bytes, preflight.Root, started, elapsed.Elapsed, token,
                allSources: true, thresholdOverride: preflight.Policy.Threshold);
        FreshPolicyDecision policy;
        using (timing?.Start("public.policy"))
            policy = FreshPolicyEvaluation.Evaluate(preflight, captured.Manifest, replay);
        string manifest;
        StagedArtifactPublication? export = null;
        if (request.ArtifactOutput is not null && policy.Evaluation.ExitCode != 1)
            using (timing?.Start("public.exportPublish"))
            {
                export = promote
                    ? await FreshManifestCapture.StageForCompletedAsync(evidence, captured, request.ArtifactOutput, token, timing, "public.exportPublish", RetainCreatedStaging)
                    : await FreshManifestCapture.StageAsync(evidence, captured, request.ArtifactOutput, token, timing, "public.exportPublish", RetainCreatedStaging);
                manifest = export.ManifestPath;
            }
        else
        {
            if (promote)
            {
                using (timing?.Start("public.privatePublish"))
                    export = await FreshManifestCapture.StageForCompletedAsync(evidence, captured,
                        Path.Combine(evidence.PrivateDirectory, "evaluation-bundle"), token, timing,
                        "public.privatePublish", RetainCreatedStaging);
                manifest = export.ManifestPath;
            }
            else
            {
                using (timing?.Start("public.privatePublish"))
                    manifest = ArtifactCaptureAdapter.PublishNewCancellable(captured.Manifest, captured.Bytes,
                        Path.Combine(evidence.PrivateDirectory, "evaluation-bundle"), token);
                using (timing?.Start("public.preExportRegate"))
                    await FreshManifestCapture.RegateAsync(evidence, token, timing, "public.preExportRegate");
            }
        }
        // Ordinary capture staging regates before copy. Provisional completed
        // staging preserves original physical/selection/test evidence, then
        // requires the complete independently current writer proof of the
        // actual bytes. The export remains absent until that outer proof succeeds.
        FreshCheckExecution.ProtectPreparedDestinations(preflight with
            { Request = request with { ArtifactOutput = null } }, evidence.Compiler.FinalGraph);
        var complete = policy.Evaluation.ExitCode != 1;
        ProvenanceResult? completedProof = null;
        Func<ProvenanceResult?, ResultDocument>? createResult = null;
        Func<Func<ResultDocument, SerializedFreshTerminal>, Task<SerializedFreshTerminal>>? completeAtWriter = null;
        if (promote)
        {
            // Keep the exact staged bytes provisional until the actual writer.
            // The complete writer validator retains its independent intermediate
            // and final runner/graph/original/physical observations.
            var finalBundle = export?.Bundle ?? ArtifactBundle.Load(manifest, preflight.Root);
            if (complete)
            {
                completeAtWriter = async prepare =>
                {
                    SerializedFreshTerminal? terminal = null;
                    await ValidateAtWriterAsync((current, proof, writerToken) =>
                    {
                        using (timing?.Start("public.validatedWriterEvidence")) { }
                        terminal = prepare((createResult ?? throw new InvalidOperationException("Fresh terminal preparation is incomplete."))(proof));
                        writerToken.ThrowIfCancellationRequested();
                        CommitExport(current.ProtectedPaths, writerToken, finalBundle, proof);
                    });
                    return terminal ?? throw new InvalidOperationException("Fresh writer proof did not prepare its terminal.");
                };
                using (timing?.Start("public.provisionalTerminalPrepared")) { }
            }
            else
            {
                // Operationally incomplete scoring keeps its established failure
                // materialization/output path; it never advertises reusable success.
                using (timing?.Start("public.finalProof"))
                    completedProof = await FreshCompletedCapture.ValidateAsync(evidence, finalBundle, token, timing: timing);
                retainTerminalProof?.Invoke(() => ValidateAtWriterAsync((current, proof, writerToken) =>
                    CommitExport(current.ProtectedPaths, writerToken, finalBundle, proof)));
            }

            async Task ValidateAtWriterAsync(Action<CurrentEvidence, ProvenanceResult, CancellationToken> publish)
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
                        await FreshCompletedCapture.ValidateAsync(evidence, finalBundle, finalDeadline.Token, timing: timing,
                            beforePublication: (current, proof) => publish(current, proof, finalDeadline.Token));
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && finalDeadline.IsCancellationRequested)
                { throw new ProcessTimeoutException("Fresh check", request.Timeout); }
            }
        }
        else if (export is not null)
            retainTerminalProof?.Invoke(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = request.Timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new ProcessTimeoutException("Fresh check", request.Timeout);
                using var finalDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                finalDeadline.CancelAfter(remaining);
                try
                {
                    var protectedPaths = evidence.Compiler.FinalGraph.GraphContexts.SelectMany(b => b.Context.ProtectedPaths
                        .Concat(b.Context.PhaseInputs.Select(i => i.Path))).ToArray();
                    var bundleFiles = SavedBundleFootprint.Validate(export.Bundle, preflight.Root,
                        protectedPaths, finalDeadline.Token);
                    await FreshManifestCapture.RegateAsync(evidence, finalDeadline.Token, timing,
                        "public.outerCapturedProof", bundleFiles);
                    _ = SavedBundleFootprint.Validate(export.Bundle, preflight.Root, protectedPaths, finalDeadline.Token);
                    CommitExport(protectedPaths, finalDeadline.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && finalDeadline.IsCancellationRequested)
                { throw new ProcessTimeoutException("Fresh check", request.Timeout); }
            });
        if (export is not null && retainTerminalProof is null)
            throw new InvalidOperationException("Fresh exports require the terminal publication boundary.");

        void CommitExport(IReadOnlyList<string> protectedPaths, CancellationToken commitToken,
            ArtifactBundle? verifiedBundle = null, ProvenanceResult? proof = null)
        {
            if (export is null) return;
            FreshCheckPreflightLoader.ValidateDestinations(request, preflight.Root,
                protectedPaths.Concat(preflight.Trusted.ContentHashes.Keys));
            FreshCheckExecution.ProtectPreparedDestinations(preflight, evidence.Compiler.FinalGraph);
            if (promote)
            {
                if (verifiedBundle is null || proof is null)
                    throw new InvalidOperationException("Completed export requires the actual terminal bundle proof.");
                _ = export.CommitCompleted(verifiedBundle, proof, commitToken, protectedPaths);
            }
            else
                _ = export.Commit(commitToken, protectedPaths);
            // If a later result-writer refusal occurs, report the moved output
            // rather than the now-absent staging path. No post-cancellation I/O.
            retainArtifactDirectory(export.Destination);
        }
        var semanticEmpty = captured.Manifest.Contexts.Count > 0 && captured.Manifest.Contexts.All(c => c.CoverageApplicability == "semantic-empty");
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
        createResult = proof => replay with
        {
            Evaluation = replay.Evaluation with
            {
                InvocationMode = "check", Policy = new(preflight.Policy.Threshold, false), Checks = checks, Findings = policy.Findings,
                Decision = new(complete, policy.Evaluation.Decision, policy.Evaluation.OperationalReasons.FirstOrDefault() ??
                    (policy.Evaluation.ExitCode == 2 ? "crap.thresholdExceeded" : "crap.withinThreshold")),
                Provenance = new("verified", "currentWorkspaceMatch", true, complete && (proof?.Reusable ?? false), proof?.Reasons ?? [ProvenanceReasonCodes.ReuseRecipeIncomplete]),
                PolicyTrust = new(preflight.Trusted.Trust, preflight.Trusted.Revision, preflight.Trusted.Policy.PolicyPath,
                    preflight.Trusted.Policy.Hash, policy.CompatibilityHash), PolicyDifferences = differences,
                CallableExemptions = policy.Exemptions.Matches, ExemptionErrors = []
            },
            Run = replay.Run with
            {
                Status = complete ? "completed" : "operationalError", ExitCode = policy.Evaluation.ExitCode,
                Artifacts = [new("manifest", manifest, "retained", complete, complete && (proof?.Reusable ?? false))],
                TestOutcomes = FreshTestOutcomeProjection.Create(evidence)
            }
        };
        if (completeAtWriter is not null)
            return FreshCheckPreparation.Pending(policy.Evaluation.ExitCode, completeAtWriter);
        return FreshCheckPreparation.Immediate(createResult(completedProof));
    }
}
