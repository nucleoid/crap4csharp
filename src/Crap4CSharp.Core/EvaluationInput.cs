using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

public sealed record CapturedCoverage(string LogicalPath, ImmutableArray<byte> Bytes, string Format, string CoordinateKind);
public sealed record EvaluationInput(string ContextId, IReadOnlyList<CapturedSource> Sources,
    IReadOnlyList<CapturedCoverage> Coverage, CSharpParseOptions ParseOptions, PolicyOptions Policy,
    ProvenanceResult Provenance)
{
    public CapturedPathPolicy PathPolicy { get; init; } = new(true, []);
    public string? ExpectedModuleIdentity { get; init; }
}
public sealed record CapturedPathPolicy(bool CaseSensitive, IReadOnlyList<ManifestReportRootMapping> ReportRootMappings);
public sealed record EvaluationMetric(string ContextId, string Path, string MethodIdentity, int StartLine,
    int EndLine, int Complexity, double? Coverage, double? Crap, string? CoverageReason);
public sealed record EvaluationProvenance(string Status, string Basis, bool PostflightVerified,
    bool Reusable, IReadOnlyList<string> Reasons);

public sealed class EvaluationSnapshot : IEquatable<EvaluationSnapshot>
{
    public EvaluationSnapshot(string identity, IReadOnlyList<EvaluationMetric> metrics,
        IReadOnlyList<string> findings, EvaluationProvenance provenance, string policyDecision, int exitCode)
    { Identity = identity; Metrics = metrics; Findings = findings; Provenance = provenance; PolicyDecision = policyDecision; ExitCode = exitCode; }
    public string Identity { get; }
    public IReadOnlyList<EvaluationMetric> Metrics { get; }
    public IReadOnlyList<string> Findings { get; }
    public EvaluationProvenance Provenance { get; }
    public string PolicyDecision { get; }
    public int ExitCode { get; }
    public bool Equals(EvaluationSnapshot? other) => other is not null && Identity == other.Identity;
    public override bool Equals(object? obj) => obj is EvaluationSnapshot other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Identity);
}

public static class CapturedLogicalPathResolver
{
    public static string? Resolve(string? reportedPath, IReadOnlyList<string> sourcePaths, CapturedPathPolicy policy)
    {
        if (reportedPath is null) return null;
        var comparison = policy.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var normalized = Normalize(reportedPath);
        var sources = sourcePaths.Select(CanonicalIdentity.NormalizeLogicalPath).ToArray();
        if (!IsAbsolute(normalized))
        {
            var relative = NormalizeRelative(normalized);
            return sources.SingleOrDefault(source => string.Equals(source, relative, comparison));
        }

        var matches = new List<string>();
        foreach (var mapping in policy.ReportRootMappings)
        {
            var root = Normalize(mapping.ReportRoot).TrimEnd('/');
            if (!string.Equals(normalized, root, comparison) &&
                !normalized.StartsWith(root + "/", comparison)) continue;
            var suffix = normalized.Length == root.Length ? string.Empty : normalized[(root.Length + 1)..];
            var logical = string.IsNullOrEmpty(suffix) ? mapping.LogicalRoot :
                string.IsNullOrEmpty(mapping.LogicalRoot) ? suffix : mapping.LogicalRoot.TrimEnd('/') + "/" + suffix;
            string candidate;
            try { candidate = CanonicalIdentity.NormalizeLogicalPath(logical); }
            catch (ArgumentException) { continue; }
            matches.AddRange(sources.Where(source => string.Equals(source, candidate, comparison)));
        }
        return matches.Distinct(policy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .Count() == 1 ? matches[0] : normalized;
    }

    private static string NormalizeRelative(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." || segment.Length == 0) throw new InvalidDataException("Captured report path escapes its logical root.");
            segments.Add(segment);
        }
        return CanonicalIdentity.NormalizeLogicalPath(string.Join('/', segments));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static bool IsAbsolute(string path) => path.StartsWith('/') ||
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
}
