using System.Security.Cryptography;
using System.Text;

namespace Crap4CSharp.Core;

public static class ResultContract
{
    public const string SchemaVersion = "1.0";
    public const string ComplexityRulesetVersion = "ordinary-methods-v1";
}

public static class CoverageReasonCodes
{
    public const string NoMatchingMethod = "coverage.noMatchingMethod";
    public const string AmbiguousMethod = "coverage.ambiguousMethod";
    public const string ConflictingModule = "coverage.conflictingModule";
    public const string NoEligiblePoints = "coverage.noEligiblePoints";
    public const string UnsupportedGeneratedMapping = "coverage.unsupportedGeneratedMapping";
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
    EvaluationDecision Decision);

public sealed record PolicyOptions(double Threshold, bool AllowMissingCoverage, string ComparisonOperator = "gt");

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
    string? CoverageReason);

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
    IReadOnlyList<string> ReasonCodes);

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
    int ExitCode);

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
