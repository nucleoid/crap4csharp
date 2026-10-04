namespace Crap4CSharp.Core;

public sealed record CallableCoverageResolution(
    IReadOnlyList<CallableCoverageObservation> Observations,
    IReadOnlyList<CoverageDiagnostic> Diagnostics);

public static class CallableCoverageResolver
{
    private static readonly IReadOnlyDictionary<string, string> TypeAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bool"] = "System.Boolean", ["byte"] = "System.Byte", ["sbyte"] = "System.SByte",
            ["char"] = "System.Char", ["decimal"] = "System.Decimal", ["double"] = "System.Double",
            ["float"] = "System.Single", ["int"] = "System.Int32", ["uint"] = "System.UInt32",
            ["long"] = "System.Int64", ["ulong"] = "System.UInt64", ["short"] = "System.Int16",
            ["ushort"] = "System.UInt16", ["object"] = "System.Object", ["string"] = "System.String",
            ["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr", ["void"] = "System.Void"
        };

    public static CallableCoverageResolution Resolve(
        CallableInventoryResult inventory,
        IEnumerable<CoverageMethod> reports,
        string? expectedModuleIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var observations = inventory.Callables.ToDictionary(item => item.ObservationId,
            _ => new Accumulator(), StringComparer.Ordinal);
        var diagnostics = new List<CoverageDiagnostic>();

        foreach (var report in reports)
        {
            if (!string.Equals(report.ContextId, inventory.ContextId, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(CoverageReasonCodes.ContextMismatch, report, [],
                    "Coverage observation belongs to a different or unbound compilation context."));
                continue;
            }
            if (expectedModuleIdentity is not null &&
                !string.Equals(report.ModuleIdentity, expectedModuleIdentity, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic(CoverageReasonCodes.ConflictingModule, report, [],
                    "Coverage observation does not identify the expected module."));
                continue;
            }
            if (IsGenerated(report))
            {
                diagnostics.Add(Diagnostic(CoverageReasonCodes.UnsupportedGeneratedMapping, report, [],
                    "Generated callable coverage requires an authoritative PE/portable-PDB mapping."));
                continue;
            }

            var byPath = inventory.Callables.Where(item => item.Applicability == CallableApplicability.Applicable &&
                PathsEqual(item.Path, report.File)).ToArray();
            var byIdentity = byPath.Where(item => SemanticMatches(item.SemanticIdentity, report)).ToArray();
            var candidates = byIdentity.Length > 0 ? byIdentity : byPath.Where(item =>
                item.CoverageCapability == "semantic-ordinary" && SpanContains(item.Span, report.SequencePoints)).ToArray();

            if (byIdentity.Length == 0 && report.MethodName.Length > 0)
            {
                candidates = HasIncompleteSignature(report)
                    ? inventory.Callables.Where(item => PathsEqual(item.Path, report.File) &&
                        SemanticHeaderMatches(item.SemanticIdentity, report)).ToArray()
                    : [];
                if (candidates.Length == 1 && candidates[0].Applicability != CallableApplicability.Applicable)
                    candidates = [];
            }
            if (candidates.Length != 1)
            {
                var code = candidates.Length > 1 || byPath.Length > 1 && report.MethodName.Length == 0
                    ? CoverageReasonCodes.AmbiguousCallableOwnership
                    : CoverageReasonCodes.NoMatchingMethod;
                var affected = candidates.Length > 0 ? candidates : byPath;
                diagnostics.Add(Diagnostic(code, report, affected, candidates.Length > 1
                    ? "Coverage point ownership is not exclusive."
                    : "Coverage observation does not identify a supported callable."));
                foreach (var item in affected) observations[item.ObservationId].Reasons.Add(code);
                continue;
            }

            var candidate = candidates[0];
            if (candidate.CoverageCapability != "semantic-ordinary")
            {
                observations[candidate.ObservationId].Reasons.Add(candidate.CoverageReason ?? CoverageReasonCodes.UnsupportedGeneratedMapping);
                diagnostics.Add(Diagnostic(candidate.CoverageReason ?? CoverageReasonCodes.UnsupportedGeneratedMapping,
                    report, [candidate], "Callable coverage capability is unsupported without authoritative generated mapping."));
                continue;
            }
            if (report.SequencePoints.Count == 0)
            {
                observations[candidate.ObservationId].Reasons.Add(CoverageReasonCodes.NoEligiblePoints);
                diagnostics.Add(Diagnostic(CoverageReasonCodes.NoEligiblePoints, report, [candidate],
                    "Coverage observation contains no eligible sequence points."));
                continue;
            }
            if (!SpanContains(candidate.Span, report.SequencePoints))
            {
                observations[candidate.ObservationId].Reasons.Add(CoverageReasonCodes.SpanMismatch);
                diagnostics.Add(Diagnostic(CoverageReasonCodes.SpanMismatch, report, [candidate],
                    "Coverage points fall outside the authored callable span."));
                continue;
            }
            var lineOwnershipConflicts = ColumnlessOwnershipConflicts(candidate, inventory.Callables,
                report.SequencePoints);
            if (lineOwnershipConflicts.Count > 0)
            {
                observations[candidate.ObservationId].Reasons.Add(CoverageReasonCodes.AmbiguousCallableOwnership);
                diagnostics.Add(Diagnostic(CoverageReasonCodes.AmbiguousCallableOwnership, report,
                    [candidate, .. lineOwnershipConflicts],
                    "Columnless coverage points share a source line with another authored callable region."));
                continue;
            }

            var accumulator = observations[candidate.ObservationId];
            if (string.IsNullOrWhiteSpace(report.ModuleIdentity))
            {
                accumulator.Reasons.Add(CoverageReasonCodes.ContextUnbound);
                diagnostics.Add(Diagnostic(CoverageReasonCodes.ContextUnbound, report, [candidate],
                    "Coverage observation has no module identity and cannot join a callable context."));
                continue;
            }
            if (accumulator.ModuleIdentity is not null &&
                !string.Equals(accumulator.ModuleIdentity, report.ModuleIdentity, StringComparison.Ordinal))
            {
                accumulator.Points.Clear();
                accumulator.Reasons.Add(CoverageReasonCodes.ConflictingModule);
                accumulator.ModuleConflict = true;
                diagnostics.Add(Diagnostic(CoverageReasonCodes.ConflictingModule, report, [candidate],
                    "Coverage observations from distinct modules cannot be unioned."));
                continue;
            }
            accumulator.ModuleIdentity ??= report.ModuleIdentity;
            if (accumulator.ModuleConflict) continue;
            foreach (var point in report.SequencePoints)
            {
                var document = report.DocumentIdentities.SingleOrDefault() ?? report.File ?? "<unknown-document>";
                var value = new CallableCoveragePoint(inventory.ContextId, document, point.Line,
                    point.StartColumn ?? 0, point.EndLine ?? point.Line, point.EndColumn ?? 0,
                    point.Offset ?? -1, point.Visits > 0);
                var key = new PointKey(value.ContextId, value.DocumentIdentity, value.StartLine, value.StartColumn,
                    value.EndLine, value.EndColumn, value.Offset);
                accumulator.Points[key] = accumulator.Points.TryGetValue(key, out var existing)
                    ? existing with { Visited = existing.Visited || value.Visited }
                    : value;
            }
        }

        var resolved = inventory.Callables.Select(item =>
        {
            var value = observations[item.ObservationId];
            if (value.Points.Count > 0)
                return new CallableCoverageObservation(item.CallableId, "known",
                    value.Points.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray(), null)
                    { ObservationId = item.ObservationId };
            var reason = item.CoverageCapability is "unsupported" or "portable-pdb-required"
                ? item.CoverageReason
                : value.Reasons.Order(StringComparer.Ordinal).FirstOrDefault() ?? item.CoverageReason;
            reason ??=
                (item.Applicability == CallableApplicability.NotApplicable ? item.ApplicabilityReason : CoverageReasonCodes.Unavailable);
            return new CallableCoverageObservation(item.CallableId,
                item.Applicability == CallableApplicability.NotApplicable ? "not-applicable" : "unknown", [], reason)
                { ObservationId = item.ObservationId };
        }).OrderBy(item => item.CallableId, StringComparer.Ordinal).ToArray();
        return new CallableCoverageResolution(resolved,
            diagnostics.GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(item => item.Code, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray());
    }

    private static bool SemanticMatches(CallableSemanticIdentity? semantic, CoverageMethod report)
    {
        if (semantic is null || semantic.MetadataName != report.MethodName ||
            !TypesEqual(semantic.TypeName, report.TypeName) ||
            report.ParameterCount is int count && count != semantic.Parameters.Count ||
            report.GenericArity is int arity && arity != semantic.GenericArity) return false;
        if (string.IsNullOrWhiteSpace(report.RawSignature)) return true;
        if (Normalize(report.RawSignature) == Normalize(semantic.ReportSignature)) return true;
        var parameters = ParameterTypes(report.RawSignature);
        return parameters is not null && parameters.SequenceEqual(
            semantic.Parameters.Select(item => Normalize(item.Type) +
                (item.RefKind == "none" ? string.Empty : "&")), StringComparer.Ordinal);
    }

    private static bool SemanticHeaderMatches(CallableSemanticIdentity? semantic, CoverageMethod report) =>
        semantic is not null && semantic.MetadataName == report.MethodName &&
        TypesEqual(semantic.TypeName, report.TypeName) &&
        (report.GenericArity is not int arity || arity == semantic.GenericArity);

    private static bool HasIncompleteSignature(CoverageMethod report) => report.RawSignature is not null &&
        !CoverageSignature.TryGetParameterContents(report.RawSignature, out _);

    private static IReadOnlyList<string>? ParameterTypes(string signature)
    {
        if (!CoverageSignature.TryGetParameterContents(signature, out var value)) return null;
        if (string.IsNullOrWhiteSpace(value)) return [];
        var output = new List<string>();
        var start = 0;
        var depth = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index == value.Length || value[index] == ',' && depth == 0)
            {
                output.Add(Normalize(value[start..index]));
                start = index + 1;
            }
            else if (value[index] is '<' or '[' or '(') depth++;
            else if (value[index] is '>' or ']' or ')') depth--;
        }
        return output;
    }

    private static string Normalize(string value)
    {
        value = CoverageSignature.StripCustomModifiers(value);
        var normalized = System.Text.RegularExpressions.Regex.Replace(value,
                @"\b(class|valuetype)\s+", string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace('/', '.').Replace('+', '.');
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"`[0-9]+", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(normalized,
            @"\b(bool|byte|sbyte|char|decimal|double|float|int|uint|long|ulong|short|ushort|object|string|nint|nuint|void)\b",
            match => TypeAliases[match.Value]);
    }
    private static bool TypesEqual(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
    private static bool PathsEqual(string left, string? right) => right is not null &&
        string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.Ordinal);
    private static bool IsGenerated(CoverageMethod report) => report.MethodName == "MoveNext" ||
        report.MethodName.StartsWith("<", StringComparison.Ordinal) || report.TypeName.Contains("<>c", StringComparison.Ordinal);

    private static bool SpanContains(CallableSourceSpan span, IReadOnlyList<CoveragePoint> points) => points.All(point =>
    {
        var endLine = point.EndLine ?? point.Line;
        if (point.Line < span.StartLine || endLine > span.EndLine) return false;
        if (point.StartColumn is int start && point.Line == span.StartLine && start < span.StartColumn) return false;
        if (point.EndColumn is int end && endLine == span.EndLine && end > span.EndColumn) return false;
        return true;
    });

    private static IReadOnlyList<CallableEntry> ColumnlessOwnershipConflicts(CallableEntry candidate,
        IReadOnlyList<CallableEntry> inventory, IReadOnlyList<CoveragePoint> points) => inventory
        .Where(other => other.ObservationId != candidate.ObservationId && PathsEqual(other.Path, candidate.Path) &&
            points.Any(point => point.StartColumn is null &&
                point.Line <= other.Span.EndLine && (point.EndLine ?? point.Line) >= other.Span.StartLine))
        .OrderBy(other => other.ObservationId, StringComparer.Ordinal).ToArray();

    private static CoverageDiagnostic Diagnostic(string code, CoverageMethod report,
        IEnumerable<CallableEntry> candidates, string message) => CoverageDiagnostic.Create(code,
            code == CoverageReasonCodes.ContextMismatch ? CoverageDiagnosticStage.Context :
            code == CoverageReasonCodes.ConflictingModule ? CoverageDiagnosticStage.Module :
            code == CoverageReasonCodes.UnsupportedGeneratedMapping ? CoverageDiagnosticStage.Generated :
            CoverageDiagnosticStage.Method,
            CoverageDiagnosticSeverity.Warning, CoverageDiagnosticScope.Observation,
            report.ReportId, report.ObservationId, report.File,
            candidateMethodIds: candidates.Select(item => item.CallableId), reportedType: report.TypeName,
            reportedMethodName: report.MethodName, reportedParameterCount: report.ParameterCount,
            moduleIdentities: report.ModuleIdentity is null ? [] : [report.ModuleIdentity], contextId: report.ContextId,
            message: message);

    private sealed class Accumulator
    {
        public Dictionary<PointKey, CallableCoveragePoint> Points { get; } = [];
        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        public string? ModuleIdentity { get; set; }
        public bool ModuleConflict { get; set; }
    }

    private readonly record struct PointKey(string ContextId, string DocumentIdentity, int StartLine,
        int StartColumn, int EndLine, int EndColumn, int Offset) : IComparable<PointKey>
    {
        public int CompareTo(PointKey other) => string.CompareOrdinal(ToString(), other.ToString());
    }
}
