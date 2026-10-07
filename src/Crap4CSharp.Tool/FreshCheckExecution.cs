using System.Collections.Immutable;
using Crap4CSharp.Core;

// Execution evidence only. Public success additionally requires owned manifest export,
// trusted pure evaluation and final result/output safety; never manufacture a green here.
internal sealed record FreshExecutionEvidence(FreshCheckPreflight Preflight, CheckExecutionPlan Plan,
    CompilerGraphPhaseResult Compiler, TestExecutionRun Tests, BoundGraphCoverage? Coverage,
    IReadOnlyDictionary<string, ImmutableArray<byte>> SafeChildren, string PrivateDirectory);

internal static class FreshCheckExecution
{
    internal static async Task<FreshExecutionEvidence> RunAsync(FreshCheckPreflight preflight, CancellationToken cancellationToken,
        Action<FreshFailureOutputProof>? retainFailureOutputProof = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var priorBundle = preflight.Request.NoBuild
            ? ArtifactBundle.Load(preflight.Request.BuildArtifacts!, preflight.Root) : null;
        if (priorBundle is not null)
            FreshCheckPreflightLoader.ValidateDestinations(preflight.Request, preflight.Root,
                priorBundle.ResolvedInputs.Append(priorBundle.ManifestPath));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(preflight.Request.Timeout);
        var token = deadline.Token;
        var selected = ProjectTargetSelector.Select(preflight.Root, preflight.Request.Project);
        var request = new ProjectContextLoadRequest(selected, preflight.Policy.Configuration, "AnyCPU",
            preflight.Policy.TargetFrameworks.ToArray(), preflight.Request.Overrides.IncludeTests ?? false,
            preflight.Request.Overrides.IncludeGenerated ?? false, preflight.Request.Timeout);
        var metadata = await ProjectContextLoader.DiscoverAsync(request, token);
        Require(metadata);
        var graph = await ProjectContextLoader.DiscoverGraphAsync(request, token);
        Require(graph);
        RequirePolicyTargets(preflight, graph);
        FreshCheckPreflightLoader.ValidateDestinations(preflight.Request, preflight.Root,
            graph.BuildGraph.Select(n => n.ProjectPath).Concat(metadata.Metadata.Select(m => m.AssetsFile)));
        if (!preflight.Request.NoRestore && !preflight.Request.NoBuild)
        {
            var restored = await ProjectBuildPreparation.RestoreAsync(request, token);
            if (restored.ExitCode != 0) throw new ProjectContextException("context.restoreFailed", "Explicit selected-root restore failed; raw restore output is not public execution evidence.");
        }
        // Workspace never bootstraps restore or treats warm outputs as compiler evidence.
        var loaded = await ProjectContextLoader.LoadGraphAsync(request, token);
        Require(loaded);
        var runners = await ProjectContextLoader.DiscoverTestRunnersAsync(request, token);
        Require(runners);
        var plan = CheckExecutionPlan.Create(runners, loaded, token);
        RequirePolicyTargets(preflight, plan.PreparedGraph);
        ProtectPreparedDestinations(preflight, plan.PreparedGraph);
        if (preflight.Request.Output is not null)
            retainFailureOutputProof?.Invoke(new FreshFailureOutputProof(preflight, request, plan.PreparedGraph,
                priorBundle is null ? [] : priorBundle.ResolvedInputs.Append(priorBundle.ManifestPath).ToArray()));
        var priorObservations = priorBundle is null ? null : FreshPriorBuild.Load(priorBundle, plan.PreparedGraph, preflight.Root, token);
        var owned = Directory.CreateTempSubdirectory("crap4csharp-fresh-private-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(owned, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var bound = await CompilerGraphExecution.RunBoundAsync(request, plan.PreparedGraph, async (node, ct) =>
        {
            if (priorObservations is not null) return priorObservations[node.Id];
            var directory = Path.Combine(owned, "build", node.Id);
            Directory.CreateDirectory(directory);
            return await CompilerEvidence.BuildAsync(node, directory, preflight.Request.Timeout, ct);
        }, token);
        var testDirectory = Path.Combine(owned, "tests"); Directory.CreateDirectory(testDirectory);
        var tests = await TestExecutionAdapter.RunVerifiedAsync(plan, bound, testDirectory, preflight.Request.Timeout, token);
        // Raw failing test outcomes must remain observable. They cannot pass coverage,
        // safe-child success export, or become reusable completed execution evidence.
        if (tests.Failure is not null || tests.Count != plan.Tests.Count || tests.Any(t => t.Process.ExitCode != 0))
            return new(preflight, plan, bound, tests, null,
                new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal), owned);
        await bound.RetainedProof!.ValidateFinalAsync(token);
        var outputFootprint = bound.FinalGraph.GraphContexts.SelectMany(b =>
            new[] { b.Context.EvaluatedIntermediateDirectory!, Path.GetDirectoryName(b.Context.EvaluatedTargetPath!)! }
                .Concat(b.Context.PhaseInputs.Where(i => i.Role == "restoreAssets").Select(i => Path.GetDirectoryName(i.Path)!)))
            .Distinct(PathIdentityPolicy.Current.Comparer).ToArray();
        if (outputFootprint.Any(directory => bound.FinalGraph.BuildGraph.Any(node => node.ProjectPath.StartsWith(
            Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
            throw new PolicyException("provenance.gitOutputFootprintUnsupported", "Generated output footprint must not contain consumer project inputs.");
        await FreshGitProof.ValidateAsync(preflight, token, outputFootprint);
        var coverage = CoverageGraphBinding.ValidateAggregate(tests, bound, token);
        var children = tests.ToDictionary(t => t.GraphId,
            t => TestChildEvidenceExport.Capture(t, tests.Outcomes.Single(o => o.GraphId == t.GraphId), token), StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        return new(preflight, plan, bound, tests, coverage, children, owned);
    }

    private static void Require(ProjectContextLoadResult result)
    {
        if (!result.Success) throw new ProjectContextException(result.FailureReason ?? "context.loadFailed", string.Join(" | ", result.Diagnostics));
    }

    private static void RequirePolicyTargets(FreshCheckPreflight preflight, ProjectContextLoadResult graph)
    {
        var paths = PathIdentityPolicy.Current.Comparer;
        foreach (var project in preflight.Policy.ProductionProjects.Concat(preflight.Policy.TestProjects))
        foreach (var framework in preflight.Policy.TargetFrameworks)
            if (!graph.BuildGraph.Any(n => paths.Equals(n.ProjectPath, Path.GetFullPath(project, preflight.Root)) &&
                n.TargetFramework == framework && n.Configuration == preflight.Policy.Configuration))
                throw new PolicyException("policy.requiredContextMissing", "Selected root graph does not include every trusted required project/configuration/framework.");
        if (graph.GraphContexts.Count == 0) return;
        foreach (var binding in graph.GraphContexts)
            if (binding.IsTestProject && !preflight.Policy.TestProjects.Any(p => paths.Equals(Path.GetFullPath(p, preflight.Root), binding.ProjectPath)) ||
                !binding.IsTestProject && preflight.Policy.TestProjects.Any(p => paths.Equals(Path.GetFullPath(p, preflight.Root), binding.ProjectPath)))
                throw new PolicyException("policy.testContextMismatch", "Prepared test-project inventory differs from trusted required test membership.");
    }

    internal static void ProtectPreparedDestinations(FreshCheckPreflight preflight, ProjectContextLoadResult graph)
    {
        var contexts = graph.GraphContexts.Select(b => b.Context).ToArray();
        FreshCheckPreflightLoader.ValidateDestinations(preflight.Request, preflight.Root,
            contexts.SelectMany(c => c.ProtectedPaths.Concat(c.PhaseInputs.Select(i => i.Path)))
                .Concat(contexts.SelectMany(c => new[] { c.EvaluatedTargetPath!, c.EvaluatedReferenceAssemblyPath! })));
        foreach (var destination in new[] { preflight.Request.Output, preflight.Request.ArtifactOutput }.Where(p => p is not null).Select(p => Path.GetFullPath(p!, preflight.Root)))
        foreach (var directory in contexts.SelectMany(c => new[] { c.EvaluatedIntermediateDirectory!, Path.GetDirectoryName(c.EvaluatedTargetPath!)! }))
            if (PathIdentityPolicy.Current.Comparer.Equals(destination, directory) || destination.StartsWith(
                Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new PolicyException("output.aliasesPolicyInput", "Fresh result/bundle cannot occupy a selected compiler output directory.");
    }
}
