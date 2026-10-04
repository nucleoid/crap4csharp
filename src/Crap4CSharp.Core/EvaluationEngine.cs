using System.Globalization;

namespace Crap4CSharp.Core;

public static class EvaluationEngine
{
    public static EvaluationSnapshot Evaluate(EvaluationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        var analyzer = new SourceAnalyzer();
        var methods = input.Sources.OrderBy(source => source.LogicalPath, StringComparer.Ordinal)
            .SelectMany(source => analyzer.AnalyzeCaptured(source.LogicalPath, source.Bytes, input.ParseOptions)
                .Select(method => method with { ContextId = input.ContextId, LogicalPath = source.LogicalPath }))
            .ToArray();
        var reports = input.Coverage.OrderBy(report => report.LogicalPath, StringComparer.Ordinal).Select(report =>
            CoverageReader.Read(report.Bytes.AsSpan(), report.LogicalPath).Select(method => method with
            { ContextId = input.ContextId, File = NormalizeCapturedPath(method.File) }).ToArray()).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var matches = CoverageMatcher.ApplyDetailed(methods, reports);
        var metrics = matches.Select(match => new EvaluationMetric(input.ContextId,
            (match.Source.LogicalPath ?? match.Source.File).Replace('\\', '/'), match.Source.CanonicalSignature,
            match.Metric.StartLine, match.Metric.EndLine, match.Metric.Complexity, match.Metric.Coverage,
            match.Metric.Crap, match.CoverageReason)).OrderBy(metric => metric.Path, StringComparer.Ordinal)
            .ThenBy(metric => metric.MethodIdentity, StringComparer.Ordinal).ThenBy(metric => metric.StartLine).ToArray();
        var findings = metrics.Where(metric => metric.Crap > input.Policy.Threshold)
            .Select(metric => FindingIdentity.Create(metric.ContextId, metric.Path, metric.MethodIdentity,
                new SourceSpan(metric.StartLine, metric.EndLine), "crap.thresholdExceeded"))
            .Order(StringComparer.Ordinal).ToArray();
        var operational = input.Provenance.Status == ProvenanceStatus.Invalid ||
            metrics.Any(metric => metric.Coverage is null) && !input.Policy.AllowMissingCoverage;
        var exit = operational ? 1 : findings.Length > 0 ? 2 : 0;
        var decision = operational ? "unknown" : findings.Length > 0 ? "fail" : metrics.Length > 0 ? "pass" : "notApplicable";
        var provenance = new EvaluationProvenance(input.Provenance.Status.ToString().ToLowerInvariant(),
            input.Provenance.Basis, input.Provenance.PostflightVerified, input.Provenance.Reusable,
            input.Provenance.Reasons.Order(StringComparer.Ordinal).ToArray());
        var values = metrics.Select(metric => string.Join("\n", metric.ContextId, metric.Path, metric.MethodIdentity,
            metric.StartLine.ToString(CultureInfo.InvariantCulture), metric.EndLine.ToString(CultureInfo.InvariantCulture),
            metric.Complexity.ToString(CultureInfo.InvariantCulture), metric.Coverage?.ToString("R", CultureInfo.InvariantCulture),
            metric.Crap?.ToString("R", CultureInfo.InvariantCulture), metric.CoverageReason)).Concat(findings)
            .Concat([provenance.Status, provenance.Basis, provenance.PostflightVerified.ToString(),
                provenance.Reusable.ToString(), decision, exit.ToString(CultureInfo.InvariantCulture)]).Concat(provenance.Reasons);
        return new EvaluationSnapshot(CanonicalIdentity.Set("evaluation", values), metrics, findings, provenance, decision, exit);
    }

    private static string? NormalizeCapturedPath(string? path)
    {
        if (path is null) return null;
        var normalized = path.Replace('\\', '/');
        if (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') normalized = normalized[2..];
        return normalized.TrimStart('/');
    }
}
