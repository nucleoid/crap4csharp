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
    string? Reason);

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
    IReadOnlyList<string> IncompleteReasons);

public static class CallableFamilyEvaluator
{
    public const string Rule = "crap.nestedFamilyRisk";

    public static IReadOnlyList<CallableFamilyMetric> Evaluate(
        CallableInventoryResult inventory,
        IEnumerable<CallableCoverageObservation> observations,
        double threshold)
    {
        if (!double.IsFinite(threshold) || threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        var byId = inventory.Callables.ToDictionary(item => item.CallableId, StringComparer.Ordinal);
        var children = inventory.Callables.Where(item => item.ParentId is not null)
            .GroupBy(item => item.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var coverage = observations.GroupBy(item => item.CallableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var output = new List<CallableFamilyMetric>();

        foreach (var root in inventory.Callables.Where(item => item.ParentId is null && children.ContainsKey(item.CallableId)))
        {
            var members = Descendants(root, children).Prepend(root)
                .Where(item => item.Applicability == CallableApplicability.Applicable).ToArray();
            var complexity = 1 + members.Sum(item => (item.Complexity ?? 1) - 1);
            var points = new Dictionary<PointIdentity, bool>();
            var incomplete = new List<string>();
            var reasons = new List<string>();
            foreach (var member in members)
            {
                if (!coverage.TryGetValue(member.CallableId, out var memberObservations) ||
                    memberObservations.Length == 0 || memberObservations.Any(item => item.Status != "known"))
                {
                    incomplete.Add(member.CallableId);
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
                reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
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
        if (!children.TryGetValue(root.CallableId, out var direct)) yield break;
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
        var byId = inventory.Callables.ToDictionary(item => item.CallableId, StringComparer.Ordinal);
        var familyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in changed)
        {
            if (!byId.TryGetValue(id, out var current)) continue;
            var nested = current.ParentId is not null;
            while (current.ParentId is not null && byId.TryGetValue(current.ParentId, out var parent)) current = parent;
            if (nested) familyIds.Add(CallableFamilyEvaluator.FamilyId(current.CallableId, inventory.Ruleset));
        }
        return new CallableChangeSelection(changed, familyIds.Order(StringComparer.Ordinal).ToArray());
    }
}
