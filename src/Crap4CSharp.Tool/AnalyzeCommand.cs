using System.Reflection;
using Crap4CSharp.Core;
using Microsoft.CodeAnalysis.CSharp;

internal static class AnalyzeCommand
{
    public static ResultDocument Replay(string manifestPath, string workingDirectory, double threshold,
        DateTimeOffset startedAt, TimeSpan duration, CancellationToken cancellationToken)
    {
        var bundle = ArtifactBundle.Load(manifestPath, workingDirectory);
        var manifest = bundle.Manifest;
        var provenance = ProvenanceVerifier.VerifyCapture(manifest, bundle.Bytes, manifest.Producer.ComplexityRuleset);
        var context = manifest.Contexts.OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException("Artifact manifest contains no analysis context.");
        var sources = context.Inputs.Where(input => input.Role == "source" && !input.Generated)
            .OrderBy(input => input.LogicalPath, StringComparer.Ordinal).Select(input =>
            {
                if (!bundle.Bytes.TryGetValue(input.Locator, out var bytes))
                    throw new InvalidDataException($"Captured source is missing: {input.LogicalPath}");
                return CapturedSource.Create(input.LogicalPath, bytes.AsSpan());
            }).ToArray();
        var coverage = manifest.Artifacts.Where(item => item.Kind == "coverage" && item.ContextId == context.Id)
            .OrderBy(item => item.Id, StringComparer.Ordinal).Select(item =>
            {
                if (!bundle.Bytes.TryGetValue(item.Locator, out var bytes))
                    throw new InvalidDataException($"Captured coverage is missing: {item.Id}");
                return new CapturedCoverage(item.Locator, bytes, item.Format ?? "unknown", item.CoordinateKind ?? "unknown");
            }).ToArray();
        var snapshot = EvaluationEngine.Evaluate(new EvaluationInput(context.Id, sources, coverage,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
            new PolicyOptions(threshold, false), provenance), cancellationToken);
        var metrics = snapshot.Metrics.Select(metric => new MetricResult(metric.ContextId, metric.Path,
            metric.MethodIdentity, null, new SourceSpan(metric.StartLine, metric.EndLine), metric.Complexity,
            metric.Coverage, metric.Crap, metric.CoverageReason)).ToArray();
        var findings = snapshot.Metrics.Where(metric => metric.Crap > threshold).Select(metric =>
        {
            var span = new SourceSpan(metric.StartLine, metric.EndLine);
            return new FindingResult(FindingIdentity.Create(metric.ContextId, metric.Path, metric.MethodIdentity, span,
                "crap.thresholdExceeded"), metric.MethodIdentity, "crap.thresholdExceeded", "error", "complexity",
                metric.ContextId, metric.Path, metric.MethodIdentity, null, span, metric.Complexity, metric.Coverage,
                metric.Crap, metric.CoverageReason, threshold, "gt", "fail", []);
        }).ToArray();
        var unknown = metrics.Where(metric => metric.Coverage is null).ToArray();
        var checks = new[]
        {
            new CheckResult("provenance", provenance.Status == ProvenanceStatus.Invalid ? "operationalError" : "pass",
                provenance.Reasons.FirstOrDefault() ?? "provenance.captureConsistent", true),
            new CheckResult("testExecution", "notApplicable", "tests.replayedCapturedEvidence", false),
            new CheckResult("coverage", unknown.Length == 0 ? "pass" : "operationalError",
                unknown.FirstOrDefault()?.CoverageReason ?? "coverage.complete", true),
            new CheckResult("crap", findings.Length > 0 ? "fail" : metrics.Length > 0 ? "pass" : "notApplicable",
                findings.Length > 0 ? "crap.thresholdExceeded" : metrics.Length > 0 ? "crap.withinThreshold" : "crap.noEligibleMethods", true)
        };
        var reasonCounts = unknown.GroupBy(item => item.CoverageReason ?? CoverageReasonCodes.Unavailable, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var evaluation = new EvaluationSection("analyze", new PolicyOptions(threshold, false),
            new EvaluationScope(".", sources.Select(source => source.LogicalPath).ToArray()),
            [new EvaluationContext(context.Id, "captured", context.Project, context.TargetFramework,
                context.Configuration, context.SourceSetHash, null)], checks, metrics, findings,
            new CoverageSummary(metrics.Length, metrics.Count(item => item.Coverage is not null), unknown.Length, reasonCounts),
            manifest.Artifacts.Select(item => new ArtifactIdentity(item.Kind, item.Locator, item.Sha256, "sha256"))
                .OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            new EvaluationDecision(snapshot.ExitCode != 1, snapshot.PolicyDecision,
                snapshot.ExitCode == 1 ? checks.First(check => check.Status == "operationalError").Reason :
                snapshot.ExitCode == 2 ? "crap.thresholdExceeded" : "crap.withinThreshold"))
        { Provenance = snapshot.Provenance };
        var run = new RunSection(Guid.NewGuid().ToString("D"), startedAt, startedAt + duration, duration.TotalMilliseconds,
            [], [], [new RunArtifact("manifest", bundle.ManifestPath, "retained", true, provenance.Reusable)],
            new CancellationDetails(false, false, null), snapshot.ExitCode == 1 ? "operationalError" : "completed", snapshot.ExitCode);
        return new ResultDocument(ResultContract.SchemaVersion, ToolVersion, manifest.Producer.ComplexityRuleset, evaluation, run);
    }

    private static string ToolVersion => typeof(AnalyzeCommand).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
