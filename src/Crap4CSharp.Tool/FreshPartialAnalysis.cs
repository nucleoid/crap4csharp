using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Diagnostic observations from genuinely failed tests. No manifest, successful
// aggregate or reusable evidence is constructed. Trusted policy filters diagnostic
// findings only; its decision never replaces the failed execution terminal.
internal static class FreshPartialAnalysis
{
    internal static async Task<ResultDocument> AttachAsync(FreshExecutionEvidence evidence,
        ResultDocument terminal, CancellationToken token)
    {
        if (evidence.Tests.Failure is not null || !evidence.Tests.Any(t => t.Process.ExitCode == 1)) return terminal;
        try
        {
            await RegateAsync(evidence, token);
            var reports = evidence.Tests.Select(t => t.Process.ExitCode == 1
                ? CoverageGraphBinding.ValidateFailedInvocation(t, evidence.Compiler, token)
                : CoverageGraphBinding.ValidateInvocation(t, evidence.Compiler, token)).ToArray();
            var graph = evidence.Compiler.FinalGraph;
            var observations = evidence.Compiler.Observations.ToDictionary(o => o.ProjectPath, PathIdentityPolicy.Current.Comparer);
            var partitions = new List<AnalyzeCommand.ReplayPartition>();
            var repositoryPaths = new Dictionary<(string, string), string>();
            long total = 0;
            foreach (var binding in graph.GraphContexts.Where(b => !b.IsTestProject || b.Context.ExclusionPolicy.IncludeTests))
            {
                token.ThrowIfCancellationRequested();
                var context = binding.Context; var compiler = observations[binding.ProjectPath];
                CompilerOutputBinding.Validate(context, compiler, token);
                var pe = CompilerOutputBinding.Read(compiler.Assembly!.Path, ref total, token);
                var pdb = CompilerOutputBinding.Read(compiler.Pdb!.Path, ref total, token);
                CompilerOutputBinding.Match(compiler.Assembly, pe); CompilerOutputBinding.Match(compiler.Pdb, pdb);
                var build = ArtifactEvidenceInspector.InspectBuild(pe, pdb);
                var sources = context.CompilerDocuments.Select(d =>
                {
                    var bytes = CompilerOutputBinding.Read(d.ResolvedPath!, ref total, token);
                    if (CanonicalIdentity.Sha256(bytes.AsSpan()) != d.ContentIdentity)
                        throw new ProjectContextException("provenance.partialSourceChanged", "Partial analysis source changed.");
                    var relative = Path.GetRelativePath(evidence.Preflight.Root, d.ResolvedPath!).Replace('\\', '/');
                    if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                        throw new ProjectContextException("provenance.partialSourceUnsupported", "Partial source is outside the selected root.");
                    repositoryPaths.Add((binding.GraphId, d.LogicalPath), relative);
                    return CapturedSource.Create(d.LogicalPath, bytes.AsSpan());
                }).ToArray();
                var references = compiler.Parameters.Single(p => p.Name == "References").Items
                    .Select(p => MetadataReference.CreateFromImage(CompilerOutputBinding.Read(p, ref total, token))).ToArray();
                var syntax = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, context.ParseOptions, s.LogicalPath)).ToArray();
                var eligible = context.CompilerDocuments.Where(d => !d.IsGenerated || context.ExclusionPolicy.IncludeGenerated)
                    .Select(d => d.LogicalPath).ToHashSet(StringComparer.Ordinal);
                var coverage = reports.SelectMany(r => r.Modules).Where(m => m.GraphId == binding.GraphId)
                    .Select((m, i) => new CapturedCoverage("partial-" + i + ".xml", m.LineProjection, "opencover", "line")).ToArray();
                // Already-normalized line projections use captured logical paths,
                // while Coverlet retains physical paths in each module File table.
                var pathPolicy = new CapturedPathPolicy(!OperatingSystem.IsWindows(), context.CompilerDocuments
                    .Select(d => new ManifestReportRootMapping(d.ResolvedPath!, d.LogicalPath)).ToArray());
                var analysis = new CallableAnalysisContext(Path.GetRelativePath(evidence.Preflight.Root, binding.ProjectPath).Replace('\\', '/'),
                    context.TargetFramework, context.Configuration, context.Platform, binding.GraphId, context.ParseOptions);
                partitions.Add(AnalyzeCommand.EvaluateBoundCallables(analysis,
                    CanonicalIdentity.Set("partial-source-set-v1", sources.Select(s => CanonicalIdentity.Tuple(s.LogicalPath, s.ContentIdentity))),
                    build.ModuleIdentity, sources.Where(s => eligible.Contains(s.LogicalPath)).ToArray(), coverage,
                    references, pathPolicy, evidence.Preflight.Policy.Threshold, syntax));
            }
            bool Selected(CallableResult callable)
            {
                var preflight = evidence.Preflight; var scope = preflight.Scope;
                var path = repositoryPaths[(callable.ContextId, callable.Path)];
                var excluded = (scope?.Diagnostics ?? []).Where(d => d.StartsWith("scope.excludedTestSource:", StringComparison.Ordinal) ||
                    d.StartsWith("scope.excludedChangedSource:", StringComparison.Ordinal)).Select(d => d[(d.IndexOf(':') + 1)..]).ToArray();
                PolicyCheckCommand.ValidateScopeSourceCase([path], (scope?.Files ?? []).Where(f => f.NewPath is not null)
                    .Select(f => f.NewPath!).Concat(excluded));
                var widened = scope is { Completeness: not ScopeCompleteness.Complete };
                return preflight.Request.Granularity == "file"
                    ? preflight.Policy.Scope == "all" || widened || excluded.Contains(path, StringComparer.Ordinal) || (scope?.Files ?? []).Any(f => f.NewPath == path)
                    : PolicyCheckCommand.IsSelected(preflight.Policy.Scope, widened, scope?.Files ?? [], callable, path, excluded);
            }
            var allCallables = partitions.SelectMany(p => p.Callables).ToArray();
            var allFamilies = partitions.SelectMany(p => p.Families).ToArray();
            var selections = allCallables.ToDictionary(c => c.ObservationId, Selected, StringComparer.Ordinal);
            var diagnosticInventory = terminal with { Evaluation = terminal.Evaluation with
            {
                Contexts = partitions.Select(p => new EvaluationContext(p.ContextId, "project", p.Project, p.TargetFramework,
                    p.Configuration, p.SourceSetHash, null)).ToArray(),
                Callables = allCallables, Families = allFamilies
            }};
            var policy = evidence.Preflight.Policy;
            var observationsForPolicy = PolicyCheckCommand.PolicyObservations(diagnosticInventory, policy, selections,
                (contextId, logicalPath) => repositoryPaths[(contextId, logicalPath)]).ToArray();
            var exemptions = PolicyCheckCommand.ParseExemptions(evidence.Preflight.Trusted.ExemptionBytes, diagnosticInventory);
            var compatibility = TrustedPolicyLoader.BoundCompatibilityHash(evidence.Preflight.Trusted.Policy,
                evidence.Preflight.Trusted.ExemptionBytes);
            var diagnosticPolicy = PolicyEvaluator.Evaluate(policy, evidence.Preflight.Trusted.Baseline,
                observationsForPolicy, exemptions.Active, compatibility);
            var callables = allCallables.Where(c => selections[c.ObservationId]).ToArray();
            var observationIds = callables.Select(c => c.ObservationId).ToHashSet(StringComparer.Ordinal);
            var families = partitions.SelectMany(p => p.Families).Where(f => f.MemberObservationIds.All(observationIds.Contains)).ToArray();
            var familyIds = families.Select(f => f.FamilyId).ToHashSet(StringComparer.Ordinal);
            callables = callables.Select(c => c with { FamilyIds = c.FamilyIds.Where(familyIds.Contains).ToArray() }).ToArray();
            var selected = callables.Select(c => (c.ContextId, c.CallableId)).ToHashSet();
            var metrics = partitions.SelectMany(p => p.Metrics).Where(m => selected.Contains((m.ContextId, m.MethodIdentity))).ToArray();
            var findings = diagnosticPolicy.Findings.Select(f => PolicyCheckCommand.ToFinding(f, diagnosticInventory, policy.Threshold)).ToArray();
            var unknown = metrics.Where(m => m.Coverage is null).ToArray();
            // Re-read every owned attachment and compiler/consumer input after analysis.
            await RegateAsync(evidence, token);
            foreach (var test in evidence.Tests)
            {
                var current = test.Process.ExitCode == 1 ? CoverageGraphBinding.ValidateFailedInvocation(test, evidence.Compiler, token)
                    : CoverageGraphBinding.ValidateInvocation(test, evidence.Compiler, token);
                if (current.Attachment.Sha256 != reports.Single(r => r.GraphId == test.GraphId).Attachment.Sha256)
                    throw new ProjectContextException("coverage.partialAttachmentChanged", "Partial collector attachment changed.");
            }
            return terminal with { Evaluation = terminal.Evaluation with
            {
                Scope = new(evidence.Preflight.Root, metrics.Select(m => m.Path).Distinct(StringComparer.Ordinal).ToArray()),
                Contexts = partitions.Select(p => new EvaluationContext(p.ContextId, "project", p.Project, p.TargetFramework,
                    p.Configuration, p.SourceSetHash, null)).ToArray(),
                Metrics = metrics, Findings = findings, Callables = callables, Families = families,
                CallableExemptions = exemptions.Matches, ExemptionErrors = diagnosticPolicy.OperationalReasons,
                Coverage = new(metrics.Length, metrics.Length - unknown.Length, unknown.Length,
                    unknown.GroupBy(m => m.CoverageReason ?? CoverageReasonCodes.Unavailable).ToDictionary(g => g.Key, g => g.Count())),
                CoverageDiagnostics = partitions.SelectMany(p => p.Diagnostics).ToArray(),
                Checks = terminal.Evaluation.Checks.Select(c => c.Name == "coverage" ? c with { Reason = "coverage.boundPartialObservations" } :
                    c.Name == "crap" ? c with { Reason = "crap.partialObservationsNotPolicyDecision" } : c).ToArray()
            }};
        }
        catch (Exception error) when (error is ProjectContextException or PolicyException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Preserve the original raw outcomes and fail closed without stale metrics.
            var reason = error is ProjectContextException context ? context.Reason : error is PolicyException policy ? policy.Code : "coverage.partialAnalysisRefused";
            return terminal with { Evaluation = terminal.Evaluation with
                { Checks = terminal.Evaluation.Checks.Select(c => c.Name == "coverage" ? c with { Reason = reason } : c).ToArray() } };
        }
    }

    private static async Task RegateAsync(FreshExecutionEvidence evidence, CancellationToken token)
    {
        await evidence.Compiler.RetainedProof!.ValidateFinalAsync(token);
        var graph = evidence.Compiler.FinalGraph;
        var footprint = graph.GraphContexts.SelectMany(b => new[] { b.Context.EvaluatedIntermediateDirectory!, Path.GetDirectoryName(b.Context.EvaluatedTargetPath!)! }
            .Concat(b.Context.PhaseInputs.Where(i => i.Role == "restoreAssets").Select(i => Path.GetDirectoryName(i.Path)!)))
            .Distinct(PathIdentityPolicy.Current.Comparer).ToArray();
        await FreshGitProof.ValidateAsync(evidence.Preflight, token, footprint);
        FreshCheckExecution.ProtectPreparedDestinations(evidence.Preflight, graph);
    }
}
