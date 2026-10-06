using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crap4CSharp.Core;

public sealed record BaselineEntry(string Kind, string EntityKey, string Rule, string Ruleset, string Path,
    string BodyChecksum, int Complexity, double Coverage, double Crap);

public sealed record BaselineDocument(string SchemaVersion, string Ruleset, string PolicyHash,
    string SourceIdentity, string Revision, IReadOnlyList<BaselineEntry> Entries)
{
    public const string Version = "baseline-v1";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static BaselineDocument Parse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            RejectDuplicateProperties(parsed.RootElement);
            var value = JsonSerializer.Deserialize<BaselineDocument>(bytes, Json)
                ?? throw new BaselineException("baseline.malformed", "Baseline is empty.");
            Validate(value, value.PolicyHash, value.Ruleset);
            return value;
        }
        catch (JsonException exception) { throw new BaselineException("baseline.malformed", exception.Message); }
    }

    public static void Validate(BaselineDocument value, string policyHash, string ruleset)
    {
        if (value.SchemaVersion != Version) throw new BaselineException("baseline.schemaUnsupported", "Unsupported baseline schema.");
        if (value.Ruleset != ruleset) throw new BaselineException("baseline.rulesetMismatch", "Baseline ruleset does not match policy.");
        if (value.PolicyHash != policyHash) throw new BaselineException("baseline.policyMismatch", "Baseline policy hash does not match policy.");
        if (string.IsNullOrWhiteSpace(value.SourceIdentity) || string.IsNullOrWhiteSpace(value.Revision))
            throw new BaselineException("baseline.sourceMissing", "Baseline source identity and revision are required.");
        if (value.Entries is null || value.Entries.Cast<BaselineEntry?>().Any(entry => entry is null))
            throw new BaselineException("baseline.entryInvalid", "Baseline contains a null entry.");
        var duplicate = value.Entries.GroupBy(item => (item.EntityKey, item.Rule, item.Ruleset), EqualityComparer<(string, string, string)>.Default)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new BaselineException("baseline.duplicateIdentity", "Baseline contains duplicate entity identities.");
        foreach (var entry in value.Entries)
        {
            if (entry.Ruleset != ruleset || entry.Kind is not ("method" or "anonymous" or "family") ||
                string.IsNullOrWhiteSpace(entry.EntityKey) || string.IsNullOrWhiteSpace(entry.Rule) ||
                string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.BodyChecksum) ||
                entry.Complexity < 0 || !double.IsFinite(entry.Coverage) || entry.Coverage is < 0 or > 1 ||
                !double.IsFinite(entry.Crap) || entry.Crap < 0)
                throw new BaselineException("baseline.entryInvalid", "Baseline contains an invalid entry.");
            try { _ = CanonicalIdentity.NormalizeLogicalPath(entry.Path); }
            catch (ArgumentException exception) { throw new BaselineException("baseline.pathInvalid", exception.Message); }
        }
    }

    public static byte[] Serialize(BaselineDocument value)
    {
        Validate(value, value.PolicyHash, value.Ruleset);
        var normalized = value with { Entries = value.Entries.OrderBy(item => item.EntityKey, StringComparer.Ordinal)
            .ThenBy(item => item.Rule, StringComparer.Ordinal).ToArray() };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, Json) + "\n");
    }

    public static BaselineDocument Generate(string policyHash, string ruleset, string sourceIdentity, string revision,
        double threshold, IEnumerable<PolicyObservation> observations) => new(Version, ruleset, policyHash, sourceIdentity, revision,
        observations.Where(item => item.Crap is not null && item.Crap > threshold && item.Coverage is not null)
            .Select(item => new BaselineEntry(item.Kind, item.EntityKey, item.Rule, item.Ruleset, item.Path,
                item.BodyChecksum, item.Complexity, item.Coverage!.Value, item.Crap!.Value))
            .OrderBy(item => item.EntityKey, StringComparer.Ordinal).ThenBy(item => item.Rule, StringComparer.Ordinal).ToArray());

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new BaselineException("baseline.duplicateKey", $"Duplicate baseline key: {property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

public sealed class BaselineException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
