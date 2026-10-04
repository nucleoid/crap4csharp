using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

public sealed record CapturedCoverage(string LogicalPath, ImmutableArray<byte> Bytes, string Format, string CoordinateKind);
public sealed record EvaluationInput(string ContextId, IReadOnlyList<CapturedSource> Sources,
    IReadOnlyList<CapturedCoverage> Coverage, CSharpParseOptions ParseOptions, PolicyOptions Policy,
    ProvenanceResult Provenance);
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
