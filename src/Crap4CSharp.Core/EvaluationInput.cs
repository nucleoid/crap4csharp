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
    public static IReadOnlyList<CoverageMethod> ResolveMethods(IEnumerable<CoverageMethod> methods,
        IReadOnlyList<string> sourcePaths, CapturedPathPolicy policy)
    {
        var comparer = policy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var sources = sourcePaths.Select(CanonicalIdentity.NormalizeLogicalPath).ToHashSet(comparer);
        var index = new CapturedLogicalPathIndex(sourcePaths, policy);
        return methods.Select(method => method with { File = index.Resolve(method.File) })
            .GroupBy(MethodCandidateIdentity, StringComparer.Ordinal).Select(group =>
            {
                var matches = group.Where(method => method.File is not null && sources.Contains(method.File))
                    .Select(method => method.File!).Distinct(comparer).ToArray();
                return group.First() with { File = matches.Length == 1 ? matches[0] : null };
            }).ToArray();
    }

    public static string? Resolve(string? reportedPath, IReadOnlyList<string> sourcePaths, CapturedPathPolicy policy)
        => new CapturedLogicalPathIndex(sourcePaths, policy).Resolve(reportedPath);

    internal static string NormalizeRelative(string path)
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

    internal static string Normalize(string path) => path.Replace('\\', '/');
    internal static bool IsAbsolute(string path) => path.StartsWith('/') ||
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';

    private static string MethodCandidateIdentity(CoverageMethod method) => CanonicalIdentity.Tuple(
        "captured-coverage-method-candidate-v1", method.ReportId, method.ModuleIdentity, method.TypeName,
        method.MethodName, method.ParameterCount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        method.RawSignature, method.MethodToken,
        string.Join("\n", method.SequencePoints.Select(point => string.Join(":", point.Line, point.Visits,
            point.StartColumn, point.EndLine, point.EndColumn, point.Offset))));
}

public sealed class CapturedLogicalPathIndex
{
    private readonly StringComparer comparer;
    private readonly StringComparison comparison;
    private readonly IReadOnlyDictionary<string, string> sources;
    private readonly (string Root, string LogicalRoot)[] mappings;

    public CapturedLogicalPathIndex(IReadOnlyList<string> sourcePaths, CapturedPathPolicy policy)
    {
        comparer = policy.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        comparison = policy.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        sources = sourcePaths.Select(CanonicalIdentity.NormalizeLogicalPath)
            .Distinct(comparer).ToDictionary(path => path, path => path, comparer);
        mappings = policy.ReportRootMappings.Select(mapping =>
            (CapturedLogicalPathResolver.Normalize(mapping.ReportRoot).TrimEnd('/'), mapping.LogicalRoot)).ToArray();
    }

    public string? Resolve(string? reportedPath)
    {
        if (reportedPath is null) return null;
        var normalized = CapturedLogicalPathResolver.Normalize(reportedPath);
        if (!CapturedLogicalPathResolver.IsAbsolute(normalized))
        {
            var relative = CapturedLogicalPathResolver.NormalizeRelative(normalized);
            return sources.TryGetValue(relative, out var source) ? source : relative;
        }

        string? match = null;
        foreach (var mapping in mappings)
        {
            if (!string.Equals(normalized, mapping.Root, comparison) &&
                !normalized.StartsWith(mapping.Root + "/", comparison)) continue;
            var suffix = normalized.Length == mapping.Root.Length ? string.Empty : normalized[(mapping.Root.Length + 1)..];
            var logical = string.IsNullOrEmpty(suffix) ? mapping.LogicalRoot :
                string.IsNullOrEmpty(mapping.LogicalRoot) ? suffix : mapping.LogicalRoot.TrimEnd('/') + "/" + suffix;
            string candidate;
            try { candidate = CanonicalIdentity.NormalizeLogicalPath(logical); }
            catch (ArgumentException) { continue; }
            if (!sources.TryGetValue(candidate, out var source)) continue;
            if (match is not null && !comparer.Equals(match, source)) return normalized;
            match = source;
        }
        return match ?? normalized;
    }
}
