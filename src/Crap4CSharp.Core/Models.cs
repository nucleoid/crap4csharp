namespace Crap4CSharp.Core;

public sealed record MethodMetric(
    string File,
    string TypeName,
    string MethodName,
    string DisplayName,
    int StartLine,
    int EndLine,
    int Complexity,
    double? Coverage)
{
    public MethodCoverageStatus? CoverageStatus { get; init; }

    public double? Crap => Coverage is double coverage
        ? Complexity * Complexity * Math.Pow(1 - coverage, 3) + Complexity
        : null;
}

public sealed record AnalysisSummary(IReadOnlyList<MethodMetric> Methods, double Threshold)
{
    public IReadOnlyList<MethodMetric> Violations => Methods
        .Where(method => method.Crap is > 0 && method.Crap > Threshold)
        .OrderByDescending(method => method.Crap)
        .ThenBy(method => method.File, StringComparer.Ordinal)
        .ThenBy(method => method.StartLine)
        .ToArray();
}

public sealed record SourceMethod(
    string File,
    string TypeName,
    string MethodName,
    string DisplayName,
    string? Signature,
    int StartLine,
    int EndLine,
    int Complexity)
{
    public string? ContextId { get; init; }
    public string CoverageTypeName { get; init; } = TypeName;
    public string CanonicalSignature { get; init; } = DisplayName;
}

public sealed record MethodCoverageStatus(
    string Status,
    double? Fraction,
    int? EligiblePoints,
    int? VisitedPoints,
    string? PrimaryReasonCode,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> DiagnosticIds);
