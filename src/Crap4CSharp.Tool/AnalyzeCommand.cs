using System.Reflection;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class AnalyzeCommand
{
    public static ResultDocument Replay(string manifestPath, string workingDirectory, string? outputPath,
        DateTimeOffset startedAt, TimeSpan duration, CancellationToken cancellationToken, bool allSources = false)
    {
        var bundle = ArtifactBundle.Load(manifestPath, workingDirectory);
        if (outputPath is not null) bundle.RejectOutputAlias(outputPath, workingDirectory);
        var manifest = bundle.Manifest;
        var provenance = ProvenanceVerifier.VerifyCaptureCancellable(manifest, bundle.Bytes, null, cancellationToken);
        // Once byte verification has failed, do not parse secondary policy/scope
        // artifacts: their failure must not replace the verifier's precise reasons.
        (CapturedScope Scope, CapturedPolicy Policy) capturedEvaluation = provenance.Status == ProvenanceStatus.Invalid
            ? (new CapturedScope([]), new CapturedPolicy(8, false))
            : CapturedEvaluationInputs.Read(bundle);
        var threshold = capturedEvaluation.Policy.Threshold;
        var scopedSources = capturedEvaluation.Scope.Sources.ToHashSet(StringComparer.Ordinal);
        var partitions = new List<ReplayPartition>();
        if (provenance.Status != ProvenanceStatus.Invalid)
        {
            foreach (var context in manifest.Contexts.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                var sourceInputs = context.Inputs.Where(input => input.Role == "source" && !input.Generated &&
                        (allSources || scopedSources.Contains(input.LogicalPath)))
                    .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).ToArray();
                var sources = sourceInputs.Select(input =>
                {
                    if (!bundle.Bytes.TryGetValue(input.Locator, out var bytes))
                        throw new InvalidDataException($"Captured source is missing: {input.LogicalPath}");
                    return CapturedSource.Create(input.LogicalPath, bytes.AsSpan());
                }).ToArray();
                var parseOptions = ParseOptions(context.ParseOptions!);
                var pathPolicy = PathPolicy(context.PathPolicy!);
                var references = context.Inputs.Where(input => input.Role == "reference")
                    .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).Select(input =>
                    {
                        if (!bundle.Bytes.TryGetValue(input.Locator, out var bytes))
                            throw new InvalidDataException($"Captured reference is missing: {input.LogicalPath}");
                        try { return MetadataReference.CreateFromImage(bytes); }
                        catch (BadImageFormatException exception)
                        { throw new InvalidDataException($"Captured reference is not a valid managed assembly: {input.LogicalPath}", exception); }
                    }).ToArray();
                var groups = manifest.Artifacts.Where(item => item.Kind == "coverage" && item.ContextId == context.Id)
                    .GroupBy(item => item.BuildId!, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
                foreach (var group in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var build = manifest.Builds.Single(item => item.Id == group.Key);
                    var partitionId = groups.Length == 1 ? context.Id : $"{context.Id}@{build.Id}";
                    var coverage = group.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item =>
                    {
                        if (!bundle.Bytes.TryGetValue(item.Locator, out var bytes))
                            throw new InvalidDataException($"Captured coverage is missing: {item.Id}");
                        return new CapturedCoverage(item.Locator, bytes, item.Format!, item.CoordinateKind!);
                    }).ToArray();
                    partitions.Add(sources.Length == 0
                        ? EmptyPartition(partitionId, context)
                        : manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1
                            ? EvaluateCallables(partitionId, context, build, sources, coverage, references, parseOptions, pathPolicy, threshold)
                            : EvaluateOrdinary(partitionId, context, build, sources, coverage, parseOptions, pathPolicy,
                                threshold, provenance, capturedEvaluation.Policy.AllowMissingCoverage, cancellationToken));
                }
            }
        }

        var metrics = partitions.SelectMany(item => item.Metrics)
            .OrderBy(item => item.ContextId, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.MethodIdentity, StringComparer.Ordinal).ThenBy(item => item.Span.StartLine).ToArray();
        var findings = partitions.SelectMany(item => item.Findings).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var callables = partitions.SelectMany(item => item.Callables).OrderBy(item => item.ContextId, StringComparer.Ordinal)
            .ThenBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Span.Start).ToArray();
        var families = partitions.SelectMany(item => item.Families).OrderBy(item => item.FamilyId, StringComparer.Ordinal).ToArray();
        var diagnostics = partitions.SelectMany(item => item.Diagnostics).GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var unknown = metrics.Where(item => item.Coverage is null).ToArray();
        var checks = new[]
        {
            new CheckResult("provenance", provenance.Status == ProvenanceStatus.Invalid ? "operationalError" : "pass",
                provenance.Status == ProvenanceStatus.Invalid
                    ? provenance.Reasons.FirstOrDefault() ?? "provenance.invalid"
                    : "provenance.captureConsistent", true),
            new CheckResult("testExecution", provenance.Status == ProvenanceStatus.Invalid ? "operationalError" : "pass",
                provenance.Status == ProvenanceStatus.Invalid ? ProvenanceReasonCodes.TestExecutionIncomplete : "tests.capturedSuccessful", true),
            new CheckResult("coverage", unknown.Length == 0 || capturedEvaluation.Policy.AllowMissingCoverage ? "pass" : "operationalError",
                unknown.FirstOrDefault()?.CoverageReason ?? "coverage.complete", true),
            new CheckResult("crap", findings.Length > 0 ? "fail" : metrics.Length > 0 ? "pass" : "notApplicable",
                findings.Length > 0 ? "crap.thresholdExceeded" : metrics.Length > 0 ? "crap.withinThreshold" : "crap.noEligibleMethods", true)
        };
        var operational = checks.Any(check => check.Status == "operationalError");
        var exit = operational ? 1 : findings.Length > 0 ? 2 : 0;
        var decision = new EvaluationDecision(!operational, operational ? "unknown" : findings.Length > 0 ? "fail" :
            metrics.Length > 0 ? "pass" : "notApplicable", operational
            ? checks.First(check => check.Status == "operationalError").Reason
            : findings.Length > 0 ? "crap.thresholdExceeded" : metrics.Length > 0 ? "crap.withinThreshold" : "crap.noEligibleMethods");
        var reasonCounts = unknown.GroupBy(item => item.CoverageReason ?? CoverageReasonCodes.Unavailable, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var evaluation = new EvaluationSection("analyze",
            new PolicyOptions(threshold, capturedEvaluation.Policy.AllowMissingCoverage),
            new EvaluationScope(".", capturedEvaluation.Scope.Sources),
            partitions.Select(partition => new EvaluationContext(partition.ContextId, "captured", partition.Project,
                partition.TargetFramework, partition.Configuration, partition.SourceSetHash, null)).ToArray(),
            checks, metrics, findings, new CoverageSummary(metrics.Length, metrics.Count(item => item.Coverage is not null),
                unknown.Length, reasonCounts),
            manifest.Artifacts.Select(item => new ArtifactIdentity(item.Kind, item.Locator, item.Sha256, "sha256"))
                .OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            decision)
        {
            Provenance = new EvaluationProvenance(provenance.Status.ToString().ToLowerInvariant(), provenance.Basis,
                provenance.PostflightVerified, provenance.Reusable, provenance.Reasons),
            Callables = manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1 ? callables : null,
            Families = manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1 ? families : null,
            CallableExemptions = manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1 ? [] : null,
            ExemptionErrors = manifest.Producer.ComplexityRuleset == ComplexityRules.CallablesV1 ? [] : null,
            CoverageDiagnostics = diagnostics,
            CoveragePathPolicy = PathPolicyResult(manifest.Contexts)
        };
        var run = new RunSection(Guid.NewGuid().ToString("D"), startedAt, startedAt + duration, duration.TotalMilliseconds,
            [], [], [new RunArtifact("manifest", bundle.ManifestPath, "retained", true, provenance.Reusable)],
            new CancellationDetails(false, false, null), operational ? "operationalError" : "completed", exit);
        return new ResultDocument(ResultContract.SchemaVersion, ToolVersion, manifest.Producer.ComplexityRuleset, evaluation, run);
    }

    private static ReplayPartition EvaluateOrdinary(string contextId, ManifestContext manifestContext,
        ManifestBuild build, IReadOnlyList<CapturedSource> sources,
        IReadOnlyList<CapturedCoverage> coverage, CSharpParseOptions parseOptions, CapturedPathPolicy pathPolicy,
        double threshold, ProvenanceResult provenance, bool allowMissingCoverage, CancellationToken cancellationToken)
    {
        var snapshot = EvaluationEngine.Evaluate(new EvaluationInput(contextId, sources, coverage, parseOptions,
            new PolicyOptions(threshold, allowMissingCoverage), provenance)
            { PathPolicy = pathPolicy, ExpectedModuleIdentity = build.ModuleIdentity }, cancellationToken);
        var metrics = snapshot.Metrics.Select(metric => new MetricResult(metric.ContextId, metric.Path,
            metric.MethodIdentity, null, new SourceSpan(metric.StartLine, metric.EndLine), metric.Complexity,
            metric.Coverage, metric.Crap, metric.CoverageReason)).ToArray();
        return new ReplayPartition(contextId, manifestContext.Project, manifestContext.TargetFramework,
            manifestContext.Configuration, manifestContext.Platform, manifestContext.SourceSetHash, metrics,
            ThresholdFindings(metrics, threshold), [], [], []);
    }

    private static ReplayPartition EmptyPartition(string contextId, ManifestContext context) => new(contextId,
        context.Project, context.TargetFramework, context.Configuration, context.Platform, context.SourceSetHash,
        [], [], [], [], []);

    private static ReplayPartition EvaluateCallables(string contextId, ManifestContext manifestContext,
        ManifestBuild build, IReadOnlyList<CapturedSource> sources, IReadOnlyList<CapturedCoverage> coverage,
        IReadOnlyList<MetadataReference> references, CSharpParseOptions parseOptions,
        CapturedPathPolicy pathPolicy, double threshold)
    {
        var context = new CallableAnalysisContext(manifestContext.Project, manifestContext.TargetFramework,
            manifestContext.Configuration, manifestContext.Platform, contextId, parseOptions);
        var inventory = CallableInventory.Merge(sources.Select(source =>
            CallableInventory.Analyze(source.Text, source.LogicalPath, context, references)).ToArray());
        var sourcePaths = sources.Select(source => source.LogicalPath).ToArray();
        var reports = coverage.SelectMany(report => CapturedLogicalPathResolver.ResolveMethods(
                CoverageReader.Read(report.Bytes.AsSpan(), report.LogicalPath), sourcePaths, pathPolicy))
            .Select(method => method with { ContextId = contextId }).ToArray();
        var resolution = CallableCoverageResolver.Resolve(inventory, reports, build.ModuleIdentity);
        var observations = resolution.Observations.ToDictionary(item => item.ObservationId!, StringComparer.Ordinal);
        var families = CallableFamilyEvaluator.Evaluate(inventory, resolution.Observations, threshold);
        var callables = inventory.Callables.Select(item =>
        {
            var observation = observations[item.ObservationId];
            var fraction = observation.Status == "known" && observation.Points.Count > 0
                ? (double)observation.Points.Count(point => point.Visited) / observation.Points.Count : (double?)null;
            var crap = item.Complexity is int complexity && fraction is double known
                ? CrapCalculator.Calculate(complexity, known) : (double?)null;
            return new CallableResult(item.CallableId, item.ObservationId, item.Kind.ToString(), item.ParentId,
                contextId, item.Path, item.Span, item.Complexity,
                item.Applicability == CallableApplicability.Applicable ? "applicable" : "not-applicable",
                observation.Status, fraction, crap, observation.Reason, item.CoverageCapability, item.BodyChecksum)
            {
                Ruleset = ComplexityRules.CallablesV1,
                SemanticSignature = item.SemanticIdentity?.ReportSignature,
                Documents = [item.Path],
                MappingEvidenceKind = observation.Status == "known" ? "semanticSourceIdentity" : null,
                FamilyIds = families.Where(family => family.MemberObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
                    .Select(family => family.FamilyId).Order(StringComparer.Ordinal).ToArray()
            };
        }).ToArray();
        var metrics = callables.Where(item => item.Applicability == "applicable").Select(item =>
            new MetricResult(contextId, item.Path, item.CallableId, item.SemanticSignature,
                new SourceSpan(item.Span.StartLine, item.Span.EndLine), item.Complexity ?? 0,
                item.Coverage, item.Crap, item.CoverageReason)).ToArray();
        var findings = ThresholdFindings(metrics, threshold).Concat(families.Where(family => family.IsViolation).Select(family =>
        {
            var root = callables.First(item => item.CallableId == family.RootCallableId);
            var span = new SourceSpan(root.Span.StartLine, root.Span.EndLine);
            return new FindingResult(FindingIdentity.Create(contextId, root.Path, family.FamilyId, span,
                CallableFamilyEvaluator.Rule), family.FamilyId, CallableFamilyEvaluator.Rule, "error", "complexity",
                contextId, root.Path, family.FamilyId, null, span, family.Complexity, family.Coverage, family.Crap,
                null, threshold, "gt", "fail", []);
        })).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        return new ReplayPartition(contextId, manifestContext.Project, manifestContext.TargetFramework,
            manifestContext.Configuration, manifestContext.Platform, manifestContext.SourceSetHash,
            metrics, findings, callables, families, resolution.Diagnostics);
    }

    private static IReadOnlyList<FindingResult> ThresholdFindings(IEnumerable<MetricResult> metrics, double threshold) =>
        metrics.Where(metric => metric.Crap > threshold).Select(metric => new FindingResult(
            FindingIdentity.Create(metric.ContextId, metric.Path, metric.MethodIdentity, metric.Span, "crap.thresholdExceeded"),
            metric.MethodIdentity, "crap.thresholdExceeded", "error", "complexity", metric.ContextId, metric.Path,
            metric.MethodIdentity, null, metric.Span, metric.Complexity, metric.Coverage, metric.Crap,
            metric.CoverageReason, threshold, "gt", "fail", [])).ToArray();

    private static CSharpParseOptions ParseOptions(ManifestParseOptions captured)
    {
        if (!LanguageVersionFacts.TryParse(captured.LanguageVersion, out var languageVersion))
            throw new InvalidDataException($"Unsupported captured C# language version: {captured.LanguageVersion}");
        if (!Enum.TryParse<Microsoft.CodeAnalysis.SourceCodeKind>(captured.SourceKind, true, out var kind))
            throw new InvalidDataException($"Unsupported captured source kind: {captured.SourceKind}");
        return CSharpParseOptions.Default.WithLanguageVersion(languageVersion).WithKind(kind)
            .WithPreprocessorSymbols(captured.PreprocessorSymbols).WithFeatures(captured.Features);
    }

    private static CapturedPathPolicy PathPolicy(ManifestPathPolicy captured) => new(
        captured.CasePolicy switch
        {
            "sensitive" => true,
            "insensitive" => false,
            _ => throw new InvalidDataException($"Unsupported captured path case policy: {captured.CasePolicy}")
        }, captured.ReportRootMappings);

    private static CoveragePathPolicyResult PathPolicyResult(IEnumerable<ManifestContext> contexts)
    {
        var values = contexts.Select(context => context.PathPolicy!).ToArray();
        var @case = values.Select(value => value.CasePolicy).Distinct(StringComparer.Ordinal).Count() == 1
            ? values[0].CasePolicy : "per-context";
        return new CoveragePathPolicyResult(@case, values.SelectMany(value => value.ReportRootMappings)
            .Select(mapping => new CoveragePathMappingResult(mapping.ReportRoot, mapping.LogicalRoot,
                CanonicalIdentity.Set("captured-report-root-mapping-v1", [mapping.ReportRoot, mapping.LogicalRoot])))
            .GroupBy(mapping => mapping.Id, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(mapping => mapping.Id, StringComparer.Ordinal).ToArray());
    }

    private static string ToolVersion => typeof(AnalyzeCommand).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed record ReplayPartition(string ContextId, string? Project, string? TargetFramework,
        string? Configuration, string? Platform, string? SourceSetHash, IReadOnlyList<MetricResult> Metrics,
        IReadOnlyList<FindingResult> Findings, IReadOnlyList<CallableResult> Callables,
        IReadOnlyList<CallableFamilyMetric> Families, IReadOnlyList<CoverageDiagnostic> Diagnostics);
}
