using System.Text.Json;

namespace Crap4CSharp.Core;

public enum ExemptionTrust { BaseTrusted, CiAuthorized, LocalUnreviewed }

public sealed record CallableExemptionMatch(
    string CallableId,
    string Status,
    string ReasonCode,
    string Justification,
    string ReviewReference,
    IReadOnlyList<string> FamilyIds,
    bool ApprovedForEnforcement);

public sealed record CallableExemptionValidation(
    IReadOnlyList<CallableExemptionMatch> Matches,
    IReadOnlyList<string> Errors,
    bool IncompleteScope);

public static class CallableExemptions
{
    public const string Version = "callable-exemptions-v1";

    public static CallableExemptionValidation Validate(
        ReadOnlySpan<byte> bytes,
        ExemptionTrust trust,
        CallableInventoryResult inventory,
        IEnumerable<CallableCoverageObservation> observations,
        IEnumerable<CallableFamilyMetric> families)
    {
        var errors = new List<string>();
        var entries = Parse(bytes, errors);
        if (errors.Count > 0) return new([], errors, false);
        var duplicate = entries.GroupBy(item => (item.Ruleset, item.ContextId, item.TargetFramework,
            item.CallableId, item.BodyChecksum, item.ReasonCode)).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) errors.Add("exemption.duplicate");

        var byCallable = inventory.Callables.GroupBy(item => item.CallableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var byObservation = observations.GroupBy(item => item.CallableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var familyById = families.ToDictionary(item => item.FamilyId, StringComparer.Ordinal);
        var matches = new List<CallableExemptionMatch>();
        foreach (var entry in entries)
        {
            if (ContainsWildcard(entry)) { errors.Add("exemption.wildcardRejected"); continue; }
            if (entry.Ruleset != inventory.Ruleset) { errors.Add("exemption.rulesetMismatch"); continue; }
            if (entry.ContextId != inventory.ContextId) { errors.Add("exemption.contextMismatch"); continue; }
            if (entry.TargetFramework != inventory.TargetFramework) { errors.Add("exemption.targetFrameworkMismatch"); continue; }
            if (!byCallable.TryGetValue(entry.CallableId, out var callables)) { errors.Add("exemption.unmatched"); continue; }
            if (callables.Length != 1) { errors.Add("exemption.multiplyMatched"); continue; }
            var callable = callables[0];
            if (entry.BodyChecksum != callable.BodyChecksum) { errors.Add("exemption.staleBody"); continue; }
            if (!byObservation.TryGetValue(entry.CallableId, out var callableObservations) || callableObservations.Length != 1)
            { errors.Add(callableObservations is { Length: > 1 } ? "exemption.multiplyMatched" : "exemption.unmatchedObservation"); continue; }
            var observation = callableObservations[0];
            if (observation.Status != "unknown" || observation.Reason != entry.ReasonCode || !IsUnsupported(entry.ReasonCode))
            { errors.Add("exemption.reasonNotUnsupported"); continue; }

            var affectedFamilies = families.Where(family => family.IncompleteCallableIds.Contains(entry.CallableId, StringComparer.Ordinal))
                .Select(family => family.FamilyId).Order(StringComparer.Ordinal).ToArray();
            if (affectedFamilies.Except(entry.FamilyIds, StringComparer.Ordinal).Any())
            { errors.Add("exemption.familyAcknowledgementMissing"); continue; }
            if (entry.FamilyIds.Any(id => !familyById.ContainsKey(id) || !affectedFamilies.Contains(id, StringComparer.Ordinal)))
            { errors.Add("exemption.familyUnmatched"); continue; }

            var approved = trust is ExemptionTrust.BaseTrusted or ExemptionTrust.CiAuthorized;
            matches.Add(new CallableExemptionMatch(entry.CallableId,
                approved ? "exempted-unsupported" : "local-unreviewed", entry.ReasonCode,
                entry.Justification, entry.ReviewReference, entry.FamilyIds, approved));
        }
        return errors.Count > 0
            ? new CallableExemptionValidation([], errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), false)
            : new CallableExemptionValidation(matches.OrderBy(item => item.CallableId, StringComparer.Ordinal).ToArray(), [], matches.Count > 0);
    }

    private static List<ExemptionEntry> Parse(ReadOnlySpan<byte> bytes, ICollection<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
                version.GetString() != Version || !root.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
            { errors.Add("exemption.malformed"); return []; }
            var output = new List<ExemptionEntry>();
            foreach (var item in entries.EnumerateArray())
            {
                if (!String(item, "ruleset", out var ruleset) || !String(item, "contextId", out var contextId) ||
                    !String(item, "targetFramework", out var tfm) || !String(item, "callableId", out var callableId) ||
                    !String(item, "bodyChecksum", out var checksum) || !String(item, "reasonCode", out var reason) ||
                    !String(item, "justification", out var justification) || !String(item, "reviewReference", out var review) ||
                    !item.TryGetProperty("familyIds", out var familyIds) || familyIds.ValueKind != JsonValueKind.Array ||
                    familyIds.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                { errors.Add("exemption.malformed"); return []; }
                var families = familyIds.EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (families.Length != families.Distinct(StringComparer.Ordinal).Count())
                { errors.Add("exemption.duplicateFamily"); return []; }
                output.Add(new(ruleset!, contextId!, tfm!, callableId!, checksum!, reason!, justification!, review!, families));
            }
            return output;
        }
        catch (JsonException)
        {
            errors.Add("exemption.malformed");
            return [];
        }
    }

    private static bool String(JsonElement item, string name, out string? value)
    {
        value = null;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString())) return false;
        value = property.GetString();
        return true;
    }

    private static bool ContainsWildcard(ExemptionEntry item) => new[]
    {
        item.Ruleset, item.ContextId, item.TargetFramework, item.CallableId, item.BodyChecksum,
        item.ReasonCode, item.Justification, item.ReviewReference
    }.Any(value => value.Contains('*', StringComparison.Ordinal));

    private static bool IsUnsupported(string reason) => reason is CoverageReasonCodes.UnsupportedGeneratedMapping or
        CoverageReasonCodes.UnsupportedCallable or CoverageReasonCodes.AmbiguousCallableOwnership;

    private sealed record ExemptionEntry(string Ruleset, string ContextId, string TargetFramework,
        string CallableId, string BodyChecksum, string ReasonCode, string Justification,
        string ReviewReference, IReadOnlyList<string> FamilyIds);
}
