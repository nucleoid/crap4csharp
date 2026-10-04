using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Crap4CSharp.Core;

public static class ResultContract
{
    public const string SchemaVersion = "1.0";
    public const string ComplexityRulesetVersion = "ordinary-methods-v1";
}

public static class CoverageReasonCodes
{
    public const string MissingPath = "coverage.missingPath";
    public const string InvalidPath = "coverage.invalidPath";
    public const string PathOutsideRoot = "coverage.pathOutsideRoot";
    public const string PathMappingConflict = "coverage.pathMappingConflict";
    public const string AmbiguousPath = "coverage.ambiguousPath";
    public const string TypeMismatch = "coverage.typeMismatch";
    public const string SignatureMismatch = "coverage.signatureMismatch";
    public const string SpanMismatch = "coverage.spanMismatch";
    public const string NoMatchingMethod = "coverage.noMatchingMethod";
    public const string AmbiguousMethod = "coverage.ambiguousMethod";
    public const string ConflictingModule = "coverage.conflictingModule";
    public const string NoEligiblePoints = "coverage.noEligiblePoints";
    public const string UnsupportedGeneratedMapping = "coverage.unsupportedGeneratedMapping";
    public const string UnsupportedMultiDocumentMapping = "coverage.unsupportedMultiDocumentMapping";
    public const string ContextMismatch = "coverage.contextMismatch";
    public const string ContextUnbound = "coverage.contextUnbound";
    public const string AmbiguousCallableOwnership = "coverage.ambiguousCallableOwnership";
    public const string UnsupportedCallable = "scope.unsupportedCallable";
    public const string PdbUnavailable = "coverage.pdbUnavailable";
    public const string PdbMalformed = "coverage.pdbMalformed";
    public const string PeUnavailable = "coverage.peUnavailable";
    public const string PeMalformed = "coverage.peMalformed";
    public const string PdbIdentityMismatch = "coverage.pdbIdentityMismatch";
    public const string SourceChecksumMismatch = "coverage.sourceChecksumMismatch";
    public const string Unavailable = "coverage.unavailable";
}

public sealed record ResultDocument(
    string SchemaVersion,
    string ToolVersion,
    string ComplexityRulesetVersion,
    EvaluationSection Evaluation,
    RunSection Run);

