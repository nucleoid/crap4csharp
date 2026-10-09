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
            preflight.Request.Overrides.IncludeGenerated ?? false, preflight.Request.Timeout);
        var intermediate = await LoadPlanAsync("intermediate");
        var compiler = execution.Compiler with { FinalGraph = intermediate.PreparedGraph };
        using (timing?.Start("intermediate.saved")) ValidateSaved(compiler, intermediate);
        if (beforeFinalValidation is not null) await beforeFinalValidation(token);
        // Independently CURRENT runners first, graph last. Both saved and original
        // immutable once-executed schedules must agree; no rebaselining snapshots.
        var final = await LoadPlanAsync("final");
        compiler = execution.Compiler with { FinalGraph = final.PreparedGraph };
        using (timing?.Start("final.saved")) ValidateSaved(compiler, final);
        var outputs = SavedCompletedProof.OutputFootprint(compiler);
        using (timing?.Start("revision.originalGit"))
            if (execution.Preflight.GitSnapshot is not null)
                await FreshGitProof.ValidateAsync(execution.Preflight, token, outputs);
        using (timing?.Start("revision.current"))
            await FreshGitProof.ValidateAsync(preflight, token, outputs);
        CurrentEvidence current;
        using (timing?.Start("projection"))
            current = CurrentEvidenceAdapter.ProjectCompleted(bundle, preflight, compiler, token);
        IReadOnlySet<string> bundleFiles;
        using (timing?.Start("physical.bundleIntermediate"))
            bundleFiles = SavedBundleFootprint.Validate(bundle, root, current.ProtectedPaths, token);
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
                runners = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
            ProjectContextLoadResult graph;
            using (timing?.Start(boundary + ".graph"))
                graph = await ProjectContextLoader.LoadGraphAsync(request, token);
            FreshCheckExecution.RequirePolicyTargets(preflight, graph);
            FreshCheckExecution.RequirePolicyTargets(execution.Preflight, graph);
            var plan = CheckExecutionPlan.Create(runners, graph, token);
            CompletedTestPlanRecipe.Validate(bytes.ToArray(), plan, root, selected, token);
            CompletedTestPlanRecipe.Validate(originalRecipe, plan, root, selected, token);
            retained.ValidateAgainstCurrentGraph(plan.PreparedGraph, token);
            return plan;
        }
        void ValidateSaved(CompilerGraphPhaseResult current, CheckExecutionPlan plan)
        {
            SavedRequiredTestResults.Validate(bundle, plan, current, root, token);
            CoverageGraphBinding.ValidateSavedNormalized(bundle, plan, current, root, token);
            SavedProductionExecutions.Validate(bundle, plan, current, root, token);
            SavedCompilerContexts.Validate(bundle, current, root, token);
            retained.ValidateAgainstCurrentGraph(current.FinalGraph, token);
        }
    }
    private static PolicyException Refuse() => new("check.retainedCompletedProofUnproven",
        "Fresh complete proof requires the original successful invocation and independently current graph, runners, physical inputs and outputs.");
}
