using System.Security.Cryptography;
using System.Text;

namespace Crap4CSharp.Core;

public enum CoverageDiagnosticStage { Path, Context, Method, Points, Module, Generated }

public enum CoverageDiagnosticSeverity { Info, Warning, Error }

public enum CoverageDiagnosticScope { Report, Observation, Method }

public enum CoveragePathResolutionStatus { Resolved, Missing, Ambiguous, Invalid, OutsideRoot, MappingConflict }

public sealed record CoverageDiagnostic(
    string Id,
    string Code,
    CoverageDiagnosticStage Stage,
    CoverageDiagnosticSeverity Severity,
    CoverageDiagnosticScope Scope,
    string? ContextId,
    string? ReportId,
    string? ObservationId,
    string? Path,
    string? ExternalRootId,
    string? MethodId,
    SourceSpan? Span,
    IReadOnlyList<string> CandidatePaths,
    IReadOnlyList<string> CandidateMethodIds,
    string? ReportedType,
    string? ReportedMethodName,
    int? ReportedParameterCount,
    IReadOnlyList<string> ModuleIdentities,
    string? MappingId,
    IReadOnlyList<string> EvidenceRefs,
    string RemediationCode,
    string Message)
{
    public static CoverageDiagnostic Create(
        string code,
        CoverageDiagnosticStage stage,
        CoverageDiagnosticSeverity severity,
        CoverageDiagnosticScope scope,
        string? reportId = null,
        string? observationId = null,
        string? path = null,
        string? methodId = null,
        SourceSpan? span = null,
        IEnumerable<string>? candidatePaths = null,
        IEnumerable<string>? candidateMethodIds = null,
        string? reportedType = null,
        string? reportedMethodName = null,
        int? reportedParameterCount = null,
        IEnumerable<string>? moduleIdentities = null,
        string? mappingId = null,
        string? externalRootId = null,
        string? contextId = null,
        string? message = null)
    {
        var paths = (candidatePaths ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var methods = (candidateMethodIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var modules = (moduleIdentities ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var stable = string.Join("\n", code, stage, scope, contextId, reportId, observationId, path, externalRootId,
            methodId, span?.StartLine, span?.EndLine, string.Join("\0", paths), string.Join("\0", methods),
            reportedType, reportedMethodName, reportedParameterCount, string.Join("\0", modules), mappingId);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable))).ToLowerInvariant();
        var evidenceRef = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"coverage-evidence\n{id}"))).ToLowerInvariant();
        return new CoverageDiagnostic(id, code, stage, severity, scope, contextId, reportId, observationId, path,
            externalRootId, methodId, span, paths, methods, reportedType, reportedMethodName, reportedParameterCount,
            modules, mappingId, [evidenceRef], Remediation(code), message ?? code);
    }

    private static string Remediation(string code) => code switch
    {
        CoverageReasonCodes.MissingPath => "coverage.configurePathMappingOrSource",
        CoverageReasonCodes.InvalidPath => "coverage.fixProducerPath",
        CoverageReasonCodes.PathOutsideRoot => "coverage.fixRootOrSelection",
        CoverageReasonCodes.PathMappingConflict => "coverage.correctPathMappings",
        CoverageReasonCodes.AmbiguousPath => "coverage.chooseSpecificRootOrMapping",
        CoverageReasonCodes.TypeMismatch => "coverage.useMatchingBuildType",
        CoverageReasonCodes.SignatureMismatch => "coverage.useMatchingBuildCallable",
        CoverageReasonCodes.SpanMismatch => "coverage.regenerateForSelectedSource",
        CoverageReasonCodes.AmbiguousMethod => "coverage.provideAuthoritativeCallableMetadata",
        CoverageReasonCodes.ConflictingModule => "coverage.useCompatibleArtifacts",
        CoverageReasonCodes.NoEligiblePoints => "coverage.captureEligibleSequencePoints",
        CoverageReasonCodes.UnsupportedGeneratedMapping => "coverage.provideGeneratedMapping",
        CoverageReasonCodes.UnsupportedMultiDocumentMapping => "coverage.useSupportedDocumentProjection",
        CoverageReasonCodes.ContextMismatch or CoverageReasonCodes.ContextUnbound => "coverage.bindReportContext",
        _ => "coverage.captureCorrectReport"
    };
}

public sealed record CoveragePathResolution(
    CoveragePathResolutionStatus Status,
    string? LocalPath,
    IReadOnlyList<string> CandidatePaths,
    string? MappingId,
    CoverageDiagnostic? Diagnostic);

public sealed record CoverageReadResult(
    IReadOnlyList<CoverageMethod> Methods,
    IReadOnlyList<CoverageDiagnostic> Diagnostics,
    IReadOnlyList<CoveragePathResolution> PathResolutions,
    string ReportId);

public sealed record CoverageMatchResult(
    IReadOnlyList<CoverageMatcher.DetailedMatch> Matches,
    IReadOnlyList<CoverageDiagnostic> Diagnostics);

public sealed class CoveragePathException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
