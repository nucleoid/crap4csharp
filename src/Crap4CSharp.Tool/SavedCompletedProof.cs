using Crap4CSharp.Core;

// Private complete captured-evidence boundary. Does not advertise a public reuse
// recipe or change captured flags. All commands come from current SDK evaluation.
internal static class SavedCompletedProof
{
    internal static async Task<CompilerGraphPhaseResult> ValidateAsync(ArtifactBundle bundle,
        FreshCheckPreflight preflight, CancellationToken token,
        Func<CancellationToken, Task>? beforeFinalValidation = null)
    {
        token.ThrowIfCancellationRequested();
        CompletedTrustedPolicy.Validate(bundle.Manifest.Artifacts, bundle.Bytes,
            preflight.Trusted.Policy.CanonicalBytes, preflight.Trusted.ExemptionBytes, token);
        var root = preflight.Root;
        var recipe = bundle.Manifest.Artifacts.Single(a => a.Kind == CompletedTestPlanRecipe.Kind);
        if (recipe.ContextId is not null || recipe.BuildId is not null || recipe.ExecutionId is not null ||
            recipe.Format != "json" || recipe.CoordinateKind is not null ||
            !bundle.Bytes.TryGetValue(recipe.Locator, out var bytes) || bytes.Length != recipe.Length ||
            CanonicalIdentity.Sha256(bytes.AsSpan()) != recipe.Sha256)
            throw new PolicyException("provenance.savedCompletedProofUnproven", "Saved plan artifact identity is incomplete.");
        var selected = ProjectTargetSelector.Select(root, preflight.Request.Project);
        if (!PathIdentityPolicy.Current.Comparer.Equals(selected,
            CompletedTestPlanRecipe.ResolveSelectedRoot(bytes.ToArray(), root, token)))
            throw new PolicyException("provenance.savedCompletedProofUnproven", "Saved selected root differs from the original current selection.");
        var platform = await FreshProjectPlatform.ResolveAsync(selected, preflight.Policy.Configuration, token);
        var request = new ProjectContextLoadRequest(selected, preflight.Policy.Configuration, platform,
            preflight.Policy.TargetFrameworks.ToArray(), preflight.Request.Overrides.IncludeTests ?? false,
            preflight.Request.Overrides.IncludeGenerated ?? false, preflight.Request.Timeout);
        var plan = await LoadPlanAsync();
        CompletedTestPlanRecipe.Validate(bytes.ToArray(), plan, root, selected, token);
        var imported = FreshPriorBuild.Load(bundle, plan.PreparedGraph, root, token);
        var compiler = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph,
            (node, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(imported[node.Id]); }, token);
        SavedRequiredTestResults.Validate(bundle, plan, compiler, root, token);
        CoverageGraphBinding.ValidateSavedNormalized(bundle, plan, compiler, root, token);
        SavedProductionExecutions.Validate(bundle, plan, compiler, root, token);
        SavedCompilerContexts.Validate(bundle, compiler, root, token);
        if (beforeFinalValidation is not null) await beforeFinalValidation(token);
        // Independently rediscover both graph and runners after intermediate gates.
        // Never recapture the original workspace selection to tolerate drift.
        var final = await LoadPlanAsync();
        CompletedTestPlanRecipe.Validate(bytes.ToArray(), final, root, selected, token);
        await compiler.RetainedProof!.ValidateFinalAsync(token);
        var outputs = OutputFootprint(compiler);
        await FreshGitProof.ValidateAsync(preflight, token, outputs);
        compiler.RetainedProof.ValidateCurrent(token);
        token.ThrowIfCancellationRequested();
        return compiler;

        async Task<CheckExecutionPlan> LoadPlanAsync()
        {
            var graph = await ProjectContextLoader.LoadGraphAsync(request, token);
            // Bind independently current required policy membership on BOTH
            // intermediate and final discovery, including nonreaching tests.
            // Production-linked saved executions are not the required schedule.
            FreshCheckExecution.RequirePolicyTargets(preflight, graph);
            var runners = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
            token.ThrowIfCancellationRequested();
            return CheckExecutionPlan.Create(runners, graph, token);
        }
    }

    // Use only after the complete saved proof has independently regated the graph.
    // The same finite evaluated footprint governs stability and revision comparison.
    internal static IReadOnlyList<string> OutputFootprint(CompilerGraphPhaseResult compiler)
    {
        var outputs = compiler.FinalGraph.GraphContexts.SelectMany(b =>
            new[] { b.Context.EvaluatedIntermediateDirectory!, Path.GetDirectoryName(b.Context.EvaluatedTargetPath!)! }
                .Concat(b.Context.PhaseInputs.Where(i => i.Role == "restoreAssets").Select(i => Path.GetDirectoryName(i.Path)!)))
            .Distinct(PathIdentityPolicy.Current.Comparer).ToArray();
        if (outputs.Any(directory => compiler.FinalGraph.BuildGraph.Any(node => node.ProjectPath.StartsWith(
            Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
            throw new PolicyException("provenance.gitOutputFootprintUnsupported", "Generated output footprint must not contain consumer project inputs.");
        return outputs;
    }
}
