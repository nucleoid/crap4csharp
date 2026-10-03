namespace Crap4CSharp.Core;

public static class CoverageMatcher
{
    public sealed record DetailedMatch(SourceMethod Source, MethodMetric Metric, string? CoverageReason)
    {
        public IReadOnlyList<string> ReasonCodes { get; init; } = [];
        public IReadOnlyList<string> DiagnosticIds { get; init; } = [];
    }

    public static IReadOnlyList<MethodMetric> Apply(
        IReadOnlyList<SourceMethod> sourceMethods,
        IEnumerable<IReadOnlyList<CoverageMethod>> reports) =>
        ApplyDetailed(sourceMethods, reports).Select(result => result.Metric).ToArray();

    public static IReadOnlyList<DetailedMatch> ApplyDetailed(
        IReadOnlyList<SourceMethod> sourceMethods,
        IEnumerable<IReadOnlyList<CoverageMethod>> reports) =>
        ApplyDetailedResult(sourceMethods, reports).Matches;

    public static CoverageMatchResult ApplyDetailedResult(
        IReadOnlyList<SourceMethod> sourceMethods,
        IEnumerable<IReadOnlyList<CoverageMethod>> reports,
        PathIdentityPolicy? pathPolicy = null)
    {
        pathPolicy ??= PathIdentityPolicy.Current;
        var reportList = reports.ToArray();
        var observations = sourceMethods.ToDictionary(method => method, _ => new Observation());
        var diagnostics = new List<CoverageDiagnostic>();
        foreach (var report in reportList)
        {
            foreach (var covered in report)
            {
                if (covered.PathResolution?.Diagnostic is { } pathDiagnostic) diagnostics.Add(pathDiagnostic);
                if (covered.File is null) continue;
                var byPath = sourceMethods.Where(source => PathsEqual(pathPolicy, source.File, covered.File)).ToArray();
                if (covered.MethodName == "MoveNext" && IsGeneratedStateMachine(covered.TypeName))
                {
                    diagnostics.Add(CoverageDiagnostic.Create(CoverageReasonCodes.UnsupportedGeneratedMapping,
                        CoverageDiagnosticStage.Generated, CoverageDiagnosticSeverity.Warning, CoverageDiagnosticScope.Observation,
                        covered.ReportId, covered.ObservationId, reportedType: covered.TypeName,
                        reportedMethodName: covered.MethodName, reportedParameterCount: covered.ParameterCount,
                        moduleIdentities: covered.ModuleIdentity is null ? [] : [covered.ModuleIdentity],
                        message: "Generated MoveNext coverage cannot be associated without authoritative metadata."));
                    continue;
                }
                if (byPath.Length == 0) continue;

                var byType = byPath.Where(source => TypesEqual(source.CoverageTypeName, covered.TypeName)).ToArray();
                if (byType.Length == 0)
                {
                    RecordRejection(byPath, covered, CoverageReasonCodes.TypeMismatch, CoverageDiagnosticStage.Method,
                        observations, diagnostics);
                    continue;
                }

                var bySignature = byType.Where(source => source.MethodName == covered.MethodName &&
                    (covered.ParameterCount is null || ParameterCount(source.Signature) == covered.ParameterCount)).ToArray();
                if (bySignature.Length == 0)
                {
                    RecordRejection(byType, covered, CoverageReasonCodes.SignatureMismatch, CoverageDiagnosticStage.Method,
                        observations, diagnostics);
                    continue;
                }

                if (covered.SequencePoints.Count == 0)
                {
                    RecordRejection(bySignature, covered, CoverageReasonCodes.NoEligiblePoints, CoverageDiagnosticStage.Points,
                        observations, diagnostics);
                    foreach (var source in bySignature) observations[source].HasCompatibleObservation = true;
                    continue;
                }

                var byLine = bySignature.Where(source => covered.SequencePoints.All(point =>
                    point.Line >= source.StartLine && point.Line <= source.EndLine)).ToArray();
                if (byLine.Length == 0)
                {
                    RecordRejection(bySignature, covered, CoverageReasonCodes.SpanMismatch, CoverageDiagnosticStage.Points,
                        observations, diagnostics);
                    continue;
                }
                if (byLine.Length > 1)
                {
                    RecordRejection(byLine, covered, CoverageReasonCodes.AmbiguousMethod, CoverageDiagnosticStage.Method,
                        observations, diagnostics);
                    continue;
                }

                var match = byLine[0];
                var observation = observations[match];
                observation.HasCompatibleObservation = true;
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

        foreach (var pair in observations.Where(pair => pair.Value.ModuleIdentities.Count > 1))
        {
            var source = pair.Key;
            var observation = pair.Value;
            observation.Reasons.Add(CoverageReasonCodes.ConflictingModule);
            var diagnostic = CoverageDiagnostic.Create(CoverageReasonCodes.ConflictingModule,
                CoverageDiagnosticStage.Module, CoverageDiagnosticSeverity.Error, CoverageDiagnosticScope.Method,
                path: LogicalPath(source), methodId: source.CanonicalSignature,
                span: new SourceSpan(source.StartLine, source.EndLine), contextId: source.ContextId,
                moduleIdentities: observation.ModuleIdentities,
                message: "Compatible observations identify conflicting modules.");
            diagnostics.Add(diagnostic);
            observation.DiagnosticIds.Add(diagnostic.Id);
        }

        var matches = sourceMethods.Select(source =>
        {
            var observation = observations[source];
            double? coverage = observation.ModuleIdentities.Count > 1 || observation.Points.Count == 0
                ? null
                : (double)observation.Points.Count(point => point.Value) / observation.Points.Count;
            if (coverage is null && observation.HasCompatibleObservation && observation.Points.Count == 0)
                observation.Reasons.Add(CoverageReasonCodes.NoEligiblePoints);
            if (coverage is null && observation.Reasons.Count == 0)
                observation.Reasons.Add(reportList.Length > 0 ? CoverageReasonCodes.NoMatchingMethod : CoverageReasonCodes.Unavailable);
            var reasons = observation.Reasons.Order(StringComparer.Ordinal).ToArray();
            var primary = coverage is null ? PrimaryReason(reasons) : null;
            var supporting = observation.DiagnosticIds.Order(StringComparer.Ordinal).ToArray();
            var status = coverage is double known
                ? new MethodCoverageStatus("known", known, observation.Points.Count, observation.Points.Count(point => point.Value), null, [], supporting)
                : new MethodCoverageStatus("unknown", null, null, null, primary, reasons, supporting);
            var metric = new MethodMetric(source.File, source.TypeName, source.MethodName, source.DisplayName,
                source.StartLine, source.EndLine, source.Complexity, coverage) { CoverageStatus = status };
            return new DetailedMatch(source, metric, primary) { ReasonCodes = reasons, DiagnosticIds = supporting };
        }).ToArray();

        var orderedDiagnostics = diagnostics.GroupBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.MethodId, StringComparer.Ordinal).ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();
        return new CoverageMatchResult(matches, orderedDiagnostics);
    }

    private static void RecordRejection(IEnumerable<SourceMethod> sources, CoverageMethod covered, string code,
        CoverageDiagnosticStage stage, IReadOnlyDictionary<SourceMethod, Observation> observations,
        ICollection<CoverageDiagnostic> diagnostics)
    {
        var candidates = sources.OrderBy(LogicalPath, StringComparer.Ordinal)
            .ThenBy(source => source.CanonicalSignature, StringComparer.Ordinal).ToArray();
        foreach (var source in candidates) observations[source].Reasons.Add(code);
        diagnostics.Add(CoverageDiagnostic.Create(code, stage, CoverageDiagnosticSeverity.Warning,
            CoverageDiagnosticScope.Observation, covered.ReportId, covered.ObservationId,
            candidatePaths: candidates.Select(LogicalPath),
            candidateMethodIds: candidates.Select(source => source.CanonicalSignature),
            reportedType: covered.TypeName, reportedMethodName: covered.MethodName,
            reportedParameterCount: covered.ParameterCount,
            moduleIdentities: covered.ModuleIdentity is null ? [] : [covered.ModuleIdentity],
            message: $"Coverage observation rejected at {stage.ToString().ToLowerInvariant()} stage."));
        foreach (var source in candidates)
        {
            var diagnostic = CoverageDiagnostic.Create(code, stage, CoverageDiagnosticSeverity.Warning,
                CoverageDiagnosticScope.Method, covered.ReportId, covered.ObservationId,
                path: LogicalPath(source), methodId: source.CanonicalSignature,
                span: new SourceSpan(source.StartLine, source.EndLine), contextId: source.ContextId,
                reportedType: covered.TypeName, reportedMethodName: covered.MethodName,
                reportedParameterCount: covered.ParameterCount,
                moduleIdentities: covered.ModuleIdentity is null ? [] : [covered.ModuleIdentity],
                message: $"Coverage observation rejected for {source.CanonicalSignature}.");
            diagnostics.Add(diagnostic);
            observations[source].DiagnosticIds.Add(diagnostic.Id);
        }
    }

    private static string PrimaryReason(IReadOnlyCollection<string> reasons)
    {
        foreach (var code in new[]
        {
            CoverageReasonCodes.ConflictingModule,
            CoverageReasonCodes.AmbiguousMethod,
            CoverageReasonCodes.SpanMismatch,
            CoverageReasonCodes.NoEligiblePoints,
            CoverageReasonCodes.SignatureMismatch,
            CoverageReasonCodes.TypeMismatch,
            CoverageReasonCodes.AmbiguousPath,
            CoverageReasonCodes.PathOutsideRoot,
            CoverageReasonCodes.InvalidPath,
            CoverageReasonCodes.MissingPath,
            CoverageReasonCodes.NoMatchingMethod,
            CoverageReasonCodes.Unavailable
        })
            if (reasons.Contains(code, StringComparer.Ordinal)) return code;
        return CoverageReasonCodes.Unavailable;
    }

    private static bool PathsEqual(PathIdentityPolicy policy, string left, string right)
    {
        try { return policy.Comparer.Equals(policy.Normalize(left), policy.Normalize(right)); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TypesEqual(string source, string covered) => source == covered.Replace('+', '.');
    private static string LogicalPath(SourceMethod source) =>
        (source.LogicalPath ?? source.File).Replace(Path.DirectorySeparatorChar, '/');
    private static bool IsGeneratedStateMachine(string typeName) =>
        typeName.Contains(">d__", StringComparison.Ordinal) && typeName.Contains('<', StringComparison.Ordinal);

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
        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        public HashSet<string> DiagnosticIds { get; } = new(StringComparer.Ordinal);
        public bool HasCompatibleObservation { get; set; }
    }

    private readonly record struct PointIdentity(int Line, int? StartColumn, int? EndLine, int? EndColumn, int? Offset);
}
