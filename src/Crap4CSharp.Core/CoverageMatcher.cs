namespace Crap4CSharp.Core;

public static class CoverageMatcher
{
    public static IReadOnlyList<MethodMetric> Apply(
        IReadOnlyList<SourceMethod> sourceMethods,
        IEnumerable<IReadOnlyList<CoverageMethod>> reports)
    {
        var observations = sourceMethods.ToDictionary(method => method, _ => new Observation());
        foreach (var report in reports)
        {
            foreach (var covered in report)
            {
                if (covered.File is null) continue;
                var candidates = sourceMethods.Where(source =>
                        PathsEqual(source.File, covered.File) &&
                        source.MethodName == covered.MethodName &&
                        TypesEqual(source.CoverageTypeName, covered.TypeName) &&
                        (covered.ParameterCount is null || ParameterCount(source.Signature) == covered.ParameterCount))
                    .ToArray();
                if (candidates.Length == 0) continue;

                var byLine = candidates.Where(source => covered.SequencePoints.Count > 0 &&
                    covered.SequencePoints.All(point => point.Line >= source.StartLine && point.Line <= source.EndLine)).ToArray();
                SourceMethod? match = byLine.Length == 1 ? byLine[0] : null;
                if (match is null) continue;

                var observation = observations[match];
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
            return new MethodMetric(source.File, source.TypeName, source.MethodName, source.DisplayName,
                source.StartLine, source.EndLine, source.Complexity, coverage);
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
    }

    private readonly record struct PointIdentity(int Line, int? StartColumn, int? EndLine, int? EndColumn, int? Offset);
}
