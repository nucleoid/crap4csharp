using System.Security.Cryptography;
using System.Text;

namespace Crap4CSharp.Core;

public sealed record CallableCoveragePoint(
    string ContextId,
    string DocumentIdentity,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int Offset,
    bool Visited);

public sealed record CallableCoverageObservation(
    string CallableId,
    string Status,
    IReadOnlyList<CallableCoveragePoint> Points,
    string? Reason)
{
    public string? ObservationId { get; init; }
}

public sealed record CallableFamilyMetric(
    string FamilyId,
    string RootCallableId,
    string Rule,
    string Ruleset,
    int Complexity,
    double? Coverage,
    int? EligiblePoints,
    int? VisitedPoints,
    double? Crap,
    bool IsViolation,
    IReadOnlyList<string> MemberCallableIds,
    IReadOnlyList<string> IncompleteCallableIds,
    IReadOnlyList<string> IncompleteReasons)
{
    public IReadOnlyList<string> MemberObservationIds { get; init; } = [];
    public IReadOnlyList<string> IncompleteObservationIds { get; init; } = [];
}

public static class CallableFamilyEvaluator
{
    public const string Rule = "crap.nestedFamilyRisk";

    public static IReadOnlyList<CallableFamilyMetric> Evaluate(
        CallableInventoryResult inventory,
        IEnumerable<CallableCoverageObservation> observations,
        double threshold)
    {
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        var children = inventory.Callables.Where(item => item.ParentObservationId is not null)
            .GroupBy(item => item.ParentObservationId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var coverage = observations.ToArray();
        var output = new List<CallableFamilyMetric>();

        foreach (var root in inventory.Callables.Where(item => item.ParentObservationId is null && children.ContainsKey(item.ObservationId)))
        {
            var members = Descendants(root, children).Prepend(root)
                .Where(item => item.Applicability == CallableApplicability.Applicable).ToArray();
            var complexity = 1 + members.Sum(item => (item.Complexity ?? 1) - 1);
            var points = new Dictionary<PointIdentity, bool>();
            var incomplete = new List<string>();
            var incompleteObservations = new List<string>();
            var reasons = new List<string>();
            foreach (var member in members)
            {
                var memberObservations = coverage.Where(item => item.ObservationId == member.ObservationId ||
                    item.ObservationId is null && item.CallableId == member.CallableId).ToArray();
                if (memberObservations.Length == 0 || memberObservations.Any(item => item.Status != "known"))
                {
                    incomplete.Add(member.CallableId);
                    incompleteObservations.Add(member.ObservationId);
                    reasons.AddRange(memberObservations?.Select(item => item.Reason).Where(item => item is not null).Cast<string>()
                        ?? [CoverageReasonCodes.Unavailable]);
                    continue;
                }
                foreach (var point in memberObservations.SelectMany(item => item.Points))
                {
                    var key = new PointIdentity(point.ContextId, point.DocumentIdentity, point.StartLine,
                        point.StartColumn, point.EndLine, point.EndColumn, point.Offset);
                    points[key] = points.TryGetValue(key, out var visited) ? visited || point.Visited : point.Visited;
                }
                if (memberObservations.All(item => item.Points.Count == 0))
                {
                    incomplete.Add(member.CallableId);
                    incompleteObservations.Add(member.ObservationId);
                    reasons.Add(CoverageReasonCodes.NoEligiblePoints);
                }
            }

            double? fraction = incomplete.Count == 0 && points.Count > 0
                ? (double)points.Count(item => item.Value) / points.Count
                : null;
            double? crap = fraction is double known ? CrapCalculator.Calculate(complexity, known) : null;
            var familyId = FamilyId(root.CallableId, inventory.Ruleset);
            output.Add(new CallableFamilyMetric(familyId, root.CallableId, Rule, inventory.Ruleset, complexity,
                fraction, fraction is null ? null : points.Count,
                fraction is null ? null : points.Count(item => item.Value), crap, crap > threshold,
                members.Select(item => item.CallableId).Order(StringComparer.Ordinal).ToArray(),
                incomplete.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
            {
                MemberObservationIds = members.Select(item => item.ObservationId).Order(StringComparer.Ordinal).ToArray(),
                IncompleteObservationIds = incompleteObservations.Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray()
            });
        }
        return output.OrderBy(item => item.FamilyId, StringComparer.Ordinal).ToArray();
    }

    public static string FamilyId(string rootCallableId, string ruleset)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{rootCallableId}\n{ruleset}\n{Rule}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IEnumerable<CallableEntry> Descendants(CallableEntry root,
        IReadOnlyDictionary<string, CallableEntry[]> children)
    {
        if (!children.TryGetValue(root.ObservationId, out var direct)) yield break;
        foreach (var child in direct)
        {
            yield return child;
            foreach (var descendant in Descendants(child, children)) yield return descendant;
        }
    }

    private readonly record struct PointIdentity(string ContextId, string DocumentIdentity, int StartLine,
        int StartColumn, int EndLine, int EndColumn, int Offset);
}

public sealed record CallableChangeSelection(IReadOnlyList<string> CallableIds, IReadOnlyList<string> FamilyIds);

public static class CallableChangeSelector
{
    public static CallableChangeSelection Select(CallableInventoryResult inventory, IEnumerable<string> changedCallableIds)
    {
        var changed = changedCallableIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var byId = inventory.Callables.GroupBy(item => item.CallableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byObservation = inventory.Callables.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in changed)
        {
            if (!byId.TryGetValue(id, out var matches)) continue;
            foreach (var match in matches)
            {
                var current = match;
                var nested = current.ParentObservationId is not null;
                while (current.ParentObservationId is not null &&
                    byObservation.TryGetValue(current.ParentObservationId, out var parent)) current = parent;
                if (nested) familyIds.Add(CallableFamilyEvaluator.FamilyId(current.CallableId, inventory.Ruleset));
            }
        }
        return new CallableChangeSelection(changed, familyIds.Order(StringComparer.Ordinal).ToArray());
    }
}
