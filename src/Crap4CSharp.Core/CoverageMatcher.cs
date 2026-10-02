namespace Crap4CSharp.Core;

public static class CoverageMatcher
{
    public sealed record DetailedMatch(SourceMethod Source, MethodMetric Metric, string? CoverageReason);

    public static IReadOnlyList<MethodMetric> Apply(
        IReadOnlyList<SourceMethod> sourceMethods,
        IEnumerable<IReadOnlyList<CoverageMethod>> reports) =>
        ApplyDetailed(sourceMethods, reports).Select(result => result.Metric).ToArray();

    public static IReadOnlyList<DetailedMatch> ApplyDetailed(
        IReadOnlyList<SourceMethod> sourceMethods,
        IEnumerable<IReadOnlyList<CoverageMethod>> reports)
    {
        var reportList = reports.ToArray();
        var observations = sourceMethods.ToDictionary(method => method, _ => new Observation());
        foreach (var report in reportList)
        {
            foreach (var covered in report)
            {
                if (covered.File is null) continue;
                foreach (var source in sourceMethods.Where(source => PathsEqual(source.File, covered.File) &&
                    covered.MethodName == "MoveNext" && covered.TypeName.Contains($"<{source.MethodName}>d__", StringComparison.Ordinal)))
                    observations[source].ObservedReason ??= CoverageReasonCodes.UnsupportedGeneratedMapping;
                var candidates = sourceMethods.Where(source =>
                        PathsEqual(source.File, covered.File) &&
                        source.MethodName == covered.MethodName &&
                        TypesEqual(source.CoverageTypeName, covered.TypeName) &&
                        (covered.ParameterCount is null || ParameterCount(source.Signature) == covered.ParameterCount))
                    .ToArray();
                if (candidates.Length == 0) continue;

                var byLine = candidates.Where(source => covered.SequencePoints.Count > 0 &&
                    covered.SequencePoints.All(point => point.Line >= source.StartLine && point.Line <= source.EndLine)).ToArray();
                var zeroPointMatch = covered.SequencePoints.Count == 0 && candidates.Length == 1 ? candidates[0] : null;
                SourceMethod? match = byLine.Length == 1 ? byLine[0] : zeroPointMatch;
                if (match is null)
                {
                    var reason = candidates.Length > 1 ? CoverageReasonCodes.AmbiguousMethod : CoverageReasonCodes.NoMatchingMethod;
                    foreach (var candidate in candidates) observations[candidate].ObservedReason ??= reason;
                    continue;
                }

                var observation = observations[match];
                observation.HasMatch = true;
                if (covered.SequencePoints.Count > 0)
                    observation.ModuleIdentities.Add(covered.ModuleIdentity ?? "<unknown module>");
                foreach (var point in covered.SequencePoints)
                {
                    var identity = new PointIdentity(point.Line, point.StartColumn, point.EndLine, point.EndColumn, point.Offset);
                    observation.Points[identity] = observation.Points.TryGetValue(identity, out var visited)
                        ? visited || point.Visits > 0
                        : point.Visits > 0;
                }
            }
        }

        return sourceMethods.Select(source =>
        {
            var observation = observations[source];
            double? coverage = observation.ModuleIdentities.Count > 1 || observation.Points.Count == 0
                ? null
                : (double)observation.Points.Count(point => point.Value) / observation.Points.Count;
            var reason = coverage is not null ? null : observation.ModuleIdentities.Count > 1
                ? CoverageReasonCodes.ConflictingModule
                : observation.HasMatch ? CoverageReasonCodes.NoEligiblePoints
                : observation.ObservedReason ?? (reportList.Length > 0 ? CoverageReasonCodes.NoMatchingMethod : CoverageReasonCodes.Unavailable);
            return new DetailedMatch(source, new MethodMetric(source.File, source.TypeName, source.MethodName, source.DisplayName,
                source.StartLine, source.EndLine, source.Complexity, coverage), reason);
        }).ToArray();
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison); }
        catch { return false; }
    }

    private static bool TypesEqual(string source, string covered) =>
        source == covered.Replace('+', '.');

    private static int? ParameterCount(string? signature)
    {
        if (signature is null) return null;
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        var inner = signature[(open + 1)..close];
        if (string.IsNullOrWhiteSpace(inner)) return 0;
        var depth = 0;
        var count = 1;
        foreach (var character in inner)
        {
            if (character is '<' or '[' or '(') depth++;
            else if (character is '>' or ']' or ')') depth--;
            else if (character == ',' && depth == 0) count++;
        }
        return count;
    }

    private sealed class Observation
    {
        public Dictionary<PointIdentity, bool> Points { get; } = [];
        public HashSet<string> ModuleIdentities { get; } = new(StringComparer.Ordinal);
        public bool HasMatch { get; set; }
        public string? ObservedReason { get; set; }
    }

    private readonly record struct PointIdentity(int Line, int? StartColumn, int? EndLine, int? EndColumn, int? Offset);
}