public sealed record EvaluationSection(
    string InvocationMode,
    PolicyOptions Policy,
    EvaluationScope Scope,
    IReadOnlyList<EvaluationContext> Contexts,
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<MetricResult> Metrics,
    IReadOnlyList<FindingResult> Findings,
    CoverageSummary Coverage,
    IReadOnlyList<ArtifactIdentity> Artifacts,
    EvaluationDecision Decision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvaluationProvenance? Provenance { get; init; }
    public CoveragePathPolicyResult CoveragePathPolicy { get; init; } = new("auto", []);
    public IReadOnlyList<CoverageDiagnostic> CoverageDiagnostics { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CallableResult>? Callables { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CallableFamilyMetric>? Families { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CallableExemptionMatch>? CallableExemptions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ExemptionErrors { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PolicyTrustResult? PolicyTrust { get; init; }
    public IReadOnlyList<PolicyDifferenceResult> PolicyDifferences { get; init; } = [];
}

public sealed record PolicyTrustResult(string Trust, string Revision, string PolicyPath,
    string PolicyHash, string CompatibilityHash);
public sealed record PolicyDifferenceResult(string Path, string Status, string TrustedHash, string? ProposedHash);

public sealed record CallableResult(
    string CallableId,
    string ObservationId,
    string Kind,
    string? ParentId,
    string ContextId,
    string Path,
    CallableSourceSpan Span,
    int? Complexity,
    string Applicability,
    string CoverageStatus,
    double? Coverage,
    double? Crap,
    string? CoverageReason,
    string CoverageCapability,
    string BodyChecksum)
{
    public string Ruleset { get; init; } = ComplexityRules.CallablesV1;
    public string? SemanticSignature { get; init; }
    public IReadOnlyList<string> Documents { get; init; } = [];
    public string? MappingEvidenceKind { get; init; }
    public IReadOnlyList<string> FamilyIds { get; init; } = [];
}

public sealed record PolicyOptions(double Threshold, bool AllowMissingCoverage, string ComparisonOperator = "gt");

public sealed record CoveragePathPolicyResult(string Case, IReadOnlyList<CoveragePathMappingResult> Mappings);
public sealed record CoveragePathMappingResult(string ReportRoot, string LocalRoot, string Id);

public sealed record EvaluationScope(string LogicalWorkspaceRoot, IReadOnlyList<string> Sources);

public sealed record EvaluationContext(
    string Id,
    string AnalysisMode,
    string? Project,
    string? TargetFramework,
    string? Configuration,
    string? SourceSetIdentity,
    string? ExternalRootIdentity);

public sealed record CheckResult(string Name, string Status, string Reason, bool Required);

public sealed record MetricResult(
    string ContextId,
    string Path,
    string MethodIdentity,
    string? Signature,
    SourceSpan Span,
    int Complexity,
    double? Coverage,
    double? Crap,
    string? CoverageReason)
{
    public MethodCoverageStatus? CoverageStatus { get; init; }
}

public sealed record SourceSpan(int StartLine, int EndLine);

public sealed record FindingResult(
    string Id,
    string EntityKey,
    string Code,
    string Severity,
    string Category,
    string ContextId,
    string Path,
    string MethodIdentity,
    string? Signature,
    SourceSpan Span,
    int Complexity,
    double? Coverage,
    double? Crap,
    string? CoverageReason,
    double Threshold,
    string ComparisonOperator,
    string Decision,
    IReadOnlyList<string> ReasonCodes)
{
    public MethodCoverageStatus? CoverageStatus { get; init; }
}

public sealed record CoverageSummary(int Methods, int Known, int Unknown, IReadOnlyDictionary<string, int> Reasons);

public sealed record ArtifactIdentity(string Kind, string Path, string? ContentIdentity, string IdentityStatus);

public sealed record EvaluationDecision(bool Completed, string PolicyDecision, string Reason);

public sealed record RunSection(
    string InvocationId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    double DurationMilliseconds,
    IReadOnlyList<CommandRecord> Commands,
    IReadOnlyList<string> DiagnosticLocations,
    IReadOnlyList<RunArtifact> Artifacts,
    CancellationDetails Cancellation,
    string Status,
    int ExitCode)
{
    public IReadOnlyList<CoverageRunEvidence> CoverageEvidence { get; init; } = [];
}

public sealed record CoverageRunEvidence(
    string Id,
    string? ReportId,
    string? ObservationId,
    string? ReportedPath,
    string? LocalPath,
    string Status,
    string? MappingId);

public sealed record CommandRecord(string FileName, IReadOnlyList<string> Arguments, int? ExitCode);
public sealed record RunArtifact(string Kind, string Path, string Retention, bool Complete, bool Reusable);
public sealed record CancellationDetails(bool Cancelled, bool TimedOut, string? Reason);

public static class EvaluationDecisionReducer
{
    public static (EvaluationDecision Decision, string Status, int ExitCode) Reduce(IReadOnlyList<CheckResult> checks, bool hasViolations)
    {
        var requiredFailure = checks.Any(check => check.Required && check.Status is "operationalError" or "cancelled");
        if (requiredFailure)
            return (new EvaluationDecision(false, "unknown", checks.First(check => check.Required && check.Status is "operationalError" or "cancelled").Reason),
                checks.Any(check => check.Status == "cancelled") ? "cancelled" : "operationalError", 1);
        if (hasViolations)
            return (new EvaluationDecision(true, "fail", "crap.thresholdExceeded"), "completed", 2);
        var crapCheck = checks.FirstOrDefault(check => check.Name == "crap");
        var applicable = crapCheck?.Status is "pass" or "fail";
        return applicable
            ? (new EvaluationDecision(true, "pass", "crap.withinThreshold"), "completed", 0)
            : (new EvaluationDecision(true, "notApplicable", crapCheck?.Reason ?? "crap.noEligibleMethods"), "completed", 0);
    }
}

public static class FindingIdentity
{
    public static string Create(string contextId, string path, string methodIdentity, SourceSpan span, string code)
    {
        var identity = string.Join("\n", contextId, path, methodIdentity,
            span.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
            span.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture), code);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}

public static class EntityIdentity
{
    public static string Create(string contextId, string path, string canonicalSignature, string code)
    {
        var identity = string.Join("\n", contextId, path, canonicalSignature, code, ResultContract.ComplexityRulesetVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }
}
