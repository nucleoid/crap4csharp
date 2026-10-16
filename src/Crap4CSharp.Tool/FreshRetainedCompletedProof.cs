using Crap4CSharp.Core;

// Invocation-only, never saved-only evidence.
// Original once-executed observations and all physical snapshots are retained.
internal static class FreshRetainedCompletedProof
{
    internal static async Task<CurrentEvidence> ValidateAsync(FreshExecutionEvidence execution,
        ArtifactBundle bundle, FreshCheckPreflight preflight, CancellationToken token,
        Func<CancellationToken, Task>? beforeFinalValidation = null,
        CompletedProofTiming? timing = null)
    {
        token.ThrowIfCancellationRequested();
        if (execution.Coverage is null || execution.Tests.Failure is not null ||
            execution.Tests.Count != execution.Plan.Tests.Count ||
            execution.Tests.Any(test => test.Process.ExitCode != 0) ||
            execution.Compiler.RetainedProof is not { } retained) throw Refuse();
        var root = preflight.Root;
        if (!PathIdentityPolicy.Current.Comparer.Equals(root, execution.Preflight.Root)) throw Refuse();
        CompletedTrustedPolicy.Validate(bundle.Manifest.Artifacts, bundle.Bytes,
            preflight.Trusted.Policy.CanonicalBytes, preflight.Trusted.ExemptionBytes, token);
        var declarations = bundle.Manifest.Artifacts.Where(a => a.Kind == CompletedTestPlanRecipe.Kind).ToArray();
        if (declarations.Length != 1) throw Refuse();
        var recipe = declarations[0];
        if (recipe.ContextId is not null || recipe.BuildId is not null || recipe.ExecutionId is not null ||
            recipe.Format != "json" || recipe.CoordinateKind is not null ||
            !bundle.Bytes.TryGetValue(recipe.Locator, out var bytes) || bytes.IsDefault ||
            bytes.Length != recipe.Length || CanonicalIdentity.Sha256(bytes.AsSpan()) != recipe.Sha256) throw Refuse();
        var selected = ProjectTargetSelector.Select(root, preflight.Request.Project);
        if (!PathIdentityPolicy.Current.Comparer.Equals(selected,
            ProjectTargetSelector.Select(root, execution.Preflight.Request.Project)) ||
            !PathIdentityPolicy.Current.Comparer.Equals(selected,
                CompletedTestPlanRecipe.ResolveSelectedRoot(bytes.ToArray(), root, token))) throw Refuse();
        var originalRecipe = CompletedTestPlanRecipe.Capture(execution.Plan, root, selected, token);
        var platform = await FreshProjectPlatform.ResolveAsync(selected, preflight.Policy.Configuration, token);
        var request = new ProjectContextLoadRequest(selected, preflight.Policy.Configuration, platform,
            preflight.Policy.TargetFrameworks.ToArray(), preflight.Request.Overrides.IncludeTests ?? false,
            preflight.Request.Overrides.IncludeGenerated ?? false, preflight.Request.Timeout)
        { NativeSdkIdentityProbe = preflight.Request.NativeSdkIdentityProbe, FrozenSdk = retained.FrozenSdk };
        var intermediate = await LoadPlanAsync("intermediate");
        var compiler = execution.Compiler with { FinalGraph = intermediate.PreparedGraph };
        using (timing?.Start("intermediate.saved")) ValidateSaved(compiler, intermediate, "intermediate");
        if (beforeFinalValidation is not null) await beforeFinalValidation(token);
        // Independently CURRENT runners first, graph last. Both saved and original
        // immutable once-executed schedules must agree; no rebaselining snapshots.
        var final = await LoadPlanAsync("final");
        compiler = execution.Compiler with { FinalGraph = final.PreparedGraph };
        using (timing?.Start("final.saved")) ValidateSaved(compiler, final, "final");
        var outputs = SavedCompletedProof.OutputFootprint(compiler);
        CurrentEvidence current;
        using (timing?.Start("projection"))
            current = CurrentEvidenceAdapter.ProjectCompleted(bundle, preflight, compiler, token);
        IReadOnlySet<string> bundleFiles;
        using (timing?.Start("physical.bundleIntermediate"))
            bundleFiles = SavedBundleFootprint.Validate(bundle, root, current.ProtectedPaths, token);
        if (execution.Preflight.GitSnapshot is not null)
        {
            // Independent read-only observations; neither consumes the other's
            // result. Keep both complete checks and wait for both owned cleanups.
            using (timing?.Start("revision.originalAndCurrentGit"))
                await FreshGitProof.ValidateOriginalAndCurrentAsync(execution.Preflight, preflight,
                    token, outputs, bundleFiles);
        }
        else
        {
            using (timing?.Start("revision.current"))
                await FreshGitProof.ValidateAsync(preflight, token, outputs, bundleFiles, currentBundleSnapshot: true);
        }
        if (execution.Preflight.GitSnapshot is null)
        {
            // Manifest ORIGINAL membership must equal the retained invocation,
            // not merely an internally consistent supplied recipe.
            if (execution.Preflight.LocalSnapshot is null || bundle.Manifest.Revision.Kind != "none" ||
                FreshLocalProof.Identity(execution.Preflight.LocalSnapshot) != bundle.Manifest.Revision.StateHash)
                throw Refuse();
            using (timing?.Start("revision.originalLocal"))
                FreshLocalProof.Validate(execution.Preflight, token, outputs, bundleFiles);
        }
        // No await or user callback after the final original/physical snapshot gates.
        using (timing?.Start("physical.originalFinal"))
            retained.ValidateAgainstCurrentGraph(final.PreparedGraph, token);
        using (timing?.Start("physical.bundleFinal"))
            _ = SavedBundleFootprint.Validate(bundle, root, current.ProtectedPaths, token);
        token.ThrowIfCancellationRequested();
        return current;

        async Task<CheckExecutionPlan> LoadPlanAsync(string boundary)
        {
            ProjectContextLoadResult runners;
            using (timing?.Start(boundary + ".runners"))
                runners = await ProjectContextLoader.DiscoverTestRunnersAsync(request with
                { CollectTiming = timing is not null, Timing = timing, TimingPrefix = boundary + ".runners.loader" }, token);
            ProjectContextLoadResult graph;
            using (timing?.Start(boundary + ".graph"))
                graph = await ProjectContextLoader.LoadGraphAsync(request with
                { CollectTiming = timing is not null, Timing = timing, TimingPrefix = boundary + ".graph.loader",
                    WarmAcquisitionSelectionProbe = preflight.Request.WarmPostBuildProbe }, token);
            FreshCheckExecution.RequirePolicyTargets(preflight, graph);
            FreshCheckExecution.RequirePolicyTargets(execution.Preflight, graph);
            var plan = CheckExecutionPlan.Create(runners, graph, token);
            CompletedTestPlanRecipe.Validate(bytes.ToArray(), plan, root, selected, token);
            CompletedTestPlanRecipe.Validate(originalRecipe, plan, root, selected, token);
            // The caller immediately enters ValidateSaved's synchronous boundary,
            // which checks this current graph against the original physical inputs
            // before its body and rereads closing bytes afterwards. Do not perform
            // a third identical physical read here; no callback or consumer effect
            // occurs between plan acquisition and that opening check.
            return plan;
        }
        void ValidateSaved(CompilerGraphPhaseResult current, CheckExecutionPlan plan, string boundary)
        {
            retained.ValidateObservedSynchronousBoundary(current.FinalGraph, token, () =>
            {
                SavedProductionExecutions.ValidateFreshBoundary(bundle, plan, current, root, token, timing, boundary);
                using (timing?.Start(boundary + ".saved.compilerContexts"))
                    SavedCompilerContexts.ValidateObserved(bundle, current, root, token, timing, boundary);
                using (timing?.Start(boundary + ".saved.originalPhysical"))
                    retained.ValidateAgainstCurrentGraph(current.FinalGraph, token);
            }, timing, boundary);
        }
    }
    private static PolicyException Refuse() => new("check.retainedCompletedProofUnproven",
        "Fresh complete proof requires the original successful invocation and independently current graph, runners, physical inputs and outputs.");
}
